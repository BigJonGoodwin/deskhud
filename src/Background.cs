// =================================================================================================
// Background.cs: apps that live minimized in the tray bubble.
//
// "startup.minimized" in settings.json names apps (from "apps") that deskhud starts at login and
// minimizes straight into the tray, so they are running but out of the layout. Obsidian is one: its window
// is the cheatsheet, waiting in the tray with the hotkey hub open until Win+/ brings it out.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DeskHud
{
    /// Starting apps minimized and keeping them that way. Runs on background threads; `minimized` is
    /// called with each window put away, so the daemon can add it to the tray on the UI thread.
    static class Background
    {
        /// Starts each named app that has no window yet and minimizes its window into the tray. Apps
        /// already open are left as they are. `started` hears about each app as its window appears (the
        /// cheatsheet opens its note in Obsidian then, while the window is still being kept down).
        public static void StartAll(Settings settings, IEnumerable<string> names, Action<long> minimized, Action<AppEntry> started)
        {
            foreach (string name in names)
            {
                AppEntry app;
                if (!settings.Apps.TryGetValue(name, out app) || string.IsNullOrEmpty(app.Exe) || string.IsNullOrEmpty(app.Start))
                {
                    Log.Write("startup: no app named \"" + name + "\" with exe and start under apps in settings.json");
                    continue;
                }
                var thread = new Thread(() => StartMinimized(app, minimized, StartupHoldMs, true, w => started(app))) { IsBackground = true, Name = "startup " + name };
                thread.SetApartmentState(ApartmentState.STA); // Start menu lookup is COM
                thread.Start();
            }
        }

        /// How long a freshly started app is watched: Obsidian shows its window again while it finishes
        /// loading, up to ten seconds in (measured), and stays put after that.
        public const int StartupHoldMs = 20000;

        /// Starts the app if it has no window, then minimizes the window it opens and keeps it minimized
        /// for `holdMs`; `adoptNew` also puts away any other window the app opens meanwhile (at login,
        /// nothing else should be opening). `appeared` (may be null) runs once the window shows, before
        /// the hold. Returns the window, the app's existing window when it was already running, or zero
        /// when nothing appeared.
        public static IntPtr StartMinimized(AppEntry app, Action<long> minimized, int holdMs, bool adoptNew, Action<IntPtr> appeared)
        {
            List<IntPtr> open = Windows(app.Exe);
            if (open.Count > 0) return open[0];
            if (!Commands.Launch(app.Start)) return IntPtr.Zero;

            IntPtr window = IntPtr.Zero;
            for (int i = 0; i < 300 && window == IntPtr.Zero; i++) // a cold start at login can take a while
            {
                Thread.Sleep(100);
                window = Windows(app.Exe).FirstOrDefault();
            }
            if (window == IntPtr.Zero)
            {
                Log.Write("startup: " + app.Exe + " opened no window");
                return IntPtr.Zero;
            }
            if (appeared != null) appeared(window);
            // Electron apps show their window again once they finish loading; keep putting it back.
            var keep = new HashSet<IntPtr> { window };
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < holdMs)
            {
                if (adoptNew) foreach (IntPtr other in Windows(app.Exe)) keep.Add(other);
                KeepMinimized(keep, minimized);
                Thread.Sleep(100);
            }
            return window;
        }

        /// Minimizes any of these windows that came back up (an app restores its main window when asked
        /// to open something, and komorebi would tile it into the current workspace).
        public static void KeepMinimized(ICollection<IntPtr> windows, Action<long> minimized)
        {
            foreach (IntPtr window in windows)
            {
                if (!Native.IsWindow(window) || Native.IsIconic(window) || !Native.IsWindowVisible(window)) continue;
                Native.ShowWindow(window, Native.SW_SHOWMINNOACTIVE);
                minimized(window.ToInt64());
            }
        }

        /// The app's visible, titled top-level windows: what you would see in Alt+Tab, minimized or not.
        public static List<IntPtr> Windows(string exe)
        {
            var pids = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                pids.Add((uint)process.Id);
                process.Dispose();
            }
            var windows = new List<IntPtr>();
            if (pids.Count == 0) return windows;
            Native.EnumWindows((hwnd, lParam) =>
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pids.Contains(pid) && Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero
                    && Native.WindowTitle(hwnd).Length > 0)
                    windows.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return windows;
        }
    }
}
