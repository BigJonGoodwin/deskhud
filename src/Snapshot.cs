// =================================================================================================
// Snapshot.cs: the window manager's state as deskhud sees it, whichever window manager runs.
//
// Snapshot, MonitorView, WorkspaceView, ContainerView, WindowView: every monitor with its own
// workspaces; Snapshot's Workspaces / FocusedWorkspace / Current are the focused monitor's. A container
// is one tile; several windows in one container form a stack (komorebi) or a tabbed split (GlazeWM),
// shown one at a time. Built by Snapshot.FromState (komorebi, Komorebi.cs) or Snapshot.FromGlazeWm
// (GlazeWm.cs), which also number the workspaces (Numbers.cs).
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace DeskHud
{
    /// One window: its handle, exe name and title.
    sealed class WindowView
    {
        public long Hwnd;
        public string Exe;
        public string Title;
        /// GlazeWM's container id (null with komorebi), for commands that name the window.
        public string Id;
    }

    /// One tile. More than one window makes it a stack; Focused is the one showing.
    sealed class ContainerView
    {
        public readonly List<WindowView> Windows = new List<WindowView>();
        public int Focused;
        /// GlazeWM: a tabbed split (its id and rectangle, where the tab strip goes). komorebi stacks leave these unset.
        public string Id;
        public bool Tabbed;
        public Rectangle Bounds;

        public WindowView Visible { get { return Windows[Math.Min(Focused, Windows.Count - 1)]; } }
    }

    /// One workspace: its tiles (Containers, in the window manager's order), floating windows, layout and tile rectangles.
    sealed class WorkspaceView
    {
        public int Index;
        /// GlazeWM's workspace id (null with komorebi).
        public string Id;
        public Rectangle Bounds;
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

    /// The parts of the window manager's state the strips and commands care about. Every monitor is kept (each
    /// gets its own island); the shortcuts below are the focused monitor's, where plain commands act.
    sealed partial class Snapshot
    {
        static readonly List<WorkspaceView> None = new List<WorkspaceView>();

        public bool Paused;
        /// The window manager re-read its config, which resets runtime settings such as komorebi's border colours.
        public bool ConfigReloaded;
        public int FocusedMonitor;
        /// The window with the window manager's focus, when it says (GlazeWM); 0 otherwise.
        public long FocusedHwnd;
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

        /// A managed window by handle, on any monitor (minimized ones are not in the tiles), or null.
        public WindowView Window(long hwnd)
        {
            foreach (var monitor in Monitors)
                foreach (var ws in monitor.Workspaces)
                {
                    foreach (var c in ws.Containers)
                        foreach (var w in c.Windows)
                            if (w.Hwnd == hwnd) return w;
                    foreach (var w in ws.Floating)
                        if (w.Hwnd == hwnd) return w;
                }
            return null;
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
    }

    /// Hands snapshots from a feed thread to the UI thread, coalesced: a burst of events costs one repaint.
    sealed class SnapshotPost
    {
        readonly SynchronizationContext ui;
        readonly Action<Snapshot> onState;
        readonly Action<bool> onOnline;
        Snapshot pending;
        int posted;
        bool online;

        /// Must be created on the UI thread.
        public SnapshotPost(Action<Snapshot> onState, Action<bool> onOnline)
        {
            ui = SynchronizationContext.Current;
            if (ui == null) throw new InvalidOperationException("a feed must be created on the UI thread");
            this.onState = onState;
            this.onOnline = onOnline;
        }

        public void Publish(Snapshot snapshot)
        {
            if (snapshot == null) return;
            // A reload must survive coalescing, or the borders would keep the config's colours.
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

        /// Feed thread only.
        public void SetOnline(bool value)
        {
            if (online == value) return;
            online = value;
            ui.Post(_ => onOnline(value), null);
        }
    }
}
