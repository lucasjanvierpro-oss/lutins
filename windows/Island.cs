// L'encoche : une pastille noire en haut au centre de chaque écran, un perso par session.
// Au survol elle s'agrandit et liste les sessions. Clic sur un perso = ouvrir l'app, clic droit = réglages.
// Chaque encoche est une fenêtre « layered » (transparence par pixel) qui ne prend jamais le focus.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lutins
{
    enum IslandSize { Small, Medium, Large }

    /// Géométrie d'une encoche, en pixels d'écran. `Width`/`Height` sont la taille réelle (qui change pendant l'animation).
    sealed class IslandLayout
    {
        public int Pixel = 3;
        public int Count;
        public bool Expanded;
        public bool Attached = true;
        public float Scale = 1;
        public int Width, Height;

        public int U(double v) { return (int)Math.Round(v * Scale); }
        public int Ear { get { return U(10); } }
        public int RowHeight { get { return U(42); } }
        public int FooterHeight { get { return U(30); } }
        public int ListWidth { get { return U(340); } }

        public Size Sprite { get { return Skins.SizeFor(Pixel); } }
        public int Gap { get { return Pixel; } }
        public int Slots { get { return Math.Max(Count, 1); } }
        public int RowWidth { get { return Slots * Sprite.Width + (Slots - 1) * Gap; } }
        public int CollapsedHeight { get { return Pixel + Sprite.Height + (int)Math.Round(Pixel * 1.5); } }
        public int ListTop { get { return CollapsedHeight + U(6); } }

        public Size Preferred
        {
            get
            {
                int body = RowWidth + Pixel * 8;
                int h = CollapsedHeight;
                if (Expanded)
                {
                    body = Math.Max(body, ListWidth);
                    h = ListTop + Math.Max(Count, 1) * RowHeight + FooterHeight + U(6);
                }
                return new Size(body + 2 * Ear, h);
            }
        }

        public Rectangle SpriteRect(int i)
        {
            int x = (Width - RowWidth) / 2 + i * (Sprite.Width + Gap);
            return new Rectangle(x, Pixel, Sprite.Width, Sprite.Height);
        }

        public Rectangle RowRect(int i)
        {
            return new Rectangle(Ear + U(8), ListTop + i * RowHeight, Width - 2 * Ear - U(16), RowHeight);
        }

        public Rectangle FooterRect
        {
            get { return new Rectangle(Ear + U(8), ListTop + Math.Max(Count, 1) * RowHeight, Width - 2 * Ear - U(16), FooterHeight); }
        }

        public Rectangle OptionsRect
        {
            get
            {
                var f = FooterRect;
                return new Rectangle(f.Right - U(30), f.Top + f.Height / 2 - U(11), U(30), U(22));
            }
        }

        public Rectangle GripRect
        {
            get
            {
                var o = OptionsRect;
                o.Offset(-U(34), 0);
                return o;
            }
        }

        public int SpriteAt(Point p)
        {
            for (int i = 0; i < Count; i++)
            {
                var r = SpriteRect(i);
                r.Inflate(Gap / 2, 0);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        public int RowAt(Point p)
        {
            if (!Expanded) return -1;
            for (int i = 0; i < Count; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        /// Collée en haut : bord du haut contre l'écran, petites « oreilles » concaves, coins arrondis en bas.
        /// Posée librement : une pastille arrondie partout.
        public GraphicsPath Shape()
        {
            float w = Width, h = Height, e = Ear;
            if (!Attached)
            {
                float r = Math.Min(U(Expanded ? 18 : 14), h / 2);
                var path = new GraphicsPath();
                var rect = new RectangleF(e, 0, w - 2 * e, h);
                path.AddArc(rect.Left, rect.Top, 2 * r, 2 * r, 180, 90);
                path.AddArc(rect.Right - 2 * r, rect.Top, 2 * r, 2 * r, 270, 90);
                path.AddArc(rect.Right - 2 * r, rect.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                path.CloseFigure();
                return path;
            }
            var edge = AttachedEdge();
            edge.CloseFigure();
            return edge;
        }

        /// Le contour sans le bord du haut (collé à l'écran), pour l'illuminer.
        public GraphicsPath Outline() { return Attached ? AttachedEdge() : Shape(); }

        GraphicsPath AttachedEdge()
        {
            float w = Width, h = Height, e = Ear;
            float r = Math.Min(U(Expanded ? 18 : 14), (h - e) / 2 + U(4));
            var p = new GraphicsPath();
            p.AddBezier(new PointF(0, 0), new PointF(e * 0.6f, 0), new PointF(e, e * 0.4f), new PointF(e, e));
            p.AddLine(e, e, e, h - r);
            p.AddBezier(new PointF(e, h - r), new PointF(e, h - r * 0.45f), new PointF(e + r * 0.45f, h), new PointF(e + r, h));
            p.AddLine(e + r, h, w - e - r, h);
            p.AddBezier(new PointF(w - e - r, h), new PointF(w - e - r * 0.45f, h), new PointF(w - e, h - r * 0.45f), new PointF(w - e, h - r));
            p.AddLine(w - e, h - r, w - e, e);
            p.AddBezier(new PointF(w - e, e), new PointF(w - e, e * 0.4f), new PointF(w - e * 0.6f, 0), new PointF(w, 0));
            return p;
        }
    }

    /// Où se trouve l'encoche sur son écran : collée en haut (`X` = centre, en fraction de la largeur)
    /// ou posée librement (`X` = centre et `Y` = distance du haut de l'écran, en pixels).
    sealed class Placement
    {
        public bool Free;
        public double X = 0.5;
        public double Y;

        public static string Key(Screen screen) { return "placement." + screen.DeviceName; }

        public static Placement Load(Screen screen)
        {
            var p = new Placement();
            var parts = Prefs.Get(Key(screen), "").Split(';');
            double x, y;
            if (parts.Length == 3
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                p.Free = parts[0] == "1";
                p.X = x;
                p.Y = y;
            }
            return p;
        }

        public void Save(Screen screen)
        {
            Prefs.Set(Key(screen), (Free ? "1" : "0") + ";" + X.ToString("R", CultureInfo.InvariantCulture)
                                   + ";" + Y.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// Ce que la souris survole dans une encoche.
    sealed class Hover
    {
        public int Row = -1;
        public bool Options;
        public bool Grip;
    }

    sealed class IslandWindow : Form
    {
        public readonly Screen Screen;
        public readonly float DpiScale;
        public IslandLayout Geometry = new IslandLayout();
        public Placement Placement;
        public readonly Hover Hover = new Hover();
        /// Position et taille affichées, et celles visées (l'encoche glisse vers sa nouvelle taille).
        public RectangleF Current;
        public Rectangle Target;
        public RectangleF AnimationFrom;
        public DateTime AnimationStart = DateTime.MinValue;
        public bool Dirty = true;
        public Action<IslandWindow, MouseEventArgs> MouseDownHandler = delegate { };
        public Action<IslandWindow, MouseEventArgs> MouseMoveHandler = delegate { };
        public Action<IslandWindow, MouseEventArgs> MouseUpHandler = delegate { };

        public IslandWindow(Screen screen)
        {
            Screen = screen;
            DpiScale = Native.ScaleOf(screen);
            Placement = Placement.Load(screen);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Lutins";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            // Un clic ne vole jamais le focus à la fenêtre où tu travailles.
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e) { MouseDownHandler(this, e); }
        protected override void OnMouseMove(MouseEventArgs e) { MouseMoveHandler(this, e); }
        protected override void OnMouseUp(MouseEventArgs e) { MouseUpHandler(this, e); }

        public Rectangle Bounds2
        {
            get { return Rectangle.Round(Current); }
        }

        /// Affiche l'image (avec sa transparence) à la position voulue.
        public void Present(Bitmap bitmap, Point position)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBitmap);
                var size = new Native.SIZE { cx = bitmap.Width, cy = bitmap.Height };
                var source = new Native.POINT { x = 0, y = 0 };
                var top = new Native.POINT { x = position.X, y = position.Y };
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, ref top, ref size, memDc, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
            }
        }

        public void KeepOnTop()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }
    }

    sealed class Island
    {
        readonly Store store;
        readonly List<IslandWindow> windows = new List<IslandWindow>();
        IslandWindow expanded, dragged;
        readonly Timer timer = new Timer { Interval = 16 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int frame = -1;
        int lastTopmost;
        DateTime? expandAt, collapseAt;
        IslandWindow expandCandidate;
        Point dragMouse;
        Point dragOrigin;
        bool mouseDown, dragging;
        public bool MenuOpen;
        public string Problem;
        public Func<ContextMenuStrip> OptionsMenu = () => new ContextMenuStrip();
        public Action<Session> OnOpen = delegate { };
        public string SizeOverride;
        public string SkinOverride;

        public Island(Store store)
        {
            this.store = store;
            timer.Tick += delegate { Tick(); };
        }

        public string SkinChoice
        {
            get
            {
                string choice = SkinOverride ?? Prefs.Get("skin", "lutin");
                return Skins.IsValid(choice) ? choice : "lutin";
            }
        }

        public IslandSize SizeChoice
        {
            get
            {
                string value = SizeOverride ?? Prefs.Get("size", "medium");
                return value == "small" ? IslandSize.Small : value == "large" ? IslandSize.Large : IslandSize.Medium;
            }
        }

        public static int BasePixel(IslandSize size) { return size == IslandSize.Small ? 2 : size == IslandSize.Large ? 4 : 3; }

        public static List<Screen> ChosenScreens()
        {
            string choice = Prefs.Get("screen", "all");
            var picked = Screen.AllScreens.Where(s => s.DeviceName == choice).ToList();
            return picked.Count > 0 ? picked : Screen.AllScreens.ToList();
        }

        /// Une encoche par écran choisi (tous par défaut).
        public void Rebuild()
        {
            foreach (var w in windows) { w.Close(); w.Dispose(); }
            windows.Clear();
            expanded = dragged = null;
            foreach (var screen in ChosenScreens())
            {
                var w = new IslandWindow(screen);
                w.MouseDownHandler = MouseDown;
                w.MouseMoveHandler = MouseMove;
                w.MouseUpHandler = MouseUp;
                windows.Add(w);
            }
            Refresh(false);
            foreach (var w in windows)
            {
                w.Show();
                Render(w);
                w.KeepOnTop();
            }
            timer.Start();
        }

        public void Refresh() { Refresh(true); }

        public void Refresh(bool animated)
        {
            foreach (var w in windows)
            {
                w.Geometry = LayoutFor(w, w == expanded);
                var target = FrameFor(w, w.Geometry.Preferred);
                if (w != dragged && target != w.Target)
                {
                    w.Target = target;
                    if (animated && w.Current.Width > 0)
                    {
                        w.AnimationFrom = w.Current;
                        w.AnimationStart = DateTime.UtcNow;
                    }
                    else
                    {
                        w.Current = target;
                        w.AnimationStart = DateTime.MinValue;
                    }
                }
                w.Dirty = true;
            }
        }

        IslandLayout LayoutFor(IslandWindow w, bool isExpanded)
        {
            return new IslandLayout
            {
                Scale = w.DpiScale,
                Pixel = Math.Max(1, (int)Math.Round(BasePixel(SizeChoice) * w.DpiScale)),
                Count = store.Sessions.Count,
                Expanded = isExpanded,
                Attached = !w.Placement.Free,
            };
        }

        static Rectangle FrameFor(IslandWindow w, Size size)
        {
            var s = w.Screen.Bounds;
            var p = w.Placement;
            double centerX = p.Free ? s.Left + p.X : s.Left + p.X * s.Width;
            double top = p.Free ? s.Top + p.Y : s.Top;
            double x = Math.Min(Math.Max(centerX - size.Width / 2.0, s.Left), s.Right - size.Width);
            double y = Math.Min(Math.Max(top, s.Top), s.Bottom - size.Height);
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), size.Width, size.Height);
        }

        /// Où sont les encoches, pour le diagnostic (`/state`).
        public List<object> Describe()
        {
            var list = new List<object>();
            foreach (var w in windows)
            {
                var r = w.Bounds2;
                var o = new JObj();
                o["screen"] = w.Screen.DeviceName;
                o["x"] = r.X;
                o["y"] = r.Y;
                o["width"] = r.Width;
                o["height"] = r.Height;
                o["scale"] = (double)w.DpiScale;
                o["expanded"] = w == expanded;
                o["free"] = w.Placement.Free;
                list.Add(o);
            }
            return list;
        }

        public void ResetPlacements()
        {
            foreach (var screen in Screen.AllScreens) Prefs.Set(Placement.Key(screen), null);
            foreach (var w in windows) w.Placement = new Placement();
            Refresh();
        }

        // MARK: Animation

        void Tick()
        {
            int newFrame = (int)(clock.ElapsedMilliseconds / (long)(Effects.FrameDuration * 1000));
            bool nextFrame = newFrame != frame;
            frame = newFrame;
            PollMouse();
            bool animating = store.Sessions.Count > 0;
            foreach (var w in windows)
            {
                bool moving = Animate(w);
                if (w.Dirty || moving || (nextFrame && (animating || w == expanded))) Render(w);
            }
            // D'autres fenêtres « toujours au-dessus » peuvent passer devant : on repasse au-dessus de temps en temps.
            int seconds = (int)(clock.ElapsedMilliseconds / 2000);
            if (seconds != lastTopmost)
            {
                lastTopmost = seconds;
                foreach (var w in windows) w.KeepOnTop();
            }
        }

        static bool Animate(IslandWindow w)
        {
            if (w.AnimationStart == DateTime.MinValue) return false;
            double t = (DateTime.UtcNow - w.AnimationStart).TotalSeconds / 0.18;
            if (t >= 1)
            {
                w.Current = w.Target;
                w.AnimationStart = DateTime.MinValue;
                return true;
            }
            double k = 1 - Math.Pow(1 - t, 3);
            var a = w.AnimationFrom;
            var b = w.Target;
            w.Current = new RectangleF((float)(a.X + (b.X - a.X) * k), (float)(a.Y + (b.Y - a.Y) * k),
                                       (float)(a.Width + (b.Width - a.Width) * k), (float)(a.Height + (b.Height - a.Height) * k));
            return true;
        }

        void Render(IslandWindow w)
        {
            w.Dirty = false;
            var rect = w.Bounds2;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var layout = LayoutFor(w, w == expanded);
            layout.Width = rect.Width;
            layout.Height = rect.Height;
            if (w == dragged) layout.Attached = !w.Placement.Free;
            w.Geometry = layout;
            using (var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    Painter.Draw(g, layout, store.Ordered, SkinChoice, Problem, Math.Max(frame, 0), w.Hover);
                }
                w.Present(bitmap, rect.Location);
            }
        }

        // MARK: Souris

        IslandWindow WindowAt(Point p)
        {
            return windows.FirstOrDefault(w => w.Bounds2.Contains(p));
        }

        /// Survol : l'encoche se déplie après 0,35 s dessus et se replie 0,3 s après l'avoir quittée.
        void PollMouse()
        {
            Point mouse = Cursor.Position;
            var over = dragging ? dragged : WindowAt(mouse);
            DateTime now = DateTime.UtcNow;
            if (over != null)
            {
                collapseAt = null;
                if (expanded != over && !dragging)
                {
                    if (expandCandidate != over) { expandCandidate = over; expandAt = now.AddSeconds(0.35); }
                    else if (expandAt.HasValue && now >= expandAt.Value)
                    {
                        expanded = over;
                        expandAt = null;
                        Refresh();
                    }
                }
            }
            else
            {
                expandCandidate = null;
                expandAt = null;
                if (expanded != null && !MenuOpen)
                {
                    if (!collapseAt.HasValue) collapseAt = now.AddSeconds(0.3);
                    else if (now >= collapseAt.Value)
                    {
                        expanded = null;
                        collapseAt = null;
                        Refresh();
                    }
                }
            }
            foreach (var w in windows)
            {
                var local = new Point(mouse.X - w.Bounds2.X, mouse.Y - w.Bounds2.Y);
                bool inside = w == over;
                var l = w.Geometry;
                int row = inside ? l.RowAt(local) : -1;
                bool options = inside && l.Expanded && l.OptionsRect.Contains(local);
                bool grip = inside && l.Expanded && l.GripRect.Contains(local);
                if (row != w.Hover.Row || options != w.Hover.Options || grip != w.Hover.Grip)
                {
                    w.Hover.Row = row;
                    w.Hover.Options = options;
                    w.Hover.Grip = grip;
                    w.Cursor = grip ? Cursors.SizeAll : options || row >= 0 ? Cursors.Hand : Cursors.Default;
                    w.Dirty = true;
                }
            }
        }

        // Un clic ouvre la session ; un glisser (depuis la poignée ou n'importe où) déplace l'encoche.
        void MouseDown(IslandWindow w, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowOptions(w, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            dragging = false;
            dragMouse = Cursor.Position;
            dragOrigin = w.Bounds2.Location;
        }

        void MouseMove(IslandWindow w, MouseEventArgs e)
        {
            if (!mouseDown || (e.Button & MouseButtons.Left) == 0) return;
            Point mouse = Cursor.Position;
            int dx = mouse.X - dragMouse.X, dy = mouse.Y - dragMouse.Y;
            if (!dragging && Math.Sqrt(dx * dx + dy * dy) < 4 * w.DpiScale) return;
            dragging = true;
            dragged = w;
            var s = w.Screen.Bounds;
            var f = w.Bounds2;
            int x = Math.Min(Math.Max(dragOrigin.X + dx, s.Left), s.Right - f.Width);
            int y = Math.Min(Math.Max(dragOrigin.Y + dy, s.Top), s.Bottom - f.Height);
            // Près du haut : elle se recolle (et s'aimante au centre). Ailleurs : posée librement.
            bool attached = y - s.Top < 40 * w.DpiScale;
            if (attached)
            {
                y = s.Top;
                if (Math.Abs(x + f.Width / 2 - (s.Left + s.Width / 2)) < 24 * w.DpiScale) x = s.Left + s.Width / 2 - f.Width / 2;
                w.Placement = new Placement { Free = false, X = (x + f.Width / 2.0 - s.Left) / s.Width, Y = 0 };
            }
            else
            {
                w.Placement = new Placement { Free = true, X = x + f.Width / 2.0 - s.Left, Y = y - s.Top };
            }
            w.Current = new RectangleF(x, y, f.Width, f.Height);
            w.Target = Rectangle.Round(w.Current);
            w.AnimationStart = DateTime.MinValue;
            Render(w);
        }

        void MouseUp(IslandWindow w, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (dragging)
            {
                dragging = false;
                dragged = null;
                w.Placement.Save(w.Screen);
                Refresh();
                return;
            }
            Click(w, e.Location);
        }

        void Click(IslandWindow w, Point p)
        {
            var l = w.Geometry;
            if (l.Expanded && l.OptionsRect.Contains(p))
            {
                ShowOptions(w, new Point(l.OptionsRect.Left, l.OptionsRect.Bottom));
                return;
            }
            var sessions = store.Ordered;
            int i = l.RowAt(p);
            if (i < 0) i = l.SpriteAt(p);
            if (i >= 0 && i < sessions.Count) OnOpen(sessions[i]);
        }

        /// Le menu s'ouvre sous l'encoche, pour ne pas passer dessous.
        void ShowOptions(IslandWindow w, Point p)
        {
            var menu = OptionsMenu();
            MenuOpen = true;
            menu.Closed += delegate { MenuOpen = false; };
            // Sans ça, le menu ne se fermerait pas en cliquant ailleurs (l'encoche ne prend jamais le focus).
            Native.SetForegroundWindow(w.Handle);
            var r = w.Bounds2;
            menu.Show(new Point(r.Left + p.X, r.Bottom + 2));
        }

        public static Bitmap Snapshot(Store store, string skin, IslandSize size, bool expandedView, float scale)
        {
            var layout = new IslandLayout
            {
                Scale = scale,
                Pixel = Math.Max(1, (int)Math.Round(BasePixel(size) * scale)),
                Count = store.Sessions.Count,
                Expanded = expandedView,
            };
            var preferred = layout.Preferred;
            layout.Width = preferred.Width;
            layout.Height = preferred.Height;
            var bitmap = new Bitmap(preferred.Width, preferred.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Painter.Draw(g, layout, store.Ordered, skin, null, 2, new Hover { Row = expandedView ? 0 : -1 });
            }
            return bitmap;
        }
    }

    // MARK: Dessin

    static class Painter
    {
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        static Font FontOf(float size, bool bold)
        {
            string key = size.ToString(CultureInfo.InvariantCulture) + (bold ? "b" : "");
            Font font;
            if (!fonts.TryGetValue(key, out font))
            {
                try { font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch (ArgumentException) { font = new Font(FontFamily.GenericSansSerif, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
                fonts[key] = font;
            }
            return font;
        }

        public static void Draw(Graphics g, IslandLayout l, List<Session> sessions, string skinChoice, string problem, int frame, Hover hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var shape = l.Shape()) g.FillPath(Brushes.Black, shape);

            // Une session finie et pas encore vue : tout le contour de l'encoche pulse en vert.
            if (sessions.Any(s => s.State == LutinState.Done))
            {
                double strength = 0.45 + 0.55 * Effects.Pulse(frame, 9);
                using (var shape = l.Shape())
                using (var edge = l.Outline())
                {
                    var clip = g.Clip;
                    g.SetClip(shape);
                    using (var wide = new Pen(Colors.Alpha(Effects.GreenGlow, 0.25 * strength), l.U(14)) { LineJoin = LineJoin.Round })
                        g.DrawPath(wide, edge);
                    using (var thin = new Pen(Colors.Alpha(Effects.GreenGlow, strength), l.U(5)) { LineJoin = LineJoin.Round })
                        g.DrawPath(thin, edge);
                    g.Clip = clip;
                }
            }

            DateTime now = DateTime.UtcNow;
            if (sessions.Count == 0)
            {
                var r = l.SpriteRect(0);
                var look = new Look(problem == null ? LutinState.Idle : LutinState.Waiting, 99);
                string skin = skinChoice == Skins.Mix ? "lutin" : skinChoice;
                Skins.Draw(g, skin, look, 0, r.X, r.Y, l.Pixel, problem == null ? 0.45 : 1, false, true, false);
            }
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var r = l.SpriteRect(i);
                Skins.Draw(g, Skins.SkinFor(s, skinChoice), new Look(s.State, (now - s.StateSince).TotalSeconds), frame, r.X, r.Y, l.Pixel);
            }

            if (!l.Expanded || l.Height <= l.CollapsedHeight + l.U(8)) return;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var line = new SolidBrush(Color.FromArgb(31, 255, 255, 255)))
                g.FillRectangle(line, l.Ear + l.U(14), l.CollapsedHeight + l.U(2), l.Width - 2 * l.Ear - l.U(28), Math.Max(1, l.U(1)));

            if (sessions.Count == 0)
            {
                var r = l.RowRect(0);
                Text(g, "Un perso apparaît dès que tu écris à Claude Code.",
                     new RectangleF(r.X + l.U(8), r.Y, r.Width - l.U(16), r.Height), l.U(12), false, 0.6);
            }
            int mini = Math.Max(2, (int)Math.Round(2 * l.Scale));
            var miniSize = Skins.SizeFor(mini);
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var r = l.RowRect(i);
                if (hover.Row == i) FillRounded(g, r, l.U(8), Color.FromArgb(23, 255, 255, 255));
                Skins.Draw(g, Skins.SkinFor(s, skinChoice), new Look(s.State, 99), frame,
                           r.X + l.U(4), r.Y + (r.Height - miniSize.Height) / 2, mini, 1, false, false, false);
                float x = r.X + l.U(6) + miniSize.Width + l.U(10);
                Text(g, s.Name, new RectangleF(x, r.Y + l.U(4), r.Right - x - l.U(6), l.U(19)), l.U(13), true, 1);
                Text(g, Texts.Status(s), new RectangleF(x, r.Y + l.U(22), r.Right - x - l.U(6), l.U(16)), l.U(11), false, 0.55);
            }

            var f = l.FooterRect;
            string footer = problem != null ? "Problème : " + problem : Texts.Summary(sessions);
            Text(g, footer, new RectangleF(f.X + l.U(6), f.Y, f.Width - l.U(80), f.Height), l.U(11), false, 0.5);

            var grip = l.GripRect;
            if (hover.Grip) FillRounded(g, grip, l.U(6), Color.FromArgb(36, 255, 255, 255));
            using (var dots = new SolidBrush(Color.FromArgb(191, 255, 255, 255)))
            {
                float cx = grip.X + grip.Width / 2f, cy = grip.Y + grip.Height / 2f, d = 2.6f * l.Scale;
                for (int col = 0; col < 2; col++)
                    for (int row = 0; row < 3; row++)
                        g.FillEllipse(dots, cx - 4 * l.Scale + col * 5.5f * l.Scale, cy - 6.5f * l.Scale + row * 5 * l.Scale, d, d);
            }
            var o = l.OptionsRect;
            if (hover.Options) FillRounded(g, o, l.U(6), Color.FromArgb(36, 255, 255, 255));
            using (var dots = new SolidBrush(Color.FromArgb(204, 255, 255, 255)))
            {
                float cx = o.X + o.Width / 2f, cy = o.Y + o.Height / 2f, d = 3f * l.Scale;
                for (int k = -1; k <= 1; k++) g.FillEllipse(dots, cx + k * 5 * l.Scale - d / 2, cy - d / 2, d, d);
            }
        }

        static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
        {
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            {
                int d = 2 * radius;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        static void Text(Graphics g, string text, RectangleF rect, float size, bool bold, double alpha)
        {
            using (var format = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.LineLimit))
            using (var brush = new SolidBrush(Colors.Alpha(Color.White, alpha)))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, FontOf(size, bold), brush, rect, format);
            }
        }
    }

    // MARK: Textes

    static class Texts
    {
        public static string Summary(List<Session> sessions)
        {
            if (sessions.Count == 0) return "Aucune session Claude en cours";
            Func<LutinState, int> count = state => sessions.Count(s => s.State == state);
            var parts = new List<string>();
            int n;
            if ((n = count(LutinState.Waiting)) > 0) parts.Add(n + " t'attend" + (n > 1 ? "ent" : ""));
            if ((n = count(LutinState.Working)) > 0) parts.Add(n + " au travail");
            if ((n = count(LutinState.Done)) > 0) parts.Add(n + " fini" + (n > 1 ? "s" : ""));
            if ((n = count(LutinState.Idle)) > 0) parts.Add(n + " endormi" + (n > 1 ? "s" : ""));
            return string.Join(" · ", parts);
        }

        public static string Status(Session s)
        {
            DateTime now = DateTime.UtcNow;
            switch (s.State)
            {
                case LutinState.Working:
                    return "Travaille · " + Tool(s.Tool) + " · " + Duration((now - (s.TurnStart ?? s.StateSince)).TotalSeconds);
                case LutinState.Waiting:
                    return (s.WaitReason ?? "T'attend") + " · " + Duration((now - s.StateSince).TotalSeconds);
                case LutinState.Done:
                    double ago = (now - s.StateSince).TotalSeconds;
                    string line = ago < 10 ? "Fini à l'instant" : "Fini il y a " + Duration(ago);
                    if (s.TurnDuration.HasValue && s.TurnDuration.Value >= 5) line += " · a bossé " + Duration(s.TurnDuration.Value);
                    return line;
                default:
                    return s.Interrupted ? "Interrompu" : "Se repose";
            }
        }

        public static string Tool(string tool)
        {
            if (tool == null) return "réfléchit";
            switch (tool)
            {
                case "Bash": case "PowerShell": case "BashOutput": return "lance une commande";
                case "Write": case "Edit": case "MultiEdit": case "NotebookEdit": return "écrit du code";
                case "Read": case "Glob": case "Grep": case "LS": return "lit des fichiers";
                case "WebSearch": case "WebFetch": return "cherche sur le web";
                case "Task": case "Agent": return "a lancé un sous-agent";
                case "TodoWrite": return "fait sa liste";
            }
            return tool.StartsWith("mcp__") ? "utilise un outil" : "s'active";
        }

        public static string Duration(double seconds)
        {
            int s = (int)seconds;
            if (s < 60) return s + " s";
            if (s < 3600) return (s / 60) + " min";
            int m = (s % 3600) / 60;
            return m > 0 ? (s / 3600) + " h " + m.ToString("00") : (s / 3600) + " h";
        }
    }

    // MARK: Windows

    static class Native
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000;
        public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        public const int ULW_ALPHA = 2;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                                      ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        /// Net sur chaque écran, même avec une mise à l'échelle différente (125 %, 150 %…).
        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }

        public static float ScaleOf(Screen screen)
        {
            try
            {
                var b = screen.Bounds;
                var monitor = MonitorFromPoint(new POINT { x = b.Left + b.Width / 2, y = b.Top + b.Height / 2 }, 2);
                uint dpiX, dpiY;
                if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0) return dpiX / 96f;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        /// Met une fenêtre d'une autre app au premier plan (Windows ne le permet qu'après une touche).
        public static void Activate(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        }
    }
}
