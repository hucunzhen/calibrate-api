using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>在图像上叠加九点/网格世界坐标系（锚点为 ImagePts[i]↔WorldPts[i] 配对，避免逆变换与行序错位）。</summary>
    internal static class AffineWorldGridOverlay
    {
        public static bool TryWorldToImage(in AffineTransform t, double worldX, double worldY, out double px, out double py)
        {
            px = py = 0;
            double det = t.A * t.E - t.B * t.D;
            if (Math.Abs(det) < 1e-15)
                return false;

            double rx = worldX - t.C;
            double ry = worldY - t.F;
            px = (t.E * rx - t.B * ry) / det;
            py = (-t.D * rx + t.A * ry) / det;
            return double.IsFinite(px) && double.IsFinite(py);
        }

        public static Point2D? WorldToImage(in AffineTransform t, in Point2D world)
        {
            return TryWorldToImage(t, world.X, world.Y, out double px, out double py)
                ? new Point2D { X = px, Y = py }
                : null;
        }

        public static Point2D[] ProjectWorldPoints(in AffineTransform t, Point2D[] worldPts)
        {
            if (worldPts == null || worldPts.Length == 0)
                return Array.Empty<Point2D>();

            var result = new Point2D[worldPts.Length];
            for (int i = 0; i < worldPts.Length; i++)
            {
                var p = WorldToImage(t, worldPts[i])
                    ?? throw new InvalidOperationException(
                        $"世界点 ({worldPts[i].X:G4}, {worldPts[i].Y:G4}) mm 无法反算到图像坐标");
                result[i] = p;
            }

            return result;
        }

        public static void DrawProbeWorldPointsOnGraphics(Graphics g, in AffineTransform transform, Point2D[]? probeWorldPts)
        {
            if (probeWorldPts == null || probeWorldPts.Length == 0)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(220, 255, 105, 180), 2f);
            using var fill = new SolidBrush(Color.FromArgb(80, 255, 105, 180));
            using var labelBrush = new SolidBrush(Color.FromArgb(240, 255, 182, 193));
            using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
            for (int i = 0; i < probeWorldPts.Length; i++)
            {
                if (!TryWorldToImage(transform, probeWorldPts[i].X, probeWorldPts[i].Y, out double px, out double py))
                    continue;
                g.FillEllipse(fill, (float)px - 5, (float)py - 5, 10, 10);
                g.DrawEllipse(pen, (float)px - 5, (float)py - 5, 10, 10);
                g.DrawString($"P{i + 1}", font, labelBrush, (float)px + 6, (float)py - 8);
            }
        }

        public static void DrawOnGraphics(
            Graphics g,
            in AffineTransform transform,
            Point2D[]? worldPts,
            int imageWidth,
            int imageHeight,
            Point2D[]? imagePts = null)
        {
            if (worldPts == null || worldPts.Length < 2)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool useImageAnchors = imagePts != null && imagePts.Length == worldPts.Length;
            var (rows, cols) = CalibrationPointGrid.InferLayout(worldPts.Length, worldPts);

            if (useImageAnchors && rows * cols == worldPts.Length)
            {
                DrawImageAnchoredGrid(g, worldPts, imagePts!, rows, cols);
                DrawAxesAtImageGrid(g, imagePts!, rows, cols);
                return;
            }

            if (rows * cols != worldPts.Length)
            {
                DrawAxesAtCenter(g, transform, worldPts, imageWidth, imageHeight);
                return;
            }

            using var gridPen = new Pen(Color.FromArgb(150, 255, 215, 0), 1.4f);
            gridPen.DashStyle = DashStyle.Dot;

            for (int r = 0; r < rows; r++)
            {
                var rowPix = new List<PointF>();
                for (int c = 0; c < cols; c++)
                {
                    if (TryMapWorldPoint(worldPts[r * cols + c], transform, imageWidth, imageHeight, out var pf))
                        rowPix.Add(pf);
                }

                if (rowPix.Count >= 2)
                    g.DrawLines(gridPen, rowPix.ToArray());
            }

            for (int c = 0; c < cols; c++)
            {
                var colPix = new List<PointF>();
                for (int r = 0; r < rows; r++)
                {
                    if (TryMapWorldPoint(worldPts[r * cols + c], transform, imageWidth, imageHeight, out var pf))
                        colPix.Add(pf);
                }

                if (colPix.Count >= 2)
                    g.DrawLines(gridPen, colPix.ToArray());
            }

            DrawAxesAtCenter(g, transform, worldPts, imageWidth, imageHeight);

            using var labelBrush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            using var labelFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            for (int i = 0; i < worldPts.Length; i++)
            {
                if (!TryMapWorldPoint(worldPts[i], transform, imageWidth, imageHeight, out var pf))
                    continue;
                DrawPointLabel(g, labelFont, labelBrush, pf, i, worldPts[i]);
            }
        }

        private static void DrawImageAnchoredGrid(
            Graphics g,
            Point2D[] worldPts,
            Point2D[] imagePts,
            int rows,
            int cols)
        {
            using var gridPen = new Pen(Color.FromArgb(150, 255, 215, 0), 1.4f);
            gridPen.DashStyle = DashStyle.Dot;

            for (int r = 0; r < rows; r++)
            {
                var rowPix = new PointF[cols];
                for (int c = 0; c < cols; c++)
                {
                    var p = imagePts[r * cols + c];
                    rowPix[c] = new PointF((float)p.X, (float)p.Y);
                }

                if (cols >= 2)
                    g.DrawLines(gridPen, rowPix);
            }

            for (int c = 0; c < cols; c++)
            {
                var colPix = new PointF[rows];
                for (int r = 0; r < rows; r++)
                {
                    var p = imagePts[r * cols + c];
                    colPix[r] = new PointF((float)p.X, (float)p.Y);
                }

                if (rows >= 2)
                    g.DrawLines(gridPen, colPix);
            }

            using var labelBrush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            using var labelFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            for (int i = 0; i < worldPts.Length; i++)
            {
                var p = imagePts[i];
                DrawPointLabel(g, labelFont, labelBrush, new PointF((float)p.X, (float)p.Y), i, worldPts[i]);
            }
        }

        private static void DrawPointLabel(Graphics g, Font font, Brush brush, PointF pixel, int index, Point2D world)
        {
            string text = $"W{index + 1}\n{world.X:F1},{world.Y:F1}";
            g.DrawString(text, font, brush, pixel.X + 4, pixel.Y + 4);
        }

        private static void DrawAxesAtImageGrid(Graphics g, Point2D[] imagePts, int rows, int cols)
        {
            int cr = rows / 2;
            int cc = cols / 2;
            int centerIdx = cr * cols + cc;
            var center = imagePts[centerIdx];
            var centerPix = new PointF((float)center.X, (float)center.Y);

            PointF? xEnd = cc + 1 < cols
                ? new PointF((float)imagePts[cr * cols + cc + 1].X, (float)imagePts[cr * cols + cc + 1].Y)
                : null;
            PointF? yEnd = cr - 1 >= 0
                ? new PointF((float)imagePts[(cr - 1) * cols + cc].X, (float)imagePts[(cr - 1) * cols + cc].Y)
                : null;

            if (xEnd != null)
                DrawAxisLine(g, centerPix, xEnd.Value, Color.FromArgb(230, 255, 70, 70), "X");
            if (yEnd != null)
                DrawAxisLine(g, centerPix, yEnd.Value, Color.FromArgb(230, 70, 220, 100), "Y");

            using var originPen = new Pen(Color.FromArgb(220, 255, 255, 255), 2f);
            g.DrawEllipse(originPen, centerPix.X - 5, centerPix.Y - 5, 10, 10);
            using var originFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var originBrush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            g.DrawString("O", originFont, originBrush, centerPix.X + 6, centerPix.Y - 14);
        }

        private static void DrawAxesAtCenter(
            Graphics g,
            in AffineTransform transform,
            Point2D[] worldPts,
            int imageWidth,
            int imageHeight)
        {
            var centerWorld = AffineTransformAdjust.GetGridCenterPoint(worldPts);
            if (centerWorld == null)
                return;

            double stepX = InferWorldStep(worldPts, rowMajorStepAlongCols: true);
            double stepY = InferWorldStep(worldPts, rowMajorStepAlongCols: false);

            var cw = centerWorld.Value;
            DrawAxis(g, transform, cw, new Point2D { X = cw.X + stepX, Y = cw.Y },
                Color.FromArgb(230, 255, 70, 70), "X", imageWidth, imageHeight);
            DrawAxis(g, transform, cw, new Point2D { X = cw.X, Y = cw.Y + stepY },
                Color.FromArgb(230, 70, 220, 100), "Y", imageWidth, imageHeight);

            if (TryMapWorldPoint(cw, transform, imageWidth, imageHeight, out var originPix))
            {
                using var originPen = new Pen(Color.FromArgb(220, 255, 255, 255), 2f);
                g.DrawEllipse(originPen, originPix.X - 5, originPix.Y - 5, 10, 10);
                using var originFont = new Font("Segoe UI", 9f, FontStyle.Bold);
                using var originBrush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
                g.DrawString("O", originFont, originBrush, originPix.X + 6, originPix.Y - 14);
            }
        }

        private static double InferWorldStep(Point2D[] worldPts, bool rowMajorStepAlongCols)
        {
            if (worldPts.Length < 2)
                return 1;

            var (rows, cols) = CalibrationPointGrid.InferLayout(worldPts.Length, worldPts);
            if (rows * cols != worldPts.Length)
                return 1;

            if (rowMajorStepAlongCols && cols >= 2)
            {
                var a = worldPts[0];
                var b = worldPts[1];
                return Math.Max(Math.Abs(b.X - a.X), 1e-6);
            }

            if (!rowMajorStepAlongCols && rows >= 2)
            {
                var a = worldPts[0];
                var b = worldPts[cols];
                return Math.Max(Math.Abs(b.Y - a.Y), 1e-6);
            }

            return 1;
        }

        private static void DrawAxisLine(Graphics g, PointF from, PointF to, Color color, string label)
        {
            using var pen = new Pen(color, 2.2f);
            pen.CustomEndCap = new AdjustableArrowCap(4, 5, true);
            g.DrawLine(pen, from, to);

            using var brush = new SolidBrush(color);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
            g.DrawString(label, font, brush, to.X + 4, to.Y + 4);
        }

        private static void DrawAxis(
            Graphics g,
            in AffineTransform transform,
            Point2D fromWorld,
            Point2D toWorld,
            Color color,
            string label,
            int imageWidth,
            int imageHeight)
        {
            if (!TryMapWorldPoint(fromWorld, transform, imageWidth, imageHeight, out var p0))
                return;
            if (!TryMapWorldPoint(toWorld, transform, imageWidth, imageHeight, out var p1))
                return;

            DrawAxisLine(g, p0, p1, color, label);
        }

        private static bool TryMapWorldPoint(
            Point2D world,
            in AffineTransform transform,
            int imageWidth,
            int imageHeight,
            out PointF pixel)
        {
            pixel = default;
            if (!TryWorldToImage(transform, world.X, world.Y, out double px, out double py))
                return false;
            if (px < -imageWidth || px > imageWidth * 2 || py < -imageHeight || py > imageHeight * 2)
                return false;
            pixel = new PointF((float)px, (float)py);
            return true;
        }
    }
}
