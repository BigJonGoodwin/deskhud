// =================================================================================================
// WindowManager.cs: the window manager deskhud drives, GlazeWM (the default) or komorebi.
//
// IWindowManager: everything deskhud asks of a window manager, as deskhud's own verbs (stack, snap,
//   go to workspace N...). GlazeWm (GlazeWm.cs) talks to GlazeWM over its WebSocket IPC; KomorebiWm
//   (KomorebiWm.cs) runs komorebic. Both hand the islands the same Snapshot (Snapshot.cs).
// Wm.Current: the one picked by `window_manager.backend` in settings.json, read once at start (a change
//   takes a deskhud restart: the feed, borders and numbering all belong to one backend).
//
// To add a verb: add a method here, implement it in both backends (a log line is fine where one
// cannot do it), and call it from Commands.Execute.
// =================================================================================================

using System;

namespace DeskHud
{
    /// What deskhud asks of the window manager. Calls other than Feed come from the command queue (or
    /// the command line) and may block; PlanWorkspace runs on the UI thread and must not.
    interface IWindowManager
    {
        /// "GlazeWM" or "komorebi", for the islands' texts and the log.
        string Name { get; }
        /// The window manager's config file, opened when its config stops it from starting.
        string ConfigFile { get; }
        /// The workspace menu's layouts: the name in WorkspaceView.Layout, the value for SetLayout, the menu text.
        string[][] Layouts { get; }
        /// The layout map's tooltip: what its clicks do.
        string MapHelp { get; }
        /// komorebi 0.1.41 gets screens wrong after hot-plug and needs a restart (Daemon.CheckScreens).
        bool ChecksScreens { get; }

        /// The state right now, or null when the window manager is not running.
        Snapshot State();
        /// Live state: `onState` for every change (coalesced), `onOnline` when it comes and goes. Both
        /// are called on the UI thread; create it there.
        IDisposable Feed(Action<Snapshot> onState, Action<bool> onOnline);
        /// A command in the window manager's own words (a whkdrc line's `komorebic ...` or `glazewm ...`).
        bool Run(string[] args);

        void Minimize();
        void Stack(string direction);
        void Unstack();
        /// The one-button stack/unstack: all windows of the split become tabs, or go back to tiles.
        void ToggleTabbed();
        void CycleTab(int step);
        void Snap();
        void ToggleFloat();
        /// Brings an app's window forward (`deskhud open`).
        void FocusApp(WindowView window);
        /// Shows the window wherever it is: its monitor, workspace, tile and tab.
        void FocusWindow(long hwnd);
        /// Has the window manager tile a window it is not managing.
        void Tile(long hwnd);
        /// The icon menu's window manager actions on one window: minimize, float, monocle, unstack, move <number>.
        void WindowAction(long hwnd, string action, string value);
        /// Win+N (`move` false) or Win+Shift+N: works out where number n is or goes (Numbers.cs) and
        /// returns the commands to queue, or null when there is nothing to do. UI thread.
        Action PlanWorkspace(Snapshot snapshot, int number, bool move);
        /// A workspace by its place on a monitor (the island it is drawn on).
        void FocusWorkspace(int monitor, int workspace);
        void FocusMonitor(int monitor);
        /// The layout map's clicks: "next", "previous" or "flip".
        void Layout(string how);
        void SetLayout(int monitor, int workspace, string value);
        void Retile();
        void TogglePause();

        /// Applies the config without losing the layout; returns the config's error instead, if any.
        string Restart();
        /// Starts the window manager; returns the config's error instead, if any.
        string Start();
        /// The config's first error, or null when it loads (or the window manager cannot say).
        string ConfigError();
        /// Window borders in the palette's colours; paused shows in the negative colour.
        void ApplyBorders(PaletteColors palette, bool paused);
        /// Puts the borders back the way Windows draws them (deskhud is quitting).
        void ResetBorders();
    }

    /// The window manager in use.
    static class Wm
    {
        static IWindowManager current;
        static readonly object Gate = new object();

        public static IWindowManager Current
        {
            get
            {
                lock (Gate)
                {
                    if (current == null) current = For(Settings.Load());
                    return current;
                }
            }
        }

        public static IWindowManager For(Settings settings)
        {
            if (string.Equals(settings.WindowManager, "komorebi", StringComparison.OrdinalIgnoreCase)) return new KomorebiWm();
            return new GlazeWm(settings.GlazeWmExe);
        }
    }
}
