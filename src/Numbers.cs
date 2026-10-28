// =================================================================================================
// Numbers.cs: workspace numbers that follow the screen you are on.
//
// komorebi keeps a separate list of workspaces for each monitor. deskhud lays numbers over them the
// way i3 does: a number belongs to one workspace on one screen while that workspace has windows or is
// on show, and it stays on that screen. A number nobody holds is free, and asking for it (Win+N,
// Win+Shift+N, the icon menu's "Move to workspace") makes it on the screen you are on, from an empty
// workspace there (`komorebic ensure-workspaces` adds one when none is left). An empty workspace out
// of sight holds no number, and the islands leave it out.
//
// WorkspaceNumbers.Apply runs on every snapshot (Snapshot.FromState) and fills WorkspaceView.Number.
// The daemon remembers the numbers per monitor (by its serial) and workspace slot in
// %LOCALAPPDATA%\deskhud\workspaces.json, so they survive restarts and a screen being replugged.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DeskHud
{
    /// Which number each komorebi workspace carries. The feed, the command queue and the UI thread
    /// all number snapshots, so everything goes through one lock.
    static class WorkspaceNumbers
    {
        // A number just asked for is kept this long while its workspace is still empty and out of
        // sight: komorebi switches to it (or creates it) a moment later.
        const int ClaimMs = 3000;

        static readonly object Gate = new object();
        // "monitor key/slot" → number, for every monitor seen, plugged in or not.
        static readonly Dictionary<string, int> held = new Dictionary<string, int>();
        static readonly Dictionary<string, int> claimedAt = new Dictionary<string, int>();
        static bool persist;
        static string saved = "";

        /// Reads the remembered numbers. Only the daemon calls this, so only the daemon writes them back.
        public static void Load()
        {
            lock (Gate)
            {
                persist = true;
                try
                {
                    if (!File.Exists(Paths.WorkspacesFile)) return;
                    var root = Json.Parse(File.ReadAllText(Paths.WorkspacesFile)) as Dictionary<string, object>;
                    if (root == null) return;
                    foreach (var pair in root)
                    {
                        double? n = Json.Number(pair.Value);
                        // "UNKNOWN/..." came from screens without a serial, which now go by their port.
                        if (n.HasValue && n.Value >= 1 && !pair.Key.StartsWith("UNKNOWN/", StringComparison.OrdinalIgnoreCase))
                            held[pair.Key] = (int)n.Value;
                    }
                    saved = Signature();
                }
                catch (Exception ex)
                {
                    Log.Error("workspace numbers", ex);
                }
            }
        }

        /// Numbers the snapshot's workspaces: fresh claims first, then every workspace with windows or
        /// on show keeps the number it had, then the others take the lowest free one. The order settles
        /// clashes and who gets low numbers: every screen's first workspace (primary first, then left to
        /// right, as Snapshot.Numbered lists them), then the rest.
        public static void Apply(Snapshot snapshot)
        {
            lock (Gate)
            {
                List<MonitorView> monitors = snapshot.Numbered();
                var slots = new List<KeyValuePair<MonitorView, WorkspaceView>>();
                foreach (var m in monitors) slots.Add(new KeyValuePair<MonitorView, WorkspaceView>(m, m.Workspaces[0]));
                foreach (var m in monitors)
                    for (int i = 1; i < m.Workspaces.Count; i++) slots.Add(new KeyValuePair<MonitorView, WorkspaceView>(m, m.Workspaces[i]));

                int now = Environment.TickCount;
                foreach (string stale in claimedAt.Where(c => unchecked(now - c.Value) >= ClaimMs).Select(c => c.Key).ToList())
                    claimedAt.Remove(stale);

                var numbers = new Dictionary<WorkspaceView, int>();
                // A claimed number is taken even before komorebi has made its workspace.
                var taken = new HashSet<int>(claimedAt.Keys.Where(held.ContainsKey).Select(k => held[k]));
                int n;
                foreach (var slot in slots)
                    if (claimedAt.ContainsKey(Key(slot.Key, slot.Value.Index)) && held.TryGetValue(Key(slot.Key, slot.Value.Index), out n))
                        numbers[slot.Value] = n;
                foreach (var slot in slots)
                    if (!numbers.ContainsKey(slot.Value) && Holds(slot.Key, slot.Value)
                        && held.TryGetValue(Key(slot.Key, slot.Value.Index), out n) && taken.Add(n))
                        numbers[slot.Value] = n;
                foreach (var slot in slots)
                {
                    if (numbers.ContainsKey(slot.Value) || !Holds(slot.Key, slot.Value)) continue;
                    for (n = 1; taken.Contains(n); n++) { }
                    taken.Add(n);
                    numbers[slot.Value] = n;
                }

                // The plugged-in screens hold exactly these now; the others keep theirs for when they return.
                var live = new HashSet<string>(monitors.Select(m => m.Key));
                foreach (string key in held.Keys.Where(k => live.Contains(MonitorOf(k)) && !claimedAt.ContainsKey(k)).ToList())
                    held.Remove(key);
                foreach (var slot in slots)
                {
                    string key = Key(slot.Key, slot.Value.Index);
                    slot.Value.Number = numbers.TryGetValue(slot.Value, out n) ? n : 0;
                    if (slot.Value.Number > 0) held[key] = slot.Value.Number;
                    // On show or with windows, it keeps its number by the usual rule: the claim is done.
                    if (Holds(slot.Key, slot.Value)) claimedAt.Remove(key);
                }
                Save();
            }
        }

        /// Where number `n` is, or is to go: the workspace holding it, on whichever screen; or else a
        /// free one on the screen you are on (Native.ScreenYouAreOn), claimed for n. With `reuseShown`,
        /// an empty workspace on show just takes the new number (Win+N from an empty workspace);
        /// otherwise it is an empty one out of sight, or one past the end (`create`: run
        /// ensure-workspaces before going there).
        public static bool Place(Snapshot snapshot, int n, bool reuseShown, out int monitor, out int workspace, out bool create)
        {
            create = false;
            if (snapshot.FindNumber(n, out monitor, out workspace)) return true;
            // komorebi's focused monitor lags when you click an empty screen, so ask Windows first;
            // with nothing in front (komorebi just switched to an empty workspace), komorebi knows.
            Rectangle yours = Native.ScreenYouAreOn();
            MonitorView here = yours.IsEmpty ? null : snapshot.ForScreen(yours);
            if (here == null && snapshot.Focused != null) here = snapshot.ForScreen(snapshot.Focused.Bounds);
            if (here == null) return false;

            WorkspaceView shown = here.Current;
            WorkspaceView spare = reuseShown && shown != null && shown.IsEmpty ? shown
                : here.Workspaces.FirstOrDefault(w => w.IsEmpty && w.Index != here.FocusedWorkspace && w.Number == 0);
            monitor = here.Index;
            workspace = spare != null ? spare.Index : here.Workspaces.Count;
            create = spare == null;
            lock (Gate)
            {
                string key = Key(here, workspace);
                held[key] = n;
                claimedAt[key] = Environment.TickCount;
            }
            Apply(snapshot);
            return true;
        }

        static bool Holds(MonitorView m, WorkspaceView ws) { return !ws.IsEmpty || ws.Index == m.FocusedWorkspace; }

        static string Key(MonitorView m, int slot) { return m.Key + "/" + slot; }

        static string MonitorOf(string key) { return key.Substring(0, Math.Max(0, key.LastIndexOf('/'))); }

        static string Signature() { return string.Join(";", held.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)); }

        static void Save()
        {
            if (!persist) return;
            string signature = Signature();
            if (signature == saved) return;
            saved = signature;
            try
            {
                Paths.WriteAtomic(Paths.WorkspacesFile, Json.Stringify(held));
            }
            catch (Exception ex)
            {
                Log.Error("workspace numbers", ex);
            }
        }
    }
}
