using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SM809
{
    internal sealed class MapTile
    {
        public int Id;
        public string Label;
        public string Function;
        public bool Locked;
        public bool Warning;
    }

    internal sealed class MapLayoutResult
    {
        public readonly Rectangle[] Tiles = new Rectangle[16];
        public Rectangle Photo;
        public Rectangle ThumbTitle;
        public Rectangle OthersTitle;
        public float FontPx;
    }

    // Posições derivadas dos números da foto (images/mouse_nr.png, 400×400): a grade do
    // polegar reproduz a coluna e a altura relativa reais dos botões 1–9.
    internal static class MapLayout
    {
        public const int Pad = 16;
        public const int Gap = 8;
        public const int TitleH = 22;
        public const int MinOtherW = 104;
        public const int PhotoHitRadius = 14;

        public static readonly Point[] Badges =
        {
            new Point(118, 152), new Point(116, 201), new Point(135, 184), new Point(153, 171),
            new Point(127, 244), new Point(145, 226), new Point(142, 292), new Point(159, 268),
            new Point(166, 233), new Point(163, 55), new Point(173, 93), new Point(195, 40),
            new Point(257, 33), new Point(246, 73), new Point(259, 162), new Point(267, 202)
        };

        // 0 = coluna externa (1,2,5,7), 1 = meio (3,6,8), 2 = interna (4,9)
        private static readonly int[] ThumbColumn = { 0, 0, 1, 2, 0, 1, 0, 1, 2 };
        private const int ThumbTopY = 152;
        private const int ThumbSpanY = 140;
        private const int ThumbMinStep = 42;

        public static MapLayoutResult Compute(Size area)
        {
            var r = new MapLayoutResult();
            int innerW = Math.Max(1, area.Width - 2 * Pad);
            int innerH = Math.Max(1, area.Height - 2 * Pad);
            int gridW = (int)Math.Round(innerW * 0.58);
            int tileW = Math.Max(1, (gridW - 2 * Gap) / 3);
            int otherCols = Math.Max(1, Math.Min(7, (innerW + Gap) / (MinOtherW + Gap)));
            int otherRows = (7 + otherCols - 1) / otherCols;
            int otherW = Math.Max(1, (innerW - (otherCols - 1) * Gap) / otherCols);

            double k = (double)ThumbSpanY / ThumbMinStep;
            double fixedH = 2 * TitleH + 2 * Gap + 16 + k * Gap + (otherRows - 1) * Gap;
            int hFit = (int)Math.Floor((innerH - fixedH) / (k + 1 + otherRows));
            int h = Math.Max(40, Math.Min(120, Math.Min(hFit, (int)Math.Round(tileW * 0.8))));

            // passo inteiro: a menor distância real (42 px) sempre vira altura do bloco + espaço
            int thumbH = ThumbSpanY * (h + Gap) / ThumbMinStep + h;
            int othersH = otherRows * h + (otherRows - 1) * Gap;
            int total = TitleH + Gap + thumbH + 16 + TitleH + Gap + othersH;
            int top = Pad + Math.Max(0, (innerH - total) / 2);

            r.ThumbTitle = new Rectangle(Pad, top, innerW, TitleH);
            int thumbTop = top + TitleH + Gap;
            for (int i = 0; i < 9; i++)
            {
                int x = Pad + ThumbColumn[i] * (tileW + Gap);
                int y = thumbTop + (Badges[i].Y - ThumbTopY) * (h + Gap) / ThumbMinStep;
                r.Tiles[i] = new Rectangle(x, y, tileW, h);
            }

            int photoX = Pad + gridW + 16;
            int photoW = Math.Max(0, Pad + innerW - photoX);
            int side = Math.Min(photoW, thumbH);
            r.Photo = new Rectangle(photoX + (photoW - side) / 2, thumbTop + (thumbH - side) / 2, side, side);

            int othersTitleY = thumbTop + thumbH + 16;
            r.OthersTitle = new Rectangle(Pad, othersTitleY, innerW, TitleH);
            int othersTop = othersTitleY + TitleH + Gap;
            for (int j = 0; j < 7; j++)
            {
                int col = j % otherCols, row = j / otherCols;
                r.Tiles[9 + j] = new Rectangle(Pad + col * (otherW + Gap), othersTop + row * (h + Gap), otherW, h);
            }
            r.FontPx = Math.Max(12f, Math.Min(18f, h * 0.22f));
            return r;
        }

        public static int PhotoHit(PointF p)
        {
            int best = -1;
            double bestD = PhotoHitRadius * PhotoHitRadius;
            for (int i = 0; i < Badges.Length; i++)
            {
                double dx = p.X - Badges[i].X, dy = p.Y - Badges[i].Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }
    }

    internal sealed class ButtonMapView : Control
    {
        private readonly List<MapTile> _tiles = new List<MapTile>();
        private readonly ToolTip _tip = new ToolTip();
        private readonly Image _photo;
        private MapLayoutResult _geometry;
        private int _selected = -1;
        private int _hover = -1;

        public event Action SelectionChanged;

        public ButtonMapView(Image photo)
        {
            _photo = photo;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            TabStop = true;
            _tip.InitialDelay = 400;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value >= 0 && value < 16 ? value : -1;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var ev = SelectionChanged;
                if (ev != null) ev();
            }
        }

        public void SetTiles(IList<MapTile> tiles)
        {
            _tiles.Clear();
            _tiles.AddRange(tiles);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tip.Dispose();
                if (_photo != null) _photo.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            _geometry = null;
            base.OnResize(e);
        }

        private MapLayoutResult Geometry()
        {
            if (_geometry == null) _geometry = MapLayout.Compute(ClientSize);
            return _geometry;
        }

        private int ItemAt(Point p)
        {
            var g = Geometry();
            for (int i = 0; i < 16; i++)
                if (g.Tiles[i].Contains(p)) return i;
            if (_photo == null || !g.Photo.Contains(p)) return -1;
            float s = g.Photo.Width / (float)_photo.Width;
            return MapLayout.PhotoHit(new PointF((p.X - g.Photo.X) / s, (p.Y - g.Photo.Y) / s));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = ItemAt(e.Location);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
            _tip.SetToolTip(this, i >= 0 && i < _tiles.Count ? TipText(_tiles[i]) : null);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover == -1) return;
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = ItemAt(e.Location);
            if (i >= 0) SelectedIndex = i;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Left)
            {
                SelectedIndex = _selected <= 0 ? 0 : _selected - 1;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Right)
            {
                SelectedIndex = _selected < 0 ? 0 : Math.Min(15, _selected + 1);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private static string TipText(MapTile t)
        {
            return "Botão " + t.Id + " — " + t.Label + ": " + t.Function + (t.Locked ? " (travado)" : "");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var geo = Geometry();
            using (var title = new Font("Segoe UI Semibold", 9.5f))
            {
                var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, "Grade do polegar", title, geo.ThumbTitle, Theme.Text, flags);
                TextRenderer.DrawText(g, "Topo e outros", title, geo.OthersTitle, Theme.Text, flags);
            }
            if (_photo != null && geo.Photo.Width > 0) DrawPhoto(g, geo.Photo);
            using (var fn = new Font("Segoe UI", geo.FontPx, GraphicsUnit.Pixel))
            using (var num = new Font("Segoe UI", Math.Max(11f, geo.FontPx * 0.75f), GraphicsUnit.Pixel))
            {
                for (int i = 0; i < 16; i++)
                    DrawTile(g, i, geo.Tiles[i], fn, num);
            }
        }

        private void DrawPhoto(Graphics g, Rectangle r)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_photo, r);
            if (_selected < 0) return;
            float s = r.Width / (float)_photo.Width;
            var c = MapLayout.Badges[_selected];
            float rad = Math.Max(7f, MapLayout.PhotoHitRadius * s);
            using (var pen = new Pen(Theme.Accent, 2.5f))
                g.DrawEllipse(pen, r.X + c.X * s - rad, r.Y + c.Y * s - rad, 2 * rad, 2 * rad);
        }

        private void DrawTile(Graphics g, int i, Rectangle r, Font fn, Font num)
        {
            var t = i < _tiles.Count ? _tiles[i] : null;
            bool sel = i == _selected;
            Color bg = sel ? Theme.AccentDark : i == _hover ? Theme.ChipHover : Theme.Chip;
            using (var path = Rounded(r, 6))
            using (var brush = new SolidBrush(bg))
            {
                g.FillPath(brush, path);
                if (sel)
                    using (var pen = new Pen(Theme.Accent, 2f))
                        g.DrawPath(pen, path);
            }
            string label = (i + 1).ToString();
            if (t != null && t.Locked) label += "  🔒";
            TextRenderer.DrawText(g, label, num, new Rectangle(r.X + 7, r.Y + 4, r.Width - 14, num.Height),
                sel ? Theme.Accent : Theme.SubText, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            if (t == null) return;
            Color fg = t.Locked ? Theme.SubText : t.Warning ? Color.FromArgb(240, 200, 90) : sel ? Color.White : Theme.Text;
            var fnRect = new Rectangle(r.X + 6, r.Y + num.Height + 2, r.Width - 12, r.Height - num.Height - 6);
            // TextBoxControl: sem ele o GDI corta a última linha visível sem pôr reticências
            TextRenderer.DrawText(g, t.Function, fn, fnRect, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
