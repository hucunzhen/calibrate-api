using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 无底图时按点列范围自适应画布：Y 向上，带坐标标尺与浅网格。
    /// </summary>
    internal static class PointsCanvasRenderer
    {
        public static Bitmap Render(
            Point2D[]? points,
            int[]? barIds,
            int[]? seriesIds,
            string? pointLineJoinMode,
            HalconXldContourBundle? xld,
            float strokeWidth,
            float dotRadius,
            Func<int, Color>? seriesColor,
            int targetW = 1280,
            int targetH = 900,
            bool drawBarIdLabels = false)
        {
            CollectBounds(points, xld, out double minX, out double maxX, out double minY, out double maxY, out int nPts, out int nXld);
            if (nPts == 0 && nXld == 0)
                return RenderEmpty(targetW);

            PadBounds(ref minX, ref maxX, ref minY, ref maxY);

            const int left = 72;
            const int right = 24;
            const int top = 28;
            const int bottom = 44;
            double bw = Math.Max(maxX - minX, 1e-9);
            double bh = Math.Max(maxY - minY, 1e-9);
            int plotMax = Math.Max(240, Math.Min(targetW, targetH) - 80);
            double pxPer = plotMax / Math.Max(bw, bh);
            int plotW = Math.Max(80, (int)Math.Round(bw * pxPer));
            int plotH = Math.Max(80, (int)Math.Round(bh * pxPer));
            double scale = Math.Min(plotW / bw, plotH / bh);
            int bmpW = left + plotW + right;
            int bmpH = top + plotH + bottom;
            bmpW = Math.Clamp((bmpW + 3) & ~3, 424, 1800);
            bmpH = Math.Clamp(bmpH, 320, 1400);
            float ox = left;
            float oy = top;

            PointF Map(double x, double y) =>
                new((float)(ox + (x - minX) * scale), (float)(oy + (maxY - y) * scale));

            var bmp = new Bitmap(bmpW, bmpH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(28, 28, 32));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawRuler(g, minX, maxX, minY, maxY, Map, bmpH, top);

            float stroke = strokeWidth <= 0.5f ? 1.2f : strokeWidth;
            if (points != null && points.Length >= 2)
                DrawPolylines(g, points, barIds, seriesIds, pointLineJoinMode, seriesColor, stroke, Map);

            if (dotRadius >= 0.5f && points != null)
                DrawDots(g, points, seriesIds, seriesColor, stroke, dotRadius, Map);

            if (xld != null && xld.ContourCount > 0)
                DrawXld(g, xld, stroke, Map);

            if (drawBarIdLabels)
                DisplayBarIdOverlay.Draw(g, points ?? Array.Empty<Point2D>(), barIds, bmpW, bmpH, Map);

            using var font = new Font("Segoe UI", 8.5f);
            using var brush = new SolidBrush(Color.FromArgb(200, 220, 220, 220));
            string cap = nPts > 0 ? $"N={nPts}" : "XLD";
            if (nXld > 0)
                cap += $"  XLD {nXld}";
            cap += "  Y↑";
            g.DrawString(cap, font, brush, left, 6f);
            return bmp;
        }

        private static Bitmap RenderEmpty(int targetW)
        {
            int w = Math.Clamp(targetW, 420, 900);
            var empty = new Bitmap(w, 160, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(empty);
            g.Clear(Color.FromArgb(32, 32, 36));
            using var f = new Font("Segoe UI", 12f);
            g.DrawString("0 个点（无轨迹可绘制）", f, Brushes.Gray, 28f, 64f);
            return empty;
        }

        private static void CollectBounds(
            Point2D[]? points,
            HalconXldContourBundle? xld,
            out double minX,
            out double maxX,
            out double minY,
            out double maxY,
            out int nPts,
            out int nXld)
        {
            minX = double.PositiveInfinity;
            maxX = double.NegativeInfinity;
            minY = double.PositiveInfinity;
            maxY = double.NegativeInfinity;
            nPts = 0;
            nXld = 0;
            if (points != null)
            {
                for (int i = 0; i < points.Length; i++)
                    Expand(points[i].X, points[i].Y, ref minX, ref maxX, ref minY, ref maxY);
                nPts = points.Length;
            }

            if (xld?.Contours != null)
            {
                foreach (var c in xld.Contours)
                {
                    if (c == null || c.Length < 2)
                        continue;
                    nXld++;
                    for (int i = 0; i < c.Length; i++)
                        Expand(c[i].X, c[i].Y, ref minX, ref maxX, ref minY, ref maxY);
                }
            }

            if (!double.IsFinite(minX))
            {
                minX = 0;
                maxX = 1;
                minY = 0;
                maxY = 1;
            }
        }

        private static void Expand(double x, double y, ref double minX, ref double maxX, ref double minY, ref double maxY)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        private static void PadBounds(ref double minX, ref double maxX, ref double minY, ref double maxY)
        {
            double bw = Math.Max(maxX - minX, 1e-9);
            double bh = Math.Max(maxY - minY, 1e-9);
            if (bw < 1e-6)
            {
                minX -= 1;
                maxX += 1;
                bw = 2;
            }

            if (bh < 1e-6)
            {
                minY -= 1;
                maxY += 1;
                bh = 2;
            }

            double mx = Math.Max(bw, bh) * 0.06 + 1e-6;
            minX -= mx;
            maxX += mx;
            minY -= mx;
            maxY += mx;
        }

        private static void DrawRuler(
            Graphics g,
            double minX,
            double maxX,
            double minY,
            double maxY,
            Func<double, double, PointF> map,
            int bmpH,
            int top)
        {
            var bl = map(minX, minY);
            var tr = map(maxX, maxY);
            float x0 = bl.X;
            float x1 = tr.X;
            float y0 = tr.Y;
            float y1 = bl.Y;
            double xStep = NiceStep(maxX - minX);
            double yStep = NiceStep(maxY - minY);

            using var gridPen = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
            using var axisPen = new Pen(Color.FromArgb(170, 200, 200, 210), 1.2f);
            using var tickPen = new Pen(Color.FromArgb(200, 220, 220, 230), 1f);
            using var font = new Font("Segoe UI", 8f);
            using var label = new SolidBrush(Color.FromArgb(210, 230, 230, 235));
            var fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            foreach (double x in Ticks(minX, maxX, xStep))
            {
                var p = map(x, minY);
                g.DrawLine(gridPen, p.X, y0, p.X, y1);
                g.DrawLine(tickPen, p.X, y1, p.X, y1 + 6f);
                g.DrawString(FormatTick(x, xStep), font, label, p.X, y1 + 8f, fmtC);
            }

            foreach (double y in Ticks(minY, maxY, yStep))
            {
                var p = map(minX, y);
                g.DrawLine(gridPen, x0, p.Y, x1, p.Y);
                g.DrawLine(tickPen, x0 - 6f, p.Y, x0, p.Y);
                g.DrawString(FormatTick(y, yStep), font, label, x0 - 8f, p.Y, fmtR);
            }

            if (minX <= 0 && 0 <= maxX)
            {
                var a = map(0, minY);
                var b = map(0, maxY);
                using var originPen = new Pen(Color.FromArgb(90, 255, 200, 80), 1.1f);
                g.DrawLine(originPen, a.X, a.Y, b.X, b.Y);
            }

            if (minY <= 0 && 0 <= maxY)
            {
                var a = map(minX, 0);
                var b = map(maxX, 0);
                using var originPen = new Pen(Color.FromArgb(90, 255, 200, 80), 1.1f);
                g.DrawLine(originPen, a.X, a.Y, b.X, b.Y);
            }

            g.DrawRectangle(axisPen, x0, y0, x1 - x0, y1 - y0);
            g.DrawString("X", font, label, x1 - 2f, bmpH - 18f, fmtR);
            g.DrawString("Y", font, label, 10f, top - 2f);
        }

        private static void DrawPolylines(
            Graphics g,
            Point2D[] points,
            int[]? barIds,
            int[]? seriesIds,
            string? pointLineJoinMode,
            Func<int, Color>? seriesColor,
            float stroke,
            Func<double, double, PointF> map)
        {
            using var pen = new Pen(Color.FromArgb(220, 0, 200, 255), stroke);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            bool colorBySeries = seriesIds != null && seriesIds.Length == points.Length && seriesColor != null
                                 && HasMultipleSeries(seriesIds);
            bool useBarSplit = colorBySeries
                               || FlowPageBarSplit(barIds, points.Length, pointLineJoinMode);

            if (!useBarSplit)
            {
                var arr = MapAll(points, map);
                if (arr.Length >= 2)
                    g.DrawLines(pen, arr);
                return;
            }

            int segStart = 0;
            for (int i = 1; i <= points.Length; i++)
            {
                bool barBreak = i < points.Length
                    && barIds != null
                    && barIds.Length == points.Length
                    && barIds[i] != barIds[i - 1];
                bool seriesBreak = colorBySeries
                    && i < points.Length
                    && seriesIds![i] != seriesIds[i - 1];
                if (i != points.Length && !barBreak && !seriesBreak)
                    continue;

                int segLen = i - segStart;
                if (segLen >= 2)
                {
                    var arr = new PointF[segLen];
                    for (int k = 0; k < segLen; k++)
                    {
                        var p = points[segStart + k];
                        arr[k] = map(p.X, p.Y);
                    }

                    if (colorBySeries)
                        pen.Color = seriesColor!(seriesIds![segStart]);
                    g.DrawLines(pen, arr);
                }

                segStart = i;
            }
        }

        private static void DrawDots(
            Graphics g,
            Point2D[] points,
            int[]? seriesIds,
            Func<int, Color>? seriesColor,
            float stroke,
            float radius,
            Func<double, double, PointF> map)
        {
            using var pen = new Pen(Color.FromArgb(180, 0, 255, 0), stroke);
            pen.StartCap = pen.EndCap = LineCap.Round;
            bool colorBySeries = seriesIds != null && seriesIds.Length == points.Length && seriesColor != null;
            for (int i = 0; i < points.Length; i++)
            {
                if (colorBySeries)
                    pen.Color = seriesColor!(seriesIds![i]);
                var c = map(points[i].X, points[i].Y);
                g.DrawEllipse(pen, c.X - radius, c.Y - radius, radius * 2f, radius * 2f);
            }
        }

        private static void DrawXld(
            Graphics g,
            HalconXldContourBundle xld,
            float stroke,
            Func<double, double, PointF> map)
        {
            using var pen = new Pen(Color.FromArgb(220, 255, 140, 0), stroke);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            foreach (var contour in xld.Contours)
            {
                if (contour == null || contour.Length < 2)
                    continue;
                var arr = new PointF[contour.Length];
                for (int i = 0; i < contour.Length; i++)
                    arr[i] = map(contour[i].X, contour[i].Y);
                g.DrawLines(pen, arr);
            }
        }

        private static PointF[] MapAll(Point2D[] points, Func<double, double, PointF> map)
        {
            var arr = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++)
                arr[i] = map(points[i].X, points[i].Y);
            return arr;
        }

        private static bool HasMultipleSeries(int[] seriesIds)
        {
            int first = seriesIds[0];
            for (int i = 1; i < seriesIds.Length; i++)
            {
                if (seriesIds[i] != first)
                    return true;
            }

            return false;
        }

        private static bool FlowPageBarSplit(int[]? barIds, int n, string? mode)
        {
            if (barIds == null || barIds.Length != n)
                return false;
            string m = (mode ?? "auto").Trim().ToLowerInvariant();
            if (m is "single" or "one" or "polyline")
                return false;
            bool multi = false;
            int first = barIds[0];
            for (int i = 1; i < n; i++)
            {
                if (barIds[i] != first)
                {
                    multi = true;
                    break;
                }
            }

            if (m is "bars" or "per_bar" or "split" or "true")
                return multi;
            return multi;
        }

        internal static double NiceStep(double span, int maxTicks = 7)
        {
            if (span <= 0 || !double.IsFinite(span))
                return 1;
            double raw = span / Math.Max(2, maxTicks);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double r = raw / mag;
            double nice = r <= 1 ? 1 : r <= 2 ? 2 : r <= 5 ? 5 : 10;
            return nice * mag;
        }

        private static IEnumerable<double> Ticks(double min, double max, double step)
        {
            if (step <= 0 || !double.IsFinite(step))
                yield break;
            int i0 = (int)Math.Ceiling((min - step * 1e-9) / step);
            int i1 = (int)Math.Floor((max + step * 1e-9) / step);
            if (i1 < i0)
                yield break;
            int n = i1 - i0 + 1;
            if (n > 40)
            {
                i0 = (int)Math.Ceiling((min - step * 1e-9) / (step * 2));
                i1 = (int)Math.Floor((max + step * 1e-9) / (step * 2));
                step *= 2;
            }

            for (int i = i0; i <= i1; i++)
                yield return i * step;
        }

        internal static string FormatTick(double v, double step)
        {
            if (Math.Abs(v) < step * 1e-9)
                v = 0;
            if (step >= 1)
                return v.ToString("0.###", CultureInfo.InvariantCulture);
            int decimals = (int)Math.Clamp(Math.Ceiling(-Math.Log10(step)) + 1, 1, 4);
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
    }
}
