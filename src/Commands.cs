// =================================================================================================
// Commands.cs: the multi-step actions behind hotkeys and clicks.
//
// Stack / unstack (with memory of where a window sat), snap the main split (1/2, 2/3, 1/3), open or
// focus an app from settings.json, focus a window clicked in the strip, tile an untiled window, the
// actions in the strip's icon menu (Window), and the touch keyboard.
// Runs on CommandQueue's worker thread (STA, because Shell.Application is COM).
// Also here: StackMemory (stacks.json), StartApps (Start menu lookup), CommandQueue.
//
// To add a verb: add a case to Execute and a method below. Talk to komorebi through Komorebic.Run.
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskHud
{
    /// Hotkey actions that take more than one komorebic call. Runs on the daemon's command thread,
    /// or straight from the command line when the daemon is not running.
    sealed class Commands
    {
        readonly Func<Settings> settings;

        public Commands(Func<Settings> settings)
        {
            this.settings = settings;
        }

        /// Runs one command: stack <left|right|up|down>, unstack, snap, open <app>, minimize,
        /// wallpaper <next|prev>, focus-window <hwnd>, tile <hwnd>, window <hwnd> <action> [arg],
        /// keyboard, or komorebic <args...>.
        public void Execute(string[] args)
        {
            string verb = args[0].ToLowerInvariant();
            string arg = args.Length > 1 ? args[1] : null;
            long hwnd;
            switch (verb)
            {
                case "stack": Stack((arg ?? "left").ToLowerInvariant()); break;
                case "unstack": Unstack(); break;
                case "snap": Snap(); break;
                case "open": Open(arg); break;
                case "minimize": Komorebic.Run("minimize"); break;
                case "wallpaper":
                    // One screen's picture: `here` is the screen under the cursor (the bar's button, the
                    // island's menu); otherwise the one you are on (Win+Shift+W).
                    bool here = args.Length > 2 && string.Equals(args[2], "here", StringComparison.OrdinalIgnoreCase);
                    System.Drawing.Rectangle screen = here ? Native.ScreenAtCursor() : Native.ScreenYouAreOn();
                    if (screen.IsEmpty)
                    {
                        Snapshot state = Komorebic.State(); // nothing in front: komorebi's focused screen
                        screen = state != null && state.Focused != null ? state.Focused.Bounds : Native.ScreenAtCursor();
                    }
                    Wallpapers.StepScreen(settings(), screen, string.Equals(arg, "prev", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    break;
                case "focus-window":
                    if (long.TryParse(arg, out hwnd)) FocusWindow(hwnd);
                    break;
                case "tile":
                    if (long.TryParse(arg, out hwnd)) Tile(hwnd);
                    break;
                case "window":
                    if (args.Length > 2 && long.TryParse(arg, out hwnd)) Window(hwnd, args[2].ToLowerInvariant(), args.Length > 3 ? args[3] : null);
                    break;
                case "keyboard": Touch.ToggleKeyboard(); break;
                case "komorebic": Komorebic.Run(args.Skip(1).ToArray()); break;
                default: Log.Write("unknown command: " + string.Join(" ", args)); break;
            }
        }

        /// Stacks the focused window onto its neighbour in `direction`. On a stack, komorebi pulls a lone
        /// neighbour in instead, so pressing again keeps gathering windows. A lone window that joins
        /// remembers where it sat, so `unstack` can put it back.
        void Stack(string direction)
        {
            Snapshot before = Komorebic.State();
            WorkspaceView ws = before == null ? null : before.Current;
            if (ws == null || ws.Monocle || before.FocusedContainer == null) return;
            long front = before.FocusedContainer.Visible.Hwnd;

            string resize = ResizeFile(0);
            bool saved = ws.Layout == "BSP" && Komorebic.Run("save-resize", resize);
            Komorebic.Run("stack", direction);

            Snapshot after = Komorebic.State();
            // komorebi answers commands in order, so by now it has written the save-resize file, if it could.
            saved = saved && File.Exists(resize);
            ContainerView merged = after == null ? null : after.FocusedContainer;
            if (merged != null && after.FocusedWorkspace == ws.Index && after.Current.Containers.Count < ws.Containers.Count)
            {
                foreach (WindowView window in merged.Windows)
                {
                    int workspace, index, slot;
                    if (!before.Locate(window.Hwnd, out workspace, out index, out slot) || workspace != ws.Index || index < 0
                        || ws.Containers[index].Windows.Count != 1) continue;
                    bool resized = saved && CopyResize(resize, ResizeFile(window.Hwnd));
                    StackMemory.Remember(window.Hwnd, new StackRecord
                    {
                        Monitor = before.MonitorIndex,
                        Workspace = ws.Index,
                        Index = index,
                        Count = ws.Containers.Count,
                        Resized = resized,
                    });
                }
                // komorebi brings whichever window it moved to the front; keep the one you were looking at.
                int frontSlot = merged.Windows.FindIndex(w => w.Hwnd == front);
                if (frontSlot >= 0 && merged.Visible.Hwnd != front) Komorebic.Run("focus-stack-window", frontSlot.ToString());
            }
            TryDelete(resize);
        }

        /// Pops the focused window out of its stack and puts it back where it was before it was stacked.
        void Unstack()
        {
            Snapshot state = Komorebic.State();
            ContainerView container = state == null ? null : state.FocusedContainer;
            if (container != null && container.Windows.Count > 1) Unstack(state, container.Visible);
        }

        void Unstack(Snapshot state, WindowView window)
        {
            StackRecord record = StackMemory.Take(window.Hwnd);
            Komorebic.Run("unstack");
            if (record == null || record.Monitor != state.MonitorIndex || record.Workspace != state.FocusedWorkspace) return;

            // Only restore when nothing else changed the workspace in the meantime.
            Snapshot after = Komorebic.State();
            int workspace, at, slot;
            if (after == null || after.Current == null || after.Current.Containers.Count != record.Count) return;
            if (!after.Locate(window.Hwnd, out workspace, out at, out slot) || workspace != record.Workspace || at < 0) return;

            string step = at > record.Index ? "previous" : "next";
            for (int moves = Math.Abs(at - record.Index); moves > 0; moves--) Komorebic.Run("cycle-move", step);
            if (record.Resized) Komorebic.Run("load-resize", ResizeFile(window.Hwnd));
            TryDelete(ResizeFile(window.Hwnd));
        }

        /// Cycles the focused container's side of the main split: 1/2, 2/3, 1/3.
        void Snap()
        {
            Snapshot state = Komorebic.State();
            WorkspaceView ws = state == null ? null : state.Current;
            if (ws == null || ws.Monocle || ws.Tiles.Count < 2 || ws.FocusedContainer < 0 || ws.FocusedContainer >= ws.Tiles.Count) return;

            int leftmost = ws.Tiles.Min(t => t.Left);
            bool primary = ws.Tiles[ws.FocusedContainer].Left <= leftmost;
            double ratio = ws.PrimaryRatio ?? 0.5;
            double share = primary ? ratio : 1 - ratio;
            double next = share > 0.58 ? 1.0 / 3 : share < 0.42 ? 0.5 : 2.0 / 3;
            double value = primary ? next : 1 - next;
            Komorebic.Run("layout-ratios", "--columns", value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// Focuses the app if any of its windows is open anywhere, otherwise starts it.
        void Open(string name)
        {
            AppEntry app;
            if (name == null || !settings().Apps.TryGetValue(name, out app))
            {
                Log.Write("open: no app named \"" + name + "\" under apps in settings.json");
                return;
            }

            if (!string.IsNullOrEmpty(app.Exe))
            {
                Snapshot state = Komorebic.State();
                WindowView existing = state == null ? null : state.FindByExe(app.Exe);
                if (existing != null)
                {
                    Komorebic.Run("eager-focus", existing.Exe);
                    return;
                }
                if (state == null && FocusProcessWindow(app.Exe)) return;
            }
            if (!string.IsNullOrEmpty(app.Start)) Launch(app.Start);
        }

        static bool FocusProcessWindow(string exe)
        {
            Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            try
            {
                foreach (var process in processes)
                {
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
                    return Native.SetForegroundWindow(window);
                }
                return false;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        /// `start` is a Start menu name ("IntelliJ IDEA" matches any version), a path, or a URI. Needs an
        /// STA thread (the Start menu lookup is COM). False when there was nothing to start.
        internal static bool Launch(string start)
        {
            bool direct = start.IndexOf('\\') >= 0 || start.IndexOf('/') >= 0;
            string appId = direct ? null : StartApps.Find(start);
            if (!direct && appId == null)
            {
                Log.Write("open: \"" + start + "\" is not a Start menu app, path or URI");
                return false;
            }
            // Through explorer, so the app is not a child of deskhud and gets a normal shell environment.
            Process.Start("explorer.exe", direct ? "\"" + start + "\"" : "\"shell:AppsFolder\\" + appId + "\"");
            return true;
        }

        /// Shows the window wherever it is: its monitor's workspace, then its tile, then its stack slot.
        void FocusWindow(long hwnd)
        {
            var window = new IntPtr(hwnd);
            Snapshot state = Komorebic.State();
            int monitor, workspace, container, slot;
            if (state == null || !state.Locate(hwnd, out monitor, out workspace, out container, out slot))
            {
                Native.ForceForeground(window);
                return;
            }

            MonitorView home = state.Monitors[monitor];
            bool switching = monitor != state.FocusedMonitor || workspace != home.FocusedWorkspace;
            if (switching)
            {
                Komorebic.Run("focus-monitor-workspace", monitor.ToString(), workspace.ToString());
                Komorebic.Query("focused-workspace-index"); // returns once komorebi has switched
            }
            if (container < 0)
            {
                Native.ForceForeground(window);
                return;
            }

            ContainerView target = home.Workspaces[workspace].Containers[container];
            if (switching || container != home.Workspaces[workspace].FocusedContainer)
            {
                Native.SetForegroundWindow(new IntPtr(target.Visible.Hwnd));
                Thread.Sleep(60); // komorebi picks up focus changes from window events, not the socket
            }
            if (target.Windows[slot].Hwnd != target.Visible.Hwnd)
            {
                Komorebic.Run("focus-stack-window", slot.ToString());
                Thread.Sleep(60);
            }
            // komorebi may count it focused already while Windows has something else in front (a menu
            // just closed, an app that opened without focus); nothing above moved focus then, so insist.
            if (Native.GetForegroundWindow() != window && Native.IsWindowVisible(window)) Native.ForceForeground(window);
        }

        /// Has komorebi tile a window it is not managing, into the current workspace. `manage` acts on the
        /// foreground window and overrides komorebi's ignore rules, since this is an explicit click.
        void Tile(long hwnd)
        {
            var window = new IntPtr(hwnd);
            if (!Native.IsWindow(window)) return;
            Native.SetForegroundWindow(window);
            Thread.Sleep(60); // komorebi picks up focus changes from window events, not the socket
            if (Native.GetForegroundWindow() == window) Komorebic.Run("manage");
        }

        /// The island's icon menu: one action on one window. close, new and location act on the window
        /// directly; the rest are komorebic commands, which only act on the focused window, so it is
        /// focused first. Actions: close, new, location, minimize, float, monocle, unstack, move <number>.
        void Window(long hwnd, string action, string value)
        {
            var window = new IntPtr(hwnd);
            if (!Native.IsWindow(window)) return;
            switch (action)
            {
                case "close":
                    Native.PostMessage(window, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return;
                case "new":
                    NewWindow(window);
                    return;
                case "location":
                    string exe = Native.ProcessPath(window);
                    if (exe != null) Process.Start("explorer.exe", "/select,\"" + exe + "\"");
                    return;
            }

            // Windows only lets the foreground app hand focus around. From the island's menu that is
            // deskhud; from a script it may not be, and komorebic would then act on the wrong window.
            FocusFirst(hwnd);
            if (Native.GetForegroundWindow() != window)
            {
                Log.Write("window: could not bring " + hwnd + " to the front, so \"" + action + "\" was skipped");
                return;
            }
            int workspace;
            switch (action)
            {
                case "minimize": Komorebic.Run("minimize"); break;
                case "float": Komorebic.Run("toggle-float"); break;
                case "monocle": Komorebic.Run("toggle-monocle"); break;
                case "unstack": Unstack(); break;
                case "move":
                    // `value` is a workspace number (Numbers.cs); a free one is made on this window's screen.
                    Snapshot state = Komorebic.State();
                    int monitor, index;
                    bool create;
                    if (int.TryParse(value, out workspace) && workspace > 0 && state != null
                        && WorkspaceNumbers.Place(state, workspace, false, out monitor, out index, out create))
                    {
                        if (create) Komorebic.Run("ensure-workspaces", monitor.ToString(), (index + 1).ToString());
                        Komorebic.Run("move-to-monitor-workspace", monitor.ToString(), index.ToString());
                    }
                    break;
                default: Log.Write("window: unknown action " + action); break;
            }
        }

        /// Focuses the window and waits until it is in front and komorebi has had a moment to see it.
        void FocusFirst(long hwnd)
        {
            FocusWindow(hwnd);
            var window = new IntPtr(hwnd);
            for (int i = 0; i < 15 && Native.GetForegroundWindow() != window; i++) Thread.Sleep(20);
            Thread.Sleep(60); // komorebi picks up focus changes from window events, not the socket
        }

        /// Starts another copy of the window's app, like a middle-click on the taskbar. Packaged apps
        /// cannot be started from their exe (WindowsApps is locked); they start by app id.
        static void NewWindow(IntPtr window)
        {
            string exe = Native.ProcessPath(window);
            bool packaged = exe == null || exe.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0
                || Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            if (!packaged)
            {
                Process.Start("explorer.exe", "\"" + exe + "\"");
                return;
            }
            string appId = ShellApps.AppId(window);
            if (appId != null) Process.Start("explorer.exe", "\"shell:AppsFolder\\" + appId + "\""); // quoted: the app chose that id
            else Log.Write("window: no way to start another " + (exe ?? "window"));
        }

        static string ResizeFile(long hwnd)
        {
            return Path.Combine(Paths.ResizeDir, hwnd.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        /// The save-resize file has once been missing by the time it was copied (deskhud.log, Oct 23), which
        /// threw and lost the whole stack record. Now the window still remembers its place, just not its size.
        static bool CopyResize(string from, string to)
        {
            try
            {
                File.Copy(from, to, true);
                return true;
            }
            catch (IOException ex)
            {
                Log.Error("stack: keeping the old size", ex);
                return false;
            }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    /// Where a window sat before it joined a stack: its tile index and the tile count at the time.
    sealed class StackRecord
    {
        public int Monitor, Workspace, Index, Count;
        public bool Resized;
    }

    /// Where each stacked window sat before it was stacked, keyed by window handle.
    static class StackMemory
    {
        public static void Remember(long hwnd, StackRecord record)
        {
            var all = Load();
            all[hwnd] = record;
            Save(all);
        }

        public static StackRecord Take(long hwnd)
        {
            var all = Load();
            StackRecord record;
            if (!all.TryGetValue(hwnd, out record)) return null;
            all.Remove(hwnd);
            Save(all);
            return record;
        }

        static Dictionary<long, StackRecord> Load()
        {
            var all = new Dictionary<long, StackRecord>();
            try
            {
                if (!File.Exists(Paths.StacksFile)) return all;
                var root = Json.Parse(File.ReadAllText(Paths.StacksFile)) as Dictionary<string, object>;
                if (root == null) return all;
                foreach (var pair in root)
                {
                    long hwnd;
                    if (!long.TryParse(pair.Key, out hwnd) || !Native.IsWindow(new IntPtr(hwnd))) continue;
                    all[hwnd] = new StackRecord
                    {
                        Monitor = (int)Json.Long(pair.Value, "monitor", 0),
                        Workspace = (int)Json.Long(pair.Value, "workspace", 0),
                        Index = (int)Json.Long(pair.Value, "index", 0),
                        Count = (int)Json.Long(pair.Value, "count", 0),
                        Resized = Json.Bool(pair.Value, "resized", false),
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error("stack memory", ex);
            }
            return all;
        }

        static void Save(Dictionary<long, StackRecord> all)
        {
            var root = all.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => (object)new Dictionary<string, object>
            {
                { "monitor", p.Value.Monitor }, { "workspace", p.Value.Workspace }, { "index", p.Value.Index },
                { "count", p.Value.Count }, { "resized", p.Value.Resized },
            });
            try { Paths.WriteAtomic(Paths.StacksFile, Json.Stringify(root)); }
            catch (IOException ex) { Log.Error("stack memory", ex); }
        }
    }

    /// Looks up Start menu apps by display name, returning the id shell:AppsFolder can launch.
    static class StartApps
    {
        public static string Find(string name)
        {
            Type type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            object shell = Activator.CreateInstance(type);
            try
            {
                dynamic folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
                string byPrefix = null;
                foreach (dynamic item in folder.Items())
                {
                    string itemName = item.Name;
                    if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase)) return item.Path;
                    if (byPrefix == null && itemName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) byPrefix = item.Path;
                }
                return byPrefix;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// One worker thread for commands, so komorebic calls never block the strip and run in order.
    sealed class CommandQueue : IDisposable
    {
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

        public CommandQueue()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "commands" };
            thread.SetApartmentState(ApartmentState.STA); // Shell.Application wants STA
            thread.Start();
        }

        void Run()
        {
            foreach (Action job in work.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception ex) { Log.Error("command", ex); }
            }
        }

        public void Enqueue(Action job)
        {
            try { work.Add(job); }
            catch (InvalidOperationException) { } // shutting down
        }

        public void Dispose()
        {
            work.CompleteAdding();
        }
    }
}
