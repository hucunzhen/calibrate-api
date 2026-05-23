// 标定 / 焊接：在世界坐标系(mm)下生成规则点列，供「发送PLC」或后续标定使用。

using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        /// <summary>
        /// 将 UI 下拉项、旧版中文别名、英文 type 统一为内部 pattern 键。
        /// </summary>
        private static string NormalizeWeldPatternToKey(string? patternRaw)
        {
            string p = (patternRaw ?? "").Trim();
            if (string.IsNullOrEmpty(p))
                throw new InvalidOperationException("焊接轨迹: pattern 为空");

            // 下拉列表（与 OperatorCatalog.Options 一致）
            switch (p)
            {
                case "九宫格 (3×3)": return "nine_3x3";
                case "十字交叉折线": return "cross_lines";
                case "五点十字": return "cross_5";
                case "L形轨迹": return "l_shape";
                case "直线段": return "line";
                case "矩形周长": return "rect";
                case "网格蛇形": return "grid_snake";
            }

            string key = p.ToLowerInvariant();
            // 英文键（不区分大小写）
            switch (key)
            {
                case "nine_3x3":
                case "cross_lines":
                case "cross_5":
                case "l_shape":
                case "line":
                case "rect":
                case "grid_snake":
                    return key;
            }

            // 旧版 / 手写别名
            if (key is "九点" or "9点" or "九宫格") return "nine_3x3";
            if (key is "十字" or "十字线" or "交叉") return "cross_lines";
            if (key is "五点十字" or "十字五点") return "cross_5";
            if (key is "l形" or "l型") return "l_shape";
            if (key is "直线") return "line";
            if (key is "矩形" or "方框") return "rect";
            if (key is "网格蛇形" or "蛇形") return "grid_snake";

            throw new InvalidOperationException(
                $"焊接轨迹: 未知 pattern「{p}」。请从下拉选择，或使用: nine_3x3, cross_lines, cross_5, l_shape, line, rect, grid_snake");
        }

        /// <summary>
        /// 根据 pattern 与参数生成世界 XY 点列（单位与 center/step 一致，通常为 mm）。
        /// </summary>
        private static Point2D[] GenerateWeldTrajectoryWorld(
            string patternRaw,
            double centerX,
            double centerY,
            double stepMm,
            double armMm,
            double legXmm,
            double legYmm,
            double angleDeg,
            int gridCols,
            int gridRows,
            int samplesPerSegment)
        {
            string key = NormalizeWeldPatternToKey(patternRaw);

            samplesPerSegment = Math.Max(1, samplesPerSegment);
            gridCols = Math.Max(1, gridCols);
            gridRows = Math.Max(1, gridRows);
            stepMm = Math.Max(1e-6, stepMm);
            armMm = Math.Max(1e-6, armMm);

            return key switch
            {
                "nine_3x3" => BuildNineGrid(centerX, centerY, stepMm, 3, 3),
                "cross_lines" => BuildCrossLines(centerX, centerY, armMm, samplesPerSegment),
                "cross_5" => BuildCrossFive(centerX, centerY, armMm),
                "l_shape" => BuildLShape(centerX, centerY, legXmm, legYmm, stepMm),
                "line" => BuildLine(centerX, centerY, armMm * 2.0, angleDeg, samplesPerSegment),
                "rect" => BuildRect(centerX, centerY, armMm * 2.0, armMm * 2.0, stepMm),
                "grid_snake" => BuildGridSnake(centerX, centerY, stepMm, gridCols, gridRows),
                _ => throw new InvalidOperationException($"焊接轨迹: 未识别的 pattern 键「{key}」")
            };
        }

        /// <summary>世界 XY 轨迹 + 统一 Z，输出基座 3D 点列。</summary>
        private static CalibPoint3D[] GenerateWeldTrajectoryWorld3D(
            string patternRaw,
            double centerX,
            double centerY,
            double centerZ,
            double stepMm,
            double armMm,
            double legXmm,
            double legYmm,
            double angleDeg,
            int gridCols,
            int gridRows,
            int samplesPerSegment)
        {
            Point2D[] xy = GenerateWeldTrajectoryWorld(
                patternRaw, centerX, centerY, stepMm, armMm, legXmm, legYmm, angleDeg, gridCols, gridRows, samplesPerSegment);
            return xy.Select(p => new CalibPoint3D(p.X, p.Y, centerZ)).ToArray();
        }

        private static Point2D[] BuildNineGrid(double cx, double cy, double step, int cols, int rows)
        {
            if (cols < 1 || rows < 1) return Array.Empty<Point2D>();
            var list = new List<Point2D>(cols * rows);
            double ox = -((cols - 1) * 0.5) * step;
            double oy = -((rows - 1) * 0.5) * step;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    list.Add(new Point2D(cx + ox + c * step, cy + oy + r * step));
            }
            return list.ToArray();
        }

        private static Point2D[] BuildCrossFive(double cx, double cy, double arm)
        {
            return new[]
            {
                new Point2D(cx, cy),
                new Point2D(cx, cy - arm),
                new Point2D(cx + arm, cy),
                new Point2D(cx, cy + arm),
                new Point2D(cx - arm, cy)
            };
        }

        private static Point2D[] BuildCrossLines(double cx, double cy, double arm, int samples)
        {
            var seg1 = InterpolateSegment(cx, cy - arm, cx, cy + arm, samples);
            var seg2 = InterpolateSegment(cx - arm, cy, cx + arm, cy, samples);
            var list = new List<Point2D>(seg1.Count + seg2.Count);
            list.AddRange(seg1);
            // 避免重复中心点：若 seg1 末与 seg2 中重复，跳过 seg2 首点
            if (list.Count > 0 && seg2.Count > 0)
            {
                var a = list[^1];
                var b = seg2[0];
                if (Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9)
                {
                    for (int i = 1; i < seg2.Count; i++) list.Add(seg2[i]);
                    return list.ToArray();
                }
            }
            list.AddRange(seg2);
            return list.ToArray();
        }

        private static List<Point2D> InterpolateSegment(double x0, double y0, double x1, double y1, int samples)
        {
            var list = new List<Point2D>(samples + 1);
            if (samples <= 1)
            {
                list.Add(new Point2D(x0, y0));
                list.Add(new Point2D(x1, y1));
                return list;
            }
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                list.Add(new Point2D(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
            }
            return list;
        }

        private static Point2D[] BuildLShape(double cx, double cy, double legX, double legY, double step)
        {
            legX = Math.Max(1e-6, legX);
            legY = Math.Max(1e-6, legY);
            var list = new List<Point2D>();
            // 水平段 (cx,cy) -> (cx+legX, cy)
            int nx = Math.Max(2, (int)Math.Ceiling(legX / step) + 1);
            for (int i = 0; i < nx; i++)
            {
                double t = Math.Min(1.0, i / (double)(nx - 1));
                list.Add(new Point2D(cx + legX * t, cy));
            }
            // 竖直段 (cx+legX, cy) -> (cx+legX, cy+legY)，跳过与上段重复的末点
            int ny = Math.Max(2, (int)Math.Ceiling(legY / step) + 1);
            for (int j = 1; j < ny; j++)
            {
                double t = j / (double)(ny - 1);
                list.Add(new Point2D(cx + legX, cy + legY * t));
            }
            return list.ToArray();
        }

        private static Point2D[] BuildLine(double cx, double cy, double length, double angleDeg, int samples)
        {
            double rad = angleDeg * (Math.PI / 180.0);
            double hx = 0.5 * length * Math.Cos(rad);
            double hy = 0.5 * length * Math.Sin(rad);
            var seg = InterpolateSegment(cx - hx, cy - hy, cx + hx, cy + hy, samples);
            return seg.ToArray();
        }

        private static Point2D[] BuildRect(double cx, double cy, double width, double height, double step)
        {
            width = Math.Max(1e-6, width);
            height = Math.Max(1e-6, height);
            double x0 = cx - width * 0.5;
            double y0 = cy - height * 0.5;
            double x1 = cx + width * 0.5;
            double y1 = cy + height * 0.5;
            var list = new List<Point2D>();

            void AddEdge(double ax, double ay, double bx, double by)
            {
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int n = Math.Max(2, (int)Math.Ceiling(len / step) + 1);
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)(n - 1);
                    var pt = new Point2D(ax + (bx - ax) * t, ay + (by - ay) * t);
                    if (list.Count > 0)
                    {
                        var last = list[^1];
                        if (Math.Abs(last.X - pt.X) < 1e-9 && Math.Abs(last.Y - pt.Y) < 1e-9)
                            continue;
                    }
                    list.Add(pt);
                }
            }

            // 顺时针：底边 -> 右边 -> 顶边 -> 左边
            AddEdge(x0, y1, x1, y1);
            AddEdge(x1, y1, x1, y0);
            AddEdge(x1, y0, x0, y0);
            AddEdge(x0, y0, x0, y1);
            return list.ToArray();
        }

        private static Point2D[] BuildGridSnake(double cx, double cy, double step, int cols, int rows)
        {
            var pts = BuildNineGrid(cx, cy, step, cols, rows);
            if (pts.Length == 0) return pts;
            // BuildNineGrid is row-major (r inner); reorder to snake by physical row in grid
            var byRow = new List<Point2D>[rows];
            for (int r = 0; r < rows; r++)
            {
                byRow[r] = new List<Point2D>(cols);
                for (int c = 0; c < cols; c++)
                    byRow[r].Add(pts[r * cols + c]);
            }
            var outList = new List<Point2D>(pts.Length);
            bool flip = false;
            for (int r = 0; r < rows; r++)
            {
                var row = byRow[r];
                if (flip)
                {
                    for (int c = row.Count - 1; c >= 0; c--)
                        outList.Add(row[c]);
                }
                else
                {
                    foreach (var p in row)
                        outList.Add(p);
                }
                flip = !flip;
            }
            return outList.ToArray();
        }
    }
}
