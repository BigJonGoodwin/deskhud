// =================================================================================================
// GlazeWm.cs: everything that talks to GlazeWM, the default window manager.
//
// GlazeWM serves its state and takes commands over a WebSocket on 127.0.0.1:6123: `query monitors`
// (the whole tree), `sub -e all` (events), `command <cmd>` / `command --id <container> <cmd>`. Every
// reply is JSON with `messageType` `client_response` (echoing `clientMessage`) or `event_subscription`.
//
// GlazeSocket: one blocking WebSocket connection, for worker threads.
// GlazeIpc: the shared connection for queries and commands (one request at a time).
// GlazeFeed: its own connection subscribed to every event; after each one it asks for the tree again
//   (one request in flight, bursts coalesce) and hands the UI thread a Snapshot.
// Snapshot.FromGlazeWm: GlazeWM's tree (monitors > workspaces > split containers > windows) as
//   deskhud's model. Every tiled window is a tile of its own; a tabbed split (the deskhud fork of
//   GlazeWM, see PLAN's contract: `"layout": "tabbed"`, `activeChildId`) is one tile holding every
//   window under it, shown one at a time, like a komorebi stack. Workspace names are the numbers.
// GlazeWm: IWindowManager in GlazeWM commands. DwmBorders: border colours, drawn by deskhud itself.
//
// GlazeWM keeps the numbering deskhud wants by itself: a workspace name lives on one monitor at a time,
// `focus --workspace N` goes to it there, and a name not in use is created on the focused monitor
// (activate_workspace: bind_to_monitor first, else the focused container's monitor). So "a free
// number opens on the screen you are on" is `focus --monitor` there, then `focus --workspace N`. The
// numbers must be workspaces in GlazeWM's config (`workspaces: - name: "1"`...) without bind_to_monitor.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace DeskHud
{
    /// One WebSocket connection to GlazeWM. Blocking calls, for worker threads only.
    sealed class GlazeSocket : IDisposable
    {
        const string Address = "ws://127.0.0.1:6123";

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly byte[] buffer = new byte[64 * 1024];

        /// A connected socket, or null when GlazeWM is not listening.
        public static GlazeSocket Connect(int timeoutMs)
        {
            var connection = new GlazeSocket();
            try
            {
                if (connection.socket.ConnectAsync(new Uri(Address), CancellationToken.None).Wait(timeoutMs)) return connection;
            }
            catch (AggregateException) { }
            connection.Dispose();
            return null;
        }

        public void Send(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Wait(socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None), 5000);
        }

        /// The next message, or null when GlazeWM closed the connection. Throws IOException after timeoutMs.
        public string Receive(int timeoutMs)
        {
            using (var message = new MemoryStream())
            {
                while (true)
                {
                    var receiving = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    Wait(receiving, timeoutMs);
                    WebSocketReceiveResult result = receiving.Result;
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        /// A WebSocket that times out is in an unknown state; it is aborted, and errors become IOException.
        void Wait(System.Threading.Tasks.Task task, int timeoutMs)
        {
            try
            {
                if (task.Wait(timeoutMs)) return;
            }
            catch (AggregateException ex)
            {
                throw new IOException("GlazeWM connection: " + ex.GetBaseException().Message, ex.GetBaseException());
            }
            socket.Abort();
            throw new IOException("GlazeWM did not answer");
        }

        public void Dispose()
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    /// Queries and commands on one shared connection, made on first use and again after GlazeWM restarts.
    static class GlazeIpc
    {
        static readonly object Gate = new object();
        static GlazeSocket socket;

        /// Sends `message` and waits for its reply: true on success with GlazeWM's `data`, else its error.
        public static bool Ask(string message, out object data, out string error)
        {
            lock (Gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (socket == null) socket = GlazeSocket.Connect(1000);
                    if (socket == null) break;
                    try
                    {
                        socket.Send(message);
                        while (true)
                        {
                            string text = socket.Receive(5000);
                            if (text == null) throw new IOException("GlazeWM closed the connection");
                            var reply = Json.Parse(text) as Dictionary<string, object>;
                            if (reply == null || Json.Str(reply, "messageType", "") != "client_response" || Json.Str(reply, "clientMessage", "") != message) continue;
                            reply.TryGetValue("data", out data);
                            error = Json.Str(reply, "error", null);
                            return Json.Bool(reply, "success", false);
                        }
                    }
                    catch (IOException)
                    {
                        // A connection left over from a GlazeWM that has since restarted: try a fresh one once.
                        socket.Dispose();
                        socket = null;
                    }
                }
            }
            data = null;
            error = "GlazeWM is not running";
            return false;
        }

        public static object Query(string what)
        {
            object data;
            string error;
            return Ask("query " + what, out data, out error) ? data : null;
        }

        /// Runs a GlazeWM command (`focus --workspace 3`), on `subject` (a container id) when given.
        public static bool Command(string command, string subject = null)
        {
            string message = subject == null ? "command " + command : "command --id " + subject + " " + command;
            object data;
            string error;
            if (Ask(message, out data, out error)) return true;
            Log.Write("glazewm: " + message + ": " + error);
            return false;
        }
    }

    /// GlazeWM's side of Snapshot: building one from `query monitors`.
    sealed partial class Snapshot
    {
        /// Builds a snapshot from the `data` of GlazeWM's `query monitors` reply ({ "monitors": [...] }).
        public static Snapshot FromGlazeWm(object data, bool paused)
        {
            var snapshot = new Snapshot { Paused = paused };
            object[] monitors = Json.Arr(data, "monitors");
            for (int m = 0; m < monitors.Length; m++)
            {
                object node = monitors[m];
                var monitor = new MonitorView
                {
                    Index = m,
                    // Stays the same across replugs; device names (\\.\DISPLAY1) are handed out in plug order.
                    Key = Json.Str(node, "hardwareId", null) ?? Json.Str(node, "devicePath", null) ?? Json.Str(node, "deviceName", m.ToString()),
                    Bounds = Rect(node),
                };
                object[] workspaces = Json.Arr(node, "children");
                for (int i = 0; i < workspaces.Length; i++)
                {
                    var ws = new GlazeWorkspace(snapshot, workspaces[i], i).View;
                    if (Json.Bool(workspaces[i], "isDisplayed", false)) monitor.FocusedWorkspace = i;
                    monitor.Workspaces.Add(ws);
                }
                if (Json.Bool(node, "hasFocus", false)) snapshot.FocusedMonitor = m;
                snapshot.Monitors.Add(monitor);
            }
            NumberByName(snapshot);
            return snapshot;
        }

        /// A workspace named with a number holds that number; any other name takes the lowest free one.
        static void NumberByName(Snapshot snapshot)
        {
            var all = snapshot.Numbered().SelectMany(m => m.Workspaces).ToList();
            var taken = new HashSet<int>();
            foreach (var ws in all)
            {
                int n;
                if (int.TryParse(ws.Name, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0 && taken.Add(n)) ws.Number = n;
            }
            foreach (var ws in all.Where(w => w.Number == 0))
            {
                int n = 1;
                while (taken.Contains(n)) n++;
                taken.Add(n);
                ws.Number = n;
            }
        }

        internal static Rectangle Rect(object node)
        {
            return new Rectangle((int)Json.Long(node, "x", 0), (int)Json.Long(node, "y", 0), (int)Json.Long(node, "width", 0), (int)Json.Long(node, "height", 0));
        }
    }

    /// Flattens one GlazeWM workspace's tree into a WorkspaceView: tiles in tree order, floating windows,
    /// and which tile has focus.
    sealed class GlazeWorkspace
    {
        public readonly WorkspaceView View;
        readonly Snapshot snapshot;
        // The window GlazeWM would focus in this workspace: its last focused one.
        readonly string focusedId;

        public GlazeWorkspace(Snapshot snapshot, object node, int index)
        {
            this.snapshot = snapshot;
            string name = Json.Str(node, "name", (index + 1).ToString());
            View = new WorkspaceView
            {
                Index = index,
                Id = Json.Str(node, "id", null),
                Name = name,
                Bounds = Snapshot.Rect(node),
                Layout = Json.Str(node, "tilingDirection", "horizontal") == "vertical" ? "Vertical" : "Horizontal",
            };
            focusedId = LastFocused(node);
            foreach (object child in Json.Arr(node, "children")) Walk(child, null, null);
            for (int c = 0; c < View.Containers.Count; c++)
                if (View.Containers[c].Windows.Any(w => w.Id == focusedId)) View.FocusedContainer = c;
        }

        /// Follows childFocusOrder down to a window: the one focused when this workspace is.
        static string LastFocused(object node)
        {
            for (int depth = 0; depth < 64 && node != null; depth++)
            {
                if (Json.Str(node, "type", "") == "window") return Json.Str(node, "id", null);
                object[] order = Json.Arr(node, "childFocusOrder");
                string next = order.Length > 0 ? order[0] as string : null;
                node = next == null ? null : Json.Arr(node, "children").FirstOrDefault(c => Json.Str(c, "id", "") == next);
            }
            return null;
        }

        /// `tabs` is the tabbed split this node sits in (every window under it is one of its tabs);
        /// `tab` the tabbed split's own child it descends from, which says whether it is the active tab.
        void Walk(object node, ContainerView tabs, string tab)
        {
            string type = Json.Str(node, "type", "");
            if (type == "window")
            {
                AddWindow(node, tabs);
                return;
            }
            if (type != "split") return;
            if (tabs != null || Json.Str(node, "layout", "tiled") != "tabbed")
            {
                foreach (object child in Json.Arr(node, "children")) Walk(child, tabs, tab);
                return;
            }

            var stack = new ContainerView { Id = Json.Str(node, "id", null), Tabbed = true, Bounds = Snapshot.Rect(node) };
            string active = ActiveChild(node);
            foreach (object child in Json.Arr(node, "children"))
            {
                string childId = Json.Str(child, "id", "");
                int before = stack.Windows.Count;
                Walk(child, stack, childId);
                if (childId == active && stack.Windows.Count > before) stack.Focused = before;
            }
            if (stack.Windows.Count == 0) return;
            View.Containers.Add(stack);
            View.Tiles.Add(stack.Bounds);
        }

        /// The active tab: `activeChildId`, or an index (`activeChildIndex` / `activeChild`), or the
        /// split's last focused child, so it still shows something sensible if the field is named otherwise.
        static string ActiveChild(object node)
        {
            object[] children = Json.Arr(node, "children");
            string id = Json.Str(node, "activeChildId", null);
            if (id != null) return id;
            long index = Json.Long(node, "activeChildIndex", Json.Long(node, "activeChild", -1));
            if (index >= 0 && index < children.Length) return Json.Str(children[index], "id", null);
            object[] order = Json.Arr(node, "childFocusOrder");
            return order.Length > 0 ? order[0] as string : null;
        }

        void AddWindow(object node, ContainerView tabs)
        {
            string exe = Json.Str(node, "processName", "");
            var window = new WindowView
            {
                Hwnd = Json.Long(node, "handle", 0),
                Id = Json.Str(node, "id", null),
                Title = Json.Str(node, "title", ""),
                // GlazeWM names the process without its extension; settings.json's apps name the exe.
                Exe = exe.Length == 0 || exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe",
            };
            snapshot.Hwnds.Add(window.Hwnd);
            if (Json.Bool(node, "hasFocus", false)) snapshot.FocusedHwnd = window.Hwnd;

            // Minimized windows stay in GlazeWM's tree; deskhud shows them in the tray bubble instead.
            string state = Json.Str(Json.Obj(node, "state"), "type", "tiling");
            if (state == "minimized") return;
            if (state == "floating")
            {
                View.Floating.Add(window);
                return;
            }
            if (tabs != null)
            {
                tabs.Windows.Add(window);
                return;
            }
            var single = new ContainerView { Id = window.Id, Bounds = Snapshot.Rect(node) };
            single.Windows.Add(window);
            View.Containers.Add(single);
            View.Tiles.Add(single.Bounds);
            // A fullscreen window covers the workspace, like komorebi's monocle.
            if (state == "fullscreen") View.Monocle = true;
        }
    }

    /// GlazeWM's live state: subscribes to every event and asks for the tree again after each one.
    /// One query is in flight at a time, so a burst of events costs one or two queries.
    sealed class GlazeFeed : IDisposable
    {
        readonly SnapshotPost post;
        readonly Thread thread;
        volatile bool stopping;
        volatile GlazeSocket socket;
        bool paused, reloaded;

        public GlazeFeed(Action<Snapshot> onState, Action<bool> onOnline)
        {
            post = new SnapshotPost(onState, onOnline);
            thread = new Thread(Loop) { IsBackground = true, Name = "glazewm-feed" };
            thread.Start();
        }

        void Loop()
        {
            int retryMs = 1000;
            while (!stopping)
            {
                try
                {
                    using (var connection = GlazeSocket.Connect(2000))
                    {
                        if (connection == null) throw new IOException("GlazeWM is not running");
                        socket = connection;
                        connection.Send("sub -e all");
                        connection.Send("query paused");
                        connection.Send("query monitors");
                        bool asking = true, stale = false;
                        string text;
                        while (!stopping && (text = connection.Receive(Timeout.Infinite)) != null)
                        {
                            var message = Json.Parse(text) as Dictionary<string, object>;
                            if (message == null) continue;
                            if (Json.Str(message, "messageType", "") == "event_subscription")
                            {
                                if (!OnEvent(Json.Obj(message, "data"))) break;
                                if (asking) stale = true;
                                else
                                {
                                    connection.Send("query monitors");
                                    asking = true;
                                }
                                continue;
                            }
                            string asked = Json.Str(message, "clientMessage", "");
                            object data;
                            message.TryGetValue("data", out data);
                            if (asked == "query paused" && data is bool) paused = (bool)data;
                            if (asked != "query monitors") continue;

                            asking = false;
                            if (Json.Bool(message, "success", false))
                            {
                                var snapshot = Snapshot.FromGlazeWm(data, paused);
                                snapshot.ConfigReloaded = reloaded;
                                reloaded = false;
                                // Online first: GlazeWm.Feed renumbers on a connection's first snapshot.
                                post.SetOnline(true);
                                post.Publish(snapshot);
                                retryMs = 1000;
                            }
                            if (stale)
                            {
                                stale = false;
                                connection.Send("query monitors");
                                asking = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // IO errors are GlazeWM going away; anything else is worth a log line, then a retry.
                    if (!(ex is IOException || ex is ObjectDisposedException)) Log.Error("glazewm feed", ex);
                }
                finally
                {
                    socket = null;
                }

                if (stopping) break;
                post.SetOnline(false);
                Thread.Sleep(retryMs);
                retryMs = Math.Min(retryMs * 2, 10000);
            }
        }

        /// Keeps what the tree does not say (paused, config reloaded). False when GlazeWM is quitting.
        bool OnEvent(object evt)
        {
            switch (Json.Str(evt, "eventType", ""))
            {
                case "pause_changed": paused = Json.Bool(evt, "isPaused", paused); break;
                case "user_config_changed": reloaded = true; break;
                case "application_exiting": return false;
            }
            return true;
        }

        public void Dispose()
        {
            stopping = true;
            var current = socket;
            if (current != null) current.Dispose();
        }
    }

    /// Window borders in the palette's colours, set on each window through DWM (Windows 11 draws
    /// them), since GlazeWM takes its border colours from its config file only. Turn GlazeWM's own
    /// `window_effects` borders off, or the two fight. Snapshots arrive on the UI thread and palettes
    /// on the command queue, so everything is under one lock.
    sealed class DwmBorders
    {
        readonly object gate = new object();
        const int DWMWA_BORDER_COLOR = 34;
        // DWMWA_COLOR_DEFAULT: the border Windows would draw.
        const int DefaultColour = unchecked((int)0xFFFFFFFF);

        readonly Dictionary<long, int> painted = new Dictionary<long, int>();
        PaletteColors palette;
        bool paused;
        Snapshot last;

        public void SetPalette(PaletteColors value, bool isPaused)
        {
            lock (gate)
            {
                palette = value;
                paused = isPaused;
                painted.Clear(); // a new palette repaints everything
                Repaint(last);
            }
        }

        /// Focused tile in the accent (a tab stack's in the stack colour, a floating one in the
        /// floating colour), everything else unfocused; paused turns the focused colour negative.
        public void Update(Snapshot snapshot)
        {
            lock (gate) Repaint(snapshot);
        }

        void Repaint(Snapshot snapshot)
        {
            last = snapshot;
            if (palette == null || snapshot == null) return;
            var seen = new HashSet<long>();
            foreach (var ws in snapshot.Monitors.SelectMany(m => m.Workspaces))
            {
                foreach (var c in ws.Containers)
                    foreach (var w in c.Windows) Paint(w.Hwnd, w.Hwnd == snapshot.FocusedHwnd ? (c.Tabbed ? palette.Stack[1] : ws.Monocle ? palette.Stack[2] : palette.Accent) : palette.Unfocused, seen);
                foreach (var w in ws.Floating) Paint(w.Hwnd, w.Hwnd == snapshot.FocusedHwnd ? palette.Stack[3] : palette.Unfocused, seen);
            }
            foreach (long gone in painted.Keys.Where(h => !seen.Contains(h)).ToList()) painted.Remove(gone);
        }

        void Paint(long hwnd, Color colour, HashSet<long> seen)
        {
            seen.Add(hwnd);
            if (paused && hwnd == last.FocusedHwnd) colour = palette.Negative;
            int value = colour.R | colour.G << 8 | colour.B << 16;
            int had;
            if (painted.TryGetValue(hwnd, out had) && had == value) return;
            painted[hwnd] = value;
            Native.DwmSetWindowAttribute(new IntPtr(hwnd), DWMWA_BORDER_COLOR, ref value, sizeof(int));
        }

        public void Reset()
        {
            lock (gate)
            {
                foreach (long hwnd in painted.Keys)
                {
                    int value = DefaultColour;
                    Native.DwmSetWindowAttribute(new IntPtr(hwnd), DWMWA_BORDER_COLOR, ref value, sizeof(int));
                }
                painted.Clear();
                palette = null; // a snapshot arriving after this must not paint them again
            }
        }
    }

    /// deskhud's verbs in GlazeWM commands.
    sealed class GlazeWm : IWindowManager
    {
        static readonly string[][] Splits =
        {
            new[] { "Horizontal", "horizontal", "Side by side" },
            new[] { "Vertical", "vertical", "One above the other" },
        };

        readonly string exe;
        readonly DwmBorders borders = new DwmBorders();

        public GlazeWm(string exe)
        {
            this.exe = string.IsNullOrWhiteSpace(exe) ? "glazewm.exe" : exe;
        }

        public string Name { get { return "GlazeWM"; } }
        public string[][] Layouts { get { return Splits; } }
        public bool ChecksScreens { get { return false; } }
        public string MapHelp { get { return "Click: snap 2/3, 1/3, 1/2  ·  right-click, middle or scroll: flip the split"; } }

        public string ConfigFile
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("GLAZEWM_CONFIG_PATH");
                return string.IsNullOrEmpty(path)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".glzr", "glazewm", "config.yaml")
                    : path;
            }
        }

        public Snapshot State()
        {
            object data = GlazeIpc.Query("monitors");
            if (data == null) return null;
            object paused = GlazeIpc.Query("paused");
            return Snapshot.FromGlazeWm(data, paused is bool && (bool)paused);
        }

        public IDisposable Feed(Action<Snapshot> onState, Action<bool> onOnline)
        {
            // The feed reports online before the first snapshot of each connection (UI thread, in order).
            bool connected = false;
            return new GlazeFeed(s =>
            {
                borders.Update(s);
                if (connected)
                {
                    connected = false;
                    Renumber(s);
                }
                onState(s);
            }, online =>
            {
                connected = online;
                onOnline(online);
            });
        }

        /// Runs StartNumbering's renames off the UI thread; GlazeWM's workspace_updated events redraw.
        static void Renumber(Snapshot snapshot)
        {
            List<string> commands = StartNumbering(snapshot);
            if (commands.Count == 0) return;
            Log.Write("glazewm: renaming the screens' workspaces so the primary screen shows the lowest number");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (string command in commands)
                    if (!GlazeIpc.Command(command)) return;
            });
        }

        /// GlazeWM starts every screen with one workspace, handed out in its own monitor order (left to
        /// right), so the primary screen can show 2. While each screen still has just that one, swap the
        /// names (through temporary ones: GlazeWM refuses a name in use) so the numbers go the way the
        /// islands count, primary first, then left to right (Snapshot.Numbered). After that, numbers
        /// follow your screen as usual, so this never moves a workspace someone made.
        internal static List<string> StartNumbering(Snapshot snapshot)
        {
            var commands = new List<string>();
            List<MonitorView> monitors = snapshot.Numbered();
            if (monitors.Count < 2 || monitors.Any(m => m.Workspaces.Count != 1)) return commands;
            var names = monitors.Select(m => m.Workspaces[0].Name).ToList();
            int n;
            if (names.Any(name => !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1)) return commands;
            var wanted = names.Select(name => int.Parse(name, CultureInfo.InvariantCulture)).OrderBy(x => x)
                .Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
            var moving = Enumerable.Range(0, names.Count).Where(i => names[i] != wanted[i]).ToList();
            foreach (int i in moving) commands.Add("update-workspace-config --workspace " + names[i] + " --name deskhud-" + wanted[i]);
            foreach (int i in moving) commands.Add("update-workspace-config --workspace deskhud-" + wanted[i] + " --name " + wanted[i]);
            return commands;
        }

        public bool Run(string[] args)
        {
            if (args.Length == 0) return false;
            object data;
            string error;
            string message = string.Join(" ", args);
            if (GlazeIpc.Ask(message, out data, out error)) return true;
            Log.Write("glazewm: " + message + ": " + error);
            return false;
        }

        public void Minimize() { GlazeIpc.Command("set-minimized"); }
        public void Stack(string direction) { GlazeIpc.Command("stack --direction " + direction); }
        public void Unstack() { GlazeIpc.Command("unstack"); }
        public void ToggleTabbed() { GlazeIpc.Command("toggle-tabbed"); }
        public void CycleTab(int step) { GlazeIpc.Command(step < 0 ? "focus --prev-tab" : "focus --next-tab"); }
        public void ToggleFloat() { GlazeIpc.Command("toggle-floating"); }
        public void Layout(string how) { GlazeIpc.Command("toggle-tiling-direction"); }
        public void Retile() { GlazeIpc.Command("wm-redraw"); }
        public void TogglePause() { GlazeIpc.Command("wm-toggle-pause"); }
        public void FocusMonitor(int monitor) { GlazeIpc.Command("focus --monitor " + monitor); }

        public void FocusApp(WindowView window)
        {
            if (window.Id != null) GlazeIpc.Command("focus --container-id " + window.Id);
        }

        /// Cycles the focused tile's share of its workspace along the split: 1/2, 2/3, 1/3.
        public void Snap()
        {
            Snapshot state = State();
            WorkspaceView ws = state == null ? null : state.Current;
            ContainerView tile = state == null ? null : state.FocusedContainer;
            if (ws == null || tile == null || ws.Containers.Count < 2 || ws.Bounds.Width <= 0 || ws.Bounds.Height <= 0) return;
            bool across = ws.Layout == "Horizontal";
            double share = across ? (double)tile.Bounds.Width / ws.Bounds.Width : (double)tile.Bounds.Height / ws.Bounds.Height;
            int next = share > 0.58 ? 33 : share < 0.42 ? 50 : 67;
            GlazeIpc.Command("size --" + (across ? "width " : "height ") + next + "%", tile.Id);
        }

        public void FocusWindow(long hwnd)
        {
            var window = new IntPtr(hwnd);
            Snapshot state = State();
            WindowView view = state == null ? null : state.Window(hwnd);
            if (view != null && view.Id != null)
            {
                GlazeIpc.Command("focus --container-id " + view.Id);
                for (int i = 0; i < 15 && Native.GetForegroundWindow() != window; i++) Thread.Sleep(20);
            }
            // GlazeWM may count it focused while Windows has another in front (a menu just closed); insist.
            if (Native.GetForegroundWindow() != window && Native.IsWindowVisible(window)) Native.ForceForeground(window);
        }

        /// A floating window GlazeWM manages is tiled; one it never took is announced again, which runs
        /// its usual handling (and its rules) for a new window.
        public void Tile(long hwnd)
        {
            Snapshot state = State();
            WindowView view = state == null ? null : state.Window(hwnd);
            if (view != null) GlazeIpc.Command("set-tiling", view.Id);
            else Native.NotifyWinEvent(Native.EVENT_OBJECT_SHOW, new IntPtr(hwnd), Native.OBJID_WINDOW, 0);
        }

        /// Commands name the window by id, so unlike komorebi nothing has to be brought to the front first.
        public void WindowAction(long hwnd, string action, string value)
        {
            Snapshot state = State();
            WindowView view = state == null ? null : state.Window(hwnd);
            if (view == null)
            {
                Log.Write("window: GlazeWM does not manage " + hwnd + ", so \"" + action + "\" was skipped");
                return;
            }
            int number;
            switch (action)
            {
                case "minimize": GlazeIpc.Command("set-minimized", view.Id); break;
                case "float": GlazeIpc.Command("toggle-floating", view.Id); break;
                case "monocle": GlazeIpc.Command("toggle-fullscreen --maximized", view.Id); break;
                case "unstack": GlazeIpc.Command("unstack", view.Id); break;
                case "move":
                    if (!int.TryParse(value, out number) || number < 1) return;
                    string target = WorkspaceName(state, number);
                    if (GlazeIpc.Command("move --workspace " + target, view.Id)) GlazeIpc.Command("focus --workspace " + target);
                    break;
                default: Log.Write("window: unknown action " + action); break;
            }
        }

        /// A number in use is a workspace on some screen, and GlazeWM goes there. A free one is a new
        /// workspace on the screen you are on: GlazeWM makes it on its focused monitor, so focus that
        /// screen first (it lags when you click an empty screen). Moving takes the window along.
        public Action PlanWorkspace(Snapshot snapshot, int number, bool move)
        {
            Rectangle yours = Native.ScreenYouAreOn();
            MonitorView here = yours.IsEmpty ? null : snapshot.ForScreen(yours);
            List<string> commands = WorkspaceCommands(snapshot, number, move, here == null ? -1 : here.Index);
            return () =>
            {
                // Each step needs the one before (the window must have moved before focus follows it).
                foreach (string command in commands)
                    if (!GlazeIpc.Command(command)) return;
            };
        }

        /// The GlazeWM commands for Win+N / Win+Shift+N, given the monitor index of the screen you are on
        /// (-1 when unknown). A number in use goes to its own screen; moving needs no monitor at all,
        /// since GlazeWM opens a new workspace on the moved window's screen, which is the one you are on.
        internal static List<string> WorkspaceCommands(Snapshot snapshot, int number, bool move, int yours)
        {
            string target = WorkspaceName(snapshot, number);
            int monitor, workspace;
            var commands = new List<string>();
            if (move) commands.Add("move --workspace " + target);
            else if (!snapshot.FindNumber(number, out monitor, out workspace) && yours >= 0 && yours != snapshot.FocusedMonitor)
                commands.Add("focus --monitor " + yours);
            commands.Add("focus --workspace " + target);
            return commands;
        }

        /// The workspace carrying the number (its name, which may not be the number itself), or the number as a new name.
        static string WorkspaceName(Snapshot snapshot, int number)
        {
            int monitor, workspace;
            return snapshot.FindNumber(number, out monitor, out workspace) ? snapshot.Monitors[monitor].Workspaces[workspace].Name : number.ToString();
        }

        public void FocusWorkspace(int monitor, int workspace)
        {
            WorkspaceView ws = At(State(), monitor, workspace);
            if (ws != null) GlazeIpc.Command("focus --workspace " + ws.Name);
        }

        public void SetLayout(int monitor, int workspace, string value)
        {
            WorkspaceView ws = At(State(), monitor, workspace);
            if (ws != null && ws.Id != null) GlazeIpc.Command("set-tiling-direction " + value, ws.Id);
        }

        static WorkspaceView At(Snapshot state, int monitor, int workspace)
        {
            if (state == null || monitor < 0 || monitor >= state.Monitors.Count) return null;
            var list = state.Monitors[monitor].Workspaces;
            return workspace >= 0 && workspace < list.Count ? list[workspace] : null;
        }

        /// GlazeWM re-reads its config in place; windows stay where they are.
        public string Restart()
        {
            object data;
            string error;
            return GlazeIpc.Ask("command wm-reload-config", out data, out error) ? null : error;
        }

        public string Start()
        {
            Process[] running = Process.GetProcessesByName("glazewm");
            foreach (var process in running) process.Dispose();
            if (running.Length > 0) return null;
            try
            {
                // Through the shell, so a bare name is found on PATH or in App Paths like at a prompt.
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(exe)) { UseShellExecute = true }).Dispose();
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("glazewm start", ex);
                return "cannot start " + exe;
            }
        }

        /// GlazeWM reports its own config errors (a dialog at start or reload).
        public string ConfigError() { return null; }

        public void ApplyBorders(PaletteColors palette, bool paused) { borders.SetPalette(palette, paused); }
        public void ResetBorders() { borders.Reset(); }
    }
}
