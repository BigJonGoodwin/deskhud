// =================================================================================================
// Yasb.cs: what deskhud needs to know about YASB's bars.
//
// Where its islands begin on each monitor, so the left islands never run under them: the media island
// sits in the centre while something plays, and the right-hand islands grow leftwards. YasbEdge asks
// each of YASB's bars, through UI Automation (Qt answers it), where its first visible centre widget, or
// else its right-hand container, starts; each StripWindow keeps its own width left of that.
//
// The colour of what's playing: the media widget draws the album art behind its title, so YasbEdge
// samples those pixels (the top half, clear of the glow drawn under it) and reports the art's colour,
// which deskhud writes back to YASB as --media-rgb for the media island's glow.
//
// It asks soon after a focus change and every PollMs otherwise, which also catches media appearing
// and tracks changing. Each bar's containers are found once and kept; after that a probe is a handful
// of cross-process calls per bar, off the UI thread.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace DeskHud
{
    /// Finds, for each screen with a YASB bar, how far from the screen's left edge (physical pixels)
    /// its first right-hand widget starts. Raises Changed on the UI thread when any of them moves;
    /// screens without a bar are left out.
    sealed class YasbEdge : IDisposable
    {
        const int PollMs = 5000;

        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = PollMs };
        readonly System.Windows.Forms.Timer soon = new System.Windows.Forms.Timer { Interval = 450 };
        int busy;
        // Each bar's centre and right containers, found once (walking down to them was most of a
        // probe's cost). Only the probe worker touches it, one at a time (`busy`).
        readonly Dictionary<IntPtr, AutomationElement[]> containers = new Dictionary<IntPtr, AutomationElement[]>();
        string last = "";
        Color? lastMedia;

        public event Action<Dictionary<Rectangle, int>> Changed;
        /// The album art's colour, when something new is playing (a different colour from the last).
        public event Action<Color> MediaChanged;

        public YasbEdge()
        {
            ui = SynchronizationContext.Current;
            if (ui == null) throw new InvalidOperationException("YasbEdge must be created on the UI thread");
            poll.Tick += delegate { Watch(); Measure(); };
            soon.Tick += delegate { soon.Stop(); Measure(); };
        }

        int missing;

        /// YASB 2.0.7 can kill itself when screens change: it restarts to rebuild its bars, the new copy
        /// gives up waiting for the old one, and the old one quits (yasb.log, "Timeout waiting for previous
        /// instance"). After three polls with no YASB at all, start it again through its logon task.
        void Watch()
        {
            Process[] running = Process.GetProcessesByName("yasb");
            foreach (var process in running) process.Dispose();
            if (running.Length > 0)
            {
                missing = 0;
                return;
            }
            if (++missing < 3) return;
            missing = -12; // a minute's grace before trying again
            Log.Write("yasb: not running, starting it again");
            ThreadPool.QueueUserWorkItem(_ => StartYasb());
        }

        static void StartYasb()
        {
            try
            {
                using (var task = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"\\desktop\\YASB\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (task.WaitForExit(5000) && task.ExitCode == 0) return;
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YASB", "yasb.exe");
            if (File.Exists(exe)) Process.Start("explorer.exe", "\"" + exe + "\"");
            else Log.Write("yasb: no logon task \\desktop\\YASB and no " + exe);
        }

        public void Start()
        {
            Measure();
            poll.Start();
        }

        /// Measures shortly, once things settle (YASB redraws a moment after a focus change).
        public void MeasureSoon()
        {
            soon.Stop();
            soon.Start();
        }

        void Measure()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            Rectangle[] screens = Array.ConvertAll(Screen.AllScreens, s => s.Bounds);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var edges = new Dictionary<Rectangle, int>();
                Rectangle media = Rectangle.Empty;
                Dictionary<Rectangle, IntPtr> bars = FindBars(screens);
                foreach (IntPtr gone in containers.Keys.Where(h => !bars.ContainsValue(h)).ToList()) containers.Remove(gone);
                foreach (var bar in bars)
                {
                    try
                    {
                        Rectangle playing;
                        double edge = Probe(bar.Value, out playing);
                        // Handed on as the distance from the screen's left edge, never 0 for a real island.
                        if (!double.IsNaN(edge) && edge > bar.Key.Left) edges[bar.Key] = (int)Math.Round(edge) - bar.Key.Left;
                        if (media.IsEmpty) media = playing;
                    }
                    catch (Exception ex)
                    {
                        // ElementNotAvailable and friends: YASB was reloading. Find the bar afresh next time.
                        containers.Remove(bar.Value);
                        if (!(ex is ElementNotAvailableException)) Log.Error("yasb edge", ex);
                    }
                }
                Color? art = media.IsEmpty ? null : ArtColour(media);
                Interlocked.Exchange(ref busy, 0);
                if (art.HasValue && art != lastMedia)
                {
                    lastMedia = art;
                    ui.Post(__ => { var handler = MediaChanged; if (handler != null) handler(art.Value); }, null);
                }
                string key = string.Join(";", edges);
                if (key == last) return;
                last = key;
                ui.Post(__ => { var handler = Changed; if (handler != null) handler(edges); }, null);
            });
        }

        /// The islands start at the first visible centre widget (hidden ones report an empty rectangle),
        /// or else where the right container does: that screen x, or NaN when the bar shows neither.
        /// (Screens left of or above the main one have negative coordinates, so no x can mean "none".)
        /// `media` is the media widget's rectangle while it shows.
        double Probe(IntPtr bar, out Rectangle media)
        {
            media = Rectangle.Empty;
            AutomationElement[] parts;
            if (!containers.TryGetValue(bar, out parts))
            {
                parts = Containers(bar);
                if (parts == null) return double.NaN;
                containers[bar] = parts;
            }
            System.Windows.Rect right = parts[1].Current.BoundingRectangle;
            double edge = right.IsEmpty || right.Width <= 0 ? double.NaN : right.Left;
            foreach (AutomationElement widget in parts[0].FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                System.Windows.Rect r = widget.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || widget.Current.IsOffscreen) continue;
                edge = double.IsNaN(edge) ? r.Left : Math.Min(edge, r.Left);
                if (widget.Current.ClassName == "MediaWidget")
                    media = new Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height);
            }
            if (double.IsNaN(edge)) containers.Remove(bar); // the right container always shows; these went stale
            return edge;
        }

        /// Bar → (one wrapper per level) → the left, centre and right containers; returns the centre
        /// and right ones, or null while YASB is still building the bar.
        static AutomationElement[] Containers(IntPtr bar)
        {
            AutomationElement element = AutomationElement.FromHandle(bar);
            for (int depth = 0; depth < 4 && element != null; depth++)
            {
                AutomationElementCollection children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                if (children.Count >= 3) return new[] { children[children.Count - 2], children[children.Count - 1] };
                element = children.Count > 0 ? children[0] : null;
            }
            return null;
        }

        /// The album art's colour: the media widget's top half, read off the screen, through the same
        /// saturated-colour average that gives app icons their glow. Null when it is all greys (no art).
        static Color? ArtColour(Rectangle widget)
        {
            var top = new Rectangle(widget.X + 2, widget.Y + widget.Height / 6, Math.Max(1, widget.Width - 4), Math.Max(1, widget.Height / 3));
            using (var shot = new Bitmap(top.Width, top.Height))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(top.Location, Point.Empty, top.Size);
                Color glow = IconSet.GlowOf(shot);
                double h, s, l;
                PaletteBuilder.ToHsl(glow, out h, out s, out l);
                if (s < 0.2) return null;
                // Steady from one sample to the next: a new colour only for a new picture.
                return PaletteBuilder.Hsl(Math.Round(h / 8) * 8, Math.Round(s * 10) / 10, 0.64);
            }
        }

        /// YASB's bars: each a full-width window along the top of a screen.
        static Dictionary<Rectangle, IntPtr> FindBars(Rectangle[] screens)
        {
            var bars = new Dictionary<Rectangle, IntPtr>();
            Native.EnumWindows((hwnd, lParam) =>
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                Native.RECT rect;
                if (!Native.GetWindowRect(hwnd, out rect)) return true;
                foreach (Rectangle screen in screens)
                {
                    if (bars.ContainsKey(screen) || Math.Abs(rect.Top - screen.Top) > 2 || Math.Abs(rect.Left - screen.Left) > 2
                        || rect.Right - rect.Left < screen.Width * 9 / 10) continue;
                    string exe = Native.ProcessPath(hwnd);
                    if (exe != null && exe.EndsWith(@"\yasb.exe", StringComparison.OrdinalIgnoreCase)) bars[screen] = hwnd;
                    break;
                }
                return true;
            }, IntPtr.Zero);
            return bars;
        }

        public void Dispose()
        {
            poll.Dispose();
            soon.Dispose();
        }
    }
}
