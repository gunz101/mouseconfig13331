using System;
using System.Drawing;
using System.Windows.Forms;

namespace SM809
{
    internal sealed class NavBar : Control
    {
        public const int BarWidth = 84;
        public const int ItemHeight = 64;
        public const int TopPad = 8;

        private readonly string[] _glyphs;
        private readonly string[] _names;
        private int _selected;
        private int _hover = -1;

        public event Action<int> SelectedChanged;

        public NavBar(string[] glyphs, string[] names)
        {
            _glyphs = glyphs;
            _names = names;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = BarWidth;
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _names.Length || value == _selected) return;
                _selected = value;
                Invalidate();
                var ev = SelectedChanged;
                if (ev != null) ev(value);
            }
        }

        internal static int IndexAt(int y, int count)
        {
            if (y < TopPad) return -1;
            int i = (y - TopPad) / ItemHeight;
            return i < count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Y, _names.Length);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
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
            int i = IndexAt(e.Y, _names.Length);
            if (i >= 0) SelectedIndex = i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            using (var icon = TaskbarBattery.IconFont(22f))
            using (var text = new Font("Segoe UI", 8.5f))
            {
                for (int i = 0; i < _names.Length; i++)
                {
                    var r = new Rectangle(0, TopPad + i * ItemHeight, Width, ItemHeight);
                    bool sel = i == _selected;
                    if (sel || i == _hover)
                        using (var b = new SolidBrush(sel ? Theme.AccentDark : Theme.ChipHover))
                            g.FillRectangle(b, r);
                    if (sel)
                        using (var bar = new SolidBrush(Theme.Accent))
                            g.FillRectangle(bar, 0, r.Y, 3, r.Height);
                    if (icon != null)
                        TextRenderer.DrawText(g, _glyphs[i], icon, new Rectangle(0, r.Y + 8, Width, 28),
                            sel ? Theme.Accent : Theme.SubText, flags);
                    TextRenderer.DrawText(g, _names[i], text, new Rectangle(0, r.Y + 38, Width, 20),
                        sel ? Color.White : Theme.SubText, flags);
                }
            }
        }
    }
}
