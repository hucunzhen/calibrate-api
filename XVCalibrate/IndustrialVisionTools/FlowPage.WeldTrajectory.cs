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
                case "方格 (4×4)": return "grid_4x4";
                case "方格 (5×5)": return "grid_5x5";
                case "方格 (6×6)": return "grid_6x6";
                case "网格+扰动 (3×3)": return "grid_jitter_3x3";
                case "网格+扰动 (4×4)": return "grid_jitter_4x4";
                case "网格+扰动 (5×5)": return "grid_jitter_5x5";
                case "网格+扰动 (6×6)": return "grid_jitter_6x6";
                case "工作区散点": return "scattered";
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
                case "grid_4x4":
                case "grid_5x5":
                case "grid_6x6":
                case "grid_jitter_3x3":
                case "grid_jitter_4x4":
                case "grid_jitter_5x5":
                case "grid_jitter_6x6":
                case "scattered":
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
            if (key is "4x4" or "四点" or "16点") return "grid_4x4";
            if (key is "5x5" or "25点") return "grid_5x5";
            if (key is "6x6" or "36点") return "grid_6x6";
            if (key is "jitter" or "扰动" or "grid_jitter") return "grid_jitter_4x4";
            if (key is "scattered" or "散点" or "random") return "scattered";
            if (key is "十字" or "十字线" or "交叉") return "cross_lines";
            if (key is "五点十字" or "十字五点") return "cross_5";
            if (key is "l形" or "l型") return "l_shape";
            if (key is "直线") return "line";
            if (key is "矩形" or "方框") return "rect";
            if (key is "网格蛇形" or "蛇形") return "grid_snake";

            throw new InvalidOperationException(
                $"焊接轨迹: 未知 pattern「{p}」。请从下拉选择，或使用: nine_3x3, grid_4x4, grid_jitter_4x4, scattered, cross_lines 等");
        }

        private static (double StepX, double StepY) ResolveWeldTrajectorySteps(double stepMm, double stepXmm, double stepYmm)
        {
            double fb = Math.Max(1e-6, stepMm);
            double sx = stepXmm > 0 ? Math.Max(1e-6, stepXmm) : fb;
            double sy = stepYmm > 0 ? Math.Max(1e-6, stepYmm) : fb;
            return (sx, sy);
        }

        private static Point2D RotatePointAroundCenter(double px, double py, double rcx, double rcy, double angleDeg)
        {
            if (Math.Abs(angleDeg) < 1e-12)
                return new Point2D(px, py);
            double rad = angleDeg * (Math.PI / 180.0);
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double dx = px - rcx;
            double dy = py - rcy;
            return new Point2D(rcx + dx * cos - dy * sin, rcy + dx * sin + dy * cos);
        }

        private static Point2D[] RotateTrajectoryPoints(Point2D[] pts, double rcx, double rcy, double rotateDeg)
        {
            if (pts.Length == 0 || Math.Abs(rotateDeg) < 1e-12)
                return pts;
            var rotated = new Point2D[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                rotated[i] = RotatePointAroundCenter(pts[i].X, pts[i].Y, rcx, rcy, rotateDeg);
            return rotated;
        }

        private static (double Rcx, double Rcy) ResolveWeldRotateCenter(
            double centerX, double centerY, double rotateCenterX, double rotateCenterY, bool hasRotateCenterX, bool hasRotateCenterY)
        {
            double rcx = hasRotateCenterX ? rotateCenterX : centerX;
            double rcy = hasRotateCenterY ? rotateCenterY : centerY;
            return (rcx, rcy);
        }

        /// <summary>
        /// 根据 pattern 与参数生成世界 XY 点列；生成后可绕旋转中心整体旋转 rotateDeg（逆时针为正）。
        /// </summary>
        private static Point2D[] GenerateWeldTrajectoryWorld(
            string patternRaw,
            double centerX,
            double centerY,
            double stepMm,
            double stepXmm,
            double stepYmm,
            double armMm,
            double legXmm,
            double legYmm,
            double angleDeg,
            int gridCols,
            int gridRows,
            int samplesPerSegment,
            double jitterRatio = 0.15,
            int randomSeed = 42,
            int scatterCount = 16,
            double rotateDeg = 0,
            double rotateCenterX = 0,
            double rotateCenterY = 0,
            bool hasRotateCenterX = false,
            bool hasRotateCenterY = false)
        {
            string key = NormalizeWeldPatternToKey(patternRaw);

            samplesPerSegment = Math.Max(1, samplesPerSegment);
            gridCols = Math.Max(1, gridCols);
            gridRows = Math.Max(1, gridRows);
            armMm = Math.Max(1e-6, armMm);
            var (stepX, stepY) = ResolveWeldTrajectorySteps(stepMm, stepXmm, stepYmm);

            Point2D[] pts = key switch
            {
                "nine_3x3" => BuildNineGrid(centerX, centerY, stepX, stepY, 3, 3),
                "grid_4x4" => BuildNineGrid(centerX, centerY, stepX, stepY, 4, 4),
                "grid_5x5" => BuildNineGrid(centerX, centerY, stepX, stepY, 5, 5),
                "grid_6x6" => BuildNineGrid(centerX, centerY, stepX, stepY, 6, 6),
                "grid_jitter_3x3" => CalibrationWorldPointGenerator.BuildJitteredGrid(
                    centerX, centerY, stepX, stepY, 3, 3, jitterRatio, randomSeed),
                "grid_jitter_4x4" => CalibrationWorldPointGenerator.BuildJitteredGrid(
                    centerX, centerY, stepX, stepY, 4, 4, jitterRatio, randomSeed),
                "grid_jitter_5x5" => CalibrationWorldPointGenerator.BuildJitteredGrid(
                    centerX, centerY, stepX, stepY, 5, 5, jitterRatio, randomSeed),
                "grid_jitter_6x6" => CalibrationWorldPointGenerator.BuildJitteredGrid(
                    centerX, centerY, stepX, stepY, 6, 6, jitterRatio, randomSeed),
                "scattered" => CalibrationWorldPointGenerator.BuildScatteredInRect(
                    centerX, centerY, armMm, armMm, scatterCount, randomSeed),
                "cross_lines" => BuildCrossLines(centerX, centerY, armMm, samplesPerSegment),
                "cross_5" => BuildCrossFive(centerX, centerY, armMm),
                "l_shape" => BuildLShape(centerX, centerY, legXmm, legYmm, stepX, stepY),
                "line" => BuildLine(centerX, centerY, armMm * 2.0, angleDeg, samplesPerSegment),
                "rect" => BuildRect(centerX, centerY, armMm * 2.0, armMm * 2.0, stepX, stepY),
                "grid_snake" => BuildGridSnake(centerX, centerY, stepX, stepY, gridCols, gridRows),
                _ => throw new InvalidOperationException($"焊接轨迹: 未识别的 pattern 键「{key}」")
            };

            var (rcx, rcy) = ResolveWeldRotateCenter(centerX, centerY, rotateCenterX, rotateCenterY, hasRotateCenterX, hasRotateCenterY);
            return RotateTrajectoryPoints(pts, rcx, rcy, rotateDeg);
        }

        /// <summary>世界 XY 轨迹 + 统一 Z，输出基座 3D 点列。</summary>
        private static CalibPoint3D[] GenerateWeldTrajectoryWorld3D(
            string patternRaw,
            double centerX,
            double centerY,
            double centerZ,
            double stepMm,
            double stepXmm,
            double stepYmm,
            double armMm,
            double legXmm,
            double legYmm,
            double angleDeg,
            int gridCols,
            int gridRows,
            int samplesPerSegment,
            double jitterRatio = 0.15,
            int randomSeed = 42,
            int scatterCount = 16,
            double rotateDeg = 0,
            double rotateCenterX = 0,
            double rotateCenterY = 0,
            bool hasRotateCenterX = false,
            bool hasRotateCenterY = false)
        {
            Point2D[] xy = GenerateWeldTrajectoryWorld(
                patternRaw, centerX, centerY, stepMm, stepXmm, stepYmm, armMm, legXmm, legYmm, angleDeg,
                gridCols, gridRows, samplesPerSegment, jitterRatio, randomSeed, scatterCount,
                rotateDeg, rotateCenterX, rotateCenterY, hasRotateCenterX, hasRotateCenterY);
            return xy.Select(p => new CalibPoint3D(p.X, p.Y, centerZ)).ToArray();
        }

        private static Point2D[] BuildNineGrid(double cx, double cy, double stepX, double stepY, int cols, int rows)
        {
            if (cols < 1 || rows < 1) return Array.Empty<Point2D>();
            var list = new List<Point2D>(cols * rows);
            double ox = -((cols - 1) * 0.5) * stepX;
            double oy = -((rows - 1) * 0.5) * stepY;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    list.Add(new Point2D(cx + ox + c * stepX, cy + oy + r * stepY));
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

        private static Point2D[] BuildLShape(double cx, double cy, double legX, double legY, double stepX, double stepY)
        {
            legX = Math.Max(1e-6, legX);
            legY = Math.Max(1e-6, legY);
            stepX = Math.Max(1e-6, stepX);
            stepY = Math.Max(1e-6, stepY);
            var list = new List<Point2D>();
            // 水平段 (cx,cy) -> (cx+legX, cy)
            int nx = Math.Max(2, (int)Math.Ceiling(legX / stepX) + 1);
            for (int i = 0; i < nx; i++)
            {
                double t = Math.Min(1.0, i / (double)(nx - 1));
                list.Add(new Point2D(cx + legX * t, cy));
            }
            // 竖直段 (cx+legX, cy) -> (cx+legX, cy+legY)，跳过与上段重复的末点
            int ny = Math.Max(2, (int)Math.Ceiling(legY / stepY) + 1);
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

        private static Point2D[] BuildRect(double cx, double cy, double width, double height, double stepX, double stepY)
        {
            width = Math.Max(1e-6, width);
            height = Math.Max(1e-6, height);
            stepX = Math.Max(1e-6, stepX);
            stepY = Math.Max(1e-6, stepY);
            double x0 = cx - width * 0.5;
            double y0 = cy - height * 0.5;
            double x1 = cx + width * 0.5;
            double y1 = cy + height * 0.5;
            var list = new List<Point2D>();

            void AddEdge(double ax, double ay, double bx, double by)
            {
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                bool horizontal = Math.Abs(by - ay) < Math.Abs(bx - ax);
                double edgeStep = horizontal ? stepX : stepY;
                int n = Math.Max(2, (int)Math.Ceiling(len / edgeStep) + 1);
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

        private static Point2D[] BuildGridSnake(double cx, double cy, double stepX, double stepY, int cols, int rows)
        {
            var pts = BuildNineGrid(cx, cy, stepX, stepY, cols, rows);
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
