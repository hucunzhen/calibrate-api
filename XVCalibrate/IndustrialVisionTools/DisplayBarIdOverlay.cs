using System;
using System.Drawing;
using System.Globalization;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>在点列各条焊道的几何中心绘制 BarId 编号。</summary>
    internal static class DisplayBarIdOverlay
    {
        public static void Draw(
            Graphics g,
            Point2D[] points,
            int[]? barIds,
            int canvasW,
            int canvasH,
            Func<double, double, PointF>? toPixel = null)
        {
            if (g == null || points == null || barIds == null)
                return;
            int n = Math.Min(points.Length, barIds.Length);
            if (n <= 0 || canvasW < 8 || canvasH < 8)
                return;

            float fontPx = Math.Clamp(Math.Min(canvasW, canvasH) / 64f, 10f, 26f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(255, 255, 224, 80));
            using var back = new SolidBrush(Color.FromArgb(175, 0, 0, 0));
            using var border = new Pen(Color.FromArgb(200, 255, 196, 60), 1f);

            int start = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i < n && barIds[i] == barIds[start])
                    continue;

                DrawOne(g, points, start, i, barIds[start], font, text, back, border, canvasW, canvasH, toPixel);
                start = i;
            }
        }

        private static void DrawOne(
            Graphics g,
            Point2D[] points,
            int start,
            int end,
            int barId,
            Font font,
            Brush text,
            Brush back,
            Pen border,
            int canvasW,
            int canvasH,
            Func<double, double, PointF>? toPixel)
        {
            if (end <= start)
                return;

            double sx = 0, sy = 0;
            int counted = 0;
            for (int i = start; i < end; i++)
            {
                double x = points[i].X;
                double y = points[i].Y;
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    continue;
                sx += x;
                sy += y;
                counted++;
            }

            if (counted == 0)
                return;

            double cx = sx / counted;
            double cy = sy / counted;
            PointF pix = toPixel != null ? toPixel(cx, cy) : new PointF((float)cx, (float)cy);
            string label = barId.ToString(CultureInfo.InvariantCulture);
            SizeF size = g.MeasureString(label, font);
            float w = size.Width + 6f;
            float h = size.Height + 2f;
            float x0 = pix.X - w / 2f;
            float y0 = pix.Y - h / 2f;
            x0 = Math.Clamp(x0, 2f, Math.Max(2f, canvasW - w - 2f));
            y0 = Math.Clamp(y0, 2f, Math.Max(2f, canvasH - h - 2f));
            g.FillRectangle(back, x0, y0, w, h);
            g.DrawRectangle(border, x0, y0, w, h);
            g.DrawString(label, font, text, x0 + 3f, y0 + 1f);
        }
    }
}
