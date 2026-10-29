// =================================================================================================
// Tabs.cs: the tab strips over tabbed containers ("stacked tabs").
//
// GlazeWM's tabbed splits (the deskhud fork; komorebi stacks have none of this) show one window at a
// time. deskhud draws a thin bar along the top of each one on show, one tab per window: its icon and
// title, the active tab lit in the palette's accent. Click a tab to bring that window to the front,
// middle-click to close it. Drawn like the islands (Strip.cs): a per-pixel-alpha layered window that
// never takes focus.
//
// TabStrips: one TabStripWindow per tabbed container on a workspace that is showing; made, moved and
//   dropped as snapshots arrive. It listens for title changes (GlazeWM sends no event for those) only
//   while some strip is up. TabStripWindow: one strip; it sits just above its active window in the
//   z-order (Restack), not topmost, so windows and menus in front of that window stay in front.
//
// To change the look: the constants at the top of TabStripWindow; the height is `tabs.height` in
// settings.json, which the GlazeWM fork should keep clear at the top of tabbed containers.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace DeskHud
{
    /// Every tab strip on screen. UI thread.
    sealed class TabStrips : IDisposable
    {
        readonly Func<string[], bool> dispatch;
        readonly IconCache icons = new IconCache();
        readonly Dictionary<string, TabStripWindow> strips = new Dictionary<string, TabStripWindow>();
        readonly Native.WinEventProc onNameChange;
        Settings settings;
        PaletteColors colors;
        IntPtr nameHook;

        public TabStrips(Settings settings, PaletteColors colors, Func<string[], bool> dispatch)
        {
            this.settings = settings;
            this.colors = colors;
            this.dispatch = dispatch;
            onNameChange = OnNameChange;
        }

        /// Puts up a strip for each tabbed container on a workspace that is showing, and drops the rest.
        public void SetSnapshot(Snapshot snapshot)
        {
            var wanted = new Dictionary<string, ContainerView>();
            if (snapshot != null)
                foreach (var monitor in snapshot.Numbered())
                {
                    WorkspaceView ws = monitor.Current;
                    // A fullscreen window covers the workspace, tabs and all.
                    if (ws == null || ws.Monocle) continue;
                    foreach (var c in ws.Containers)
                        if (c.Tabbed && c.Id != null && c.Windows.Count > 0 && !c.Bounds.IsEmpty) wanted[c.Id] = c;
                }

            foreach (string gone in strips.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                strips[gone].Close();
                strips.Remove(gone);
            }
            foreach (var pair in wanted)
            {
                TabStripWindow strip;
                if (!strips.TryGetValue(pair.Key, out strip))
                {
                    strip = new TabStripWindow(icons, dispatch);
                    strips[pair.Key] = strip;
                }
                strip.Show(pair.Value, colors, settings.TabsHeight);
            }
            icons.Retain(wanted.Values.SelectMany(c => c.Windows).Select(w => w.Hwnd).ToList());
            WatchTitles();
        }

        public void SetPalette(PaletteColors value)
        {
            colors = value;
            foreach (var strip in strips.Values) strip.SetPalette(value);
        }

        public void SetSettings(Settings value)
        {
            settings = value;
            foreach (var strip in strips.Values) strip.SetHeight(value.TabsHeight);
        }

        /// The z-order moves whenever focus does; keep each strip just above its window.
        public void Restack()
        {
            foreach (var strip in strips.Values) strip.Restack();
        }

        /// Title changes anywhere raise this event, so the hook is only on while a strip is up.
        void WatchTitles()
        {
            bool wanted = strips.Count > 0;
            if (wanted == (nameHook != IntPtr.Zero)) return;
            if (wanted)
            {
                nameHook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, onNameChange,
                    0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
                return;
            }
            Native.UnhookWinEvent(nameHook);
            nameHook = IntPtr.Zero;
        }

        void OnNameChange(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            foreach (var strip in strips.Values)
                if (strip.Holds(hwnd.ToInt64())) strip.Render();
        }

        public void Dispose()
        {
            if (nameHook != IntPtr.Zero) Native.UnhookWinEvent(nameHook);
            foreach (var strip in strips.Values) strip.Close();
            strips.Clear();
            icons.Dispose();
        }
    }

    /// One tabbed container's strip: a tab per window, the active one lit.
    sealed class TabStripWindow : Form
    {
        // Logical pixels, times the monitor's scale. Pad: around each tab. Icon: app icon size.
        // Radius: tab corners. Underline: the active tab's accent line.
        const float Pad = 2, IconSize = 16, IconGap = 6, TextPad = 8, Radius = 5, Underline = 2, FontSize = 12;

        readonly IconCache icons;
        readonly Func<string[], bool> dispatch;
        ContainerView container;
        PaletteColors colors;
        int height = 26;
        float scale = 1;
        int hover = -1;

        public TabStripWindow(IconCache icons, Func<string[], bool> dispatch)
        {
            this.icons = icons;
            this.dispatch = dispatch;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "deskhud tabs";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        public bool Holds(long hwnd) { return container != null && container.Windows.Any(w => w.Hwnd == hwnd); }

        public void Show(ContainerView value, PaletteColors palette, int logicalHeight)
        {
            container = value;
            colors = palette;
            height = logicalHeight;
            if (!Visible) Show();
            Render();
            Restack();
        }

        public void SetPalette(PaletteColors value)
        {
            colors = value;
            Render();
        }

        public void SetHeight(int value)
        {
            height = value;
            Render();
        }

        float S(float logical) { return logical * scale; }

        int TabAt(int x)
        {
            if (container == null || container.Windows.Count == 0 || Width <= 0) return -1;
            return Math.Max(0, Math.Min(container.Windows.Count - 1, x * container.Windows.Count / Width));
        }

        public void Render()
        {
            if (!IsHandleCreated || container == null || colors == null) return;
            Rectangle area = container.Bounds;
            float monitorScale = Native.ScaleAt(area);
            scale = monitorScale > 0 ? monitorScale : 1;
            int w = Math.Max(1, area.Width), h = Math.Max(1, (int)Math.Round(S(height)));
            using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // ClearType needs an opaque background to blend with; the strip's corners are not.
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Draw(g, w, h);
                Push(bitmap, area.Location);
            }
        }

        void Draw(Graphics g, int w, int h)
        {
            using (var back = new SolidBrush(Color.FromArgb(235, 16, 17, 19)))
            using (var path = RoundRect(new RectangleF(0, 0, w, h), S(Radius + Pad)))
                g.FillPath(back, path);

            int count = container.Windows.Count;
            float tabW = (float)w / count;
            using (var font = new Font(SystemFonts.MessageBoxFont.FontFamily, S(FontSize), GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
                for (int i = 0; i < count; i++)
                {
                    WindowView window = container.Windows[i];
                    bool active = i == container.Focused;
                    var tab = new RectangleF(i * tabW + S(Pad), S(Pad), tabW - 2 * S(Pad), h - 2 * S(Pad));
                    if (active || i == hover)
                        using (var fill = new SolidBrush(Color.FromArgb(active ? 70 : 28, active ? colors.Accent : colors.Ink)))
                        using (var path = RoundRect(tab, S(Radius)))
                            g.FillPath(fill, path);
                    if (active)
                        using (var line = new SolidBrush(colors.Accent))
                            g.FillRectangle(line, tab.X + S(Radius), tab.Bottom - S(Underline), tab.Width - 2 * S(Radius), S(Underline));

                    int iconPx = (int)Math.Round(S(IconSize));
                    float x = tab.X + S(TextPad);
                    Bitmap icon = icons.Get(window.Hwnd, iconPx);
                    if (icon != null) g.DrawImage(icon, new Rectangle((int)x, (int)(tab.Y + (tab.Height - iconPx) / 2), iconPx, iconPx));
                    x += iconPx + S(IconGap);

                    string title = Native.WindowTitle(new IntPtr(window.Hwnd));
                    if (string.IsNullOrEmpty(title)) title = window.Title ?? "";
                    var text = new RectangleF(x, tab.Y, Math.Max(0, tab.Right - S(TextPad) - x), tab.Height);
                    using (var ink = new SolidBrush(active ? colors.Ink : colors.InkSoft))
                        g.DrawString(title, font, ink, text, format);
                }
        }

        void Push(Bitmap bitmap, Point at)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = Native.SelectObject(memoryDc, hbitmap);
            try
            {
                var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                var source = new Native.POINT(0, 0);
                var target = new Native.POINT(at.X, at.Y);
                var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
                Native.UpdateLayeredWindow(Handle, screenDc, ref target, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memoryDc, previous);
                Native.DeleteObject(hbitmap);
                Native.DeleteDC(memoryDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// Just above the active tab's window: SetWindowPos puts a window below the one it is given, so
        /// it is given the window currently above that one (or the top, if none or that one is topmost).
        public void Restack()
        {
            if (!IsHandleCreated || container == null) return;
            var front = new IntPtr(container.Visible.Hwnd);
            IntPtr above = Native.GetWindow(front, Native.GW_HWNDPREV);
            if (above == Handle) return;
            if (above != IntPtr.Zero && (Native.WindowLong(above, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0) above = IntPtr.Zero;
            Native.SetWindowPos(Handle, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int at = TabAt(e.X);
            if (at == hover) return;
            hover = at;
            Render();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int at = TabAt(e.X);
            if (at < 0) return;
            string id = container.Windows[at].Hwnd.ToString();
            if (e.Button == MouseButtons.Left) dispatch(new[] { "focus-window", id });
            if (e.Button == MouseButtons.Middle) dispatch(new[] { "window", id, "close" });
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
