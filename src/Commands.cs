// =================================================================================================
// Commands.cs: the actions behind hotkeys and clicks.
//
// Execute maps each verb to the window manager in use (Wm.Current: GlazeWM, GlazeWm.cs; or komorebi,
// KomorebiWm.cs) or does it here when no window manager is involved: open or focus an app from
// settings.json, the icon menu's close / new window / file location, wallpapers, the touch keyboard.
// Runs on CommandQueue's worker thread (STA, because Shell.Application is COM).
// Also here: StartApps (Start menu lookup), CommandQueue.
//
// To add a verb: add a case to Execute; window manager work goes through IWindowManager.
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskHud
{
    /// The actions behind hotkeys, clicks and `deskhud <verb>`. Runs on the daemon's command thread,
    /// or straight from the command line when the daemon is not running.
    sealed class Commands
    {
        readonly Func<Settings> settings;

        public Commands(Func<Settings> settings)
        {
            this.settings = settings;
        }

        /// Runs one command: stack <left|right|up|down>, unstack, toggle-tabbed, cycle-tab <next|previous>,
        /// snap, open <app>, minimize, wallpaper <next|prev> [here], focus-window <hwnd>, tile <hwnd>,
        /// window <hwnd> <action> [arg], go-to <monitor> <workspace>, focus-monitor <monitor>,
        /// layout <next|previous|flip>, workspace-layout <monitor> <workspace> <value>, retile, pause,
        /// keyboard, or komorebic / glazewm <args...>: the window manager's own commands.
        public void Execute(string[] args)
        {
            IWindowManager wm = Wm.Current;
            string verb = args[0].ToLowerInvariant();
            string arg = args.Length > 1 ? args[1] : null;
            long hwnd;
            int monitor, workspace;
            switch (verb)
            {
                case "stack": wm.Stack((arg ?? "left").ToLowerInvariant()); break;
                case "unstack": wm.Unstack(); break;
                case "toggle-tabbed": wm.ToggleTabbed(); break;
                case "cycle-tab": wm.CycleTab(string.Equals(arg, "previous", StringComparison.OrdinalIgnoreCase) ? -1 : 1); break;
                case "snap": wm.Snap(); break;
                case "open": Open(arg); break;
                case "minimize": wm.Minimize(); break;
                case "wallpaper":
                    // One screen's picture: `here` is the screen under the cursor (the bar's button, the
                    // island's menu); otherwise the one you are on (Win+Shift+W).
                    bool here = args.Length > 2 && string.Equals(args[2], "here", StringComparison.OrdinalIgnoreCase);
                    System.Drawing.Rectangle screen = here ? Native.ScreenAtCursor() : Native.ScreenYouAreOn();
                    if (screen.IsEmpty)
                    {
                        Snapshot state = wm.State(); // nothing in front: the window manager's focused screen
                        screen = state != null && state.Focused != null ? state.Focused.Bounds : Native.ScreenAtCursor();
                    }
                    Wallpapers.StepScreen(settings(), screen, string.Equals(arg, "prev", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    break;
                case "focus-window":
                    if (long.TryParse(arg, out hwnd)) wm.FocusWindow(hwnd);
                    break;
                case "tile":
                    if (long.TryParse(arg, out hwnd)) wm.Tile(hwnd);
                    break;
                case "window":
                    if (args.Length > 2 && long.TryParse(arg, out hwnd)) Window(hwnd, args[2].ToLowerInvariant(), args.Length > 3 ? args[3] : null);
                    break;
                case "go-to":
                    if (args.Length > 2 && int.TryParse(arg, out monitor) && int.TryParse(args[2], out workspace)) wm.FocusWorkspace(monitor, workspace);
                    break;
                case "focus-monitor":
                    if (int.TryParse(arg, out monitor)) wm.FocusMonitor(monitor);
                    break;
                case "layout": wm.Layout((arg ?? "next").ToLowerInvariant()); break;
                case "workspace-layout":
                    if (args.Length > 3 && int.TryParse(arg, out monitor) && int.TryParse(args[2], out workspace)) wm.SetLayout(monitor, workspace, args[3]);
                    break;
                case "retile": wm.Retile(); break;
                case "pause": wm.TogglePause(); break;
                case "keyboard": Touch.ToggleKeyboard(); break;
                case "komorebic":
                case "glazewm":
                    // The window manager's own words; only the one in use can run them.
                    if (verb == "komorebic" ? wm is KomorebiWm : wm is GlazeWm) wm.Run(args.Skip(1).ToArray());
                    else Log.Write(verb + ": the window manager in use is " + wm.Name + ", skipped: " + string.Join(" ", args));
                    break;
                default: Log.Write("unknown command: " + string.Join(" ", args)); break;
            }
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
                Snapshot state = Wm.Current.State();
                WindowView existing = state == null ? null : state.FindByExe(app.Exe);
                if (existing != null)
                {
                    Wm.Current.FocusApp(existing);
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

        /// The island's icon menu: one action on one window. close, new and location act on the window
        /// directly; the rest go to the window manager. Actions: close, new, location, minimize, float,
        /// monocle, unstack, move <number>.
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

            Wm.Current.WindowAction(hwnd, action, value);
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
            // The app chose that id; one with a quote in it could break out of the quoted argument.
            if (appId != null && appId.IndexOf('"') < 0) Process.Start("explorer.exe", "\"shell:AppsFolder\\" + appId + "\"");
            else Log.Write("window: no way to start another " + (exe ?? "window"));
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

    /// One worker thread for commands, so window manager calls never block the strip and run in order.
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
