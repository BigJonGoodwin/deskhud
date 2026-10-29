// =================================================================================================
// Komorebi.cs: everything that talks to komorebi, the tiling window manager.
//
// Snapshot.FromState: the shared state model (Snapshot.cs) built from `komorebic state`. A komorebi
//   "container" is one tile; several windows in one container form a stack. Building a snapshot also
//   numbers its workspaces (Numbers.cs).
// Komorebic: runs komorebic.exe (deskhud never opens komorebi's socket itself) and restarts komorebi
//   so its saved layout comes back.
// KomorebiFeed: subscribes to komorebi's event pipe and hands each new state to the UI thread.
// The verbs deskhud runs through komorebic are in KomorebiWm.cs.
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
    /// komorebi's side of Snapshot: building one from `komorebic state`.
    sealed partial class Snapshot
    {
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

        readonly SnapshotPost post;
        readonly Thread thread;
        volatile bool stopping;
        NamedPipeServerStream pipe;

        public KomorebiFeed(Action<Snapshot> onState, Action<bool> onOnline)
        {
            post = new SnapshotPost(onState, onOnline);
            thread = new Thread(Loop) { IsBackground = true, Name = "komorebi-feed" };
            thread.Start();
        }

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
                        post.Publish(Komorebic.State());
                        post.SetOnline(true);

                        var reader = new StreamReader(server, new UTF8Encoding(false));
                        string line;
                        while (!stopping && (line = reader.ReadLine()) != null)
                        {
                            if (line.Length > 0) post.Publish(Parse(line));
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
                post.SetOnline(false);
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

        public void Dispose()
        {
            stopping = true;
            var current = pipe;
            if (current != null) current.Dispose();
            Komorebic.Run("unsubscribe-pipe", PipeName);
        }
    }
}
