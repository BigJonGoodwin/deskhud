// =================================================================================================
// KomorebiWm.cs: deskhud's verbs as komorebic commands (`window_manager.backend: "komorebi"`).
//
// Stack / unstack with memory of where a window sat (StackMemory, stacks.json, and komorebi's
// save-resize files), snap the main split (1/2, 2/3, 1/3), focus a window clicked on an island, tile
// an untiled window, the icon menu's actions, numbered workspaces through Numbers.cs, restarts that
// keep the layout, border colours. komorebic acts on the focused window, so anything aimed at one
// window by handle focuses it first and checks it really is in front.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DeskHud
{
    /// deskhud's verbs in komorebic commands. Runs on the command queue (or the command line).
    sealed class KomorebiWm : IWindowManager
    {
        /// komorebi's layouts: the name in its state, the komorebic value, and what the menu says.
        static readonly string[][] KomorebiLayouts =
        {
            new[] { "BSP", "bsp", "BSP (split in halves)" },
            new[] { "Columns", "columns", "Columns" },
            new[] { "Rows", "rows", "Rows" },
            new[] { "VerticalStack", "vertical-stack", "Main + vertical stack" },
            new[] { "RightMainVerticalStack", "right-main-vertical-stack", "Vertical stack + main" },
            new[] { "HorizontalStack", "horizontal-stack", "Main + horizontal stack" },
            new[] { "UltrawideVerticalStack", "ultrawide-vertical-stack", "Ultrawide" },
            new[] { "Grid", "grid", "Grid" },
            new[] { "Scrolling", "scrolling", "Scrolling" },
        };

        public string Name { get { return "komorebi"; } }
        public string[][] Layouts { get { return KomorebiLayouts; } }
        public bool ChecksScreens { get { return true; } }
        public string MapHelp { get { return "Click: snap 2/3, 1/3, 1/2  ·  right-click: next layout  ·  middle: flip  ·  scroll: change layout"; } }
        public string ConfigFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "komorebi.json"); } }

        public Snapshot State() { return Komorebic.State(); }
        public IDisposable Feed(Action<Snapshot> onState, Action<bool> onOnline) { return new KomorebiFeed(onState, onOnline); }
        public bool Run(string[] args) { return Komorebic.Run(args); }

        public void Minimize() { Komorebic.Run("minimize"); }
        public void CycleTab(int step) { Komorebic.Run("cycle-stack", step < 0 ? "previous" : "next"); }
        public void ToggleFloat() { Komorebic.Run("toggle-float"); }
        public void FocusApp(WindowView window) { Komorebic.Run("eager-focus", window.Exe); }
        public void FocusWorkspace(int monitor, int workspace) { Komorebic.Run("focus-monitor-workspace", monitor.ToString(), workspace.ToString()); }
        public void FocusMonitor(int monitor) { Komorebic.Run("focus-monitor", monitor.ToString()); }
        public void SetLayout(int monitor, int workspace, string value) { Komorebic.Run("workspace-layout", monitor.ToString(), workspace.ToString(), value); }
        public void Retile() { Komorebic.Run("retile"); }
        public void TogglePause() { Komorebic.Run("toggle-pause"); }
        public string Restart() { return Komorebic.Restart(); }
        public string Start() { return Komorebic.Start(); }
        public string ConfigError() { return Komorebic.ConfigError(); }
        public void ApplyBorders(PaletteColors palette, bool paused) { Borders.Apply(palette, paused); }
        /// komorebi draws its own borders and puts them away when it stops.
        public void ResetBorders() { }

        public void Layout(string how)
        {
            if (how == "flip") Komorebic.Run("flip-layout", "horizontal");
            else Komorebic.Run("cycle-layout", how == "previous" ? "previous" : "next");
        }

        /// komorebi has no tabbed layout: a stack already shows one window at a time, so the toggle
        /// stacks the window onto its left neighbour, or pops it out of its stack.
        public void ToggleTabbed()
        {
            Snapshot state = Komorebic.State();
            ContainerView container = state == null ? null : state.FocusedContainer;
            if (container == null) return;
            if (container.Windows.Count > 1) Unstack();
            else Stack("left");
        }

        /// A number lives on one screen; a free one is made on the screen you are on (Numbers.cs).
        /// Placing it renumbers at once, so the islands show it before komorebi has switched.
        public Action PlanWorkspace(Snapshot snapshot, int number, bool move)
        {
            int monitor, index;
            bool create;
            if (!WorkspaceNumbers.Place(snapshot, number, !move, out monitor, out index, out create)) return null;
            string command = move ? "move-to-monitor-workspace" : "focus-monitor-workspace";
            return () =>
            {
                if (create) Komorebic.Run("ensure-workspaces", monitor.ToString(), (index + 1).ToString());
                Komorebic.Run(command, monitor.ToString(), index.ToString());
            };
        }

        /// Stacks the focused window onto its neighbour in `direction`. On a stack, komorebi pulls a lone
        /// neighbour in instead, so pressing again keeps gathering windows. A lone window that joins
        /// remembers where it sat, so `unstack` can put it back.
        public void Stack(string direction)
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
        public void Unstack()
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
        public void Snap()
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

        /// Shows the window wherever it is: its monitor's workspace, then its tile, then its stack slot.
        public void FocusWindow(long hwnd)
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
        public void Tile(long hwnd)
        {
            var window = new IntPtr(hwnd);
            if (!Native.IsWindow(window)) return;
            Native.SetForegroundWindow(window);
            Thread.Sleep(60); // komorebi picks up focus changes from window events, not the socket
            if (Native.GetForegroundWindow() == window) Komorebic.Run("manage");
        }

        /// The icon menu's komorebic actions: minimize, float, monocle, unstack, move <number>.
        /// komorebic only acts on the focused window, so it is focused first.
        public void WindowAction(long hwnd, string action, string value)
        {
            var window = new IntPtr(hwnd);
            if (!Native.IsWindow(window)) return;
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
}
