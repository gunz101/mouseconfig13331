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
            int h = Math.Max(40, Math.Min(96, Math.Min(hFit, (int)Math.Round(tileW * 0.7))));

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
            r.FontPx = Math.Max(12f, Math.Min(18f, h * 0.26f));
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
}
