// =================================================================================================
// Cheatsheet.cs: Win+/ and the bar's book button.
//
// The cheatsheet is Obsidian's own window, kept loaded and minimized in the tray bubble with the
// hotkey hub open (deskhud starts it that way at login). Win+/ brings it to the workspace you are on,
// floating where you last left it (kept as a share of the screen, so it lands in the same area on
// any monitor). Pressing it again with the window in front puts it back in the tray; with the window
// behind something, brings it forward. Closing it with its X quits Obsidian as usual; the next Win+/
// starts it again.
//
// komorebi floats every Obsidian window (floating_applications in komorebi.json). The note is
// "cheatsheet" in settings.json; the remembered spot is cheatsheet.json in deskhud's state folder.
// The first-time size is WidthShare / HeightShare below, centred.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskHud
{
    /// Win+/ and the bar's book button: Obsidian's window, summoned to the current workspace at its
    /// remembered spot, or sent back to the tray.
    sealed class Cheatsheet
    {
        const string ObsidianExe = "Obsidian.exe";
        const double WidthShare = 0.6, HeightShare = 0.85;
        const int SW_MINIMIZE = 6;
        static readonly string SpotFile = Path.Combine(Paths.StateDir, "cheatsheet.json");

        readonly Func<Settings> settings;
        readonly Action<long> minimized;
        int busy;

        /// `minimized` is told about each Obsidian window put away, for the tray bubble.
        public Cheatsheet(Func<Settings> settings, Action<long> minimized)
        {
            this.settings = settings;
            this.minimized = minimized;
        }

        /// False when the file is not inside an Obsidian vault, so the caller can open it some other way.
        public bool Toggle(string path)
        {
            if (!InVault(path)) return false;
            if (Interlocked.Exchange(ref busy, 1) == 1) return true; // a second press while the first is still moving it
            var thread = new Thread(() =>
            {
                try { Run(path); }
                catch (Exception ex) { Log.Error("cheatsheet", ex); }
                finally { Interlocked.Exchange(ref busy, 0); }
            }) { IsBackground = true, Name = "cheatsheet" };
            thread.SetApartmentState(ApartmentState.STA); // starting Obsidian looks it up in the Start menu (COM)
            thread.Start();
            return true;
        }

        /// deskhud just started Obsidian (at login): open the hub in it, so it is ready behind Win+/.
        public void Started(AppEntry app)
        {
            if (!string.Equals(app.Exe, ObsidianExe, StringComparison.OrdinalIgnoreCase)) return;
            string path = Environment.ExpandEnvironmentVariables(settings().Cheatsheet);
            if (InVault(path)) OpenNote(path);
        }

        void Run(string path)
        {
            string prefix = Path.GetFileNameWithoutExtension(path) + " - ";
            IntPtr window = MainWindow(prefix);
            if (window == IntPtr.Zero)
            {
                // Not running: start it minimized (so it never joins the layout), then bring it here.
                AppEntry app = settings().Apps.Values.FirstOrDefault(a => string.Equals(a.Exe, ObsidianExe, StringComparison.OrdinalIgnoreCase));
                if (app == null || string.IsNullOrEmpty(app.Start))
                {
                    OpenNote(path);
                    return;
                }
                window = Background.StartMinimized(app, minimized, 2500, false, null);
                if (window == IntPtr.Zero) return;
            }
            else if (!Native.IsIconic(window) && !Cloaked(window))
            {
                // On screen here: in front, it goes back to the tray; behind something, it comes forward.
                if (Native.GetForegroundWindow() == window)
                {
                    Remember(window);
                    Native.ShowWindow(window, SW_MINIMIZE);
                    minimized(window.ToInt64());
                }
                else Native.ForceForeground(window);
                return;
            }
            else if (Cloaked(window))
            {
                // Open on another workspace. Minimizing drops it from there; bringing it back puts it here.
                Remember(window);
                Native.ShowWindow(window, Native.SW_SHOWMINNOACTIVE);
                for (int i = 0; i < 20 && !Native.IsIconic(window); i++) Thread.Sleep(25);
                Thread.Sleep(80); // let komorebi see it leave
            }

            Rectangle spot = Spot();
            Native.ShowWindow(window, Native.SW_RESTORE);
            Place(window, spot);
            Native.ForceForeground(window);
            if (!Native.WindowTitle(window).StartsWith(prefix)) OpenNote(path);
            Settle(window, spot);
        }

        /// Obsidian's window: the one showing the note if there are several, else the first.
        static IntPtr MainWindow(string prefix)
        {
            List<IntPtr> windows = Background.Windows(ObsidianExe);
            IntPtr showing = windows.FirstOrDefault(h => Native.WindowTitle(h).StartsWith(prefix));
            return showing != IntPtr.Zero ? showing : windows.FirstOrDefault();
        }

        /// Opens the note in Obsidian. Your tab settings switch to it if it is already open.
        static void OpenNote(string path)
        {
            Process.Start("explorer.exe", "\"obsidian://open?path=" + Uri.EscapeDataString(path) + "\"");
        }

        /// komorebi takes the window back a moment after it shows (floating, by its rule) and may move
        /// it; hold the spot briefly. If it came back tiled (a layout restored from before the rule),
        /// float it once komorebi has it focused.
        void Settle(IntPtr window, Rectangle spot)
        {
            bool floating = false;
            for (int i = 0; i < 15; i++)
            {
                if (!floating && i % 3 == 0)
                {
                    Snapshot state = Wm.Current.State();
                    if (state != null)
                    {
                        int monitor, workspace, container, slot;
                        bool known = state.Locate(window.ToInt64(), out monitor, out workspace, out container, out slot);
                        floating = known && container < 0;
                        ContainerView focused = state.FocusedContainer;
                        if (known && container >= 0 && focused != null && focused.Visible.Hwnd == window.ToInt64())
                            Wm.Current.ToggleFloat();
                    }
                }
                Place(window, spot);
                // komorebi can hand focus elsewhere as it takes the window in; take it back early on.
                if (i < 6 && Native.GetForegroundWindow() != window) Native.ForceForeground(window);
                Thread.Sleep(80);
            }
        }

        static void Place(IntPtr window, Rectangle spot)
        {
            Native.RECT now;
            if (Native.GetWindowRect(window, out now) && now.Left == spot.Left && now.Top == spot.Top
                && now.Right == spot.Right && now.Bottom == spot.Bottom) return;
            Native.SetWindowPos(window, IntPtr.Zero, spot.X, spot.Y, spot.Width, spot.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        /// Where the window goes: its remembered share of the screen, on the monitor under the mouse.
        static Rectangle Spot()
        {
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            Rectangle work = WorkArea(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));
            double x = (1 - WidthShare) / 2, y = (1 - HeightShare) / 2, w = WidthShare, h = HeightShare;
            try
            {
                if (File.Exists(SpotFile))
                {
                    object saved = Json.Parse(File.ReadAllText(SpotFile));
                    x = Json.Double(saved, "x", x); y = Json.Double(saved, "y", y);
                    w = Json.Double(saved, "w", w); h = Json.Double(saved, "h", h);
                }
            }
            catch (Exception ex) { Log.Error("cheatsheet spot", ex); }
            w = Clamp(w, 0.2, 1); h = Clamp(h, 0.2, 1);
            x = Clamp(x, 0, 1 - w); y = Clamp(y, 0, 1 - h);
            return new Rectangle(work.Left + (int)Math.Round(x * work.Width), work.Top + (int)Math.Round(y * work.Height),
                (int)Math.Round(w * work.Width), (int)Math.Round(h * work.Height));
        }

        /// Saves where the window is, as shares of its monitor's work area.
        static void Remember(IntPtr window)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(window, out r) || r.Right <= r.Left) return;
            Rectangle work = WorkArea(Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST));
            if (work.Width <= 0 || work.Height <= 0) return;
            var spot = new Dictionary<string, object>
            {
                { "x", (double)(r.Left - work.Left) / work.Width }, { "y", (double)(r.Top - work.Top) / work.Height },
                { "w", (double)(r.Right - r.Left) / work.Width }, { "h", (double)(r.Bottom - r.Top) / work.Height },
            };
            try { Paths.WriteAtomic(SpotFile, Json.Stringify(spot)); }
            catch (IOException ex) { Log.Error("cheatsheet spot", ex); }
        }

        static Rectangle WorkArea(IntPtr monitor)
        {
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(monitor, ref info)) return Rectangle.Empty;
            return Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        /// komorebi hides windows on other workspaces by cloaking them.
        static bool Cloaked(IntPtr window)
        {
            int cloaked;
            return Native.DwmGetWindowAttribute(window, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }

        /// Obsidian opens files only from its vaults, listed in %APPDATA%\obsidian\obsidian.json.
        static bool InVault(string path)
        {
            string list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
            if (!File.Exists(path) || !File.Exists(list)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                var vaults = Json.Obj(Json.Parse(File.ReadAllText(list)), "vaults");
                return vaults != null && vaults.Values.Any(v =>
                {
                    string root = Json.Str(v, "path", null);
                    return !string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                Log.Error("cheatsheet: reading obsidian.json", ex);
                return false;
            }
        }
    }
}
