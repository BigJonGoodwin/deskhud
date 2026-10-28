// =================================================================================================
// Menus.cs: the left island's right-click menus, drawn to match the bar.
//
// IslandMenu builds one menu: near-black with the bar's glow rising at the bottom, the wallpaper's
// accent lighting the hovered item, glyphs coloured by what they do, rounded corners and an accent
// border (Windows 11 draws those two). It also shows the menu so it closes like any other, although
// the strip itself never takes focus.
//
// The menus themselves (what each holds) are StripWindow.Show*Menu. Glyphs are Segoe Fluent Icons
// code points, listed in Glyphs below.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeskHud
{
    /// Segoe Fluent Icons code points used in the menus and on the island.
    static class Glyphs
    {
        public const char Settings = '', Wallpaper = '', Refresh = '', Bar = '', Power = '';
        public const char Keyboard = '', NewWindow = '', Minimize = '', Restore = '', Close = '';
        public const char Float = '', Tile = '', Monocle = '', Unstack = '', Workspaces = '', Folder = '';

        public static readonly string Font = Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        static bool Installed(string family)
        {
            using (var fonts = new InstalledFontCollection())
                foreach (FontFamily f in fonts.Families)
                    if (f.Name == family) return true;
            return false;
        }
    }

    /// One right-click menu. Build it with Header / Item / Submenu / Child / Separator, then Show; it
    /// disposes itself (and the images it made) once closed.
    sealed class IslandMenu
    {
        readonly ContextMenuStrip menu;
        readonly GlowRenderer renderer;
        readonly PaletteColors colors;
        readonly float scale;
        readonly int imagePixels;
        readonly Padding itemPadding;
        readonly Font font, bold;
        // Images and fonts this menu made; WinForms leaves them to us.
        readonly List<IDisposable> owned = new List<IDisposable>();

        /// `touch` gives the items more room for fingers.
        public IslandMenu(PaletteColors colors, float scale, bool touch)
        {
            this.colors = colors;
            this.scale = scale;
            imagePixels = (int)Math.Round(16 * scale);
            int vertical = (int)Math.Round((touch ? 7 : 3) * scale);
            itemPadding = new Padding(0, vertical, (int)Math.Round(6 * scale), vertical);
            renderer = new GlowRenderer(colors, scale);
            font = Keep(new Font("Segoe UI", 9.75f));
            bold = Keep(new Font(font, FontStyle.Bold));
            menu = new ContextMenuStrip();
            Style(menu);
        }

        void Style(ToolStripDropDownMenu dropDown)
        {
            dropDown.Renderer = renderer;
            dropDown.Font = font;
            dropDown.ShowImageMargin = true;
            dropDown.ShowCheckMargin = false;
            dropDown.ImageScalingSize = new Size(imagePixels, imagePixels);
            dropDown.Padding = new Padding(0, (int)Math.Round(5 * scale), 0, (int)Math.Round(5 * scale));
            dropDown.Opened += delegate { Decorate(dropDown.Handle); };
        }

        /// The first line: the thing the menu is about (an app's icon and title), in the accent.
        public ToolStripMenuItem Header(string text, Image icon, Action action)
        {
            // A copy: the icon cache may let go of the original while the menu is open.
            var item = Add(menu.Items, text, icon == null ? null : Keep(new Bitmap(icon, imagePixels, imagePixels)), action);
            item.Font = bold;
            item.Tag = GlowRenderer.HeaderTag;
            return item;
        }

        public ToolStripMenuItem Item(string text, char glyph, Color colour, Action action)
        {
            return Add(menu.Items, text, Glyph(glyph, colour), action);
        }

        public ToolStripMenuItem Submenu(string text, char glyph, Color colour)
        {
            var item = Add(menu.Items, text, Glyph(glyph, colour), null);
            Style((ToolStripDropDownMenu)item.DropDown);
            return item;
        }

        /// An entry in a submenu; `current` marks where things are now (the window's workspace, the layout).
        public ToolStripMenuItem Child(ToolStripMenuItem parent, string text, Image image, bool current, Action action)
        {
            var item = Add(parent.DropDownItems, text, image, current ? null : action);
            if (current)
            {
                item.Tag = GlowRenderer.CurrentTag;
                item.Font = bold;
            }
            return item;
        }

        public void Separator()
        {
            menu.Items.Add(new ToolStripSeparator { Margin = new Padding(0, (int)Math.Round(3 * scale), 0, (int)Math.Round(3 * scale)) });
        }

        ToolStripMenuItem Add(ToolStripItemCollection into, string text, Image image, Action action)
        {
            var item = new ToolStripMenuItem(text, image) { Padding = itemPadding, ImageScaling = ToolStripItemImageScaling.SizeToFit };
            if (action != null) item.Click += delegate { action(); };
            into.Add(item);
            return item;
        }

        /// A Segoe Fluent Icons glyph, `colour`ed by what the item does.
        public Bitmap Glyph(char glyph, Color colour)
        {
            var bitmap = new Bitmap(imagePixels, imagePixels, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font(Glyphs.Font, imagePixels * 0.8f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(colour))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, imagePixels, imagePixels), format);
            }
            return Keep(bitmap);
        }

        /// A glowing dot, for workspaces.
        public Bitmap Dot(Color colour)
        {
            var bitmap = new Bitmap(imagePixels, imagePixels, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float c = imagePixels / 2f, glow = imagePixels * 0.46f, core = imagePixels * 0.2f;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(c - glow, c - glow, 2 * glow, 2 * glow);
                    using (var light = new PathGradientBrush(path) { CenterColor = Color.FromArgb(150, colour), SurroundColors = new[] { Color.FromArgb(0, colour) } })
                        g.FillPath(light, path);
                }
                using (var brush = new SolidBrush(colour)) g.FillEllipse(brush, c - core, c - core, 2 * core, 2 * core);
            }
            return Keep(bitmap);
        }

        T Keep<T>(T thing) where T : IDisposable
        {
            owned.Add(thing);
            return thing;
        }

        /// Windows 11 rounds the menu and draws its border in the accent.
        void Decorate(IntPtr handle)
        {
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            Color edge = Blend(colors.Accent, Color.Black, 0.45f);
            int colorRef = edge.R | (edge.G << 8) | (edge.B << 16);
            Native.DwmSetWindowAttribute(handle, Native.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }

        /// Shows the menu at `at` (in `owner`'s client pixels). The strip never activates, and a menu whose
        /// app is not in the foreground does not close when you click elsewhere, so the menu takes the
        /// foreground for as long as it is open and hands it back if it closes without a pick.
        public void Show(Control owner, Point at, Action closed)
        {
            IntPtr previous = Native.GetForegroundWindow();
            menu.Opened += delegate { Native.SetForegroundWindow(menu.Handle); };
            menu.Closed += (s, e) =>
            {
                if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked && previous != IntPtr.Zero && IsOurs(Native.GetForegroundWindow()))
                    Native.SetForegroundWindow(previous);
                closed();
                owner.BeginInvoke(new Action(Dispose));
            };
            menu.Show(owner, at);
        }

        static readonly uint OwnProcess = (uint)Process.GetCurrentProcess().Id;

        static bool IsOurs(IntPtr window)
        {
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            return pid == OwnProcess;
        }

        void Dispose()
        {
            menu.Dispose();
            foreach (var thing in owned) thing.Dispose();
            owned.Clear();
        }

        internal static Color Blend(Color a, Color b, float amount)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }
    }

    /// Paints the menus: a dark body with the bar's glow rising at the bottom, the hovered item lit in the
    /// accent with a bright notch on its left, the header and current entries in the accent.
    sealed class GlowRenderer : ToolStripProfessionalRenderer
    {
        public const string HeaderTag = "header", CurrentTag = "current";
        static readonly Color Body = Color.FromArgb(0x0d, 0x0e, 0x11);

        readonly PaletteColors colors;
        readonly float scale;

        public GlowRenderer(PaletteColors colors, float scale) : base(new Table(colors))
        {
            this.colors = colors;
            this.scale = scale;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Rectangle r = e.AffectedBounds;
            using (var brush = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Body, Body, 90f))
            {
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Body, Body, IslandMenu.Blend(Body, colors.BarGlow, 0.85f) },
                    Positions = new[] { 0f, 0.55f, 1f },
                };
                e.Graphics.FillRectangle(brush, r);
            }
        }

        // Windows 11 draws the border, rounded, in the accent (IslandMenu.Decorate).
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            float inset = 5 * scale, radius = 6 * scale;
            var r = new RectangleF(inset, 1, e.Item.Width - 2 * inset, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, radius))
            {
                using (var fill = new LinearGradientBrush(r, Color.FromArgb(78, colors.Accent), Color.FromArgb(16, colors.Accent), 0f))
                    e.Graphics.FillPath(fill, path);
                using (var edge = new Pen(Color.FromArgb(110, colors.Accent), Math.Max(1f, scale * 0.8f)))
                    e.Graphics.DrawPath(edge, path);
            }
            // A lit notch on the left, with a little light around it.
            float notchH = r.Height * 0.5f, notchW = 2.5f * scale, x = r.X + 3 * scale, y = r.Y + (r.Height - notchH) / 2;
            using (var halo = Round(new RectangleF(x - 2 * scale, y - 2 * scale, notchW + 4 * scale, notchH + 4 * scale), 3 * scale))
            using (var light = new SolidBrush(Color.FromArgb(60, colors.Accent)))
                e.Graphics.FillPath(light, halo);
            using (var notch = Round(new RectangleF(x, y, notchW, notchH), notchW / 2))
            using (var bright = new SolidBrush(colors.Accent))
                e.Graphics.FillPath(bright, notch);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            string tag = e.Item.Tag as string;
            e.TextColor = !e.Item.Enabled ? colors.InkSoft : tag == HeaderTag || tag == CurrentTag ? colors.Accent : colors.Ink;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Selected ? colors.Accent : colors.InkSoft;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2, inset = (int)Math.Round(12 * scale);
            using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255))) e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// Colours the base renderer still asks for (submenu frames, separators it draws itself).
        sealed class Table : ProfessionalColorTable
        {
            readonly PaletteColors p;
            public Table(PaletteColors palette) { p = palette; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return Body; } }
            public override Color MenuBorder { get { return Color.FromArgb(0x2b, 0x2c, 0x2e); } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuItemSelected { get { return p.SurfaceHigh; } }
            public override Color ImageMarginGradientBegin { get { return Body; } }
            public override Color ImageMarginGradientMiddle { get { return Body; } }
            public override Color ImageMarginGradientEnd { get { return Body; } }
            public override Color SeparatorDark { get { return Color.FromArgb(0x2b, 0x2c, 0x2e); } }
            public override Color SeparatorLight { get { return Body; } }
        }
    }
}
