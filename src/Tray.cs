// =================================================================================================
// Tray.cs: minimized windows and app icons.
//
// MinimizedTray: the windows minimized with Win+M, so they can be picked and brought back.
// IconCache / IconSet: app icons for the strip, with their glow colour, shaped halo and rim, cached
//   per window and per pixel size.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace DeskHud
{
    /// Minimized windows, so they stay targetable: scroll picks one, click brings it back.
    /// UI thread only.
    sealed class MinimizedTray
    {
        readonly List<long> windows = new List<long>();
        int target = -1;

        public event Action Changed;

        public IList<long> Windows { get { return windows.AsReadOnly(); } }
        public int Target { get { return target; } }

        public void Add(long hwnd)
        {
            windows.Remove(hwnd);
            windows.Add(hwnd);
            target = windows.Count - 1;
            Raise();
        }

        public void Remove(long hwnd)
        {
            int at = windows.IndexOf(hwnd);
            if (at < 0) return;
            windows.RemoveAt(at);
            if (target >= at) target--;
            if (target < 0 && windows.Count > 0) target = 0;
            Raise();
        }

        /// Takes in the app windows already minimized when deskhud starts. The list lives only here, so a
        /// rebuild or a crash forgot them, and with the taskbar hidden nothing else brings them back.
        public void AddExisting()
        {
            int before = windows.Count;
            Native.EnumWindows((hwnd, lParam) =>
            {
                if (IsMinimizedApp(hwnd) && !windows.Contains(hwnd.ToInt64())) windows.Add(hwnd.ToInt64());
                return true;
            }, IntPtr.Zero);
            if (windows.Count == before) return;
            target = windows.Count - 1;
            Raise();
        }

        /// What a taskbar would show as minimized: visible, minimized, unowned, captioned, not a tool
        /// window and not cloaked (on another virtual desktop).
        static bool IsMinimizedApp(IntPtr hwnd)
        {
            if (!Native.IsWindowVisible(hwnd) || !Native.IsIconic(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
            long style = Native.WindowLong(hwnd, Native.GWL_STYLE), ex = Native.WindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((style & Native.WS_CAPTION) != Native.WS_CAPTION || (ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            return Native.WindowTitle(hwnd).Length > 0;
        }

        /// Drops windows that were closed or restored behind our back.
        public void Prune()
        {
            int before = windows.Count;
            windows.RemoveAll(h => !Native.IsWindow(new IntPtr(h)) || !Native.IsIconic(new IntPtr(h)));
            if (windows.Count == before) return;
            target = Math.Min(Math.Max(target, 0), windows.Count - 1);
            Raise();
        }

        public void Cycle(int step)
        {
            if (windows.Count == 0) return;
            target = ((target + step) % windows.Count + windows.Count) % windows.Count;
            Raise();
        }

        public void RestoreTarget()
        {
            Prune();
            if (target >= 0) Restore(windows[target]);
        }

        public void RestoreAll()
        {
            Prune();
            foreach (long hwnd in windows.ToArray()) Restore(hwnd);
        }

        public void RestoreWindow(long hwnd)
        {
            if (windows.Contains(hwnd)) Restore(hwnd);
        }

        static void Restore(long hwnd)
        {
            var window = new IntPtr(hwnd);
            Native.ShowWindow(window, Native.SW_RESTORE);
            Native.SetForegroundWindow(window);
        }

        void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }

    /// App icons at every pixel size the strip draws (workspaces shrink their icons when crowded),
    /// one IconSet per size.
    sealed class IconCache : IDisposable
    {
        readonly Dictionary<int, IconSet> sets = new Dictionary<int, IconSet>();

        IconSet Set(int pixels)
        {
            IconSet set;
            if (!sets.TryGetValue(pixels, out set))
            {
                set = new IconSet();
                sets[pixels] = set;
            }
            return set;
        }

        public Bitmap Get(long hwnd, int pixels) { return Set(pixels).Get(hwnd, pixels); }
        public Color? Glow(long hwnd, int pixels) { return Set(pixels).Glow(hwnd, pixels); }
        public Bitmap Halo(long hwnd, int pixels, int pad) { return Set(pixels).Halo(hwnd, pixels, pad); }
        public Bitmap Rim(long hwnd, int pixels, int pad) { return Set(pixels).Rim(hwnd, pixels, pad); }

        public void Retain(ICollection<long> alive)
        {
            foreach (var set in sets.Values) set.Retain(alive);
        }

        /// The pixel sizes change with the monitor scale; drop every set then.
        public void Reset()
        {
            foreach (var set in sets.Values) set.Dispose();
            sets.Clear();
        }

        public void Dispose()
        {
            Reset();
        }
    }

    /// Real app icons at one pixel size, cached per window. The exe's own icon is preferred
    /// because it has crisp size variants; packaged apps (all ApplicationFrameHost.exe) get theirs
    /// from the shell by app id, and anything else falls back to the window's icon.
    sealed class IconSet : IDisposable
    {
        readonly Dictionary<string, Bitmap> byExe = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<long, Bitmap> byWindow = new Dictionary<long, Bitmap>();
        readonly Dictionary<long, string> exeOfWindow = new Dictionary<long, string>();
        readonly HashSet<long> missing = new HashSet<long>();
        readonly Dictionary<Bitmap, Color> glows = new Dictionary<Bitmap, Color>();
        readonly Dictionary<Bitmap, Bitmap> halos = new Dictionary<Bitmap, Bitmap>();
        readonly Dictionary<Bitmap, Bitmap> rims = new Dictionary<Bitmap, Bitmap>();

        /// The icon blurred into a soft light of its own shape and colours, `pad` pixels bigger on each side.
        public Bitmap Halo(long hwnd, int pixels, int pad)
        {
            Bitmap icon = Get(hwnd, pixels);
            if (icon == null) return null;
            Bitmap halo;
            if (!halos.TryGetValue(icon, out halo))
            {
                halo = Effects.Halo(icon, pad);
                halos[icon] = halo;
            }
            return halo;
        }

        /// A thin outline that traces the icon's edge, in its glow colour, the same size as Halo.
        public Bitmap Rim(long hwnd, int pixels, int pad)
        {
            Bitmap icon = Get(hwnd, pixels);
            if (icon == null) return null;
            Bitmap rim;
            if (!rims.TryGetValue(icon, out rim))
            {
                Color glow = Glow(hwnd, pixels) ?? Color.White;
                double h, s, l;
                PaletteBuilder.ToHsl(glow, out h, out s, out l);
                rim = Effects.Rim(icon, pad, PaletteBuilder.Hsl(h, s, 0.8));
                rims[icon] = rim;
            }
            return rim;
        }

        void Forget(Bitmap icon)
        {
            Bitmap extra;
            if (halos.TryGetValue(icon, out extra)) { extra.Dispose(); halos.Remove(icon); }
            if (rims.TryGetValue(icon, out extra)) { extra.Dispose(); rims.Remove(icon); }
            glows.Remove(icon);
        }

        /// The icon's own colour, brightened into a glow; null when the window has no icon.
        public Color? Glow(long hwnd, int pixels)
        {
            Bitmap icon = Get(hwnd, pixels);
            if (icon == null) return null;
            Color glow;
            if (!glows.TryGetValue(icon, out glow))
            {
                glow = GlowOf(icon);
                glows[icon] = glow;
            }
            return glow;
        }

        /// Average of the opaque pixels, weighted towards saturated ones, then lifted to a glow:
        /// a grey icon gets a soft grey glow, a coloured icon a vivid one.
        internal static Color GlowOf(Bitmap icon)
        {
            double r = 0, g = 0, b = 0, weight = 0, saturation = 0, count = 0;
            for (int y = 0; y < icon.Height; y++)
            {
                for (int x = 0; x < icon.Width; x++)
                {
                    Color c = icon.GetPixel(x, y);
                    if (c.A < 128) continue;
                    double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
                    double s = max <= 0 ? 0 : (max - min) / max;
                    double w = s * max + 0.04;
                    r += c.R * w; g += c.G * w; b += c.B * w;
                    weight += w;
                    saturation += s;
                    count++;
                }
            }
            if (weight <= 0) return Color.FromArgb(200, 200, 210);

            Color average = Color.FromArgb((int)(r / weight), (int)(g / weight), (int)(b / weight));
            double h, sat, light;
            PaletteBuilder.ToHsl(average, out h, out sat, out light);
            bool grey = saturation / count < 0.12;
            return PaletteBuilder.Hsl(h, grey ? 0.08 : Math.Max(0.5, Math.Min(0.9, sat * 1.15)), 0.64);
        }

        /// The window's icon at this size, or null when it has none.
        public Bitmap Get(long hwnd, int pixels)
        {
            Bitmap icon;
            if (byWindow.TryGetValue(hwnd, out icon)) return icon;
            string exe;
            if (exeOfWindow.TryGetValue(hwnd, out exe)) return byExe[exe];
            if (missing.Contains(hwnd)) return null;

            var window = new IntPtr(hwnd);
            exe = Native.ProcessPath(window);
            bool packaged = exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            if (exe != null && !packaged)
            {
                if (!byExe.TryGetValue(exe, out icon))
                {
                    icon = FromExe(exe, pixels);
                    if (icon != null) byExe[exe] = icon;
                }
                if (icon != null)
                {
                    exeOfWindow[hwnd] = exe;
                    return icon;
                }
            }

            icon = packaged ? FromAppId(window, pixels) ?? FromWindow(window, pixels) : FromWindow(window, pixels) ?? FromAppId(window, pixels);
            if (icon != null) byWindow[hwnd] = icon;
            else missing.Add(hwnd);
            return icon;
        }

        /// Forgets windows that no longer exist.
        public void Retain(ICollection<long> alive)
        {
            foreach (long hwnd in new List<long>(byWindow.Keys))
            {
                if (alive.Contains(hwnd)) continue;
                Forget(byWindow[hwnd]);
                byWindow[hwnd].Dispose();
                byWindow.Remove(hwnd);
            }
            foreach (long hwnd in new List<long>(exeOfWindow.Keys))
            {
                if (!alive.Contains(hwnd)) exeOfWindow.Remove(hwnd);
            }
            missing.RemoveWhere(hwnd => !alive.Contains(hwnd));
        }

        static Bitmap FromExe(string exe, int pixels)
        {
            var handles = new IntPtr[1];
            var ids = new uint[1];
            if (Native.PrivateExtractIcons(exe, 0, pixels, pixels, handles, ids, 1, 0) == 0 || handles[0] == IntPtr.Zero) return null;
            try
            {
                using (var icon = Icon.FromHandle(handles[0])) return Fit(icon.ToBitmap(), pixels);
            }
            finally
            {
                Native.DestroyIcon(handles[0]);
            }
        }

        static Bitmap FromWindow(IntPtr window, int pixels)
        {
            IntPtr handle = Ask(window, Native.ICON_BIG);
            if (handle == IntPtr.Zero) handle = Ask(window, Native.ICON_SMALL2);
            if (handle == IntPtr.Zero) handle = Native.GetClassLongPtr(window, Native.GCLP_HICON);
            if (handle == IntPtr.Zero) handle = Native.GetClassLongPtr(window, Native.GCLP_HICONSM);
            if (handle == IntPtr.Zero) return null;
            // The window owns this handle; copy the pixels and leave it alone.
            using (var icon = Icon.FromHandle(handle)) return Fit(icon.ToBitmap(), pixels);
        }

        static Bitmap FromAppId(IntPtr window, int pixels)
        {
            Bitmap icon = ShellApps.Icon(ShellApps.AppId(window), pixels);
            return icon == null ? null : Fit(icon, pixels);
        }

        static IntPtr Ask(IntPtr window, int kind)
        {
            IntPtr result;
            Native.SendMessageTimeout(window, Native.WM_GETICON, new IntPtr(kind), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 50, out result);
            return result;
        }

        static Bitmap Fit(Bitmap source, int pixels)
        {
            if (source.Width == pixels && source.Height == pixels) return source;
            var fitted = new Bitmap(pixels, pixels);
            using (var g = Graphics.FromImage(fitted))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, pixels, pixels));
            }
            source.Dispose();
            return fitted;
        }

        void Clear()
        {
            foreach (var extra in halos.Values) extra.Dispose();
            foreach (var extra in rims.Values) extra.Dispose();
            halos.Clear();
            rims.Clear();
            foreach (var icon in byExe.Values) icon.Dispose();
            foreach (var icon in byWindow.Values) icon.Dispose();
            byExe.Clear();
            byWindow.Clear();
            exeOfWindow.Clear();
            missing.Clear();
            glows.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
