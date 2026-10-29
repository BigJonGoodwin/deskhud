// =================================================================================================
// Strip.cs: the bar's left island, which deskhud draws itself.
//
// A per-pixel-alpha layered window that sits on top of YASB's bar in the same island shape, so the
// two read as one bar. It shows the Windows button, the touch keyboard button (tablet posture), each
// workspace (app icons grouped by stack, with a live layout map for the focused one), untiled windows
// (dashed bubble) and minimized windows (tray bubble). It repaints only when something changes: a
// window manager event, a palette, a hover, or an animation frame.
//
// To change sizes: the constants at the top of StripWindow (logical pixels, scaled by the monitor).
// Colours: Palette.cs. Glows: Glow / DeepBubble / LightPool. Clicks and scrolling: OnMouseUp /
// OnMouseWheel. Right-click menus: Show*Menu (drawn by Menus.cs). Tooltips: TipFor.
// The bar height, rail and radii come from "bar" in settings.json and must match YASB. The island
// never grows under YASB's first island (Yasb.cs measures where that starts).
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace DeskHud
{
    /// The bar's left island: the Windows button, one bubble per workspace holding its app icons (stacks
    /// split by carved grooves), a live map of the focused workspace's layout, untiled windows and
    /// minimized windows.
    ///
    /// It is a per-pixel-alpha layered window whose outline continues YASB's adaptive rail, so it reads
    /// as one bar with YASB. It only repaints when something changes.
    sealed class StripWindow : Form
    {
        // Sizes, in logical pixels (multiplied by the monitor scale, and by TabletZoom in tablet posture).
        // Safe to tune; rebuild to see them.
        //   PadLeft/PadRight: island edge to first/last bubble.  Gap: space between bubbles.
        //   BubbleH: bubble height; equals YASB's --bubble-h (26) plus its two 1px borders.
        //   TabletZoom: everything inside the bar grows by this in tablet posture, to YASB's tablet
        //   --bubble-h (32, see YasbColors.Write); the bar itself keeps its height.
        //   HomeW: Windows button width.  LeadPad/InnerGap/ContentPad: padding inside bubbles.
        //   NumberW: room for the workspace number at the start of each workspace bubble.
        //   IconSize: app icons; MidIcon/SmallIcon: the sizes they shrink to when the island is crowded.
        //   IconGap: between icons in a stack.  StackGap: between stacks (the groove sits in the middle).
        //   GlowPad: how far an icon's glow reaches.  Dot*: folded workspaces, drawn as dots.
        //   MapW/MapH: the layout map.  MaxIcons: icons per stack before a +N badge.
        //   AnimationMs: hover and fold animation length.
        //   YasbGroupPadding: -qproperty-grouppadding in styles.css.  IslandGap: wallpaper left showing
        //   between this island and YASB's first one.
        const float PadLeft = 12, PadRight = 8, Gap = 5;
        const float BubbleH = 28, HomeW = 30, TabletZoom = 34f / 28;
        const float LeadPad = 9, InnerGap = 6, ContentPad = 7, NumberW = 9;
        const float IconSize = 17, MidIcon = 15, SmallIcon = 14, IconGap = 3, StackGap = 11, GlowPad = 4;
        const float DotSize = 5, DotGap = 2.5f, DotGroupGap = 5;
        const float MapW = 26, MapH = 17;
        const int MaxIcons = 6;
        const double AnimationMs = 170;
        const float YasbGroupPadding = 8, IslandGap = 6;

        /// What a clickable area on the island is.
        enum HitKind { Home, Keyboard, Offline, Workspace, Window, Map, Tray, TrayWindow, LooseWindow }

        /// A clickable area, rebuilt on every layout pass; Workspace and Hwnd say what it belongs to.
        sealed class Hit
        {
            public HitKind Kind;
            public RectangleF Bounds;
            public int Workspace = -1;
            public long Hwnd;
            public long StackHwnd;
        }

        readonly Func<string[], bool> dispatch;
        readonly MinimizedTray tray;
        readonly IconCache icons = new IconCache();
        readonly List<Hit> hits = new List<Hit>();
        readonly Dictionary<int, double> expand = new Dictionary<int, double>();
        readonly Timer animation = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer tipDelay = new Timer { Interval = 450 };
        readonly ToolTip tip = new ToolTip { OwnerDraw = true, ShowAlways = true, UseAnimation = false, UseFading = false };
        readonly Font tipFont = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints + 1);
        readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        readonly Bitmap measureSurface = new Bitmap(1, 1);
        readonly Graphics measure;
        readonly Dictionary<string, float> textWidths = new Dictionary<string, float>();
        // Blurred pills for Glow, by height and reach in pixels.
        readonly Dictionary<long, Bitmap> blooms = new Dictionary<long, Bitmap>();

        Settings settings;
        PaletteColors colors;
        Snapshot snapshot;
        // This island's monitor in komorebi's state (its own workspaces), or null when komorebi has none here.
        MonitorView view;
        // The screen this island sits on (physical pixels); the primary one also watches system messages.
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        bool primary = true;
        IList<long> loose = new List<long>();
        float scale = 1, zoom = 1;
        Font smallFont, glyphFont, numberFont;
        bool tablet = Touch.IsTablet;
        // How far in from the screen's left YASB's first island widget starts (physical pixels; an offset,
        // since screens left of or above the main one have negative x); 0 while unknown.
        int yasbEdge;
        // komorebi.json's error while komorebi is offline because of it (Daemon.ReportConfig).
        string komorebiError;
        Graphics g;
        bool drawing, hiddenForFullscreen, firstSnapshot = true;
        double trayExpand;
        int crowding;
        long lastFrame;
        Hit hover;
        int hoverWorkspace = -1;
        bool hoverTray, menuOpen;
        string tipText = "";

        public event Action TaskbarRecreated;
        /// The laptop went into tablet posture (true) or back (false).
        public event Action<bool> PostureChanged;

        public StripWindow(Settings settings, PaletteColors colors, MinimizedTray tray, Func<string[], bool> dispatch)
        {
            this.settings = settings;
            this.colors = colors;
            this.tray = tray;
            this.dispatch = dispatch;
            measure = Graphics.FromImage(measureSurface);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "deskhud";

            animation.Tick += delegate { Step(); };
            tipDelay.Tick += delegate { tipDelay.Stop(); ShowTip(); };
            tip.Popup += (s, e) => e.ToolTipSize = TextRenderer.MeasureText(tipText, tipFont) + new Size(16, 10);
            tip.Draw += DrawTip;
            tray.Changed += Render;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetScale(Native.GetDpiForWindow(Handle) / 96f);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            if (m.Msg == Native.WM_DPICHANGED)
            {
                SetScale((m.WParam.ToInt32() & 0xFFFF) / 96f);
                Render();
                return;
            }
            if (primary && taskbarCreated != 0 && m.Msg == (int)taskbarCreated && TaskbarRecreated != null) TaskbarRecreated();
            // Windows announces posture changes ("ConvertibleSlateMode") as a setting change; the metric
            // is the reliable part, so compare it on any setting change. Only the primary island reports
            // it; the daemon passes it on to the others.
            if (primary && m.Msg == Native.WM_SETTINGCHANGE && Touch.IsTablet != tablet)
            {
                SetTablet(!tablet);
                if (PostureChanged != null) PostureChanged(tablet);
            }
            base.WndProc(ref m);
        }

        public bool Tablet { get { return tablet; } }
        public Rectangle ScreenBounds { get { return screen; } }

        /// Tablet posture: everything in the bar grows by TabletZoom, and the keyboard bubble shows.
        public void SetTablet(bool value)
        {
            tablet = value;
            float wanted = value ? TabletZoom : 1;
            if (wanted != zoom)
            {
                zoom = wanted;
                SetScale(scale);
            }
            Render();
        }

        /// Puts the island on this screen. The primary one handles system messages for all of them.
        public void SetScreen(Rectangle bounds, bool isPrimary)
        {
            // On another screen it shows another monitor's windows: they did not move, so none animates.
            if (bounds != screen) settled = false;
            screen = bounds;
            primary = isPrimary;
            view = snapshot == null ? null : snapshot.ForScreen(screen);
            Render();
        }

        /// Why komorebi will not start (komorebi.json's error), shown on the offline bubble; null to clear.
        public void SetKomorebiError(string error)
        {
            if (error == komorebiError) return;
            komorebiError = error;
            Render();
        }

        /// Where YASB's first island starts (Yasb.cs); the island keeps left of it.
        public void SetYasbEdge(int x)
        {
            if (x == yasbEdge) return;
            yasbEdge = x;
            // YASB's islands change width every few seconds (network rates, window titles); this island
            // only looks different when that changes how crowded it has to be.
            if (view != null && snapshot != null && IsHandleCreated && Crowding() == crowding) return;
            Render();
        }

        public void SetSnapshot(Snapshot value)
        {
            snapshot = value;
            view = value == null ? null : value.ForScreen(screen);
            // YASB may start after us at login and land on top; komorebi events are a cheap moment to re-raise.
            if (!hiddenForFullscreen && IsHandleCreated)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (value != null)
            {
                icons.Retain(value.Hwnds.Concat(tray.Windows).Concat(loose).Concat(ghosts.Select(gh => gh.Hwnd)).ToList());
                if (firstSnapshot && view != null)
                {
                    foreach (var ws in view.Workspaces) expand[ws.Index] = TargetExpand(ws);
                    firstSnapshot = false;
                }
            }
            TrackMoves(value);
            Animate();
        }

        // ---- windows arriving and leaving ----

        /// An icon on its way out: drawn where it last sat, sliding toward where the window went.
        sealed class Ghost
        {
            public long Hwnd;
            public RectangleF From;
            public float Dx, Dy;
            public long Start;
        }

        const double ArriveMs = 460, LeaveMs = 340;
        // Which workspace each window on this island was in last time, and where its icon was drawn.
        Dictionary<long, int> homes = new Dictionary<long, int>();
        readonly Dictionary<long, RectangleF> drawnAt = new Dictionary<long, RectangleF>();
        readonly Dictionary<long, long> arrivals = new Dictionary<long, long>();
        readonly List<Ghost> ghosts = new List<Ghost>();
        bool settled;

        /// Compares this island's windows with last time. One that arrived (moved here from another
        /// workspace or screen, or just opened) pops in with a ring of light; one that left for another
        /// workspace, screen or the tray leaves a ghost sliding that way. Nothing animates on the first
        /// snapshot, or after komorebi comes back, when everything would "arrive" at once.
        void TrackMoves(Snapshot value)
        {
            var now = new Dictionary<long, int>();
            if (view != null)
                foreach (var ws in view.Workspaces)
                {
                    foreach (var c in ws.Containers)
                        foreach (var w in c.Windows) now[w.Hwnd] = ws.Index;
                    foreach (var w in ws.Floating) now[w.Hwnd] = ws.Index;
                }
            if (value == null)
            {
                settled = false;
                homes = now;
                return;
            }
            if (settled)
            {
                long t = clock.ElapsedMilliseconds;
                foreach (var pair in now)
                {
                    int before;
                    bool known = homes.TryGetValue(pair.Key, out before);
                    if (known && before == pair.Value) continue;
                    arrivals[pair.Key] = t;
                    RectangleF from;
                    if (known && drawnAt.TryGetValue(pair.Key, out from))
                        ghosts.Add(new Ghost { Hwnd = pair.Key, From = from, Dx = pair.Value > before ? 1 : -1, Start = t });
                }
                foreach (var pair in homes)
                {
                    RectangleF from;
                    if (now.ContainsKey(pair.Key) || !drawnAt.TryGetValue(pair.Key, out from)) continue;
                    var ghost = new Ghost { Hwnd = pair.Key, From = from, Start = t };
                    int monitor, workspace, container, slot;
                    if (tray.Windows.Contains(pair.Key)) ghost.Dy = 1; // down into the tray
                    else if (value.Locate(pair.Key, out monitor, out workspace, out container, out slot))
                    {
                        Rectangle there = value.Monitors[monitor].Bounds, here = view == null ? screen : view.Bounds;
                        ghost.Dx = there.Left >= here.Right ? 1 : there.Right <= here.Left ? -1 : 0;
                        if (ghost.Dx == 0) ghost.Dy = there.Top < here.Top ? -1 : 1;
                    }
                    else continue; // closed: it just goes
                    ghosts.Add(ghost);
                }
            }
            homes = now;
            settled = view != null;
        }

        /// 0 to 1 through the window's arrival, or 1 when it is not arriving.
        float Arrival(long hwnd)
        {
            long start;
            if (!arrivals.TryGetValue(hwnd, out start)) return 1;
            double p = (clock.ElapsedMilliseconds - start) / ArriveMs;
            if (p < 1) return (float)p;
            arrivals.Remove(hwnd);
            return 1;
        }

        /// A pop with a little overshoot.
        static float Pop(float t)
        {
            const float back = 1.7f;
            float u = t - 1;
            return 1 + (back + 1) * u * u * u + back * u * u;
        }

        /// The arriving icon's ring of light, in its own colour, spreading and fading.
        void ArrivalRing(RectangleF icon, Color colour, float t)
        {
            float r = icon.Width * (0.55f + 0.75f * t), cx = icon.X + icon.Width / 2, cy = icon.Y + icon.Height / 2;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (var light = new PathGradientBrush(path) { CenterColor = WithAlpha(colour, 0), SurroundColors = new[] { WithAlpha(colour, 0) } })
                {
                    light.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { WithAlpha(colour, 0), WithAlpha(colour, (int)(170 * (1 - t))), WithAlpha(colour, 0) },
                        Positions = new[] { 0f, 0.25f, 1f },
                    };
                    g.FillPath(light, path);
                }
            }
        }

        /// The leaving icons, drawn over everything else on the island.
        void Ghosts()
        {
            long now = clock.ElapsedMilliseconds;
            ghosts.RemoveAll(gh => now - gh.Start >= LeaveMs);
            if (!drawing) return;
            int pixels = (int)Math.Round(S(IconSize));
            foreach (var ghost in ghosts)
            {
                float t = Ease((now - ghost.Start) / LeaveMs);
                float size = ghost.From.Width * (1 - 0.35f * t), travel = S(26) * t;
                var rect = new RectangleF(ghost.From.X + (ghost.From.Width - size) / 2 + ghost.Dx * travel,
                    ghost.From.Y + (ghost.From.Height - size) / 2 + ghost.Dy * travel * 0.6f, size, size);
                Bitmap bitmap = icons.Get(ghost.Hwnd, pixels);
                if (bitmap != null) Faded(bitmap, Rectangle.Round(rect), 1 - t);
            }
        }

        /// Open windows komorebi is not tiling; each island shows the ones on its own screen.
        public void SetLoose(IList<long> value)
        {
            var mine = value.Where(OnMyScreen).ToList();
            if (mine.SequenceEqual(loose)) return;
            loose = mine;
            Render();
        }

        bool OnMyScreen(long hwnd)
        {
            Native.RECT rect;
            if (!Native.GetWindowRect(new IntPtr(hwnd), out rect)) return false;
            var centre = new Point(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);
            return Screen.FromPoint(centre).Bounds == screen;
        }

        /// True when komorebi's focus is on this island's monitor: its current workspace glows fully.
        bool MonitorFocused
        {
            get
            {
                // komorebi may list this screen twice (Snapshot.Numbered); focus on either entry counts.
                return snapshot != null && view != null && (snapshot.Focused == null || snapshot.Numbered().Count < 2
                    || snapshot.ForScreen(snapshot.Focused.Bounds) == view);
            }
        }

        public void SetPalette(PaletteColors value)
        {
            colors = value;
            Render();
        }

        public void SetSettings(Settings value)
        {
            settings = value;
            Render();
        }

        /// Fullscreen apps get the whole screen. On a focus change the strip also re-asserts itself above
        /// YASB, which shares this part of the screen; `raise` is false for the frequent move events.
        public void OnForeground(IntPtr window, bool raise)
        {
            IntPtr monitor;
            bool fullscreen = Native.IsFullscreen(window, out monitor) && monitor == Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST);
            if (fullscreen != hiddenForFullscreen)
            {
                hiddenForFullscreen = fullscreen;
                Native.ShowWindow(Handle, fullscreen ? Native.SW_HIDE : Native.SW_SHOWNOACTIVATE);
                if (!fullscreen) Render();
            }
            if (raise && !fullscreen)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void SetScale(float value)
        {
            if (value <= 0) value = 1;
            scale = value;
            if (smallFont != null) smallFont.Dispose();
            if (glyphFont != null) glyphFont.Dispose();
            if (numberFont != null) numberFont.Dispose();
            smallFont = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFont = new Font(Glyphs.Font, S(14.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            numberFont = new Font("Segoe UI Semibold", S(10.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            textWidths.Clear();
            icons.Reset();
            ClearBlooms();
        }

        void ClearBlooms()
        {
            foreach (var bloom in blooms.Values) bloom.Dispose();
            blooms.Clear();
        }

        /// Content sizes: the monitor scale times the tablet zoom.
        float S(float logical) { return logical * scale * zoom; }
        /// The bar's own geometry (height, rail, radii), which never zooms: it must match YASB's.
        float B(float logical) { return logical * scale; }

        // ---- animation ----

        double TargetExpand(WorkspaceView ws)
        {
            if (ws.IsEmpty || view == null) return 0;
            return ws.Index == view.FocusedWorkspace || ws.Index == hoverWorkspace ? 1 : 0;
        }

        double Progress(int workspace)
        {
            double value;
            return expand.TryGetValue(workspace, out value) ? value : 0;
        }

        void Animate()
        {
            lastFrame = clock.ElapsedMilliseconds;
            if (!animation.Enabled) animation.Start();
            Step();
        }

        void Step()
        {
            long now = clock.ElapsedMilliseconds;
            double delta = (now - lastFrame) / AnimationMs;
            lastFrame = now;
            bool moving = false;

            if (view != null)
            {
                foreach (var ws in view.Workspaces)
                {
                    double current = Progress(ws.Index), target = TargetExpand(ws);
                    if (current == target) continue;
                    expand[ws.Index] = target > current ? Math.Min(target, current + delta) : Math.Max(target, current - delta);
                    moving = true;
                }
            }
            double trayTarget = hoverTray ? 1 : 0;
            if (trayExpand != trayTarget)
            {
                trayExpand = trayTarget > trayExpand ? Math.Min(1, trayExpand + delta) : Math.Max(0, trayExpand - delta);
                moving = true;
            }
            if (arrivals.Count > 0 || ghosts.Count > 0) moving = true;

            if (!moving) animation.Stop();
            Render();
        }

        static float Ease(double t) { return (float)(1 - Math.Pow(1 - t, 3)); }

        // ---- rendering ----

        public void Render()
        {
            if (!IsHandleCreated || hiddenForFullscreen || smallFont == null) return;
            // Each monitor has its own scale; follow the one this island sits on.
            float monitorScale = Native.ScaleAt(screen);
            if (monitorScale > 0 && Math.Abs(monitorScale - scale) > 0.001f) SetScale(monitorScale);

            g = measure;
            drawing = false;
            float width = Compose();

            float barHeight = B(settings.BarHeight), rail = B(settings.RailHeight);
            float fillet = Math.Min(B(settings.IslandRadius), (barHeight - rail) / 2);
            int pixelWidth = (int)Math.Ceiling(width + fillet);
            int pixelHeight = (int)Math.Ceiling(barHeight + B(settings.EdgeRadius));

            using (var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb))
            using (var surface = Graphics.FromImage(bitmap))
            {
                surface.SmoothingMode = SmoothingMode.AntiAlias;
                surface.PixelOffsetMode = PixelOffsetMode.HighQuality;
                surface.InterpolationMode = InterpolationMode.HighQualityBicubic;
                surface.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var island = IslandPath(width, barHeight, rail, fillet))
                {
                    using (var fill = BarFill(pixelWidth, pixelHeight, barHeight))
                        surface.FillPath(fill, island);
                    // Glows light the island, never the desktop around it.
                    surface.SetClip(island);

                    g = surface;
                    drawing = true;
                    Compose();
                    g = measure;
                }
                Push(bitmap);
            }
        }

        /// Light rising from behind the bar's lower edge, with no edge line: black down to 30% of the frame,
        /// the glow colour by the bar's bottom. The stops match `.yasb-bar` in styles.css; both frames are the
        /// bar plus the edge curves below it, so the same fractions line up.
        Brush BarFill(int width, int height, float barHeight)
        {
            var brush = new LinearGradientBrush(new Rectangle(0, -1, width, height + 2), Color.Black, colors.BarGlow, 90f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.Black, Color.Black, colors.BarGlow, colors.BarGlow },
                Positions = new[] { 0f, 0.30f, Math.Min(0.99f, barHeight / height), 1f },
            };
            return brush;
        }

        /// The island's outline, the same curves YASB draws: a concave fillet into the rail on the right,
        /// a rounded bottom corner, and a curve down into the screen edge on the left.
        GraphicsPath IslandPath(float right, float height, float rail, float fillet)
        {
            float corner = Math.Min(fillet, right / 2);
            float edge = B(settings.EdgeRadius);
            var path = new GraphicsPath();
            path.AddLine(0, 0, right + fillet, 0);
            path.AddLine(right + fillet, 0, right + fillet, rail);
            if (fillet > 0) path.AddArc(right, rail, 2 * fillet, 2 * fillet, 270, -90);
            path.AddLine(right, rail + fillet, right, height - corner);
            if (corner > 0) path.AddArc(right - 2 * corner, height - 2 * corner, 2 * corner, 2 * corner, 0, 90);
            if (edge > 0)
            {
                path.AddLine(right - corner, height, edge, height);
                path.AddArc(0, height, 2 * edge, 2 * edge, 270, -90);
            }
            else
            {
                path.AddLine(right - corner, height, 0, height);
            }
            path.CloseFigure();
            return path;
        }

        void Push(Bitmap bitmap)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = Native.SelectObject(memoryDc, hbitmap);
            try
            {
                var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                var source = new Native.POINT(0, 0);
                var target = new Native.POINT(screen.Left, screen.Top);
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

        /// Lays out (and when `drawing`, paints) everything left to right. Returns the island's width.
        float Compose()
        {
            hits.Clear();
            float x = S(PadLeft);
            x = Home(x) + S(Gap);
            if (ShowsKeyboard) x = KeyboardButton(x) + S(Gap);
            if (snapshot == null) x = Offline(x) + S(Gap);
            else if (view != null)
            {
                crowding = Crowding();
                foreach (var ws in ShownWorkspaces()) x = Workspace(ws, x) + S(Gap);
            }
            if (loose.Count > 0) x = Loose(x) + S(Gap);

            // Minimized windows belong to no monitor; every island offers them.
            var minimized = tray.Windows.Where(h => Native.IsWindow(new IntPtr(h)) && Native.IsIconic(new IntPtr(h))).ToList();
            if (minimized.Count > 0) x = Tray(minimized, x) + S(Gap);
            Ghosts();
            return x - S(Gap) + S(PadRight);
        }

        float BubbleTop { get { float rail = B(settings.RailHeight); return rail + (B(settings.BarHeight) - rail - S(BubbleH)) / 2; } }
        float MidY { get { return BubbleTop + S(BubbleH) / 2; } }

        float Home(float x)
        {
            var rect = new RectangleF(x, BubbleTop, S(HomeW), S(BubbleH));
            AddHit(HitKind.Home, rect);
            if (drawing)
            {
                bool hovered = IsHovered(HitKind.Home);
                Glow(rect, colors.Accent, hovered ? 0.7f : 0.28f, 6);
                Bubble(rect, hovered ? colors.SurfaceHigh : colors.Surface, 1);
                Underglow(rect);
                // The Windows mark as four outlined panes, hollow so the background shows through.
                float pane = S(4.6f), gap = S(1.6f), size = pane * 2 + gap;
                float left = rect.X + (rect.Width - size) / 2, top = rect.Y + (rect.Height - size) / 2;
                using (var pen = new Pen(hovered ? colors.Accent : colors.Ink, Math.Max(1f, S(1.2f))))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var square = new RectangleF(left + (i % 2) * (pane + gap), top + (i / 2) * (pane + gap), pane, pane);
                        using (var path = RoundRect(square, S(1.2f))) g.DrawPath(pen, path);
                    }
                }
            }
            return rect.Right;
        }

        /// The touch keyboard button stands in for the hidden taskbar's, in tablet posture by default.
        bool ShowsKeyboard
        {
            get { return settings.KeyboardButton == "always" || settings.KeyboardButton == "auto" && tablet; }
        }

        float KeyboardButton(float x)
        {
            var rect = new RectangleF(x, BubbleTop, S(HomeW), S(BubbleH));
            AddHit(HitKind.Keyboard, rect);
            if (drawing)
            {
                bool hovered = IsHovered(HitKind.Keyboard);
                Glow(rect, colors.Stack[2], hovered ? 0.75f : 0.4f, 6);
                Bubble(rect, hovered ? colors.SurfaceHigh : colors.Surface, 1);
                Underglow(rect);
                Caption(Glyphs.Keyboard.ToString(), glyphFont, hovered ? colors.Accent : colors.Ink, rect, 1);
            }
            return rect.Right;
        }

        float Offline(float x)
        {
            // With komorebi.json broken, komorebi quits at every start; say so, and the click opens the file.
            string text = komorebiError == null ? Wm.Current.Name + " offline · click to start"
                : Wm.Current.Name + " config: " + (komorebiError.Length > 60 ? komorebiError.Substring(0, 57) + "..." : komorebiError) + " · click to fix";
            var rect = new RectangleF(x, BubbleTop, TextWidth(text, smallFont) + S(20), S(BubbleH));
            AddHit(HitKind.Offline, rect);
            if (drawing)
            {
                Bubble(rect, IsHovered(HitKind.Offline) ? colors.SurfaceHigh : colors.Surface, 1);
                Caption(text, smallFont, colors.InkSoft, rect, 1);
            }
            return rect.Right;
        }

        // ---- crowding: icons stay visible and shrink only when the island would grow past its budget ----

        /// 0: every workspace at full size. 1: other workspaces' icons shrink. 2: the focused one's
        /// too, a little. 3: other workspaces fold to dots (hover still opens them).
        int Crowding()
        {
            float budget = Budget();
            float fixedWidth = S(PadLeft) + S(HomeW) + S(Gap) + S(PadRight) + (ShowsKeyboard ? S(HomeW) + S(Gap) : 0);
            for (int level = 0; level < 3; level++)
            {
                float width = fixedWidth;
                foreach (var ws in ShownWorkspaces()) width += WorkspaceWidth(ws, level, 0) + S(Gap);
                if (width <= budget) return level;
            }
            return 3;
        }

        /// How wide the island may grow: bar.strip_max_width, and never past the point where its right
        /// curve would meet YASB's first island (its widgets start yasbEdge in from the screen's left;
        /// its outline starts the group padding and a fillet before that, and this island's own fillet
        /// runs past its width).
        float Budget()
        {
            float budget = B(settings.StripMaxWidth);
            if (yasbEdge <= 0) return budget;
            float room = yasbEdge - B(YasbGroupPadding + 2 * settings.IslandRadius + IslandGap);
            return Math.Max(S(PadLeft + HomeW + PadRight), Math.Min(budget, room));
        }

        float IconFor(WorkspaceView ws, int level)
        {
            if (ws.Index == view.FocusedWorkspace) return level >= 2 ? MidIcon : IconSize;
            return level >= 1 ? SmallIcon : IconSize;
        }

        bool Folds(WorkspaceView ws, int level)
        {
            return level >= 3 && ws.Index != view.FocusedWorkspace;
        }

        /// `progress` only matters when the workspace is folded to dots: 0 folded, 1 opened by hover.
        float WorkspaceWidth(WorkspaceView ws, int level, float progress)
        {
            if (ws.IsEmpty) return S(BubbleH);
            bool active = ws.Index == view.FocusedWorkspace;
            float open = (active ? S(MapW) + S(InnerGap) : 0) + GroupsWidth(ws, IconFor(ws, level));
            float pads = 2 * S(ContentPad) + S(NumberW);
            if (!Folds(ws, level)) return pads + open;
            float folded = DotsWidth(ws);
            return pads + folded + (open - folded) * progress;
        }

        /// The workspace's number (its position, as Win+1–7 counts) tucked into the bubble's left end:
        /// a barely-there grey on the others, the accent with a little light behind it where you are.
        /// With hidden empty workspaces and several monitors, it says which one is which at a glance.
        void Number(WorkspaceView ws, RectangleF box, bool active, bool hovered)
        {
            if (!drawing) return;
            string text = NumberOf(ws).ToString();
            if (active)
            {
                float r = Math.Min(box.Width, box.Height) * 0.75f;
                float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                    int strength = MonitorFocused ? 95 : 45;
                    using (var light = new PathGradientBrush(path) { CenterColor = WithAlpha(colors.Accent, strength), SurroundColors = new[] { WithAlpha(colors.Accent, 0) } })
                        g.FillPath(light, path);
                }
                Caption(text, numberFont, MonitorFocused ? colors.Accent : colors.InkSoft, box, 1);
                return;
            }
            Caption(text, numberFont, WithAlpha(colors.Ink, hovered ? 170 : 92), box, 1);
        }

        float Workspace(WorkspaceView ws, float x)
        {
            bool active = ws.Index == view.FocusedWorkspace;
            bool hovered = hoverWorkspace == ws.Index;

            if (ws.IsEmpty)
            {
                // Only reached for the focused workspace: its number, glowing, says where you are.
                var marker = new RectangleF(x, BubbleTop, S(BubbleH), S(BubbleH));
                AddHit(HitKind.Workspace, marker, ws.Index);
                if (drawing)
                {
                    DeepBubble(marker, colors.Accent);
                    Number(ws, RectangleF.Inflate(marker, -S(5), -S(5)), true, false);
                }
                return marker.Right;
            }

            float progress = Folds(ws, crowding) ? Ease(Progress(ws.Index)) : 1;
            float iconSize = IconFor(ws, crowding);
            var rect = new RectangleF(x, BubbleTop, WorkspaceWidth(ws, crowding, progress), S(BubbleH));
            AddHit(HitKind.Workspace, rect, ws.Index);

            float contentX = rect.X + S(ContentPad) + S(NumberW);
            if (drawing)
            {
                // The focused workspace glows in the colour of the app you are in.
                if (active) DeepBubble(rect, ws.FocusedContainer >= 0 ? ContainerColour(ws, ws.FocusedContainer) : colors.Accent);
                else ShallowBubble(rect, hovered);
                Number(ws, new RectangleF(rect.X + S(ContentPad) - S(2.5f), rect.Y, S(NumberW), rect.Height), active, hovered);
            }

            GraphicsState saved = null;
            if (drawing)
            {
                saved = g.Save();
                // Clip to the bubble itself, so glows can reach its edge but never past it.
                using (var shape = RoundRect(rect, rect.Height / 2)) g.SetClip(shape);
            }
            if (progress < 1) Dots(ws, contentX, 1 - progress);
            if (progress > 0)
            {
                float groupsX = contentX;
                if (active)
                {
                    Map(ws, new RectangleF(contentX, MidY - S(MapH) / 2, S(MapW), S(MapH)), progress);
                    groupsX += S(MapW) + S(InnerGap);
                }
                // Out-of-focus workspaces are dimmer as well as shallower.
                Groups(ws, groupsX, progress, iconSize, active ? 1f : hovered ? 0.95f : 0.85f);
            }
            if (saved != null) g.Restore(saved);
            return rect.Right;
        }

        IEnumerable<List<WindowView>> GroupsOf(WorkspaceView ws)
        {
            foreach (var c in ws.Containers) yield return c.Windows;
            if (ws.Floating.Count > 0) yield return ws.Floating;
        }

        float DotsWidth(WorkspaceView ws)
        {
            var groups = GroupsOf(ws).ToList();
            float width = groups.Sum(w => Math.Min(w.Count, MaxIcons) * S(DotSize) + (Math.Min(w.Count, MaxIcons) - 1) * S(DotGap));
            return width + Math.Max(0, groups.Count - 1) * S(DotGroupGap);
        }

        float GroupsWidth(WorkspaceView ws, float iconSize)
        {
            var groups = GroupsOf(ws).ToList();
            return groups.Sum(w => GroupWidth(w.Count, iconSize)) + Math.Max(0, groups.Count - 1) * S(StackGap);
        }

        float GroupWidth(int windows, float iconSize)
        {
            int shown = Math.Min(windows, MaxIcons);
            return shown * S(iconSize) + (shown - 1) * S(IconGap);
        }

        void Dots(WorkspaceView ws, float x, float alpha)
        {
            if (!drawing) return;
            int index = 0;
            foreach (var windows in GroupsOf(ws))
            {
                Color colour = ContainerColour(ws, index);
                int shown = Math.Min(windows.Count, MaxIcons);
                for (int i = 0; i < shown; i++)
                {
                    var dot = new RectangleF(x, MidY - S(DotSize) / 2, S(DotSize), S(DotSize));
                    using (var brush = new SolidBrush(WithAlpha(colour, (int)(230 * alpha)))) g.FillEllipse(brush, dot);
                    x += S(DotSize) + S(DotGap);
                }
                x += S(DotGroupGap) - S(DotGap);
                index++;
            }
        }

        /// Stacks are told apart by spacing and a faint divider, not boxes. Each app glows in its own
        /// shape; the focused one glows brighter and gets a rim that traces its outline. Windows hidden
        /// behind others in a stack sit dim and unlit.
        void Groups(WorkspaceView ws, float x, float alpha, float iconSize, float dim)
        {
            bool active = ws.Index == view.FocusedWorkspace;
            int iconPixels = (int)Math.Round(S(iconSize));
            int index = 0;
            foreach (var windows in GroupsOf(ws))
            {
                bool floating = index >= ws.Containers.Count;
                int visible = floating ? -1 : ws.Containers[index].Focused;
                bool focused = active && index == ws.FocusedContainer;
                Color colour = ContainerColour(ws, index);

                if (index > 0 && drawing) Divider(x - S(StackGap) / 2, alpha, active, floating);

                int shown = Math.Min(windows.Count, MaxIcons);
                for (int i = 0; i < shown; i++)
                {
                    var window = windows[i];
                    var iconRect = new RectangleF(x, MidY - S(iconSize) / 2, S(iconSize), S(iconSize));
                    if (alpha > 0.5f)
                        AddHit(HitKind.Window, iconRect, ws.Index, window.Hwnd, floating || windows.Count < 2 ? 0 : windows[Math.Max(visible, 0)].Hwnd);
                    if (drawing)
                    {
                        bool showing = floating || i == visible;
                        bool lead = focused && showing;
                        float glow = !showing ? 0 : lead ? 0.95f : active ? 0.4f : 0.22f;
                        float opacity = (showing ? 1 : 0.6f) * dim * alpha;
                        float arrive = Arrival(window.Hwnd);
                        drawnAt[window.Hwnd] = iconRect;
                        if (arrive < 1)
                        {
                            // Just arrived: a ring of its own light, and the icon popping up to size.
                            ArrivalRing(iconRect, icons.Glow(window.Hwnd, iconPixels) ?? colour, arrive);
                            float k = 0.35f + 0.65f * Pop(arrive);
                            var popped = new RectangleF(iconRect.X + iconRect.Width * (1 - k) / 2, iconRect.Y + iconRect.Height * (1 - k) / 2, iconRect.Width * k, iconRect.Height * k);
                            Bitmap bitmap = icons.Get(window.Hwnd, iconPixels);
                            if (bitmap != null) Faded(bitmap, Rectangle.Round(popped), opacity);
                            else AppIcon(window.Hwnd, window.Exe, popped, iconPixels, opacity, colour);
                        }
                        else
                        {
                            if (lead) LightPool(iconRect, colour, alpha);
                            Light(window.Hwnd, iconRect, iconPixels, glow * dim * alpha, lead ? alpha : 0);
                            AppIcon(window.Hwnd, window.Exe, iconRect, iconPixels, opacity, colour);
                        }
                        if (i == shown - 1 && windows.Count > shown)
                            Badge("+" + (windows.Count - shown + 1), iconRect, alpha);
                    }
                    x += S(iconSize) + S(IconGap);
                }
                x += S(StackGap) - S(IconGap);
                index++;
            }
        }

        /// Stacks are set apart by a groove: a dark cut with a lit edge beside it, so it reads as carved
        /// into the bubble. In the focused workspace it also glows faintly in the accent. Floating
        /// windows (on this workspace but out of the grid) are set apart by a column of round dots
        /// instead, so the two never look alike.
        void Divider(float cx, float alpha, bool active, bool dotted)
        {
            if (dotted)
            {
                FloatingDivider(cx, alpha, active);
                return;
            }
            float top = MidY - S(8), bottom = MidY + S(8), width = Math.Max(1f, S(1));
            if (active)
            {
                for (int ring = 3; ring >= 1; ring--)
                    using (var pen = new Pen(WithAlpha(colors.Accent, (int)(66 * alpha / ring)), width + S(ring * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, cx, top + S(1), cx, bottom - S(1));
            }
            using (var cut = new Pen(Color.FromArgb((int)(210 * alpha), 0, 0, 0), width))
            using (var lit = new Pen(WithAlpha(colors.Ink, (int)(120 * alpha)), width))
            {
                g.DrawLine(cut, cx - width / 2, top, cx - width / 2, bottom);
                g.DrawLine(lit, cx + width / 2, top, cx + width / 2, bottom);
            }
        }

        /// Three lit dots, each with a little halo, in the floating border colour.
        void FloatingDivider(float cx, float alpha, bool active)
        {
            Color tone = colors.Stack[3];
            float dot = S(2.6f), halo = S(5.5f), step = S(6);
            for (int i = -1; i <= 1; i++)
            {
                float cy = MidY + i * step;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - halo / 2, cy - halo / 2, halo, halo);
                    using (var light = new PathGradientBrush(path) { CenterColor = WithAlpha(tone, (int)((active ? 150 : 90) * alpha)), SurroundColors = new[] { WithAlpha(tone, 0) } })
                        g.FillPath(light, path);
                }
                using (var brush = new SolidBrush(WithAlpha(tone, (int)(235 * alpha)))) g.FillEllipse(brush, cx - dot / 2, cy - dot / 2, dot, dot);
            }
        }

        /// The icon's glow (a blurred copy of itself) and, for the focused app, the rim tracing its edge.
        void Light(long hwnd, RectangleF icon, int pixels, float glow, float rim)
        {
            int pad = (int)Math.Round(S(GlowPad));
            var area = new Rectangle((int)Math.Round(icon.X) - pad, (int)Math.Round(icon.Y) - pad, pixels + 2 * pad, pixels + 2 * pad);
            if (glow > 0.01f)
            {
                Bitmap halo = icons.Halo(hwnd, pixels, pad);
                if (halo != null) Faded(halo, area, glow);
            }
            if (rim > 0.01f)
            {
                Bitmap edge = icons.Rim(hwnd, pixels, pad);
                if (edge != null) Faded(edge, area, rim);
            }
        }

        /// Each tile of the focused workspace, scaled down, in the colour of its stack bubble.
        void Map(WorkspaceView ws, RectangleF rect, float alpha)
        {
            AddHit(HitKind.Map, rect, ws.Index);
            if (!drawing) return;

            using (var path = RoundRect(rect, S(3)))
            {
                using (var back = new SolidBrush(Color.FromArgb((int)(150 * alpha), 0, 0, 0))) g.FillPath(back, path);
                using (var outline = new Pen(WithAlpha(colors.InkSoft, (int)(95 * alpha)), Math.Max(1f, S(0.8f)))) g.DrawPath(outline, path);
            }

            if (snapshot.Paused)
            {
                // Paused: the layout freezes; show pause bars in the negative colour.
                float barW = S(2.6f), barH = S(8), cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
                using (var brush = new SolidBrush(WithAlpha(colors.Negative, (int)(255 * alpha))))
                {
                    g.FillRectangle(brush, cx - barW - S(1.2f), cy - barH / 2, barW, barH);
                    g.FillRectangle(brush, cx + S(1.2f), cy - barH / 2, barW, barH);
                }
                return;
            }

            var tiles = ws.Monocle || ws.Tiles.Count == 0 ? new List<Rectangle>() : ws.Tiles.Take(ws.Containers.Count).ToList();
            var inner = RectangleF.Inflate(rect, -S(1.5f), -S(1.5f));
            if (tiles.Count == 0)
            {
                using (var brush = new SolidBrush(WithAlpha(ContainerColour(ws, Math.Max(0, ws.FocusedContainer)), (int)(230 * alpha))))
                using (var path = RoundRect(inner, S(1.5f)))
                    g.FillPath(brush, path);
                return;
            }

            float minX = tiles.Min(t => t.Left), minY = tiles.Min(t => t.Top);
            float spanX = Math.Max(1, tiles.Max(t => t.Right) - minX), spanY = Math.Max(1, tiles.Max(t => t.Bottom) - minY);
            float inset = Math.Max(0.6f, S(0.6f));
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                var cell = new RectangleF(
                    inner.X + (t.Left - minX) / spanX * inner.Width + inset,
                    inner.Y + (t.Top - minY) / spanY * inner.Height + inset,
                    t.Width / spanX * inner.Width - 2 * inset,
                    t.Height / spanY * inner.Height - 2 * inset);
                bool focused = i == ws.FocusedContainer;
                using (var brush = new SolidBrush(WithAlpha(ContainerColour(ws, i), (int)((focused ? 255 : 120) * alpha))))
                    g.FillRectangle(brush, cell);
            }
        }

        /// A stack's colour is the glow of the window showing in it, so the layout map, the dots and
        /// the stack bubble all match the app. Floating windows stay neutral.
        Color ContainerColour(WorkspaceView ws, int index)
        {
            if (index >= ws.Containers.Count) return colors.InkSoft;
            Color? glow = icons.Glow(ws.Containers[index].Visible.Hwnd, (int)Math.Round(S(IconSize)));
            return glow ?? colors.StackColor(index);
        }

        /// Windows komorebi is not tiling sit in a dashed bubble of their own: they float over every
        /// workspace. Clicking one tiles it into the current workspace.
        float Loose(float x)
        {
            int shown = Math.Min(loose.Count, MaxIcons);
            int iconPixels = (int)Math.Round(S(IconSize));
            var rect = new RectangleF(x, BubbleTop, 2 * S(ContentPad) + GroupWidth(shown, IconSize), S(BubbleH));
            if (drawing)
            {
                Glow(rect, colors.Negative, 0.35f, 5);
                using (var path = RoundRect(RectangleF.Inflate(rect, -0.5f, -0.5f), rect.Height / 2 - 0.5f))
                {
                    using (var fill = new SolidBrush(WithAlpha(colors.Negative, 24))) g.FillPath(fill, path);
                    using (var pen = new Pen(WithAlpha(colors.Negative, 210), Math.Max(1f, S(1.2f))) { DashStyle = DashStyle.Dash })
                        g.DrawPath(pen, path);
                }
            }
            float ix = rect.X + S(ContentPad);
            for (int i = 0; i < shown; i++)
            {
                var iconRect = new RectangleF(ix, MidY - S(IconSize) / 2, S(IconSize), S(IconSize));
                AddHit(HitKind.LooseWindow, iconRect, -1, loose[i]);
                if (drawing)
                {
                    AppIcon(loose[i], null, iconRect, iconPixels, 1, colors.InkSoft);
                    if (i == shown - 1 && loose.Count > shown) Badge("+" + (loose.Count - shown + 1), iconRect, 1);
                }
                ix += S(IconSize) + S(IconGap);
            }
            return rect.Right;
        }

        float Tray(List<long> windows, float x)
        {
            float progress = Ease(trayExpand);
            int target = Math.Max(0, Math.Min(tray.Target, windows.Count - 1));
            float glyph = S(10);
            float collapsed = S(IconSize) + (windows.Count > 1 ? S(4) + TextWidth("+" + (windows.Count - 1), smallFont) : 0);
            float expanded = windows.Count * S(IconSize) + (windows.Count - 1) * S(IconGap);
            var rect = new RectangleF(x, BubbleTop, S(LeadPad) + glyph + S(InnerGap) + collapsed + (expanded - collapsed) * progress + S(ContentPad), S(BubbleH));
            AddHit(HitKind.Tray, rect);
            int iconPixels = (int)Math.Round(S(IconSize));

            float cx = rect.X + S(LeadPad);
            if (drawing)
            {
                if (hoverTray) Glow(rect, colors.Accent, 0.55f, 6);
                Bubble(rect, hoverTray ? colors.SurfaceHigh : colors.Surface, 1);
                // A tray: an open box with a chevron dropping into it.
                using (var pen = new Pen(colors.InkSoft, Math.Max(1f, S(1.2f))))
                {
                    float cy = MidY;
                    g.DrawLines(pen, new[] { new PointF(cx, cy), new PointF(cx, cy + S(4)), new PointF(cx + glyph, cy + S(4)), new PointF(cx + glyph, cy) });
                    g.DrawLines(pen, new[] { new PointF(cx + S(2.5f), cy - S(3)), new PointF(cx + glyph / 2, cy - S(0.5f)), new PointF(cx + glyph - S(2.5f), cy - S(3)) });
                }
            }

            float ix = cx + glyph + S(InnerGap);
            if (progress < 1 && drawing)
            {
                var iconRect = new RectangleF(ix, MidY - S(IconSize) / 2, S(IconSize), S(IconSize));
                AppIcon(windows[target], null, iconRect, iconPixels, 1 - progress, colors.InkSoft);
                if (windows.Count > 1)
                {
                    var countRect = new RectangleF(iconRect.Right + S(4), rect.Y, TextWidth("+" + (windows.Count - 1), smallFont), rect.Height);
                    Caption("+" + (windows.Count - 1), smallFont, colors.InkSoft, countRect, 1 - progress);
                }
            }
            if (progress > 0)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    var iconRect = new RectangleF(ix, MidY - S(IconSize) / 2, S(IconSize), S(IconSize));
                    if (progress > 0.5f) AddHit(HitKind.TrayWindow, iconRect, -1, windows[i]);
                    if (drawing) AppIcon(windows[i], null, iconRect, iconPixels, (i == target ? 1 : 0.5f) * progress, colors.InkSoft);
                    ix += S(IconSize) + S(IconGap);
                }
            }
            return rect.Right;
        }

        // ---- primitives ----

        void Bubble(RectangleF rect, Color fill, float alpha)
        {
            using (var path = RoundRect(rect, rect.Height / 2))
            {
                var body = new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2);
                using (var brush = new LinearGradientBrush(body, WithAlpha(Shade(fill, 0.035f), (int)(255 * alpha)), WithAlpha(Shade(fill, -0.02f), (int)(255 * alpha)), 90f))
                    g.FillPath(brush, path);
                // A hairline of light along the top edge, fading out by the middle.
                using (var light = new LinearGradientBrush(body, Color.FromArgb((int)(40 * alpha), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    light.Blend = new Blend { Factors = new[] { 0f, 1f, 1f }, Positions = new[] { 0f, 0.5f, 1f } };
                    using (var pen = new Pen(light, Math.Max(1f, S(1))))
                    using (var edge = RoundRect(RectangleF.Inflate(rect, -0.5f, -0.5f), rect.Height / 2 - 0.5f))
                        g.DrawPath(pen, edge);
                }
            }
        }

        /// Light from the wallpaper's accent peeking out under a bubble, the same as YASB's system bubble on
        /// the right (styles.css, .grouper.system): a gradient into the lower half and a lit lower rim.
        void Underglow(RectangleF rect)
        {
            var body = new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2);
            using (var path = RoundRect(rect, rect.Height / 2))
            using (var glow = new LinearGradientBrush(body, WithAlpha(colors.Accent, 0), WithAlpha(colors.Accent, 115), 90f))
            {
                glow.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.55f, 1f } };
                g.FillPath(glow, path);
            }
            GraphicsState saved = g.Save();
            g.SetClip(new RectangleF(rect.X - 1, rect.Y + rect.Height / 2, rect.Width + 2, rect.Height / 2 + 1));
            using (var edge = RoundRect(RectangleF.Inflate(rect, -0.5f, -0.5f), rect.Height / 2 - 0.5f))
            using (var pen = new Pen(WithAlpha(colors.Accent, 217), Math.Max(1f, S(1))))
                g.DrawPath(pen, edge);
            g.Restore(saved);
        }

        /// Soft light spilling out around a bubble: a blurred pill in `colour`, `strength` 0 to 1, reaching
        /// `reach` logical pixels. One small bitmap per size: its left cap, its middle column stretched to
        /// the bubble's width, its right cap. Draw it before the bubble, which covers the inside.
        void Glow(RectangleF rect, Color colour, float strength, float reach)
        {
            if (!drawing || strength <= 0.01f) return;
            int pad = Math.Max(1, (int)Math.Round(S(reach))), height = Math.Max(2, (int)Math.Round(rect.Height));
            long key = ((long)height << 16) | (uint)pad;
            Bitmap sprite;
            if (!blooms.TryGetValue(key, out sprite))
            {
                sprite = Effects.PillBloom(height, pad);
                blooms[key] = sprite;
            }
            int w = sprite.Width, h = sprite.Height, cap = pad + height / 2 + 1, rightCap = w - cap - 1;
            float left = rect.X - pad, top = rect.Y + (rect.Height - height) / 2f - pad;
            float middle = rect.Width + 2 * pad - cap - rightCap;
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix { Matrix00 = colour.R / 255f, Matrix11 = colour.G / 255f, Matrix22 = colour.B / 255f, Matrix33 = Math.Min(1, strength) });
                // Mirrors at the edges, so the stretched column stays one even colour.
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                if (middle < 0)
                {
                    Blit(sprite, new RectangleF(left, top, rect.Width + 2 * pad, h), new Rectangle(0, 0, w, h), attributes);
                    return;
                }
                Blit(sprite, new RectangleF(left, top, cap, h), new Rectangle(0, 0, cap, h), attributes);
                Blit(sprite, new RectangleF(left + cap, top, middle, h), new Rectangle(cap, 0, 1, h), attributes);
                Blit(sprite, new RectangleF(left + cap + middle, top, rightCap, h), new Rectangle(cap + 1, 0, rightCap, h), attributes);
            }
        }

        void Blit(Bitmap bitmap, RectangleF to, Rectangle from, ImageAttributes attributes)
        {
            var corners = new[] { new PointF(to.Left, to.Top), new PointF(to.Right, to.Top), new PointF(to.Left, to.Bottom) };
            g.DrawImage(bitmap, corners, from, GraphicsUnit.Pixel, attributes);
        }

        /// A pool of the app's own light on the bubble floor under the focused icon.
        void LightPool(RectangleF icon, Color colour, float alpha)
        {
            if (!drawing) return;
            float w = icon.Width * 1.7f, h = S(8);
            var ellipse = new RectangleF(icon.X + icon.Width / 2 - w / 2, icon.Bottom - h * 0.45f, w, h);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(ellipse);
                using (var brush = new PathGradientBrush(path) { CenterColor = WithAlpha(colour, (int)(150 * alpha)), SurroundColors = new[] { WithAlpha(colour, 0) } })
                    g.FillPath(brush, path);
            }
        }

        /// The focused workspace reads as deeper: a darker well with a shadow under its top edge, light
        /// on its lower lip and an accent rim, glowing around it: wide in `glow` (the focused app's colour)
        /// and tight in the accent.
        void DeepBubble(RectangleF rect, Color glow)
        {
            float radius = rect.Height / 2;
            // On a monitor komorebi is not focused on, the current workspace glows at half strength.
            float strength = MonitorFocused || view == null ? 1 : 0.5f;
            Glow(rect, glow, 0.8f * strength, 9);
            Glow(rect, colors.Accent, 0.6f * strength, 3.5f);

            var body = new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2);
            using (var path = RoundRect(rect, radius))
            {
                using (var fill = new LinearGradientBrush(body, Shade(colors.Surface, -0.03f), colors.SurfaceHigh, 90f))
                    g.FillPath(fill, path);
                using (var shadow = new LinearGradientBrush(body, Color.FromArgb(130, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                {
                    shadow.Blend = new Blend { Factors = new[] { 0f, 1f, 1f }, Positions = new[] { 0f, 0.45f, 1f } };
                    g.FillPath(shadow, path);
                }
            }
            using (var edge = RoundRect(RectangleF.Inflate(rect, -0.5f, -0.5f), radius - 0.5f))
            {
                using (var lip = new LinearGradientBrush(body, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(50, 255, 255, 255), 90f))
                {
                    lip.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.6f, 1f } };
                    using (var pen = new Pen(lip, Math.Max(1f, S(1)))) g.DrawPath(pen, edge);
                }
                using (var pen = new Pen(WithAlpha(colors.Accent, 120), Math.Max(1f, S(1)))) g.DrawPath(pen, edge);
            }
        }

        /// Workspaces out of focus sit shallow: a flat, dimmer surface with only a hairline edge.
        void ShallowBubble(RectangleF rect, bool hovered)
        {
            if (hovered) Glow(rect, colors.Accent, 0.45f, 6);
            using (var path = RoundRect(rect, rect.Height / 2))
            using (var fill = new SolidBrush(WithAlpha(hovered ? colors.SurfaceHigh : colors.Surface, hovered ? 230 : 190)))
                g.FillPath(fill, path);
            using (var edge = RoundRect(RectangleF.Inflate(rect, -0.5f, -0.5f), rect.Height / 2 - 0.5f))
            using (var pen = new Pen(Color.FromArgb(hovered ? 26 : 14, 255, 255, 255), Math.Max(1f, S(1))))
                g.DrawPath(pen, edge);
        }

        void AppIcon(long hwnd, string exe, RectangleF rect, int pixels, float opacity, Color fallback)
        {
            Bitmap bitmap = icons.Get(hwnd, pixels);
            if (bitmap == null)
            {
                // No icon to be had: a lettered disc in the stack colour. Packaged apps all run in
                // ApplicationFrameHost, so their title says more than their exe.
                bool packaged = string.Equals(exe, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                string name = string.IsNullOrEmpty(exe) || packaged ? Native.WindowTitle(new IntPtr(hwnd)) : exe;
                string letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
                using (var brush = new SolidBrush(WithAlpha(fallback, (int)(160 * opacity)))) g.FillEllipse(brush, rect);
                Caption(letter, smallFont, WithAlpha(colors.Ink, (int)(255 * opacity)), rect, 1);
                return;
            }
            Faded(bitmap, new Rectangle((int)Math.Round(rect.X), (int)Math.Round(rect.Y), pixels, pixels), opacity);
        }

        void Faded(Bitmap bitmap, Rectangle destination, float opacity)
        {
            if (opacity >= 0.999f)
            {
                g.DrawImage(bitmap, destination);
                return;
            }
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                g.DrawImage(bitmap, destination, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        void Badge(string text, RectangleF icon, float alpha)
        {
            var rect = new RectangleF(icon.X, icon.Y, icon.Width, icon.Height);
            using (var brush = new SolidBrush(Color.FromArgb((int)(200 * alpha), 0, 0, 0))) g.FillEllipse(brush, rect);
            Caption(text, smallFont, colors.Ink, rect, alpha);
        }

        void Caption(string text, Font font, Color colour, RectangleF rect, float alpha)
        {
            using (var brush = new SolidBrush(WithAlpha(colour, (int)(colour.A * alpha))))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, brush, rect, format);
        }

        float TextWidth(string text, Font font)
        {
            string key = font.Name + "|" + text;
            float width;
            if (!textWidths.TryGetValue(key, out width))
            {
                width = measure.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                textWidths[key] = width;
            }
            return width;
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
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

        static Color WithAlpha(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B); }

        static Color Shade(Color c, float amount)
        {
            int delta = (int)Math.Round(255 * amount);
            return Color.FromArgb(c.A, Clamp(c.R + delta), Clamp(c.G + delta), Clamp(c.B + delta));
        }

        static int Clamp(int value) { return Math.Max(0, Math.Min(255, value)); }

        // ---- input ----

        void AddHit(HitKind kind, RectangleF bounds, int workspace = -1, long hwnd = 0, long stackHwnd = 0)
        {
            hits.Add(new Hit { Kind = kind, Bounds = bounds, Workspace = workspace, Hwnd = hwnd, StackHwnd = stackHwnd });
        }

        Hit HitAt(Point point)
        {
            // Last added wins, so icons beat the workspace bubble they sit in.
            for (int i = hits.Count - 1; i >= 0; i--)
                if (hits[i].Bounds.Contains(point)) return hits[i];
            return null;
        }

        bool IsHovered(HitKind kind) { return hover != null && hover.Kind == kind; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hit hit = HitAt(e.Location);
            bool changed = !SameHit(hit, hover);
            hover = hit;

            int workspace = -1;
            foreach (var h in hits)
                if (h.Kind == HitKind.Workspace && h.Bounds.Contains(e.Location)) workspace = h.Workspace;
            bool overTray = hits.Any(h => (h.Kind == HitKind.Tray || h.Kind == HitKind.TrayWindow) && h.Bounds.Contains(e.Location));

            if (workspace != hoverWorkspace || overTray != hoverTray)
            {
                hoverWorkspace = workspace;
                hoverTray = overTray;
                Animate();
            }
            else if (changed)
            {
                Render();
            }

            if (changed)
            {
                tip.Hide(this);
                tipDelay.Stop();
                if (hit != null) tipDelay.Start();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null;
            hoverWorkspace = -1;
            hoverTray = false;
            tipDelay.Stop();
            tip.Hide(this);
            Animate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Hit hit = HitAt(e.Location);
            if (hit == null) return;
            tip.Hide(this);

            bool left = e.Button == MouseButtons.Left, right = e.Button == MouseButtons.Right, middle = e.Button == MouseButtons.Middle;
            switch (hit.Kind)
            {
                case HitKind.Home:
                    if (left) dispatch(new[] { "start-menu" });
                    if (right) ShowHomeMenu(hit.Bounds);
                    break;
                case HitKind.Keyboard:
                    if (left) dispatch(new[] { "keyboard" });
                    if (right) ShowHomeMenu(hit.Bounds);
                    break;
                case HitKind.Offline:
                    if (left) dispatch(new[] { "start-wm" });
                    break;
                case HitKind.Workspace:
                    if (left) GoTo(hit.Workspace);
                    if (right) ShowWorkspaceMenu(hit);
                    break;
                case HitKind.Window:
                    if (left) dispatch(new[] { "focus-window", hit.Hwnd.ToString() });
                    if (middle) dispatch(new[] { "window", hit.Hwnd.ToString(), "new" });
                    if (right) ShowWindowMenu(hit);
                    break;
                case HitKind.Map:
                    // The map acts on the focused workspace, so bring this monitor into focus first.
                    if (left || right || middle) FocusThisMonitor();
                    if (left) dispatch(new[] { "snap" });
                    if (right) dispatch(new[] { "layout", "next" });
                    if (middle) dispatch(new[] { "layout", "flip" });
                    break;
                case HitKind.Tray:
                    if (left) dispatch(new[] { "restore" });
                    if (right) dispatch(new[] { "restore-all" });
                    break;
                case HitKind.TrayWindow:
                    if (left) dispatch(new[] { "restore", hit.Hwnd.ToString() });
                    if (right) ShowTrayMenu(hit);
                    break;
                case HitKind.LooseWindow:
                    if (left) dispatch(new[] { "tile", hit.Hwnd.ToString() });
                    if (right) ShowLooseMenu(hit);
                    break;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            string direction = e.Delta > 0 ? "previous" : "next";
            Hit hit = HitAt(e.Location);
            if (hit == null) return;

            if (hit.Kind == HitKind.Map)
            {
                FocusThisMonitor();
                dispatch(new[] { "layout", direction });
            }
            else if (hit.Kind == HitKind.Tray || hit.Kind == HitKind.TrayWindow)
                dispatch(new[] { "tray", direction });
            else if (hit.Kind == HitKind.Window && hit.StackHwnd != 0)
            {
                // Scrolling a stack flips through its windows (its tabs).
                dispatch(new[] { "focus-window", hit.StackHwnd.ToString() });
                dispatch(new[] { "cycle-tab", direction });
            }
            else
                Cycle(e.Delta > 0 ? -1 : 1);
        }

        static bool SameHit(Hit a, Hit b)
        {
            if (a == null || b == null) return a == b;
            return a.Kind == b.Kind && a.Workspace == b.Workspace && a.Hwnd == b.Hwnd;
        }

        void ShowTip()
        {
            if (menuOpen) return;
            tipText = TipFor(hover);
            if (string.IsNullOrEmpty(tipText)) return;
            tip.Show(tipText, this, (int)hover.Bounds.X, (int)(B(settings.BarHeight) + S(4)));
        }

        string TipFor(Hit hit)
        {
            if (hit == null || view == null && hit.Kind != HitKind.Home && hit.Kind != HitKind.Keyboard && hit.Kind != HitKind.Tray) return null;
            switch (hit.Kind)
            {
                case HitKind.Home:
                    return "Start menu  ·  right-click: deskhud menu";
                case HitKind.Keyboard:
                    return "Touch keyboard  ·  tap to show or hide";
                case HitKind.Workspace:
                    var ws = view.Workspaces.FirstOrDefault(w => w.Index == hit.Workspace);
                    if (ws == null) return null;
                    int count = ws.Containers.Sum(c => c.Windows.Count) + ws.Floating.Count;
                    string windows = count == 0 ? "empty" : count == 1 ? "1 window" : count + " windows";
                    return "Workspace " + NumberOf(ws) + "  ·  " + windows + (ws.Index == view.FocusedWorkspace ? "" : "  ·  click to go there")
                        + "  ·  right-click: layout";
                case HitKind.Map:
                    if (snapshot.Paused) return Wm.Current.Name + " is paused  ·  Win+Shift+P resumes";
                    var current = view.Current;
                    return "Layout: " + (current == null ? "?" : current.Monocle ? "monocle" : current.Layout) + "\n" + Wm.Current.MapHelp;
                case HitKind.Tray:
                    return "Minimized windows  ·  click: bring back  ·  right-click: all  ·  scroll: pick";
                case HitKind.Window:
                case HitKind.TrayWindow:
                case HitKind.LooseWindow:
                    string title = Native.WindowTitle(new IntPtr(hit.Hwnd));
                    if (string.IsNullOrEmpty(title)) return null;
                    if (hit.Kind == HitKind.LooseWindow) return "Not tiled: " + title + "  ·  click to tile it here  ·  right-click: menu";
                    return hit.Kind == HitKind.Window ? title + "\nMiddle-click: new window  ·  right-click: menu" : title;
                default:
                    return null;
            }
        }

        void DrawTip(object sender, DrawToolTipEventArgs e)
        {
            using (var back = new SolidBrush(Color.FromArgb(0x12, 0x13, 0x15)))
            using (var border = new Pen(Color.FromArgb(0x2b, 0x2c, 0x2e)))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
                e.Graphics.DrawRectangle(border, new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1));
            }
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, tipFont, e.Bounds, colors.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ---- this island's monitor ----

        /// Workspace `index` on this island's own monitor (a plain focus-workspace would use whichever
        /// monitor has focus).
        void GoTo(int index)
        {
            if (view == null) return;
            dispatch(new[] { "go-to", view.Index.ToString(), index.ToString() });
        }

        void FocusThisMonitor()
        {
            if (view != null && !MonitorFocused) dispatch(new[] { "focus-monitor", view.Index.ToString() });
        }

        /// Scrolling: the next or previous workspace the island shows, in number order, wrapping round.
        void Cycle(int step)
        {
            if (view == null) return;
            var shown = ShownWorkspaces().ToList();
            int at = shown.FindIndex(w => w.Index == view.FocusedWorkspace);
            if (shown.Count < 2 || at < 0) return;
            GoTo(shown[((at + step) % shown.Count + shown.Count) % shown.Count].Index);
        }

        /// The workspaces this island shows, in number order: those with windows, and the one on show.
        /// Empty ones stay hidden (they hold no number, Numbers.cs) until something lands in them.
        List<WorkspaceView> ShownWorkspaces()
        {
            return view.Workspaces.Where(ws => !ws.IsEmpty || ws.Index == view.FocusedWorkspace).OrderBy(NumberOf).ToList();
        }

        // ---- right-click menus (drawn by Menus.cs; the actions are deskhud verbs) ----

        IslandMenu NewMenu()
        {
            return new IslandMenu(colors, scale, tablet);
        }

        /// Tooltips stay away while a menu is open; they would land on top of it.
        void ShowMenu(IslandMenu menu, RectangleF anchor)
        {
            menuOpen = true;
            tipDelay.Stop();
            tip.Hide(this);
            menu.Show(this, new Point((int)anchor.X, (int)(B(settings.BarHeight) + S(4))), () => menuOpen = false);
        }

        void ShowHomeMenu(RectangleF anchor)
        {
            var menu = NewMenu();
            menu.Item("Touch keyboard", Glyphs.Keyboard, colors.Stack[2], () => dispatch(new[] { "keyboard" }));
            menu.Item("Next wallpaper", Glyphs.Wallpaper, colors.NextWall, () => dispatch(new[] { "wallpaper", "next", "here" }));
            menu.Item("Open settings.json", Glyphs.Settings, colors.InkSoft, () => dispatch(new[] { "settings" }));
            menu.Separator();
            menu.Item("Restart " + Wm.Current.Name, Glyphs.Refresh, colors.Stack[1], () => dispatch(new[] { "restart-wm" }));
            menu.Item("Reload YASB", Glyphs.Bar, colors.Stack[3], () => dispatch(new[] { "reload-yasb" }));
            menu.Separator();
            menu.Item("Quit deskhud", Glyphs.Power, colors.Negative, () => dispatch(new[] { "quit" }));
            ShowMenu(menu, anchor);
        }

        /// An app icon: everything you can do to that one window.
        void ShowWindowMenu(Hit hit)
        {
            if (view == null) return;
            string id = hit.Hwnd.ToString();
            var ws = view.Workspaces.FirstOrDefault(w => w.Index == hit.Workspace);
            if (ws == null) return;
            bool floating = ws.Floating.Any(w => w.Hwnd == hit.Hwnd);
            var container = ws.Containers.FirstOrDefault(c => c.Windows.Any(w => w.Hwnd == hit.Hwnd));
            Color tone = ContainerColour(ws, container == null ? ws.Containers.Count : ws.Containers.IndexOf(container));

            var menu = NewMenu();
            menu.Header(MenuTitle(hit.Hwnd), icons.Get(hit.Hwnd, (int)Math.Round(S(IconSize))), () => dispatch(new[] { "focus-window", id }));
            menu.Separator();
            menu.Item("New window", Glyphs.NewWindow, colors.Accent, () => dispatch(new[] { "window", id, "new" }));
            menu.Item("Minimize to tray", Glyphs.Minimize, colors.InkSoft, () => dispatch(new[] { "window", id, "minimize" }));
            menu.Item(floating ? "Tile it" : "Float it", floating ? Glyphs.Tile : Glyphs.Float, tone, () => dispatch(new[] { "window", id, "float" }));
            menu.Item("Fill the workspace (monocle)", Glyphs.Monocle, colors.Stack[2], () => dispatch(new[] { "window", id, "monocle" }));
            if (container != null && container.Windows.Count > 1)
                menu.Item(container.Tabbed ? "Pop out of the tabs" : "Pop out of the stack", Glyphs.Unstack, colors.Stack[1], () => dispatch(new[] { "window", id, "unstack" }));
            var move = menu.Submenu("Move to workspace", Glyphs.Workspaces, colors.Accent);
            // Every screen's numbered workspaces (moving to another screen's sends the window there),
            // then a new one on this screen with the lowest free number.
            var targets = snapshot.Numbered()
                .SelectMany(m => m.Workspaces.Where(w => w.Number > 0).Select(w => new { Monitor = m, Workspace = w, Number = w.Number }))
                .OrderBy(t => t.Number);
            foreach (var target in targets)
            {
                string number = target.Number.ToString();
                string where = target.Monitor.Index == view.Index ? "" : "  ·  " + Direction(target.Monitor.Bounds);
                menu.Child(move, "Workspace " + number + WindowCount(target.Workspace) + where, menu.Dot(colors.StackColor(target.Number - 1)),
                    target.Monitor.Index == view.Index && target.Workspace.Index == ws.Index,
                    () => dispatch(new[] { "window", id, "move", number }));
            }
            int free = snapshot.FreeNumber();
            string fresh = free.ToString();
            menu.Child(move, "Workspace " + fresh + "  ·  new", menu.Dot(colors.StackColor(free - 1)), false,
                () => dispatch(new[] { "window", id, "move", fresh }));
            menu.Item("Open file location", Glyphs.Folder, colors.Stack[3], () => dispatch(new[] { "window", id, "location" }));
            menu.Separator();
            menu.Item("Close window", Glyphs.Close, colors.Negative, () => dispatch(new[] { "window", id, "close" }));
            ShowMenu(menu, hit.Bounds);
        }

        /// A workspace bubble: go there, pick its layout.
        void ShowWorkspaceMenu(Hit hit)
        {
            if (view == null) return;
            var ws = view.Workspaces.FirstOrDefault(w => w.Index == hit.Workspace);
            if (ws == null) return;
            string index = ws.Index.ToString(), monitor = view.Index.ToString();

            var menu = NewMenu();
            menu.Header("Workspace " + NumberOf(ws) + WindowCount(ws), menu.Dot(colors.StackColor(ws.Index)), () => GoTo(ws.Index));
            menu.Separator();
            var layouts = menu.Submenu("Layout", Glyphs.Tile, colors.Accent);
            foreach (var layout in Wm.Current.Layouts)
            {
                string value = layout[1];
                menu.Child(layouts, layout[2], menu.Glyph(Glyphs.Tile, colors.InkSoft), !ws.Monocle && ws.Layout == layout[0],
                    () => dispatch(new[] { "workspace-layout", monitor, index, value }));
            }
            if (ws.Index == view.FocusedWorkspace && MonitorFocused)
            {
                menu.Item("Snap the split: 2/3, 1/3, 1/2", Glyphs.Monocle, colors.Stack[2], () => dispatch(new[] { "snap" }));
                menu.Item("Even out the sizes (retile)", Glyphs.Refresh, colors.Stack[1], () => dispatch(new[] { "retile" }));
            }
            ShowMenu(menu, hit.Bounds);
        }

        void ShowTrayMenu(Hit hit)
        {
            string id = hit.Hwnd.ToString();
            var menu = NewMenu();
            menu.Header(MenuTitle(hit.Hwnd), icons.Get(hit.Hwnd, (int)Math.Round(S(IconSize))), () => dispatch(new[] { "restore", id }));
            menu.Separator();
            menu.Item("Bring back", Glyphs.Restore, colors.Accent, () => dispatch(new[] { "restore", id }));
            menu.Item("Bring them all back", Glyphs.Restore, colors.Stack[1], () => dispatch(new[] { "restore-all" }));
            menu.Separator();
            menu.Item("Close window", Glyphs.Close, colors.Negative, () => dispatch(new[] { "window", id, "close" }));
            ShowMenu(menu, hit.Bounds);
        }

        void ShowLooseMenu(Hit hit)
        {
            string id = hit.Hwnd.ToString();
            var menu = NewMenu();
            menu.Header(MenuTitle(hit.Hwnd), icons.Get(hit.Hwnd, (int)Math.Round(S(IconSize))), () => dispatch(new[] { "focus-window", id }));
            menu.Separator();
            menu.Item("Tile it here", Glyphs.Tile, colors.Accent, () => dispatch(new[] { "tile", id }));
            menu.Item("Minimize", Glyphs.Minimize, colors.InkSoft, () => dispatch(new[] { "window", id, "minimize" }));
            menu.Item("Open file location", Glyphs.Folder, colors.Stack[3], () => dispatch(new[] { "window", id, "location" }));
            menu.Separator();
            menu.Item("Close window", Glyphs.Close, colors.Negative, () => dispatch(new[] { "window", id, "close" }));
            ShowMenu(menu, hit.Bounds);
        }

        /// The workspace's number across all screens (Numbers.cs), as Win+N counts.
        int NumberOf(WorkspaceView ws)
        {
            int n = snapshot != null && view != null ? snapshot.Number(view.Index, ws.Index) : 0;
            return n > 0 ? n : ws.Index + 1;
        }

        /// Where another monitor sits from this island's: "screen to the left" and so on.
        string Direction(Rectangle other)
        {
            Rectangle mine = view == null ? screen : view.Bounds;
            if (other.Right <= mine.Left) return "screen to the left";
            if (other.Left >= mine.Right) return "screen to the right";
            return other.Bottom <= mine.Top ? "screen above" : "screen below";
        }

        static string MenuTitle(long hwnd)
        {
            string title = Native.WindowTitle(new IntPtr(hwnd));
            if (string.IsNullOrEmpty(title)) return "(untitled window)";
            return title.Length <= 46 ? title : title.Substring(0, 45) + "…";
        }

        static string WindowCount(WorkspaceView ws)
        {
            int count = ws.Containers.Sum(c => c.Windows.Count) + ws.Floating.Count;
            return count == 0 ? "  ·  empty" : "  ·  " + count + (count == 1 ? " window" : " windows");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animation.Dispose();
                tipDelay.Dispose();
                tip.Dispose();
                icons.Dispose();
                ClearBlooms();
                measure.Dispose();
                measureSurface.Dispose();
                if (smallFont != null) smallFont.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
