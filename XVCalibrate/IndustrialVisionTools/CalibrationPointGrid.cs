using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CalibOperatorPInvoke;
using GdiPen = System.Drawing.Pen;

namespace CalibOperatorCLI_Example
{
    /// <summary>九点/阵列标定：按世界点行优先顺序推断网格，绘制行列连线（非闭合折线）。</summary>
    internal static class CalibrationPointGrid
    {
        public static bool IsGridLineJoinMode(string? mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            return m is "grid" or "nine" or "nine_grid" or "3x3" or "4x4" or "5x5" or "6x6" or "九宫格";
        }

        /// <summary>按点数、世界点坐标或显式 hint 推断 N×M 网格（底行优先行优先顺序）。</summary>
        public static (int rows, int cols) InferLayout(int pointCount, Point2D[]? worldPts = null)
        {
            int n = worldPts?.Length ?? pointCount;
            if (worldPts != null && worldPts.Length != pointCount)
                n = pointCount;

            if (worldPts != null && worldPts.Length == n && n >= 4)
            {
                var fromWorld = TryInferFromWorldCoordinates(worldPts);
                if (fromWorld.HasValue)
                    return fromWorld.Value;
            }

            int side = (int)Math.Round(Math.Sqrt(n));
            if (side >= 2 && side * side == n)
                return (side, side);

            for (int rows = (int)Math.Floor(Math.Sqrt(n)); rows >= 2; rows--)
            {
                if (n % rows == 0)
                    return (rows, n / rows);
            }

            return (1, Math.Max(1, n));
        }

        public static (int rows, int cols) ResolveLayout(
            int pointCount,
            Point2D[]? worldPts,
            int hintRows = 0,
            int hintCols = 0)
        {
            if (hintRows > 0 && hintCols > 0 && hintRows * hintCols == pointCount)
                return (hintRows, hintCols);
            return InferLayout(pointCount, worldPts);
        }

        private static (int rows, int cols)? TryInferFromWorldCoordinates(Point2D[] worldPts)
        {
            int rowLevels = CountDistinctLevels(worldPts.Select(p => p.Y).ToArray());
            int colLevels = CountDistinctLevels(worldPts.Select(p => p.X).ToArray());
            if (rowLevels >= 2 && colLevels >= 2 && rowLevels * colLevels == worldPts.Length)
                return (rowLevels, colLevels);
            return null;
        }

        /// <summary>按间距聚类统计轴向上不同层级数（允许坐标有小偏差）。</summary>
        private static int CountDistinctLevels(double[] values)
        {
            if (values.Length == 0)
                return 0;
            if (values.Length == 1)
                return 1;

            var order = values.Select((v, i) => (v, i)).OrderBy(x => x.v).ToArray();
            var gaps = new List<double>();
            for (int j = 0; j < order.Length - 1; j++)
            {
                double g = order[j + 1].v - order[j].v;
                if (g > 1e-6)
                    gaps.Add(g);
            }

            if (gaps.Count == 0)
                return 1;

            gaps.Sort();
            double pitch = gaps[gaps.Count / 2];
            double mergeTol = Math.Max(pitch * 0.35, 1e-3);

            int levels = 1;
            double last = order[0].v;
            for (int j = 1; j < order.Length; j++)
            {
                if (order[j].v - last > mergeTol)
                {
                    levels++;
                    last = order[j].v;
                }
            }

            return levels;
        }

        public static void DrawOnGraphics(GdiPen pen, Graphics g, Point2D[] pts, int rows, int cols)
        {
            if (pts == null || pts.Length < 2)
                return;

            if (rows < 1 || cols < 1 || rows * cols != pts.Length)
            {
                var fallback = pts.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                if (fallback.Length >= 3)
                    g.DrawPolygon(pen, fallback);
                else if (fallback.Length == 2)
                    g.DrawLines(pen, fallback);
                return;
            }

            for (int r = 0; r < rows; r++)
            {
                var row = new PointF[cols];
                for (int c = 0; c < cols; c++)
                {
                    var p = pts[r * cols + c];
                    row[c] = new PointF((float)p.X, (float)p.Y);
                }
                if (cols >= 2)
                    g.DrawLines(pen, row);
            }

            for (int c = 0; c < cols; c++)
            {
                var col = new PointF[rows];
                for (int r = 0; r < rows; r++)
                {
                    var p = pts[r * cols + c];
                    col[r] = new PointF((float)p.X, (float)p.Y);
                }
                if (rows >= 2)
                    g.DrawLines(pen, col);
            }
        }

        public static void DrawOnWpfCanvas(Canvas canvas, Point2D[] pts, int rows, int cols, System.Windows.Media.Brush stroke, double thickness = 1.8)
        {
            canvas.Children.Clear();
            if (pts == null || pts.Length < 2)
                return;

            if (rows < 1 || cols < 1 || rows * cols != pts.Length)
                return;

            void AddLine(double x1, double y1, double x2, double y2)
            {
                var line = new Line
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false
                };
                canvas.Children.Add(line);
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    var a = pts[r * cols + c];
                    var b = pts[r * cols + c + 1];
                    AddLine(a.X, a.Y, b.X, b.Y);
                }
            }

            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows - 1; r++)
                {
                    var a = pts[r * cols + c];
                    var b = pts[(r + 1) * cols + c];
                    AddLine(a.X, a.Y, b.X, b.Y);
                }
            }
        }
    }
}
