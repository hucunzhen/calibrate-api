using System;
using System.Collections.Generic;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 九点像素点序号：数学坐标系（左下角原点，X 向右、Y 向上），对小幅格点旋转鲁棒。
    /// 输入为 HALCON 图像 row/col（Y 向下）；输出 permute：sorted[k] = 原下标。
    /// </summary>
    internal static class NinePointPixelGridSort
    {
        public static int[] SortIndices(int n, double[] rows, double[] cols, int gridRows, int gridCols)
        {
            if (n <= 1)
                return Enumerable.Range(0, n).ToArray();

            if (gridRows <= 0 || gridCols <= 0 || gridRows * gridCols != n)
                (gridRows, gridCols) = InferGrid(n);

            double maxRow = rows.Max();
            var xm = new double[n];
            var ym = new double[n];
            for (int i = 0; i < n; i++)
            {
                xm[i] = cols[i];
                ym[i] = maxRow - rows[i];
            }

            int[]? best = null;
            int bestScore = int.MinValue;

            void Consider(int[] order)
            {
                int score = ScoreRowMajor(order, xm, ym, gridRows, gridCols);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = order;
                }
            }

            // 无旋转或小幅旋转时，直接在数学坐标 (X 右, Y 上) 上落格最稳
            Consider(SnapGridOrder(ym, xm, gridRows, gridCols));

            // 对方阵格点 PCA 主轴不稳定，按 1° 步进扫描小幅旋转并在 u/v 两轴上落格
            int angleSteps = gridRows == gridCols ? 90 : 45;
            for (int deg = 0; deg < angleSteps; deg++)
            {
                double ang = deg * Math.PI / 180.0;
                ProjectToUv(xm, ym, ang, out var u, out var v);
                Consider(SnapGridOrder(v, u, gridRows, gridCols));
                Consider(SnapGridOrder(u, v, gridRows, gridCols));
                Consider(BuildRowMajorOrderByGaps(v, u, gridRows, gridCols));
                Consider(BuildRowMajorOrderByGaps(u, v, gridRows, gridCols));
            }

            return best ?? Enumerable.Range(0, n).ToArray();
        }

        private static (int rows, int cols) InferGrid(int n)
        {
            int side = (int)Math.Round(Math.Sqrt(n));
            if (side >= 2 && side * side == n)
                return (side, side);
            return (n, 1);
        }

        private static int[] SnapGridOrder(double[] rowCoord, double[] colCoord, int gridRows, int gridCols)
        {
            int n = rowCoord.Length;
            double pitchR = EstimatePitchFromProjections(rowCoord, gridRows);
            double pitchC = EstimatePitchFromProjections(colCoord, gridCols);
            if (pitchR < 1e-6 || pitchC < 1e-6)
                return FallbackRowMajor(rowCoord, colCoord, gridRows, gridCols);

            double minR = rowCoord.Min();
            double minC = colCoord.Min();
            var tagged = new (int ir, int ic, int idx)[n];
            for (int i = 0; i < n; i++)
            {
                int ir = (int)Math.Round((rowCoord[i] - minR) / pitchR);
                int ic = (int)Math.Round((colCoord[i] - minC) / pitchC);
                tagged[i] = (Math.Clamp(ir, 0, gridRows - 1), Math.Clamp(ic, 0, gridCols - 1), i);
            }

            var irRemap = BuildAxisRemap(tagged.Select(t => (t.ir, t.idx)).ToArray(), rowCoord, gridRows);
            var icRemap = BuildAxisRemap(tagged.Select(t => (t.ic, t.idx)).ToArray(), colCoord, gridCols);

            return tagged
                .OrderBy(t => irRemap[t.ir] * gridCols + icRemap[t.ic])
                .ThenBy(t => t.idx)
                .Select(t => t.idx)
                .ToArray();
        }

        /// <summary>将原始轴标签重映射为 0..k-1，按坐标均值升序（底行/左列优先）。</summary>
        private static Dictionary<int, int> BuildAxisRemap((int label, int idx)[] pairs, double[] coord, int expectedLabels)
        {
            var groups = pairs
                .GroupBy(p => p.label)
                .Select(g => (label: g.Key, mean: g.Average(p => coord[p.idx])))
                .OrderBy(x => x.mean)
                .ToArray();

            var remap = new Dictionary<int, int>();
            for (int i = 0; i < groups.Length; i++)
                remap[groups[i].label] = i;

            for (int label = 0; label < expectedLabels; label++)
            {
                if (!remap.ContainsKey(label))
                    remap[label] = label;
            }

            return remap;
        }

        private static int[] BuildRowMajorOrderByGaps(double[] rowCoord, double[] colCoord, int gridRows, int gridCols)
        {
            int n = rowCoord.Length;
            var rowClusters = ClusterIndicesByLargestGaps(rowCoord, gridRows);
            if (rowClusters.Length != gridRows)
                return FallbackRowMajor(rowCoord, colCoord, gridRows, gridCols);

            var rowOrder = rowClusters
                .Select((members, idx) => (idx, mean: members.Average(i => rowCoord[i])))
                .OrderBy(x => x.mean)
                .Select(x => x.idx)
                .ToArray();

            var ordered = new List<int>(n);
            foreach (int ri in rowOrder)
            {
                var members = rowClusters[ri];
                if (members.Length == 0)
                    return FallbackRowMajor(rowCoord, colCoord, gridRows, gridCols);
                ordered.AddRange(members.OrderBy(i => colCoord[i]));
            }

            return ordered.Count == n
                ? ordered.ToArray()
                : FallbackRowMajor(rowCoord, colCoord, gridRows, gridCols);
        }

        private static int[] FallbackRowMajor(double[] rowCoord, double[] colCoord, int gridRows, int gridCols)
        {
            int n = rowCoord.Length;
            var rowOrder = Enumerable.Range(0, n).OrderBy(i => rowCoord[i]).ToArray();
            var result = new List<int>(n);
            int perRow = Math.Max(1, n / gridRows);
            for (int r = 0; r < gridRows; r++)
            {
                int start = r * perRow;
                int count = r < gridRows - 1 ? perRow : n - start;
                if (start >= n)
                    break;
                var slice = rowOrder.Skip(start).Take(count).OrderBy(i => colCoord[i]);
                result.AddRange(slice);
            }

            return result.Count == n ? result.ToArray() : rowOrder;
        }

        private static int[][] ClusterIndicesByLargestGaps(double[] values, int clusterCount)
        {
            int n = values.Length;
            if (clusterCount <= 1)
                return new[] { Enumerable.Range(0, n).ToArray() };

            var order = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
            var gaps = new List<(int between, double gap)>();
            for (int j = 0; j < n - 1; j++)
                gaps.Add((j, values[order[j + 1]] - values[order[j]]));

            int splitsNeeded = clusterCount - 1;
            if (gaps.Count < splitsNeeded)
                return new[] { order };

            var splitBetween = gaps
                .OrderByDescending(x => x.gap)
                .Take(splitsNeeded)
                .Select(x => x.between)
                .OrderBy(x => x)
                .ToArray();

            var clusters = new List<int[]>();
            int begin = 0;
            foreach (int b in splitBetween)
            {
                clusters.Add(order[begin..(b + 1)]);
                begin = b + 1;
            }

            clusters.Add(order[begin..]);
            return clusters.ToArray();
        }

        private static int ScoreRowMajor(int[] order, double[] xm, double[] ym, int gridRows, int gridCols)
        {
            if (order.Length != gridRows * gridCols)
                return int.MinValue;

            int score = 0;
            for (int r = 0; r < gridRows; r++)
            {
                var rowIdx = order.Skip(r * gridCols).Take(gridCols).ToArray();
                double meanY = rowIdx.Average(i => ym[i]);
                if (r > 0)
                {
                    var prev = order.Skip((r - 1) * gridCols).Take(gridCols).ToArray();
                    double prevY = prev.Average(i => ym[i]);
                    if (meanY > prevY + 1e-6)
                        score += 10;
                    else if (Math.Abs(meanY - prevY) <= 1e-6)
                        score += 1;
                    else
                        score -= 50;
                }

                for (int c = 1; c < gridCols; c++)
                {
                    if (xm[rowIdx[c]] > xm[rowIdx[c - 1]] + 1e-6)
                        score += 5;
                }

                if (rowIdx.Length == gridCols)
                    score += 2;

                var distinctCells = rowIdx.Select(i => (xm[i], ym[i])).Distinct().Count();
                if (distinctCells == gridCols)
                    score += 3;
            }

            return score;
        }

        private static double EstimatePitchFromProjections(double[] proj, int expectedCells)
        {
            if (proj.Length < 2)
                return 0;

            var sorted = proj.OrderBy(x => x).ToArray();
            var diffs = new List<double>();
            for (int i = 1; i < sorted.Length; i++)
            {
                double d = sorted[i] - sorted[i - 1];
                if (d > 1e-3)
                    diffs.Add(d);
            }

            if (diffs.Count == 0)
                return 0;

            diffs.Sort();
            double median = diffs[diffs.Count / 2];
            if (expectedCells > 1 && sorted.Length >= expectedCells)
            {
                double fromSpan = (sorted[^1] - sorted[0]) / Math.Max(1, expectedCells - 1);
                if (fromSpan > 1e-3)
                    return (median + fromSpan) * 0.5;
            }

            return median;
        }

        private static void ProjectToUv(double[] xs, double[] ys, double angleRad, out double[] u, out double[] v)
        {
            int n = xs.Length;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            u = new double[n];
            v = new double[n];
            for (int i = 0; i < n; i++)
            {
                u[i] = xs[i] * cos + ys[i] * sin;
                v[i] = -xs[i] * sin + ys[i] * cos;
            }
        }

        private static double EstimatePrincipalAngleRad(double[] xs, double[] ys)
        {
            int n = xs.Length;
            double mx = xs.Average();
            double my = ys.Average();
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - mx;
                double dy = ys[i] - my;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            if (sxx + syy < 1e-6)
                return 0;
            return 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        }
    }
}
