// =================================================================================================
// Palette.cs: colours from the wallpaper, and the wallpaper itself.
//
// PaletteBuilder: finds the wallpaper's main hues and builds PaletteColors (accent, surfaces, text,
//   stack hues, the CPU/RAM pair, the bar glow).
// PaletteStore: caches the palette in %LOCALAPPDATA%\deskhud\palette.json.
// YasbColors: writes it as CSS variables to ~/.config/yasb/deskhud_colors.css (imported by styles.css).
// Borders: sends it to komorebi's window borders.
// Wallpapers: each monitor's own picture; a new picture on one screen (StepScreen, the wallpaper
//   button), from wallpapers.folder, keeping Windows' slideshow going.
// With a different picture on each monitor, each left island takes its own screen's palette; YASB and
// the window borders follow the primary screen's.
//
// To add a colour for YASB: add a field to PaletteColors, set it in FromHues, save and load it in
// PaletteStore, add a Var(...) line in YasbColors.Write, then use var(--name) in styles.css.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskHud
{
    /// Colours derived from the wallpaper. One accent; the stack hues tell containers apart.
    sealed class PaletteColors
    {
        public Color Accent, Negative, Surface, SurfaceHigh, Ink, InkSoft, Unfocused, NextWall;
        /// CPU and RAM icons: a soft matching pair from one wallpaper hue.
        public Color Cpu, Ram;
        /// The light rising from behind the bar's lower edge: the accent mixed into black, opaque so the bar
        /// stays solid.
        public Color BarGlow;
        public Color[] Stack = new Color[6];
        /// Bubble tints for wifi, bluetooth, volume, battery: each a slightly different shade of the wallpaper.
        public Color[] Hud = new Color[4];
        public string Source = "";
        public long SourceStamp;

        public Color StackColor(int index) { return Stack[index % Stack.Length]; }

        public static PaletteColors Default()
        {
            return PaletteBuilder.FromHues(new List<HuePeak>(), 225, 0.35, null);
        }
    }

    /// One of the wallpaper's main hues, with how saturated and how common it is.
    struct HuePeak
    {
        public double Hue, Saturation, Weight;
    }

    /// Turns a wallpaper into a palette. To change the look, tune lightness and saturation in FromHues.
    static class PaletteBuilder
    {
        static readonly double[] FillOffsets = { 40, -40, 100, -100, 180, 140, -140 };

        public static PaletteColors Build(string wallpaper, string nextWallpaper)
        {
            List<HuePeak> peaks = Analyse(wallpaper);
            double hue = peaks.Count > 0 ? peaks[0].Hue : 225;
            double saturation = peaks.Count > 0 ? Clamp(peaks[0].Saturation, 0.45, 0.80) : 0.35;

            Color? next = null;
            if (!string.IsNullOrEmpty(nextWallpaper))
            {
                List<HuePeak> nextPeaks = Analyse(nextWallpaper);
                if (nextPeaks.Count > 0) next = Hsl(nextPeaks[0].Hue, Clamp(nextPeaks[0].Saturation, 0.45, 0.80), 0.68);
            }

            var palette = FromHues(peaks, hue, saturation, next);
            palette.Source = wallpaper ?? "";
            palette.SourceStamp = Stamp(wallpaper);
            return palette;
        }

        public static PaletteColors FromHues(List<HuePeak> peaks, double hue, double saturation, Color? nextWall)
        {
            var hues = peaks.Select(p => p.Hue).ToList();
            if (hues.Count == 0) hues.Add(hue);
            foreach (double offset in FillOffsets)
            {
                if (hues.Count >= 6) break;
                double candidate = Wrap(hue + offset);
                if (hues.All(h => HueDistance(h, candidate) >= 30)) hues.Add(candidate);
            }

            var palette = new PaletteColors
            {
                Accent = Hsl(hue, saturation, 0.70),
                Negative = Hsl(hue + 180, saturation, 0.66),
                Surface = Hsl(hue, 0.16, 0.085),
                SurfaceHigh = Hsl(hue, 0.16, 0.13),
                Ink = Hsl(hue, 0.14, 0.93),
                InkSoft = Hsl(hue, 0.10, 0.64),
                Unfocused = Hsl(hue, 0.12, 0.22),
            };
            for (int i = 0; i < palette.Stack.Length; i++) palette.Stack[i] = Hsl(hues[i % hues.Count], 0.58, 0.68);
            palette.Stack[0] = palette.Accent;
            for (int i = 0; i < palette.Hud.Length; i++) palette.Hud[i] = Hsl(hues[(i + 1) % hues.Count], 0.28, 0.12);
            // A calm matching pair that changes family with every wallpaper: the wallpaper's next-strongest hue
            // after the accent for the CPU, a close neighbour of it for the RAM, both softened.
            double pairHue = hues.Count > 1 ? hues[1] : Wrap(hue + 120);
            palette.Cpu = Hsl(pairHue, 0.45, 0.72);
            palette.Ram = Hsl(pairHue + 28, 0.45, 0.72);
            const double glow = 0.32; // weaker than the button glows (0.45), so those still read as accents
            palette.BarGlow = Color.FromArgb((int)(palette.Accent.R * glow), (int)(palette.Accent.G * glow), (int)(palette.Accent.B * glow));
            palette.NextWall = nextWall ?? palette.Accent;
            return palette;
        }

        public static long Stamp(string path)
        {
            try { return string.IsNullOrEmpty(path) || !File.Exists(path) ? 0 : File.GetLastWriteTimeUtc(path).Ticks; }
            catch (IOException) { return 0; }
        }

        /// Up to six well-separated hues, strongest first, weighted towards saturated and bright pixels.
        static List<HuePeak> Analyse(string path)
        {
            var peaks = new List<HuePeak>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return peaks;

            const int W = 64, H = 36, Bins = 24;
            var pixels = new int[W * H];
            try
            {
                if (!ReadGdi(path, W, H, pixels)) ReadWic(path, W, H, pixels);
            }
            catch (Exception ex)
            {
                Log.Error("wallpaper " + path, ex);
                return peaks;
            }

            var weight = new double[Bins];
            var saturation = new double[Bins];
            var x = new double[Bins];
            var y = new double[Bins];
            foreach (int argb in pixels)
            {
                double h, s, v;
                ToHsv(Color.FromArgb(argb), out h, out s, out v);
                if (s < 0.18 || v < 0.18) continue;
                double w = s * s * v;
                int bin = (int)(h / 15) % Bins;
                weight[bin] += w;
                saturation[bin] += s * w;
                x[bin] += Math.Cos(h * Math.PI / 180) * w;
                y[bin] += Math.Sin(h * Math.PI / 180) * w;
            }

            var smooth = new double[Bins];
            for (int i = 0; i < Bins; i++)
                smooth[i] = weight[(i + Bins - 1) % Bins] * 0.25 + weight[i] * 0.5 + weight[(i + 1) % Bins] * 0.25;

            foreach (int i in Enumerable.Range(0, Bins).OrderByDescending(i => smooth[i]))
            {
                if (smooth[i] <= 0 || peaks.Count == 6) break;
                double hue = weight[i] > 0 ? Wrap(Math.Atan2(y[i], x[i]) * 180 / Math.PI) : i * 15 + 7.5;
                if (peaks.Any(p => HueDistance(p.Hue, hue) < 35)) continue;
                peaks.Add(new HuePeak
                {
                    Hue = hue,
                    Saturation = weight[i] > 0 ? saturation[i] / weight[i] : 0.5,
                    Weight = smooth[i],
                });
            }
            return peaks;
        }

        /// The picture scaled to width x height ARGB pixels through GDI+. False when GDI+ does not know
        /// the format ("Parameter is not valid"): it cannot read WebP, which Windows' slideshow shows.
        static bool ReadGdi(string path, int width, int height, int[] pixels)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            {
                Image image;
                try { image = Image.FromStream(stream, false, false); }
                catch (ArgumentException) { return false; }
                using (image)
                using (var small = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(image, new Rectangle(0, 0, width, height));
                    }
                    var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    small.UnlockBits(data);
                }
            }
            return true;
        }

        /// The same through WIC (WPF's decoders), which reads every format Windows has a codec for.
        /// Bgra32 in memory is GDI+'s 32bppArgb as an int.
        static void ReadWic(string path, int width, int height, int[] pixels)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = width;
            image.DecodePixelHeight = height;
            image.EndInit();
            new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                .CopyPixels(pixels, width * 4, 0);
        }

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            h = Wrap(h);
        }

        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double v;
            ToHsv(c, out h, out s, out v);
            l = (max + min) / 2;
            s = d <= 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        }

        public static Color Hsl(double h, double s, double l)
        {
            h = Wrap(h) / 360;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            return Color.FromArgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
        }

        static int Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double value = t < 1.0 / 6 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                : p;
            return (int)Math.Round(Clamp(value, 0, 1) * 255);
        }

        static double Wrap(double hue) { return ((hue % 360) + 360) % 360; }
        static double HueDistance(double a, double b) { double d = Math.Abs(a - b) % 360; return Math.Min(d, 360 - d); }
        static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
    }

    /// The last palette, so the bar starts in the right colours before the wallpaper is re-read.
    static class PaletteStore
    {
        public static void Save(PaletteColors p)
        {
            var map = new Dictionary<string, object>
            {
                { "source", p.Source }, { "stamp", p.SourceStamp.ToString(CultureInfo.InvariantCulture) },
                { "accent", Hex(p.Accent) }, { "negative", Hex(p.Negative) }, { "surface", Hex(p.Surface) },
                { "surface_high", Hex(p.SurfaceHigh) }, { "ink", Hex(p.Ink) }, { "ink_soft", Hex(p.InkSoft) },
                { "unfocused", Hex(p.Unfocused) }, { "next_wall", Hex(p.NextWall) },
                { "cpu", Hex(p.Cpu) }, { "ram", Hex(p.Ram) }, { "bar_glow", Hex(p.BarGlow) },
                { "stack", p.Stack.Select(Hex).ToArray() }, { "hud", p.Hud.Select(Hex).ToArray() },
            };
            try { Paths.WriteAtomic(Paths.PaletteCache, Json.Stringify(map)); }
            catch (IOException ex) { Log.Error("palette cache", ex); }
        }

        public static PaletteColors Load()
        {
            try
            {
                if (!File.Exists(Paths.PaletteCache)) return null;
                object root = Json.Parse(File.ReadAllText(Paths.PaletteCache));
                if (Json.Str(root, "bar_glow", null) == null) return null; // written by an older deskhud: rebuild
                var p = new PaletteColors
                {
                    Source = Json.Str(root, "source", ""),
                    SourceStamp = long.Parse(Json.Str(root, "stamp", "0"), CultureInfo.InvariantCulture),
                    Accent = Parse(Json.Str(root, "accent", null)),
                    Negative = Parse(Json.Str(root, "negative", null)),
                    Surface = Parse(Json.Str(root, "surface", null)),
                    SurfaceHigh = Parse(Json.Str(root, "surface_high", null)),
                    Ink = Parse(Json.Str(root, "ink", null)),
                    InkSoft = Parse(Json.Str(root, "ink_soft", null)),
                    Unfocused = Parse(Json.Str(root, "unfocused", null)),
                    NextWall = Parse(Json.Str(root, "next_wall", null)),
                    Cpu = Parse(Json.Str(root, "cpu", null)),
                    Ram = Parse(Json.Str(root, "ram", null)),
                    BarGlow = Parse(Json.Str(root, "bar_glow", null)),
                    Stack = Json.Arr(root, "stack").Select(v => Parse(v as string)).ToArray(),
                    Hud = Json.Arr(root, "hud").Select(v => Parse(v as string)).ToArray(),
                };
                return p.Stack.Length == 6 && p.Hud.Length == 4 ? p : null;
            }
            catch (Exception ex)
            {
                Log.Error("palette cache", ex);
                return null;
            }
        }

        public static string Hex(Color c) { return string.Format("#{0:x2}{1:x2}{2:x2}", c.R, c.G, c.B); }
        static Color Parse(string hex) { return ColorTranslator.FromHtml(hex); }
    }

    /// Writes the palette, and the bubble sizes for the current posture, as CSS variables that the YASB
    /// stylesheet imports. YASB reloads it on change, without restarting the bar.
    static class YasbColors
    {
        // Laptop / tablet: bubble, the bubbles inside a group, icons, small icons, text (px). The bar
        // height stays put (config.yaml); StripWindow.BubbleH and TabletZoom follow --bubble-h + 2.
        static readonly int[] Laptop = { 26, 22, 17, 15, 14 };
        static readonly int[] Tablet = { 32, 28, 20, 18, 16 };

        /// The media island's glow before anything has played: the audio violet from styles.css.
        static readonly Color NoArt = Color.FromArgb(0xc9, 0xa7, 0xff);

        /// `media` is the colour of the album art playing (Yasb.cs), or null for none yet.
        public static void Write(PaletteColors p, bool tablet, Color? media)
        {
            var css = new StringBuilder();
            css.AppendLine("/* Generated by deskhud from the current wallpaper and posture. Edits here are overwritten. */");
            css.AppendLine(":root {");
            int[] size = tablet ? Tablet : Laptop;
            css.AppendFormat("    --bubble-h: {0}px;\n    --inner-h: {1}px;\n    --icon-size: {2}px;\n    --icon-small: {3}px;\n    --text-size: {4}px;\n",
                size[0], size[1], size[2], size[3], size[4]);
            Var(css, "accent", p.Accent);
            Var(css, "negative", p.Negative);
            Var(css, "surface", p.Surface);
            Var(css, "surface-high", p.SurfaceHigh);
            Var(css, "ink", p.Ink);
            Var(css, "ink-soft", p.InkSoft);
            Var(css, "next-wall", p.NextWall);
            Var(css, "cpu", p.Cpu);
            Var(css, "ram", p.Ram);
            Var(css, "bar-glow", p.BarGlow);
            Var(css, "media", media ?? NoArt);
            for (int i = 0; i < p.Stack.Length; i++) Var(css, "stack-" + (i + 1), p.Stack[i]);
            string[] hud = { "wifi", "bluetooth", "volume", "battery" };
            for (int i = 0; i < hud.Length; i++) Var(css, "hud-" + hud[i], p.Hud[i]);
            css.AppendLine("}");

            string text = css.ToString();
            try
            {
                if (File.Exists(Paths.YasbColors) && File.ReadAllText(Paths.YasbColors) == text) return;
                Paths.WriteAtomic(Paths.YasbColors, text);
            }
            catch (IOException ex) { Log.Error("yasb colours", ex); }
        }

        static void Var(StringBuilder css, string name, Color c)
        {
            css.AppendFormat("    --{0}: {1};\n", name, PaletteStore.Hex(c));
            css.AppendFormat("    --{0}-rgb: {1}, {2}, {3};\n", name, c.R, c.G, c.B);
        }
    }

    /// komorebi window borders follow the palette; paused flips the focused colours to the negative.
    static class Borders
    {
        public static void Apply(PaletteColors p, bool paused)
        {
            Set("single", paused ? p.Negative : p.Accent);
            Set("stack", paused ? p.Negative : p.Stack[1]);
            Set("monocle", paused ? p.Negative : p.Stack[2]);
            Set("floating", p.Stack[3]);
            Set("unfocused", p.Unfocused);
        }

        static void Set(string kind, Color c)
        {
            Komorebic.Run("border-colour", c.R.ToString(), c.G.ToString(), c.B.ToString(), "--window-kind", kind);
        }
    }

    /// The current wallpaper, and moving to the next or previous one.
    static class Wallpapers
    {
        static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".jfif" };
        // What each screen showed before, newest last, by monitor id: "previous" on that screen. Only
        // the command queue touches it.
        static readonly Dictionary<string, List<string>> earlier = new Dictionary<string, List<string>>();
        static readonly Random random = new Random();

        public static string Current()
        {
            var path = new StringBuilder(1024);
            return Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0) ? path.ToString() : "";
        }

        /// Each monitor's own picture, by the monitor's rectangle: Windows can show a different one on
        /// every screen (Settings > Personalization > Background, right-click a picture). Monitors it
        /// cannot answer for are left out; they take the main palette.
        public static Dictionary<Rectangle, string> PerMonitor()
        {
            var result = new Dictionary<Rectangle, string>();
            IDesktopWallpaper shell = null;
            try
            {
                shell = Shell();
                uint count = shell.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    try
                    {
                        string id = shell.GetMonitorDevicePathAt(i);
                        Native.RECT r;
                        shell.GetMonitorRECT(id, out r);
                        string path = shell.GetWallpaper(id);
                        if (r.Right > r.Left && !string.IsNullOrEmpty(path) && File.Exists(path))
                            result[Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)] = path;
                    }
                    catch (COMException) { } // a monitor that is listed but not attached
                }
            }
            catch (COMException ex)
            {
                Log.Error("wallpaper per monitor", ex);
            }
            finally
            {
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
            return result;
        }

        public static List<string> InFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new List<string>();
            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// The image `step` places away from `current` in the folder, wrapping around.
        public static string Neighbour(Settings settings, string current, int step)
        {
            var images = InFolder(settings.WallpaperFolderExpanded);
            if (images.Count == 0) return null;
            int at = images.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
            int next = at < 0 ? (step > 0 ? 0 : images.Count - 1) : ((at + step) % images.Count + images.Count) % images.Count;
            return images[next];
        }

        /// True while Windows' own slideshow (Settings > Personalization > Background) picks the wallpaper.
        public static bool SlideshowRunning()
        {
            IDesktopWallpaper shell = null;
            try
            {
                shell = Shell();
                return (shell.GetStatus() & DSS_SLIDESHOW) != 0;
            }
            catch (COMException)
            {
                return false;
            }
            finally
            {
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        /// With Windows' slideshow on, asks it for its next image (it may shuffle); otherwise steps through
        /// wallpapers.folder in name order.
        public static void Step(Settings settings, int step)
        {
            if (SlideshowRunning())
            {
                AdvanceSlideshow(step);
                return;
            }
            string target = Neighbour(settings, Current(), step);
            if (target == null)
            {
                Log.Write("wallpaper: set wallpapers.folder in settings.json to a folder with images");
                return;
            }
            if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, target, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
                Log.Write("wallpaper: could not set " + target);
        }

        /// The wallpaper button and Win+Shift+W: a new picture on one screen (physical pixels), the others
        /// left alone. Windows' slideshow only advances whichever screen is due next (asked for a given
        /// one it says "not implemented"), so the picture comes from wallpapers.folder: at random while
        /// the slideshow shuffles, else the next by name; back is what that screen showed before. Setting
        /// one screen's picture stops the slideshow, so it is set going again on the same pictures.
        public static void StepScreen(Settings settings, Rectangle screen, int step)
        {
            IDesktopWallpaper shell = null;
            IntPtr slideshow = IntPtr.Zero;
            bool stopped = false;
            int options = 0;
            uint tick = 0;
            try
            {
                shell = Shell();
                string id = MonitorAt(shell, screen);
                if (id == null)
                {
                    Step(settings, step); // Windows cannot say which monitor that is
                    return;
                }
                string current = shell.GetWallpaper(id) ?? "";
                if ((shell.GetStatus() & DSS_SLIDESHOW) != 0)
                {
                    shell.GetSlideshowOptions(out options, out tick);
                    slideshow = shell.GetSlideshow();
                }
                bool shuffle = slideshow != IntPtr.Zero && (options & DSO_SHUFFLEIMAGES) != 0;
                string target = step > 0 ? Pick(settings, shell, id, current, shuffle) : Back(settings, id, current, shuffle);
                if (target == null)
                {
                    Log.Write("wallpaper: no " + (step > 0 ? "other" : "earlier") + " picture for this screen (wallpapers.folder)");
                    return;
                }
                if (step > 0) Earlier(id).Add(current);
                stopped = slideshow != IntPtr.Zero;
                shell.SetWallpaper(id, target);
            }
            catch (Exception ex) // COMException, or a picture Windows cannot show
            {
                Log.Error("wallpaper", ex);
            }
            finally
            {
                if (stopped)
                {
                    try
                    {
                        Resume(shell, slideshow, options, tick);
                    }
                    catch (COMException ex)
                    {
                        Log.Error("wallpaper: restarting the slideshow", ex);
                    }
                }
                if (slideshow != IntPtr.Zero) Marshal.Release(slideshow);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        /// Sets the slideshow going again. Windows stops it a moment after SetWallpaper returns (about
        /// 200 ms); set going before that, it also puts the screen's old picture back.
        static void Resume(IDesktopWallpaper shell, IntPtr slideshow, int options, uint tick)
        {
            for (int i = 0; i < 30 && (shell.GetStatus() & DSS_SLIDESHOW) != 0; i++) Thread.Sleep(100);
            shell.SetSlideshow(slideshow);
            shell.SetSlideshowOptions(options, tick);
        }

        /// The monitor id IDesktopWallpaper uses for the screen; null when none of its monitors is there.
        static string MonitorAt(IDesktopWallpaper shell, Rectangle screen)
        {
            var centre = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            uint count = shell.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    string id = shell.GetMonitorDevicePathAt(i);
                    Native.RECT r;
                    shell.GetMonitorRECT(id, out r);
                    if (Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(centre)) return id;
                }
                catch (COMException) { } // a monitor that is listed but not attached
            }
            return null;
        }

        /// The next picture for one screen: while the slideshow shuffles, a random one that no screen is
        /// showing and this one has not shown lately; otherwise the one after it by name.
        static string Pick(Settings settings, IDesktopWallpaper shell, string id, string current, bool shuffle)
        {
            if (!shuffle) return Neighbour(settings, current, 1);
            List<string> images = InFolder(settings.WallpaperFolderExpanded);
            var showing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint count = shell.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try { showing.Add(shell.GetWallpaper(shell.GetMonitorDevicePathAt(i)) ?? ""); }
                catch (COMException) { }
            }
            List<string> recent = Earlier(id);
            var lately = new HashSet<string>(recent.Skip(recent.Count - Math.Min(recent.Count, images.Count / 2)), StringComparer.OrdinalIgnoreCase);
            var fresh = images.Where(p => !showing.Contains(p) && !lately.Contains(p)).ToList();
            if (fresh.Count == 0) fresh = images.Where(p => !string.Equals(p, current, StringComparison.OrdinalIgnoreCase)).ToList();
            return fresh.Count == 0 ? null : fresh[random.Next(fresh.Count)];
        }

        /// The picture this screen showed before; without one (and not shuffling), the one before by name.
        static string Back(Settings settings, string id, string current, bool shuffle)
        {
            List<string> before = Earlier(id);
            while (before.Count > 0)
            {
                string picture = before[before.Count - 1];
                before.RemoveAt(before.Count - 1);
                if (File.Exists(picture) && !string.Equals(picture, current, StringComparison.OrdinalIgnoreCase)) return picture;
            }
            return shuffle ? null : Neighbour(settings, current, -1);
        }

        static List<string> Earlier(string id)
        {
            List<string> list;
            if (!earlier.TryGetValue(id, out list)) earlier[id] = list = new List<string>();
            if (list.Count > 64) list.RemoveRange(0, list.Count - 64);
            return list;
        }

        static void AdvanceSlideshow(int step)
        {
            IDesktopWallpaper shell = null;
            try
            {
                shell = Shell();
                shell.AdvanceSlideshow(null, step > 0 ? DSD_FORWARD : DSD_BACKWARD); // null: the monitor due to change next
            }
            catch (Exception ex) // COMException, or NotImplementedException when Windows cannot go back
            {
                Log.Write("wallpaper: Windows' slideshow did not move " + (step > 0 ? "forward" : "back") + " (" + ex.Message + ")");
            }
            finally
            {
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        static readonly Guid DesktopWallpaperClass = new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
        const int DSS_SLIDESHOW = 0x02, DSD_FORWARD = 0, DSD_BACKWARD = 1, DSO_SHUFFLEIMAGES = 0x01;

        static IDesktopWallpaper Shell()
        {
            return (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(DesktopWallpaperClass));
        }
    }

    /// The shell's wallpaper object. Every method up to the last one used is declared, in vtable order.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out Native.RECT rect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(int options, uint tick);
        void GetSlideshowOptions(out int options, out uint tick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorId, int direction);
        int GetStatus();
    }
}
