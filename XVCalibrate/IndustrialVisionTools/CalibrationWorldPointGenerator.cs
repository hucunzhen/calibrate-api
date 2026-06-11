using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>标定用世界点列：规则网格、带扰动网格、工作区散点。</summary>
    internal static class CalibrationWorldPointGenerator
    {
        public const double MaxJitterRatio = 0.35;

        /// <summary>规则网格每点叠加 [-ratio×步距, +ratio×步距] 内随机偏移（可复现）。</summary>
        public static Point2D[] BuildJitteredGrid(
            double centerX,
            double centerY,
            double stepX,
            double stepY,
            int cols,
            int rows,
            double jitterRatio,
            int randomSeed)
        {
            var basePts = BuildRegularGrid(centerX, centerY, stepX, stepY, cols, rows);
            jitterRatio = Math.Clamp(jitterRatio, 0, MaxJitterRatio);
            if (jitterRatio <= 1e-9)
                return basePts;

            var rng = new Random(randomSeed);
            double jx = stepX * jitterRatio;
            double jy = stepY * jitterRatio;
            var result = new Point2D[basePts.Length];
            for (int i = 0; i < basePts.Length; i++)
            {
                result[i] = new Point2D
                {
                    X = basePts[i].X + (rng.NextDouble() * 2 - 1) * jx,
                    Y = basePts[i].Y + (rng.NextDouble() * 2 - 1) * jy
                };
            }

            return result;
        }

        /// <summary>在工作区矩形内生成散点，尽量保持最小间距（蓝噪声式拒绝采样）。</summary>
        public static Point2D[] BuildScatteredInRect(
            double centerX,
            double centerY,
            double halfWidth,
            double halfHeight,
            int count,
            int randomSeed,
            double minSeparationRatio = 0.35)
        {
            count = Math.Max(4, count);
            halfWidth = Math.Max(1e-3, halfWidth);
            halfHeight = Math.Max(1e-3, halfHeight);
            minSeparationRatio = Math.Clamp(minSeparationRatio, 0.15, 0.6);

            double area = 4 * halfWidth * halfHeight;
            double minDist = Math.Sqrt(area / count) * minSeparationRatio;
            minDist = Math.Max(minDist, 1e-3);

            var rng = new Random(randomSeed);
            var pts = new List<Point2D>(count);
            int attempts = 0;
            int maxAttempts = count * 500;
            while (pts.Count < count && attempts < maxAttempts)
            {
                attempts++;
                var candidate = new Point2D
                {
                    X = centerX + (rng.NextDouble() * 2 - 1) * halfWidth,
                    Y = centerY + (rng.NextDouble() * 2 - 1) * halfHeight
                };

                bool tooClose = false;
                foreach (var p in pts)
                {
                    double dx = candidate.X - p.X;
                    double dy = candidate.Y - p.Y;
                    if (dx * dx + dy * dy < minDist * minDist)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                    pts.Add(candidate);
            }

            if (pts.Count < count)
            {
                while (pts.Count < count)
                {
                    pts.Add(new Point2D
                    {
                        X = centerX + (rng.NextDouble() * 2 - 1) * halfWidth,
                        Y = centerY + (rng.NextDouble() * 2 - 1) * halfHeight
                    });
                }
            }

            return pts.ToArray();
        }

        public static Point2D[] BuildRegularGrid(
            double centerX,
            double centerY,
            double stepX,
            double stepY,
            int cols,
            int rows)
        {
            if (cols < 1 || rows < 1)
                return Array.Empty<Point2D>();

            var list = new List<Point2D>(cols * rows);
            double ox = -((cols - 1) * 0.5) * stepX;
            double oy = -((rows - 1) * 0.5) * stepY;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    list.Add(new Point2D(centerX + ox + c * stepX, centerY + oy + r * stepY));
            }

            return list.ToArray();
        }
    }
}
