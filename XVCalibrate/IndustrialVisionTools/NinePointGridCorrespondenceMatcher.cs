using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 世界点↔像素点默认配对：先分行、再分列，用归一化位置 + 容差匹配（允许检测有小幅偏差）。
    /// </summary>
    internal static class NinePointGridCorrespondenceMatcher
    {
        private const double ColumnMatchCostTolerance = 0.55;
        private const double RowBandSnapRatio = 0.42;

        /// <summary>imageIndexForWorld[i] = 与 worldPts[i] 配对的 imagePts 下标。</summary>
        public static int[] Match(Point2D[] worldPts, Point2D[] imagePts) =>
            Match(worldPts, imagePts, 0, 0);

        public static int[] Match(Point2D[] worldPts, Point2D[] imagePts, int hintRows, int hintCols) =>
            Match(worldPts, imagePts, hintRows, hintCols, useProximityOnly: false);

        public static int[] Match(
            Point2D[] worldPts,
            Point2D[] imagePts,
            int hintRows,
            int hintCols,
            bool useProximityOnly)
        {
            int n = worldPts.Length;
            if (n == 0 || imagePts == null || imagePts.Length != n)
                throw new ArgumentException("世界点与像素点数量须一致且非空");

            if (useProximityOnly)
                return MatchByNormalizedProximity(worldPts, imagePts);

            if (TryMatchGrid(worldPts, imagePts, hintRows, hintCols, out int[]? gridMapping))
                return gridMapping!;

            return MatchByNormalizedProximity(worldPts, imagePts);
        }

        /// <summary>散点/强扰动网格：归一化坐标下贪心最近邻一对一配对。</summary>
        public static int[] MatchByNormalizedProximity(Point2D[] worldPts, Point2D[] imagePts)
        {
            int n = worldPts.Length;
            double[] wX = worldPts.Select(p => p.X).ToArray();
            double[] wY = worldPts.Select(p => p.Y).ToArray();
            double maxImgRow = imagePts.Max(p => p.Y);
            double[] iX = imagePts.Select(p => p.X).ToArray();
            double[] iY = imagePts.Select(p => maxImgRow - p.Y).ToArray();

            double[] wNx = NormalizeAxis(wX);
            double[] wNy = NormalizeAxis(wY);
            double[] iNx = NormalizeAxis(iX);
            double[] iNy = NormalizeAxis(iY);

            var candidates = new List<(double cost, int wi, int ii)>(n * n);
            for (int wi = 0; wi < n; wi++)
            {
                for (int ii = 0; ii < n; ii++)
                {
                    double dx = wNx[wi] - iNx[ii];
                    double dy = wNy[wi] - iNy[ii];
                    candidates.Add((dx * dx + dy * dy, wi, ii));
                }
            }

            candidates.Sort((a, b) => a.cost.CompareTo(b.cost));
            var imageForWorld = new int[n];
            var usedWorld = new bool[n];
            var usedImage = new bool[n];
            int assigned = 0;
            foreach (var (cost, wi, ii) in candidates)
            {
                if (usedWorld[wi] || usedImage[ii])
                    continue;
                imageForWorld[wi] = ii;
                usedWorld[wi] = true;
                usedImage[ii] = true;
                assigned++;
                if (assigned == n)
                    break;
            }

            if (assigned != n)
                throw new InvalidOperationException("散点近邻配对未完成，请检查世界点与检测点数量/分布");

            return imageForWorld;
        }

        private static double[] NormalizeAxis(double[] values)
        {
            if (values.Length == 0)
                return Array.Empty<double>();
            double min = values.Min();
            double max = values.Max();
            double span = max - min;
            if (span < 1e-9)
                return values.Select(_ => 0.5).ToArray();
            return values.Select(v => (v - min) / span).ToArray();
        }

        private static bool TryMatchGrid(
            Point2D[] worldPts,
            Point2D[] imagePts,
            int hintRows,
            int hintCols,
            out int[]? imageForWorld)
        {
            imageForWorld = null;
            int n = worldPts.Length;
            var (gridRows, gridCols) = CalibrationPointGrid.ResolveLayout(n, worldPts, hintRows, hintCols);
            if (gridRows * gridCols != n)
                return false;

            double maxImgRow = imagePts.Max(p => p.Y);
            double[] wX = worldPts.Select(p => p.X).ToArray();
            double[] wY = worldPts.Select(p => p.Y).ToArray();
            double[] iX = imagePts.Select(p => p.X).ToArray();
            double[] iY = imagePts.Select(p => maxImgRow - p.Y).ToArray();

            var worldRows = ClusterRows(wY, gridRows);
            var imageRows = ClusterRows(iY, gridRows);
            if (worldRows.Length != gridRows || imageRows.Length != gridRows)
                return false;

            int[] worldRowOrder = OrderClustersByMean(worldRows, wY);
            int[] imageRowOrder = OrderClustersByMean(imageRows, iY);

            var mapping = new int[n];
            var usedImage = new bool[n];
            int matched = 0;

            for (int rank = 0; rank < gridRows; rank++)
            {
                int wCluster = worldRowOrder[rank];
                int iCluster = imageRowOrder[rank];
                matched += MatchRowMembers(
                    worldRows[wCluster], wX,
                    imageRows[iCluster], iX,
                    mapping, usedImage);
            }

            if (matched != n)
                return false;

            imageForWorld = mapping;
            return true;
        }

        private static int MatchRowMembers(
            int[] worldMembers,
            double[] worldX,
            int[] imageMembers,
            double[] imageX,
            int[] imageForWorld,
            bool[] usedImage)
        {
            if (worldMembers.Length == 0 || imageMembers.Length == 0)
                return 0;

            var wOrder = worldMembers.OrderBy(i => worldX[i]).ToArray();
            var iOrder = imageMembers.OrderBy(i => imageX[i]).ToArray();
            double[] wNorm = NormalizePositions(wOrder, worldX);
            double[] iNorm = NormalizePositions(iOrder, imageX);

            if (wOrder.Length == iOrder.Length && wOrder.Length <= 8)
            {
                int[] bestPerm = FindBestColumnPermutation(wNorm, iNorm, ColumnMatchCostTolerance);
                if (bestPerm != null)
                {
                    for (int k = 0; k < wOrder.Length; k++)
                    {
                        int wi = wOrder[k];
                        int ii = iOrder[bestPerm[k]];
                        imageForWorld[wi] = ii;
                        usedImage[ii] = true;
                    }

                    return wOrder.Length;
                }
            }

            return GreedyColumnMatch(wOrder, wNorm, iOrder, iNorm, imageForWorld, usedImage);
        }

        private static int GreedyColumnMatch(
            int[] wOrder,
            double[] wNorm,
            int[] iOrder,
            double[] iNorm,
            int[] imageForWorld,
            bool[] usedImage)
        {
            int matched = 0;
            var remaining = new List<int>(iOrder);
            for (int k = 0; k < wOrder.Length; k++)
            {
                int wi = wOrder[k];
                double target = wNorm[k];
                int bestJ = -1;
                double bestCost = double.MaxValue;
                for (int j = 0; j < remaining.Count; j++)
                {
                    int ii = remaining[j];
                    int idxInOrder = Array.IndexOf(iOrder, ii);
                    double cost = Math.Abs(target - iNorm[idxInOrder]);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestJ = j;
                    }
                }

                if (bestJ < 0 || bestCost > ColumnMatchCostTolerance)
                    continue;

                int picked = remaining[bestJ];
                imageForWorld[wi] = picked;
                usedImage[picked] = true;
                remaining.RemoveAt(bestJ);
                matched++;
            }

            return matched;
        }

        private static int[]? FindBestColumnPermutation(double[] wNorm, double[] iNorm, double maxCost)
        {
            int m = wNorm.Length;
            if (m != iNorm.Length || m == 0)
                return null;

            int[] indices = Enumerable.Range(0, m).ToArray();
            int[]? best = null;
            double bestCost = double.MaxValue;
            Permute(indices, 0, perm =>
            {
                double cost = 0;
                for (int k = 0; k < m; k++)
                    cost += Math.Abs(wNorm[k] - iNorm[perm[k]]);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = (int[])perm.Clone();
                }
            });

            if (best == null || bestCost > maxCost * m)
                return null;
            return best;
        }

        private static void Permute(int[] arr, int start, Action<int[]> visit)
        {
            if (start >= arr.Length)
            {
                visit(arr);
                return;
            }

            for (int i = start; i < arr.Length; i++)
            {
                (arr[start], arr[i]) = (arr[i], arr[start]);
                Permute(arr, start + 1, visit);
                (arr[start], arr[i]) = (arr[i], arr[start]);
            }
        }

        private static double[] NormalizePositions(int[] indices, double[] coord)
        {
            if (indices.Length == 0)
                return Array.Empty<double>();
            if (indices.Length == 1)
                return new[] { 0.5 };

            double min = indices.Min(i => coord[i]);
            double max = indices.Max(i => coord[i]);
            double span = max - min;
            if (span < 1e-9)
                return indices.Select(_ => 0.5).ToArray();

            return indices.Select(i => (coord[i] - min) / span).ToArray();
        }

        private static int[][] ClusterRows(double[] y, int gridRows)
        {
            int n = y.Length;
            // 大行数方阵：间距落格比「取最大间隙」更稳，避免 6×6 等场景错分行
            if (gridRows >= 4 && n >= gridRows * gridRows)
            {
                var bands = AssignToPitchBands(y, gridRows);
                if (bands.Length == gridRows && bands.All(c => c.Length > 0))
                    return bands;
            }

            var gap = ClusterIndicesByLargestGaps(y, gridRows);
            if (gap.Length == gridRows && gap.All(c => c.Length > 0))
                return gap;

            return AssignToPitchBands(y, gridRows);
        }

        /// <summary>按行间距落格；点偏离行中心在 pitch×ratio 内仍归同一行。</summary>
        private static int[][] AssignToPitchBands(double[] y, int gridRows)
        {
            int n = y.Length;
            var clusters = new List<int>[gridRows];
            for (int r = 0; r < gridRows; r++)
                clusters[r] = new List<int>();

            double pitch = EstimatePitch(y, gridRows);
            if (pitch < 1e-6)
            {
                int[] order = Enumerable.Range(0, n).OrderBy(i => y[i]).ToArray();
                int perRow = Math.Max(1, n / gridRows);
                for (int r = 0; r < gridRows; r++)
                {
                    int start = r * perRow;
                    int count = r < gridRows - 1 ? perRow : n - start;
                    if (start >= n)
                        break;
                    clusters[r].AddRange(order.Skip(start).Take(count));
                }

                return clusters.Select(c => c.ToArray()).ToArray();
            }

            double minY = y.Min();
            double snap = pitch * RowBandSnapRatio;
            for (int i = 0; i < n; i++)
            {
                double rel = y[i] - minY;
                int row = (int)Math.Round(rel / pitch);
                row = Math.Clamp(row, 0, gridRows - 1);

                if (Math.Abs(y[i] - (minY + row * pitch)) > snap + pitch * 0.5)
                {
                    int best = row;
                    double bestDist = Math.Abs(y[i] - (minY + row * pitch));
                    for (int r = 0; r < gridRows; r++)
                    {
                        double d = Math.Abs(y[i] - (minY + r * pitch));
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = r;
                        }
                    }

                    row = best;
                }

                clusters[row].Add(i);
            }

            return clusters.Select(c => c.ToArray()).ToArray();
        }

        private static int[] OrderClustersByMean(int[][] clusters, double[] values)
        {
            return clusters
                .Select((members, idx) => (idx, mean: members.Length > 0 ? members.Average(i => values[i]) : double.MaxValue))
                .OrderBy(x => x.mean)
                .ThenBy(x => x.idx)
                .Select(x => x.idx)
                .ToArray();
        }

        private static double EstimatePitch(double[] values, int expectedCells)
        {
            if (values.Length < 2)
                return 0;

            var sorted = values.OrderBy(v => v).ToArray();
            var diffs = new List<double>();
            for (int i = 1; i < sorted.Length; i++)
            {
                double d = sorted[i] - sorted[i - 1];
                if (d > 1e-6)
                    diffs.Add(d);
            }

            if (diffs.Count == 0)
                return 0;

            diffs.Sort();
            double median = diffs[diffs.Count / 2];
            if (expectedCells > 1 && sorted.Length >= expectedCells)
            {
                double fromSpan = (sorted[^1] - sorted[0]) / Math.Max(1, expectedCells - 1);
                if (fromSpan > 1e-6)
                    return (median + fromSpan) * 0.5;
            }

            return median;
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

        private static int[] FallbackImageSortOnly(Point2D[] worldPts, Point2D[] imagePts, int gridRows, int gridCols)
        {
            int n = worldPts.Length;
            double[] rows = imagePts.Select(p => p.Y).ToArray();
            double[] cols = imagePts.Select(p => p.X).ToArray();
            int[] order = NinePointPixelGridSort.SortIndices(n, rows, cols, gridRows, gridCols);
            var mapping = new int[n];
            for (int i = 0; i < n; i++)
                mapping[i] = order[i];
            return mapping;
        }
    }
}
