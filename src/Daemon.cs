// =================================================================================================
// Daemon.cs: the long-running process that wires everything together.
//
// Owns the strip (left island), the keyboard hook, the komorebi feed, the weather/network ticker, the
// taskbar hider (and its tablet-posture fix), the YASB edge probe, the wallpaper palette and the file
// watchers (settings.json, whkdrc, and Windows' own wallpaper file). After the screens change it checks
// that komorebi sees them as Windows does (CheckScreens) and restarts komorebi when it does not, and it
// never starts or restarts komorebi on a komorebi.json that would not load. Everything here runs on
// the UI thread; anything slow (komorebic calls, COM) goes through CommandQueue so the bar and the
// keyboard never wait.
//
// To add a command: handle it in Dispatch if it is quick and UI-side, otherwise in Commands.Execute.
// Then bind it in ~/.config/whkdrc as `deskhud <verb>`, or call `deskhud.exe <verb>` from YASB.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeskHud
{
    /// The long-running side of deskhud. Everything here runs on the UI thread; komorebic calls go
    /// through the command queue. It reacts to events only: komorebi notifications, window events,
    /// wallpaper, settings and posture changes. Nothing polls except the ticker's 5 second phase and
    /// the YASB edge probe at the same pace.
    sealed class Daemon : ApplicationContext
    {
        readonly CommandQueue queue = new CommandQueue();
        readonly MinimizedTray tray = new MinimizedTray();
        readonly Cheatsheet cheatsheet;
        readonly LooseWindows loose = new LooseWindows();
        // Settles bursts of komorebi events (and gives a window that blinked out a moment to come back)
        // before the open windows are compared with what komorebi tiles.
        readonly Timer looseCheck = new Timer { Interval = 250 };
        readonly FileSystemWatcher wallpaperWatcher;
        readonly Timer wallpaperDebounce = new Timer { Interval = 500 };
        readonly TaskbarHider taskbar = new TaskbarHider();
        // Explorer finishes swapping in the touch taskbar a moment after the posture changes; ask again then.
        readonly Timer postureSettle = new Timer { Interval = 1500 };
        readonly YasbEdge yasb;
        // Screens take a few seconds to settle after a change (komorebi, Windows and YASB all redo
        // their layouts); CheckScreens waits this long after the last change.
        readonly Timer screenCheck = new Timer { Interval = 10000 };
        // The screen arrangement komorebi was last restarted for (CheckScreens; command queue only).
        string healedFor;
        readonly Timer settingsDebounce = new Timer { Interval = 400 };
        readonly List<IntPtr> hooks = new List<IntPtr>();
        readonly Native.WinEventProc onWinEvent;
        readonly Commands commands;
        // The primary screen's island; it also anchors the UI thread (BeginInvoke, file watchers).
        readonly StripWindow strip;
        // Islands on the other screens, one each.
        readonly List<StripWindow> extras = new List<StripWindow>();
        Dictionary<Rectangle, int> yasbEdges = new Dictionary<Rectangle, int>();
        readonly IpcWindow ipc;
        readonly Ticker ticker;
        readonly KomorebiFeed feed;
        readonly HotkeyRunner hotkeys;
        readonly KeyboardHook keyboard;
        readonly FileSystemWatcher hotkeyWatcher;
        readonly Timer hotkeyDebounce = new Timer { Interval = 400 };
        readonly FileSystemWatcher settingsWatcher;

        Settings settings;
        // Each monitor's palette, by its rectangle, when monitors show different pictures (else empty).
        Dictionary<Rectangle, PaletteColors> monitorPalettes = new Dictionary<Rectangle, PaletteColors>();
        string monitorPictures = "";
        // The colour of the album art playing in YASB's media island (Yasb.cs); null until something plays.
        Color? mediaColour;
        PaletteColors colors;
        Snapshot snapshot;
        HashSet<long> recentlyManaged = new HashSet<long>();
        bool paused;
        bool paletteBusy, paletteStale;
        IntPtr locationHook, destroyHook;
        uint locationProcess;
        bool disposed;

        public Daemon()
        {
            settings = Settings.Load();
            WorkspaceNumbers.Load();
            colors = PaletteStore.Load() ?? PaletteColors.Default();
            commands = new Commands(() => settings);

            strip = new StripWindow(settings, colors, tray, Dispatch);
            strip.TaskbarRecreated += taskbar.Reattach;
            strip.PostureChanged += OnPosture;
            strip.SetScreen(Screen.PrimaryScreen.Bounds, true);
            strip.Show(); // also installs the UI synchronization context the feed and ticker post to
            strip.Render();
            SyncScreens();
            tray.AddExisting(); // minimized before this deskhud started (a rebuild, a crash)
            postureSettle.Tick += delegate { postureSettle.Stop(); taskbar.Reassert(); };
            screenCheck.Tick += delegate { screenCheck.Stop(); CheckScreens(); };
            cheatsheet = new Cheatsheet(() => settings, MinimizedElsewhere);
            yasb = new YasbEdge();
            yasb.Changed += OnYasbEdges;
            yasb.MediaChanged += OnMedia;
            yasb.Start();

            ipc = new IpcWindow(DispatchExternal);
            ticker = new Ticker(() => settings, colors);
            ticker.Start();
            taskbar.Apply(settings.HideWindowsTaskbar);
            // Hotkeys live in whkdrc but run here, from one hook that never blocks typing.
            hotkeys = new HotkeyRunner(args => { strip.BeginInvoke(new Action(() => Dispatch(args))); return true; });
            keyboard = new KeyboardHook(hotkeys.Enqueue, settings.WinTap);
            keyboard.SetHotkeys(HotkeyFile.Load());
            hotkeyWatcher = new FileSystemWatcher(Path.GetDirectoryName(HotkeyFile.FilePath), Path.GetFileName(HotkeyFile.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = strip,
            };
            FileSystemEventHandler hotkeysChanged = delegate { hotkeyDebounce.Stop(); hotkeyDebounce.Start(); };
            hotkeyWatcher.Changed += hotkeysChanged;
            hotkeyWatcher.Created += hotkeysChanged;
            hotkeyWatcher.EnableRaisingEvents = true;
            hotkeyDebounce.Tick += delegate { hotkeyDebounce.Stop(); keyboard.SetHotkeys(HotkeyFile.Load()); };
            looseCheck.Tick += delegate { looseCheck.Stop(); RefreshLoose(); };
            // Windows' slideshow changes the picture without telling other apps, but it rewrites this file each time.
            string themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
            if (Directory.Exists(themes))
            {
                wallpaperWatcher = new FileSystemWatcher(themes, "Transcoded*") // Transcoded_000, _001...: one per monitor
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    SynchronizingObject = strip,
                };
                FileSystemEventHandler wallpaperChanged = delegate { wallpaperDebounce.Stop(); wallpaperDebounce.Start(); };
                wallpaperWatcher.Changed += wallpaperChanged;
                wallpaperWatcher.Created += wallpaperChanged;
                wallpaperWatcher.Renamed += delegate { wallpaperDebounce.Stop(); wallpaperDebounce.Start(); };
                wallpaperWatcher.EnableRaisingEvents = true;
            }
            wallpaperDebounce.Tick += delegate { wallpaperDebounce.Stop(); RefreshPalette(false); };
            WriteYasb();

            onWinEvent = OnWinEvent;
            uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, onWinEvent, 0, 0, flags));
            hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, onWinEvent, 0, 0, flags));

            settingsWatcher = new FileSystemWatcher(Paths.ConfigDir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = strip,
            };
            FileSystemEventHandler changed = delegate { settingsDebounce.Stop(); settingsDebounce.Start(); };
            settingsWatcher.Changed += changed;
            settingsWatcher.Created += changed;
            settingsWatcher.Renamed += delegate { settingsDebounce.Stop(); settingsDebounce.Start(); };
            settingsWatcher.EnableRaisingEvents = true;
            settingsDebounce.Tick += delegate { settingsDebounce.Stop(); ReloadSettings(); };

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            feed = new KomorebiFeed(OnSnapshot, OnOnline);
            feed.Start();
            Background.StartAll(settings, settings.StartMinimized, MinimizedElsewhere, cheatsheet.Started);

            RefreshPalette(false);
            OnForeground(Native.GetForegroundWindow());
        }

        string lastExternal;
        int lastExternalAt;

        /// Commands from YASB and the command line (`deskhud <command>`). A repeat of the same command
        /// while it keeps arriving is dropped, so a double click or an auto-repeating key elsewhere cannot
        /// toggle something back and forth.
        bool DispatchExternal(string[] args)
        {
            string key = string.Join(" ", args);
            int now = Environment.TickCount;
            bool repeat = key == lastExternal && unchecked(now - lastExternalAt) < 400;
            lastExternal = key;
            lastExternalAt = now;
            return repeat || Dispatch(args);
        }

        /// Commands from the strip, the hotkeys, and YASB once debounced. Quick UI-side ones run here; the rest queue.
        bool Dispatch(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0])) return false;
            string verb = args[0].Trim().ToLowerInvariant();
            string arg = args.Length > 1 ? args[1] : null;
            long hwnd;

            switch (verb)
            {
                case "minimize":
                    IntPtr foreground = Native.GetForegroundWindow();
                    if (foreground != IntPtr.Zero) tray.Add(foreground.ToInt64());
                    break; // komorebi does the minimizing, from the queue
                case "restore":
                    if (long.TryParse(arg, out hwnd)) tray.RestoreWindow(hwnd);
                    else tray.RestoreTarget();
                    return true;
                case "restore-all":
                    tray.RestoreAll();
                    return true;
                case "tray":
                    tray.Cycle(string.Equals(arg, "previous", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    return true;
                case "weather":
                    ticker.Refresh();
                    return true;
                case "start-menu":
                    Native.TapWinKey();
                    return true;
                case "settings":
                    OpenFile(Paths.SettingsFile);
                    return true;
                case "cheatsheet":
                    string sheet = Environment.ExpandEnvironmentVariables(settings.Cheatsheet);
                    if (!cheatsheet.Toggle(sheet)) OpenFile(sheet);
                    return true;
                case "reload":
                    ReloadSettings();
                    keyboard.SetHotkeys(HotkeyFile.Load());
                    RefreshPalette(true);
                    return true;
                case "reload-yasb":
                    Process.Start(new ProcessStartInfo("yasbc.exe", "reload") { UseShellExecute = false, CreateNoWindow = true });
                    return true;
                case "workspace":
                case "move-to-workspace":
                    // A number lives on one screen; a free one is made on the screen you are on (Numbers.cs).
                    int number, monitor, index;
                    bool create;
                    if (snapshot == null || !int.TryParse(arg, out number) || number < 1
                        || !WorkspaceNumbers.Place(snapshot, number, verb == "workspace", out monitor, out index, out create)) return true;
                    foreach (var island in Islands) island.Render(); // an empty workspace on show may have just been renumbered
                    string command = verb == "workspace" ? "focus-monitor-workspace" : "move-to-monitor-workspace";
                    queue.Enqueue(() =>
                    {
                        if (create) Komorebic.Run("ensure-workspaces", monitor.ToString(), (index + 1).ToString());
                        Komorebic.Run(command, monitor.ToString(), index.ToString());
                    });
                    return true;
                case "restart-komorebi":
                    queue.Enqueue(() => ReportConfig(Komorebic.Restart(), false));
                    return true;
                case "start-komorebi":
                    // From the offline bubble: with komorebi.json broken, open it rather than fail again.
                    queue.Enqueue(() => ReportConfig(Komorebic.Start(), true));
                    return true;
                case "quit":
                    ExitThread();
                    return true;
            }

            queue.Enqueue(() => commands.Execute(args));
            return true;
        }

        /// A window deskhud minimized from a background thread (startup apps, Obsidian behind the
        /// cheatsheet). komorebi may never have managed it, so it is put in the tray here.
        void MinimizedElsewhere(long hwnd)
        {
            strip.BeginInvoke(new Action(() => tray.Add(hwnd)));
        }

        void OnSnapshot(Snapshot value)
        {
            recentlyManaged = snapshot == null ? new HashSet<long>(value.Hwnds) : new HashSet<long>(snapshot.Hwnds.Union(value.Hwnds));
            loose.Track(snapshot, value);
            snapshot = value;
            tray.Prune();
            foreach (var island in Islands) island.SetSnapshot(value);
            CheckLooseSoon();
            if (value.Paused != paused || value.ConfigReloaded)
            {
                paused = value.Paused;
                ApplyBorders();
            }
        }

        void OnOnline(bool online)
        {
            if (online)
            {
                ApplyBorders(); // a restarted komorebi starts from the colours in komorebi.json
                ShowConfigError(null);
                CheckScreensSoon(); // komorebi may have started (at login, say) seeing the screens wrong
                return;
            }
            snapshot = null;
            foreach (var island in Islands) island.SetSnapshot(null);
            CheckLooseSoon();
            // Say why, when it is komorebi.json: komorebi quits on a broken one without a word.
            queue.Enqueue(() => ReportConfig(Komorebic.ConfigError(), false));
        }

        /// From the command queue: shows komorebi.json's error (or clears it) on the islands' offline
        /// bubble, and opens the file when asked to start komorebi while it is broken.
        void ReportConfig(string error, bool openIfBroken)
        {
            if (error != null) Log.Write("komorebi.json: " + error);
            strip.BeginInvoke(new Action(() =>
            {
                ShowConfigError(error);
                if (error != null && openIfBroken) OpenFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "komorebi.json"));
            }));
        }

        void ShowConfigError(string error)
        {
            foreach (var island in Islands) island.SetKomorebiError(error);
        }

        void CheckScreensSoon()
        {
            screenCheck.Stop();
            screenCheck.Start();
        }

        /// Once the screens have settled: when komorebi's picture of them differs from Windows' (a
        /// screen listed twice, or given another's rectangle; komorebi 0.1.41 does this on hot-plug),
        /// it tiles that screen's windows in the wrong place and its island stays empty. Restarting
        /// komorebi (which keeps the layout) makes it look again. Once per arrangement, so a komorebi
        /// that still gets it wrong is not restarted over and over.
        void CheckScreens()
        {
            Rectangle[] screens = Array.ConvertAll(Screen.AllScreens, s => s.Bounds);
            string arrangement = string.Join(" ", screens.Select(r => r.X + "," + r.Y + "," + r.Width + "x" + r.Height));
            queue.Enqueue(() =>
            {
                Snapshot fresh = Komorebic.State();
                if (fresh == null) return;
                if (fresh.MatchesScreens(screens))
                {
                    healedFor = null;
                    return;
                }
                string seen = string.Join(" ", fresh.Monitors.Select(m => m.Bounds.X + "," + m.Bounds.Y + "," + m.Bounds.Width + "x" + m.Bounds.Height));
                if (arrangement == healedFor)
                {
                    Log.Write("komorebi: still sees the screens as " + seen + " (Windows: " + arrangement + ") after a restart; leaving it");
                    return;
                }
                healedFor = arrangement;
                Log.Write("komorebi: sees the screens as " + seen + " (Windows: " + arrangement + "), restarting it");
                ReportConfig(Komorebic.Restart(), false);
            });
        }

        void CheckLooseSoon()
        {
            looseCheck.Stop();
            looseCheck.Start();
        }

        void RefreshLoose()
        {
            if (loose.Refresh(snapshot)) CheckLooseSoon(); // see whether komorebi took them
            foreach (var island in Islands) island.SetLoose(loose.Windows);
            WatchLooseClosing();
        }

        /// Hears windows closing while the dashed bubble holds some: an untiled window (a dialog, say)
        /// that closes sends komorebi nothing, so its icon would stay until something else happened.
        /// Every window that closes anywhere raises this event, so the hook is off the rest of the time.
        void WatchLooseClosing()
        {
            bool wanted = loose.Windows.Count > 0;
            if (wanted == (destroyHook != IntPtr.Zero)) return;
            if (wanted)
            {
                destroyHook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_DESTROY, IntPtr.Zero, onWinEvent,
                    0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
                return;
            }
            Native.UnhookWinEvent(destroyHook);
            destroyHook = IntPtr.Zero;
        }

        void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            switch (type)
            {
                case Native.EVENT_SYSTEM_FOREGROUND:
                    OnForeground(hwnd);
                    break;
                case Native.EVENT_SYSTEM_MINIMIZESTART:
                    if (recentlyManaged.Contains(hwnd.ToInt64())) tray.Add(hwnd.ToInt64());
                    break;
                case Native.EVENT_SYSTEM_MINIMIZEEND:
                    tray.Remove(hwnd.ToInt64());
                    break;
                case Native.EVENT_OBJECT_DESTROY:
                    if (loose.Windows.Contains(hwnd.ToInt64())) CheckLooseSoon();
                    break;
                case Native.EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd == Native.GetForegroundWindow()) foreach (var island in Islands) island.OnForeground(hwnd, false);
                    break;
            }
        }

        /// Keyboard folded away (tablet) or back. Keep the hidden taskbar from reserving space for the
        /// touch taskbar, now and once Explorer has settled; every island and YASB's bubbles grow for
        /// fingers (or shrink back), and the keyboard bubble shows or hides.
        void OnPosture(bool tablet)
        {
            Log.Write(tablet ? "posture: tablet" : "posture: laptop");
            taskbar.Reassert();
            postureSettle.Stop();
            postureSettle.Start();
            foreach (var island in extras) island.SetTablet(tablet);
            WriteYasb();
        }

        IEnumerable<StripWindow> Islands
        {
            get
            {
                yield return strip;
                foreach (var island in extras) yield return island;
            }
        }

        /// One island per screen, each showing that monitor's own workspaces. The primary screen keeps
        /// `strip`; islands for the others are made or dropped as screens come and go.
        void SyncScreens()
        {
            var others = Screen.AllScreens.Where(s => !s.Primary).Select(s => s.Bounds).ToList();
            while (extras.Count > others.Count)
            {
                var gone = extras[extras.Count - 1];
                extras.RemoveAt(extras.Count - 1);
                gone.Close();
            }
            while (extras.Count < others.Count)
            {
                var island = new StripWindow(settings, colors, tray, Dispatch);
                extras.Add(island);
                island.SetScreen(others[extras.Count - 1], false);
                island.Show();
            }
            strip.SetScreen(Screen.PrimaryScreen.Bounds, true);
            for (int i = 0; i < others.Count; i++) extras[i].SetScreen(others[i], false);
            foreach (var island in Islands)
            {
                island.SetTablet(strip.Tablet);
                island.SetSnapshot(snapshot);
                island.SetLoose(loose.Windows);
                island.SetYasbEdge(EdgeFor(island));
                island.SetPalette(PaletteFor(island));
            }
        }

        void OnYasbEdges(Dictionary<Rectangle, int> edges)
        {
            yasbEdges = edges;
            foreach (var island in Islands) island.SetYasbEdge(EdgeFor(island));
        }

        int EdgeFor(StripWindow island)
        {
            int edge;
            return yasbEdges.TryGetValue(island.ScreenBounds, out edge) ? edge : 0;
        }

        void OnForeground(IntPtr hwnd)
        {
            foreach (var island in Islands) island.OnForeground(hwnd, true);
            CheckLooseSoon(); // a window komorebi has no event for, such as one opened while paused
            yasb.MeasureSoon(); // YASB's window title island changes width with the focus

            // Follow the focused app's moves too, to catch fullscreen without a focus change (F11, video).
            uint process;
            Native.GetWindowThreadProcessId(hwnd, out process);
            if (process == locationProcess) return;
            if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
            locationProcess = process;
            locationHook = process == 0 ? IntPtr.Zero : Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE,
                Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, onWinEvent, process, 0, Native.WINEVENT_OUTOFCONTEXT);
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop) strip.BeginInvoke(new Action(() => RefreshPalette(false)));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            strip.BeginInvoke(new Action(() =>
            {
                SyncScreens();
                RefreshPalette(false); // monitors came or went: their pictures may differ
                CheckScreensSoon();
            }));
        }

        /// Re-reads the wallpapers' colours when a wallpaper changed: the main one (YASB, borders, the
        /// primary island) and, when a monitor shows a picture of its own, that one for its island.
        /// Decoding runs off the UI thread.
        void RefreshPalette(bool force)
        {
            if (paletteBusy)
            {
                paletteStale = true;
                return;
            }
            string wallpaper = Wallpapers.Current();
            Dictionary<Rectangle, string> pictures = Wallpapers.PerMonitor();
            // One picture everywhere (or one monitor): the main palette covers it. Windows names the
            // original file here but its own copy in Current(), so comparing paths alone would decode twice.
            if (pictures.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) pictures.Clear();
            // With a picture per monitor, Windows' copy is of whichever screen changed last; the primary
            // screen's picture leads instead, so YASB matches the island beside it there.
            string primary = PictureOn(pictures, Screen.PrimaryScreen.Bounds);
            if (primary != null) wallpaper = primary;
            string signature = string.Join(";", pictures.Select(p => p.Key + "=" + p.Value + "@" + PaletteBuilder.Stamp(p.Value)));
            bool unchanged = string.Equals(wallpaper, colors.Source, StringComparison.OrdinalIgnoreCase)
                && PaletteBuilder.Stamp(wallpaper) == colors.SourceStamp && signature == monitorPictures;
            if (!force && unchanged) return;

            paletteBusy = true;
            // Windows' slideshow may shuffle, so its next image is unknown; the button then takes the current colour.
            string next = Wallpapers.SlideshowRunning() ? null : Wallpapers.Neighbour(settings, wallpaper, 1);
            Task.Factory.StartNew(() =>
            {
                var main = PaletteBuilder.Build(wallpaper, next);
                var others = new Dictionary<string, PaletteColors>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in pictures.Values)
                    if (!others.ContainsKey(path) && !string.Equals(path, wallpaper, StringComparison.OrdinalIgnoreCase))
                        others[path] = PaletteBuilder.Build(path, null);
                return Tuple.Create(main, others);
            }).ContinueWith(task =>
            {
                paletteBusy = false;
                GC.Collect(); // decoding full-size wallpapers leaves large buffers that would otherwise linger
                if (task.IsFaulted) Log.Error("palette", task.Exception.GetBaseException());
                else
                {
                    monitorPictures = signature;
                    var main = task.Result.Item1;
                    var others = task.Result.Item2;
                    monitorPalettes = pictures.ToDictionary(p => p.Key, p => others.ContainsKey(p.Value) ? others[p.Value] : main);
                    ApplyPalette(main);
                }
                if (paletteStale)
                {
                    paletteStale = false;
                    RefreshPalette(false);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ApplyPalette(PaletteColors value)
        {
            colors = value;
            PaletteStore.Save(value);
            WriteYasb();
            foreach (var island in Islands) island.SetPalette(PaletteFor(island));
            ticker.SetPalette(value);
            ApplyBorders();
        }

        /// The palette of the picture on the island's own screen; the main one when they all match.
        PaletteColors PaletteFor(StripWindow island)
        {
            Rectangle screen = island.ScreenBounds;
            var centre = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            foreach (var pair in monitorPalettes)
                if (pair.Key.Contains(centre)) return pair.Value;
            return colors;
        }

        static string PictureOn(Dictionary<Rectangle, string> pictures, Rectangle screen)
        {
            var centre = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            foreach (var pair in pictures)
                if (pair.Key.Contains(centre)) return pair.Value;
            return null;
        }

        /// YASB's generated variables: the main palette, the posture's sizes, the album art's colour.
        void WriteYasb()
        {
            YasbColors.Write(colors, strip.Tablet, mediaColour);
        }

        void OnMedia(Color art)
        {
            mediaColour = art;
            WriteYasb();
        }

        void ApplyBorders()
        {
            PaletteColors current = colors;
            bool isPaused = paused;
            queue.Enqueue(() => Borders.Apply(current, isPaused));
        }

        void ReloadSettings()
        {
            Settings previous = settings;
            settings = Settings.Load();
            foreach (var island in Islands) island.SetSettings(settings);
            taskbar.Apply(settings.HideWindowsTaskbar);
            keyboard.SetTapAction(settings.WinTap);
            if (previous.WeatherCity != settings.WeatherCity || previous.Fahrenheit != settings.Fahrenheit) ticker.Refresh();
            if (previous.WallpaperFolder != settings.WallpaperFolder) RefreshPalette(true);
        }

        static void OpenFile(string path)
        {
            if (!File.Exists(path))
            {
                Log.Write("open: " + path + " does not exist");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                Process.Start("notepad.exe", "\"" + path + "\""); // no app registered for this file type
            }
        }

        protected override void ExitThreadCore()
        {
            Dispose(true);
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                disposed = true;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                foreach (IntPtr hook in hooks) Native.UnhookWinEvent(hook);
                if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
                if (destroyHook != IntPtr.Zero) Native.UnhookWinEvent(destroyHook);
                settingsWatcher.Dispose();
                settingsDebounce.Dispose();
                feed.Dispose();
                hotkeyWatcher.Dispose();
                hotkeyDebounce.Dispose();
                looseCheck.Dispose();
                if (wallpaperWatcher != null) wallpaperWatcher.Dispose();
                wallpaperDebounce.Dispose();
                keyboard.Dispose();
                hotkeys.Dispose();
                yasb.Dispose();
                postureSettle.Dispose();
                screenCheck.Dispose();
                taskbar.Dispose();
                ticker.Dispose();
                ipc.Dispose();
                queue.Dispose();
                foreach (var island in extras) island.Close();
                strip.Close();
            }
            base.Dispose(disposing);
        }
    }
}
