// =================================================================================================
// Komorebi.cs: everything that talks to komorebi, the tiling window manager.
//
// Snapshot, MonitorView, WorkspaceView, ContainerView, WindowView: a trimmed copy of `komorebic state`.
//   Each monitor has its own workspaces; Snapshot's Workspaces / FocusedWorkspace / Current are the
//   focused monitor's, which is what komorebic commands act on. A "container" is one tile; several
//   windows in one container form a stack. Building a snapshot also numbers its workspaces (Numbers.cs).
// Komorebic: runs komorebic.exe (deskhud never opens komorebi's socket itself) and restarts komorebi
//   so its saved layout comes back.
// KomorebiFeed: subscribes to komorebi's event pipe and hands each new state to the UI thread.
//
// Careful: never use `komorebic replace-configuration`. In komorebi 0.1.41 it piles every window
// from every workspace onto one. Apply komorebi.json changes with Komorebic.Restart instead.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeskHud
{
    /// One window: its handle, exe name and title.
    sealed class WindowView
    {
        public long Hwnd;
        public string Exe;
        public string Title;
    }

    /// One tile. More than one window makes it a stack; Focused is the one showing.
    sealed class ContainerView
    {
        public readonly List<WindowView> Windows = new List<WindowView>();
        public int Focused;

        public WindowView Visible { get { return Windows[Math.Min(Focused, Windows.Count - 1)]; } }
    }

    /// One workspace: its tiles (Containers, in komorebi's order), floating windows, layout and tile rectangles.
    sealed class WorkspaceView
    {
        public int Index;
        /// Its number across all screens (Numbers.cs); 0 when it holds none (empty and out of sight).
        public int Number;
        public string Name;
        public string Layout;
        public bool Monocle;
        public int FocusedContainer = -1;
        public double? PrimaryRatio;
        public readonly List<ContainerView> Containers = new List<ContainerView>();
        public readonly List<WindowView> Floating = new List<WindowView>();
        /// latest_layout: one rectangle per tiled container, in screen pixels.
        public readonly List<Rectangle> Tiles = new List<Rectangle>();

        public bool IsEmpty { get { return Containers.Count == 0 && Floating.Count == 0; } }
    }

    /// One monitor: where it is (screen pixels) and its own workspaces.
    sealed class MonitorView
    {
        public int Index;
        /// Stays the same while the screen is unplugged and plugged back in: its serial number.
        public string Key;
        public Rectangle Bounds;
        public int FocusedWorkspace;
        public readonly List<WorkspaceView> Workspaces = new List<WorkspaceView>();

        public WorkspaceView Current
        {
            get { return FocusedWorkspace >= 0 && FocusedWorkspace < Workspaces.Count ? Workspaces[FocusedWorkspace] : null; }
        }
    }

    /// The parts of komorebi's state the strips and commands care about. Every monitor is kept (each
    /// gets its own island); the shortcuts below are the focused monitor's, where komorebic acts.
    sealed class Snapshot
    {
        static readonly List<WorkspaceView> None = new List<WorkspaceView>();

        public bool Paused;
        /// komorebi re-read komorebi.json, which resets runtime settings such as border colours.
        public bool ConfigReloaded;
        public int FocusedMonitor;
        public readonly List<MonitorView> Monitors = new List<MonitorView>();
        /// Every managed window on every monitor.
        public readonly HashSet<long> Hwnds = new HashSet<long>();

        public MonitorView Focused
        {
            get { return FocusedMonitor >= 0 && FocusedMonitor < Monitors.Count ? Monitors[FocusedMonitor] : null; }
        }

        public int MonitorIndex { get { return FocusedMonitor; } }
        public List<WorkspaceView> Workspaces { get { return Focused == null ? None : Focused.Workspaces; } }
        public int FocusedWorkspace { get { return Focused == null ? 0 : Focused.FocusedWorkspace; } }
        public WorkspaceView Current { get { return Focused == null ? null : Focused.Current; } }

        /// The monitors that take numbers, in numbering order: the primary screen (at 0,0) first, then
        /// the rest left to right. komorebi 0.1.41 can list a hot-plugged screen twice (same id, same
        /// rectangle); only its first entry counts, the one its island shows (ForScreen).
        public List<MonitorView> Numbered()
        {
            var distinct = new List<MonitorView>();
            foreach (var m in Monitors)
                if (!distinct.Any(d => d.Bounds.Contains(Centre(m.Bounds)))) distinct.Add(m);
            return distinct.Where(m => m.Workspaces.Count > 0)
                .OrderBy(m => m.Bounds.Contains(0, 0) ? 0 : 1).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        }

        static Point Centre(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

        /// The workspace's number, as Win+N and the islands count (Numbers.cs); 0 when it holds none.
        public int Number(int monitor, int workspace)
        {
            if (monitor < 0 || monitor >= Monitors.Count || workspace < 0 || workspace >= Monitors[monitor].Workspaces.Count) return 0;
            return Monitors[monitor].Workspaces[workspace].Number;
        }

        /// The monitor and workspace that carry `number`, on whichever screen.
        public bool FindNumber(int number, out int monitor, out int workspace)
        {
            foreach (var m in Numbered())
                foreach (var ws in m.Workspaces)
                    if (ws.Number == number)
                    {
                        monitor = m.Index;
                        workspace = ws.Index;
                        return true;
                    }
            monitor = workspace = -1;
            return false;
        }

        /// The lowest number no workspace holds: where "a new workspace" goes.
        public int FreeNumber()
        {
            var used = new HashSet<int>(Numbered().SelectMany(m => m.Workspaces).Select(w => w.Number));
            int n = 1;
            while (used.Contains(n)) n++;
            return n;
        }

        /// True when komorebi sees exactly these screens (physical pixels): one monitor for each, with
        /// its rectangle. After a screen change komorebi 0.1.41 can list one twice, or give a new one
        /// another screen's rectangle, and then tiles that screen's windows in the wrong place.
        public bool MatchesScreens(ICollection<Rectangle> screens)
        {
            return Monitors.Count == screens.Count && screens.All(s => Monitors.Count(m => m.Bounds == s) == 1);
        }

        /// The monitor showing this part of the screen (matched by the screen's centre), or null.
        public MonitorView ForScreen(Rectangle screen)
        {
            var centre = Centre(screen);
            return Monitors.FirstOrDefault(m => m.Bounds.Contains(centre));
        }

        /// Finds a window on any monitor: its monitor, workspace, container (-1 when floating) and slot.
        public bool Locate(long hwnd, out int monitor, out int workspace, out int container, out int window)
        {
            for (monitor = 0; monitor < Monitors.Count; monitor++)
            {
                var list = Monitors[monitor].Workspaces;
                for (workspace = 0; workspace < list.Count; workspace++)
                {
                    if (Find(list[workspace], hwnd, out container, out window)) return true;
                }
            }
            monitor = workspace = container = window = -1;
            return false;
        }

        static bool Find(WorkspaceView ws, long hwnd, out int container, out int window)
        {
            for (container = 0; container < ws.Containers.Count; container++)
            {
                window = ws.Containers[container].Windows.FindIndex(w => w.Hwnd == hwnd);
                if (window >= 0) return true;
            }
            container = -1;
            window = ws.Floating.FindIndex(w => w.Hwnd == hwnd);
            return window >= 0;
        }

        public ContainerView FocusedContainer
        {
            get
            {
                var ws = Current;
                return ws != null && ws.FocusedContainer >= 0 && ws.FocusedContainer < ws.Containers.Count
                    ? ws.Containers[ws.FocusedContainer] : null;
            }
        }

        /// A window of this exe on any monitor.
        public WindowView FindByExe(string exe)
        {
            foreach (var monitor in Monitors)
                foreach (var ws in monitor.Workspaces)
                {
                    foreach (var c in ws.Containers)
                        foreach (var w in c.Windows)
                            if (string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)) return w;
                    foreach (var w in ws.Floating)
                        if (string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)) return w;
                }
            return null;
        }

        /// Finds which workspace, container and stack slot of the focused monitor hold a window.
        public bool Locate(long hwnd, out int workspace, out int container, out int window)
        {
            for (workspace = 0; workspace < Workspaces.Count; workspace++)
                if (Find(Workspaces[workspace], hwnd, out container, out window)) return true;
            workspace = container = window = -1;
            return false;
        }

        /// Builds a snapshot from the JSON of `komorebic state`.
        public static Snapshot FromState(object state)
        {
            var snapshot = new Snapshot { Paused = Json.Bool(state, "is_paused", false) };
            var monitorRing = Json.Obj(state, "monitors");
            object[] monitors = Json.Arr(monitorRing, "elements");
            if (monitors.Length == 0) return snapshot;

            snapshot.FocusedMonitor = Clamp((int)Json.Long(monitorRing, "focused", 0), monitors.Length - 1);
            for (int m = 0; m < monitors.Length; m++)
            {
                var workspaceRing = Json.Obj(monitors[m], "workspaces");
                // Screens that cannot tell komorebi their serial report "UNKNOWN"; their port then tells them apart.
                string serial = Json.Str(monitors[m], "serial_number_id", "");
                bool known = serial.Length > 0 && !serial.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
                var monitor = new MonitorView
                {
                    Index = m,
                    Key = known ? serial : Json.Str(monitors[m], "device_id", Json.Str(monitors[m], "name", m.ToString())),
                    Bounds = ToRect(Json.Obj(monitors[m], "size")),
                    FocusedWorkspace = (int)Json.Long(workspaceRing, "focused", 0),
                };
                object[] workspaces = Json.Arr(workspaceRing, "elements");
                for (int i = 0; i < workspaces.Length; i++)
                {
                    var view = ParseWorkspace(workspaces[i], i);
                    foreach (var c in view.Containers)
                        foreach (var w in c.Windows) snapshot.Hwnds.Add(w.Hwnd);
                    foreach (var w in view.Floating) snapshot.Hwnds.Add(w.Hwnd);
                    monitor.Workspaces.Add(view);
                }
                snapshot.Monitors.Add(monitor);
            }
            WorkspaceNumbers.Apply(snapshot);
            return snapshot;
        }

        /// komorebi rects are left/top plus width/height stored in right/bottom.
        static Rectangle ToRect(object node)
        {
            return new Rectangle((int)Json.Long(node, "left", 0), (int)Json.Long(node, "top", 0),
                (int)Json.Long(node, "right", 0), (int)Json.Long(node, "bottom", 0));
        }

        static WorkspaceView ParseWorkspace(object node, int index)
        {
            var view = new WorkspaceView
            {
                Index = index,
                Name = Json.Str(node, "name", null) ?? (index + 1).ToString(),
                Layout = Json.Str(Json.Obj(node, "layout"), "Default", "Custom"),
            };

            var containerRing = Json.Obj(node, "containers");
            foreach (object c in Json.Arr(containerRing, "elements")) view.Containers.Add(ParseContainer(c));
            if (view.Containers.Count > 0) view.FocusedContainer = (int)Json.Long(containerRing, "focused", 0);

            // Monocle and maximized windows are lifted out of the container list; put them back where they came from.
            var monocle = Json.Obj(node, "monocle_container");
            if (monocle != null)
            {
                int at = Clamp((int)Json.Long(node, "monocle_container_restore_idx", view.Containers.Count), view.Containers.Count);
                view.Containers.Insert(at, ParseContainer(monocle));
                view.FocusedContainer = at;
                view.Monocle = true;
            }
            var maximized = Json.Obj(node, "maximized_window");
            if (maximized != null)
            {
                int at = Clamp((int)Json.Long(node, "maximized_window_restore_idx", view.Containers.Count), view.Containers.Count);
                var single = new ContainerView();
                single.Windows.Add(ParseWindow(maximized));
                view.Containers.Insert(at, single);
                view.FocusedContainer = at;
            }

            foreach (object w in Json.Arr(Json.Obj(node, "floating_windows"), "elements")) view.Floating.Add(ParseWindow(w));
            foreach (object r in Json.Arr(node, "latest_layout")) view.Tiles.Add(ToRect(r));

            object[] ratios = Json.Arr(Json.Obj(node, "layout_options"), "column_ratios");
            if (ratios.Length > 0) view.PrimaryRatio = Json.Number(ratios[0]);
            return view;
        }

        static ContainerView ParseContainer(object node)
        {
            var container = new ContainerView();
            var windowRing = Json.Obj(node, "windows");
            foreach (object w in Json.Arr(windowRing, "elements")) container.Windows.Add(ParseWindow(w));
            container.Focused = Clamp((int)Json.Long(windowRing, "focused", 0), Math.Max(0, container.Windows.Count - 1));
            return container;
        }

        static WindowView ParseWindow(object node)
        {
            return new WindowView
            {
                Hwnd = Json.Long(node, "hwnd", 0),
                Exe = Json.Str(node, "exe", ""),
                Title = Json.Str(node, "title", ""),
            };
        }

        static int Clamp(int value, int max) { return Math.Max(0, Math.Min(value, max)); }
    }

    /// Runs komorebic.exe. komorebic sends a command and exits; komorebi handles commands in order,
    /// so a query issued after a command sees its result.
    static class Komorebic
    {
        /// Runs komorebic with these arguments and returns its exit code; `output` is what it printed.
        public static int Run(out string output, params string[] args)
        {
            var info = new ProcessStartInfo("komorebic.exe", string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            try
            {
                using (var process = Process.Start(info))
                {
                    // Drain stderr on the side: a panic backtrace can fill the pipe while we read stdout.
                    process.ErrorDataReceived += delegate { };
                    process.BeginErrorReadLine();
                    output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        return -1;
                    }
                    return process.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("komorebic", ex);
                output = "";
                return -1;
            }
        }

        public static bool Run(params string[] args)
        {
            string ignored;
            return Run(out ignored, args) == 0;
        }

        public static string Query(string what)
        {
            string output;
            return Run(out output, "query", what) == 0 ? output.Trim() : null;
        }

        /// The current state, asked of komorebi right now; null when komorebi is not running.
        public static Snapshot State()
        {
            string json;
            if (Run(out json, "state") != 0 || string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return Snapshot.FromState(Json.Parse(json));
            }
            catch (Exception ex)
            {
                Log.Error("komorebic state", ex);
                return null;
            }
        }

        /// komorebi.json's first error as `komorebic check` puts it ("missing field `name` at line 61
        /// column 41"), or null when it loads (or komorebic cannot say). komorebi quits at start on a
        /// config with errors and tells no one, so check before stopping or starting it.
        public static string ConfigError()
        {
            var info = new ProcessStartInfo("komorebic.exe", "check")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (var process = Process.Start(info))
                {
                    var errors = new StringBuilder();
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                    process.BeginErrorReadLine();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        process.Kill();
                        return null;
                    }
                    process.WaitForExit(); // lets the last stderr lines arrive
                    string all;
                    lock (errors) all = output + "\n" + errors;
                    foreach (string line in all.Split('\n'))
                        if (line.TrimStart().StartsWith("Error:", StringComparison.Ordinal)) return line.Trim().Substring(6).Trim();
                    return null;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("komorebic check", ex);
                return null;
            }
        }

        /// Applies komorebi.json without losing the layout: komorebi saves its state when it stops and
        /// restores it when it starts. (replace-configuration piles every window onto one workspace.)
        /// Returns komorebi.json's error instead, leaving komorebi running, when the config would not load.
        public static string Restart()
        {
            string error = ConfigError();
            if (error != null) return error;
            Run("stop");
            // komorebi writes its state file and puts every window back on screen before it exits.
            for (int i = 0; i < 100 && IsRunning(); i++) Thread.Sleep(100);
            Launch();
            return null;
        }

        /// Returns komorebi.json's error instead of starting a komorebi that would quit straight away.
        public static string Start()
        {
            string error = ConfigError();
            if (error == null) Launch();
            return error;
        }

        /// Without redirected output: komorebi inherits komorebic's handles and would hold our pipes open.
        static void Launch()
        {
            Process.Start(new ProcessStartInfo("komorebic-no-console.exe", "start") { UseShellExecute = false, CreateNoWindow = true }).Dispose();
        }

        static bool IsRunning()
        {
            Process[] processes = Process.GetProcessesByName("komorebi");
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }

        static string Quote(string arg)
        {
            return arg.IndexOfAny(new[] { ' ', '"' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }

    /// Live komorebi state over a named pipe subscription. Reconnects when komorebi restarts.
    /// Snapshots are coalesced so a burst of events costs one repaint.
    sealed class KomorebiFeed : IDisposable
    {
        const string PipeName = "deskhud";

        readonly SynchronizationContext ui;
        readonly Action<Snapshot> onState;
        readonly Action<bool> onOnline;
        readonly Thread thread;
        volatile bool stopping;
        NamedPipeServerStream pipe;
        Snapshot pending;
        int posted;
        bool online;

        public KomorebiFeed(Action<Snapshot> onState, Action<bool> onOnline)
        {
            ui = SynchronizationContext.Current;
            if (ui == null) throw new InvalidOperationException("KomorebiFeed must be created on the UI thread");
            this.onState = onState;
            this.onOnline = onOnline;
            thread = new Thread(Loop) { IsBackground = true, Name = "komorebi-feed" };
        }

        public void Start() { thread.Start(); }

        void Loop()
        {
            int retryMs = 1000;
            while (!stopping)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 20, 0))
                    {
                        pipe = server;
                        IAsyncResult connecting = server.BeginWaitForConnection(null, null);
                        if (!Komorebic.Run("subscribe-pipe", PipeName) || !connecting.AsyncWaitHandle.WaitOne(3000))
                            throw new IOException("komorebi is not running");
                        server.EndWaitForConnection(connecting);

                        retryMs = 1000;
                        Publish(Komorebic.State());
                        SetOnline(true);

                        var reader = new StreamReader(server, new UTF8Encoding(false));
                        string line;
                        while (!stopping && (line = reader.ReadLine()) != null)
                        {
                            if (line.Length > 0) Publish(Parse(line));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // IO errors are komorebi going away; anything else is worth a log line, then a retry.
                    if (!(ex is IOException || ex is ObjectDisposedException)) Log.Error("komorebi feed", ex);
                }
                finally
                {
                    pipe = null;
                }

                if (stopping) break;
                SetOnline(false);
                Thread.Sleep(retryMs);
                retryMs = Math.Min(retryMs * 2, 10000);
            }
        }

        static Snapshot Parse(string line)
        {
            try
            {
                object notification = Json.Parse(line);
                var snapshot = Snapshot.FromState(Json.Obj(notification, "state"));
                snapshot.ConfigReloaded = IsConfigReload(Json.Obj(notification, "event"));
                return snapshot;
            }
            catch (Exception ex)
            {
                Log.Error("komorebi notification", ex);
                return null;
            }
        }

        /// The event is a socket message or window event, possibly wrapped one level deep.
        static bool IsConfigReload(object evt)
        {
            for (int depth = 0; depth < 2 && evt != null; depth++)
            {
                string type = Json.Str(evt, "type", "");
                if (type == "ReplaceConfiguration" || type == "ReloadConfiguration" || type == "ReloadStaticConfiguration") return true;
                evt = Json.Obj(evt, "content");
            }
            return false;
        }

        void Publish(Snapshot snapshot)
        {
            if (snapshot == null) return;
            // A reload must survive coalescing, or the borders would keep komorebi.json's colours.
            var replaced = Interlocked.Exchange(ref pending, snapshot);
            if (replaced != null && replaced.ConfigReloaded) snapshot.ConfigReloaded = true;
            if (Interlocked.Exchange(ref posted, 1) == 1) return;
            ui.Post(_ =>
            {
                Interlocked.Exchange(ref posted, 0);
                var latest = Interlocked.Exchange(ref pending, null);
                if (latest != null) onState(latest);
            }, null);
        }

        void SetOnline(bool value)
        {
            if (online == value) return;
            online = value;
            ui.Post(_ => onOnline(value), null);
        }

        public void Dispose()
        {
            stopping = true;
            var current = pipe;
            if (current != null) current.Dispose();
            Komorebic.Run("unsubscribe-pipe", PipeName);
        }
    }
}
