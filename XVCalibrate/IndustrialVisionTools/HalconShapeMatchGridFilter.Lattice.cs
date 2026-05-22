using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    /// <summary>匹配点聚类：定向 u/v、全局列/行中心线、等间距一体阵列。</summary>
    internal static partial class HalconShapeMatchGridFilter
    {
        public sealed class LatticeFitResult
        {
            public RowColumnLattice Lattice { get; init; } = new();
            public int InputCount { get; init; }
            public double AngleRad { get; init; }
            public double EstimatedAngleDeg { get; init; }
            public bool AxesSwapped { get; init; }
            public int UserGridRows { get; init; }
            public int UserGridCols { get; init; }
            public int EffRows { get; init; }
            public int EffCols { get; init; }
            public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
            /// <summary>参与共识的 N 连（如 2×8 的 8 颗）在图像中的排布方向角（°），与 HALCON 模板角不同。</summary>
            public double ChainDirectionAngleDeg { get; init; } = double.NaN;
            public double MatchConcentration { get; init; }
            public double[] U { get; init; } = Array.Empty<double>();
            public double[] V { get; init; } = Array.Empty<double>();
            public double PitchCol { get; init; }
            public double PitchRow { get; init; }
            public double SnapU { get; init; }
            public double SnapV { get; init; }
            public double[] CellRow { get; init; } = Array.Empty<double>();
            public double[] CellCol { get; init; } = Array.Empty<double>();
            public bool[] CellFound { get; init; } = Array.Empty<bool>();
            public double[] CellAngleDeg { get; init; } = Array.Empty<double>();
            public int[] PointGridRow { get; init; } = Array.Empty<int>();
            public int[] PointGridCol { get; init; } = Array.Empty<int>();
        }

        internal static HalconShapeMatchChainDirectionResult EstimateChainDirection(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "ChainDir" : diagnosticTag;
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return new HalconShapeMatchChainDirectionResult { InputCount = 0 };

            double[] r = rows!;
            double[] c = cols!;
            DiagLattice(tag, $"估计链向: 阵列 {gridRows}×{gridCols}, 输入 {n}");

            double consensusMatchDeg = double.NaN;
            double matchConc = 0;
            int[] consensusPick = Array.Empty<int>();
            double chainDirectionDeg = double.NaN;
            if (angles != null && angles.Length >= n)
            {
                (consensusMatchDeg, matchConc, consensusPick) = ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);
                chainDirectionDeg = EstimateChainDirectionAngleDeg(c, r, consensusPick);
            }

            return new HalconShapeMatchChainDirectionResult
            {
                InputCount = n,
                ChainDirectionAngleDeg = chainDirectionDeg,
                ConsensusMatchAngleDeg = consensusMatchDeg,
                MatchConcentration = matchConc,
                ConsensusPickIndices = consensusPick
            };
        }

        public static LatticeFitResult FitLattice(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double pitchRow,
            double pitchCol,
            double? gridAngleDeg,
            double snapTolerancePx,
            string? diagnosticTag = null,
            double? chainDirectionAngleDeg = null,
            double? consensusMatchAngleDeg = null,
            int[]? consensusPickIndices = null)
        {

            string tag = string.IsNullOrEmpty(diagnosticTag) ? "LatticeFit" : diagnosticTag;
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
            {
                DiagLattice(tag, "输入为空");
                return new LatticeFitResult { InputCount = 0 };
            }
            if (gridRows < 1 || gridCols < 1)
                throw new ArgumentException("gridRows / gridCols 至少为 1");

            double[] r = rows!;
            double[] c = cols!;

            DiagLattice(tag, $"阵列 {gridRows}×{gridCols}, 输入 {n}, pitchRow={pitchRow}, pitchCol={pitchCol}, " +
                $"gridAngle={(gridAngleDeg.HasValue ? gridAngleDeg.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "auto")}");

            bool userFixedAngle = gridAngleDeg.HasValue && !double.IsNaN(gridAngleDeg.Value);
            bool chainFromUpstream = chainDirectionAngleDeg.HasValue && !double.IsNaN(chainDirectionAngleDeg.Value);
            double consensusMatchDeg = consensusMatchAngleDeg ?? double.NaN;
            double matchConc = 0;
            double chainDirectionDeg = chainFromUpstream ? chainDirectionAngleDeg!.Value : double.NaN;
            int[] consensusPick = consensusPickIndices ?? Array.Empty<int>();

            if (angles != null && angles.Length >= n)
            {
                if (consensusPick.Length == 0 || double.IsNaN(consensusMatchDeg))
                {
                    (double computedDeg, double computedConc, int[] computedPick) =
                        ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);
                    if (consensusPick.Length == 0)
                        consensusPick = computedPick;
                    if (double.IsNaN(consensusMatchDeg))
                        consensusMatchDeg = computedDeg;
                    matchConc = computedConc;
                }
                else if (!double.IsNaN(consensusMatchDeg))
                    matchConc = MatchAngleConcentrationOnIndices(angles, consensusPick);

                if (!chainFromUpstream && consensusPick.Length > 0)
                    chainDirectionDeg = EstimateChainDirectionAngleDeg(c, r, consensusPick);
            }

            if (chainFromUpstream)
                DiagLattice(tag, $"使用上游格网角 θ={chainDirectionDeg:F1}°");
            bool useChainAlignedGrid = !userFixedAngle && !double.IsNaN(chainDirectionDeg);
            bool useTemplateTilt = !userFixedAngle && !useChainAlignedGrid && angles != null && matchConc >= 0.3;
            OrientationChoice orientation;
            if (userFixedAngle)
                orientation = ResolveOrientationWithFixedAngle(c, r, angles, scores, gridRows, gridCols, gridAngleDeg!.Value, consensusMatchDeg, tag);
            else if (useChainAlignedGrid)
                orientation = ResolveOrientationAtChainAngle(c, r, scores, gridRows, gridCols, chainDirectionDeg, tag);
            else if (useTemplateTilt)
                orientation = ResolveOrientationFromTemplateTilt(c, r, angles!, scores, gridRows, gridCols, consensusMatchDeg, tag);
            else
                orientation = ResolveOrientationAuto(c, r, angles, scores, gridRows, gridCols, consensusMatchDeg, tag);

            double angleRad = orientation.AngleRad;
            bool swapUv = orientation.SwapUv;
            if (!useChainAlignedGrid && !useTemplateTilt && !userFixedAngle && !double.IsNaN(consensusMatchDeg) && matchConc >= 0.3)
            {
                double beforeDeg = angleRad * 180.0 / Math.PI;
                angleRad = SnapGridAngleToMatchRad(angleRad, consensusMatchDeg);
                DiagLattice(tag, $"对齐模板角: {beforeDeg:F1}°→{angleRad * 180.0 / Math.PI:F1}° (共识≈{consensusMatchDeg:F1}°)");
            }

            if (useChainAlignedGrid)
            {
                chainDirectionDeg = angleRad * 180.0 / Math.PI;
                DiagLattice(tag, $"格网按链向角定向(先列后行聚类): θ={chainDirectionDeg:F1}° swap={swapUv}");
            }

            int effRows = swapUv ? gridCols : gridRows;
            int effCols = swapUv ? gridRows : gridCols;
            ProjectToUv(c, r, angleRad, out var u, out var v);
            double pitchU = pitchCol > 0 ? pitchCol : EstimatePitchFromProjections(u, effCols);
            double pitchV = pitchRow > 0 ? pitchRow : EstimatePitchFromProjections(v, effRows);
            if (pitchU < 1e-3 || pitchV < 1e-3)
            {
                DiagLattice(tag, "格距估计失败");
                return new LatticeFitResult
                {
                    InputCount = n,
                    AngleRad = angleRad,
                    EstimatedAngleDeg = angleRad * 180.0 / Math.PI,
                    AxesSwapped = swapUv,
                    UserGridRows = gridRows,
                    UserGridCols = gridCols,
                    EffRows = effRows,
                    EffCols = effCols,
                    ConsensusMatchAngleDeg = consensusMatchDeg,
                    ChainDirectionAngleDeg = chainDirectionDeg,
                    MatchConcentration = matchConc,
                    U = u,
                    V = v,
                    PitchCol = pitchU,
                    PitchRow = pitchV
                };
            }

            var lattice = FitColumnFirstLattice(u, v, effRows, effCols, pitchU, pitchV, scores);
            if (consensusPick.Length > 0)
            {
                lattice = RefineLatticeFromConsensusPick(lattice, u, v, scores, consensusPick, tag);
                DiagLattice(tag, $"行/列中心已拟合到共识 {consensusPick.Length} 个高分模板点");
            }
            else
            {
                lattice = RefineLatticeFromPointClusters(lattice, u, v, scores);
                lattice = RefineRowCentersFromTopMatches(lattice, u, v, scores);
                lattice = EnforceUniformColumnsPreserveRows(lattice);
            }
            ComputeLatticeSnapTolerance(lattice, snapTolerancePx, out double snapU, out double snapV);
            DiagLattice(tag, $"聚类列/行中心线: pitchU={lattice.PitchU:F1}, pitchV={lattice.PitchV:F1}, snapU={snapU:F1}, snapV={snapV:F1}");
            LogLatticeLines(tag, lattice);

            BuildCellGridGeometry(lattice, angleRad, new Dictionary<(int, int), int>(), new HashSet<int>(), consensusMatchDeg,
                out double[] cellRow, out double[] cellCol, out bool[] cellFound, out double[] cellAngleDeg);

            return new LatticeFitResult
            {
                Lattice = lattice,
                InputCount = n,
                AngleRad = angleRad,
                EstimatedAngleDeg = angleRad * 180.0 / Math.PI,
                AxesSwapped = swapUv,
                UserGridRows = gridRows,
                UserGridCols = gridCols,
                EffRows = effRows,
                EffCols = effCols,
                ConsensusMatchAngleDeg = consensusMatchDeg,
                ChainDirectionAngleDeg = chainDirectionDeg,
                MatchConcentration = matchConc,
                U = u,
                V = v,
                PitchCol = lattice.MeanPitchU,
                PitchRow = lattice.PitchV,
                SnapU = snapU,
                SnapV = snapV,
                CellRow = cellRow,
                CellCol = cellCol,
                CellFound = cellFound,
                CellAngleDeg = cellAngleDeg,
                PointGridRow = lattice.PointRow,
                PointGridCol = lattice.PointCol
            };

        }

        public static LatticeFitResult? TryImportLatticeFit(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double[]? colCenterU,
            double[]? rowCenterV,
            double pitchCol,
            double pitchRow,
            double latticeAngleDeg,
            int axesSwappedInt,
            double snapU,
            double snapV,
            double consensusMatchDeg)
        {
            if (colCenterU == null || colCenterU.Length == 0 || rowCenterV == null || rowCenterV.Length == 0)
                return null;
            int n = Math.Min(rows.Length, cols.Length);
            if (n == 0) return null;
            bool swapUv = axesSwappedInt != 0;
            int effRows = swapUv ? gridCols : gridRows;
            int effCols = swapUv ? gridRows : gridCols;
            if (colCenterU.Length != effCols || rowCenterV.Length != effRows)
                return null;

            double angleRad = latticeAngleDeg * Math.PI / 180.0;
            ProjectToUv(cols, rows, angleRad, out var u, out var v);
            var pointRow = new int[n];
            var pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowCenterV, v[i]);
                pointCol[i] = NearestIndex1D(colCenterU, u[i]);
            }
            var lattice = new RowColumnLattice
            {
                Rows = effRows,
                Cols = effCols,
                PitchU = pitchCol > 0 ? pitchCol : EstimatePitchFromOrderedCenters(colCenterU, 1),
                PitchV = pitchRow > 0 ? pitchRow : EstimatePitchFromOrderedCenters(rowCenterV, 1),
                ColU = colCenterU,
                RowV = rowCenterV,
                PointRow = pointRow,
                PointCol = pointCol
            };
            double su = snapU > 0 ? snapU : 0;
            double sv = snapV > 0 ? snapV : 0;
            if (su < 1e-6 || sv < 1e-6)
                ComputeLatticeSnapTolerance(lattice, 0, out su, out sv);
            double chainDirectionDeg = double.NaN;
            if (double.IsNaN(consensusMatchDeg) && angles != null)
            {
                (consensusMatchDeg, _, int[] pick) = ComputeConsensusMatchAngle(angles, scores, cols, rows, n, gridRows, gridCols, null);
                chainDirectionDeg = EstimateChainDirectionAngleDeg(cols, rows, pick);
            }
            else if (angles != null)
            {
                int[] pick = SelectIndicesForAngleConsensus(cols, rows, scores, n, gridRows, gridCols, null);
                chainDirectionDeg = EstimateChainDirectionAngleDeg(cols, rows, pick);
            }
            double matchConc = angles != null && !double.IsNaN(consensusMatchDeg)
                ? MatchAngleConcentrationOnIndices(angles, SelectIndicesForAngleConsensus(cols, rows, scores, n, gridRows, gridCols, null))
                : 0;
            BuildCellGridGeometry(lattice, angleRad, new Dictionary<(int, int), int>(), new HashSet<int>(), consensusMatchDeg,
                out double[] cellRow, out double[] cellCol, out bool[] cellFound, out double[] cellAngleDeg);
            return new LatticeFitResult
            {
                Lattice = lattice,
                InputCount = n,
                AngleRad = angleRad,
                EstimatedAngleDeg = latticeAngleDeg,
                AxesSwapped = swapUv,
                UserGridRows = gridRows,
                UserGridCols = gridCols,
                EffRows = effRows,
                EffCols = effCols,
                ConsensusMatchAngleDeg = consensusMatchDeg,
                ChainDirectionAngleDeg = chainDirectionDeg,
                MatchConcentration = matchConc,
                U = u,
                V = v,
                PitchCol = lattice.MeanPitchU,
                PitchRow = lattice.PitchV,
                SnapU = su,
                SnapV = sv,
                CellRow = cellRow,
                CellCol = cellCol,
                CellFound = cellFound,
                CellAngleDeg = cellAngleDeg,
                PointGridRow = pointRow,
                PointGridCol = pointCol
            };
        }

        private static void DiagLattice(string tag, string message) =>
            HalconShapeMatchGridDiagnostics.Log($"[{tag}] {message}");

        /// <summary>投影到 u,v 后：全局列中心线 ColU + 全局行中心线 RowV（列不随行漂移）。</summary>
        public sealed class RowColumnLattice
        {
            public int Rows { get; init; }
            public int Cols { get; init; }
            public double PitchU { get; init; }
            public double PitchV { get; init; }
            public double MeanPitchU => PitchU;
            public double[] RowV { get; init; } = Array.Empty<double>();
            public double[] ColU { get; init; } = Array.Empty<double>();
            public int[] PointRow { get; init; } = Array.Empty<int>();
            public int[] PointCol { get; init; } = Array.Empty<int>();

            public double IdealU(int ir, int ic) => ColU[ic];
            public double IdealV(int ir) => RowV[ir];
        }



        private readonly struct OrientationChoice

        {

            public double AngleRad { get; init; }

            public bool SwapUv { get; init; }

        }



        private readonly struct AlignmentMetrics

        {

            public int UniqueCells { get; init; }

            public int SnappedCount { get; init; }

            public bool MatchesUserGridDims { get; init; }

            public int AspectBonus { get; init; }

        }
        /// <summary>
        /// 在已定向的 u,v 上：先对全部点 u 聚类得到各列中心线，再对 v 聚类得到各行中心线，最后等间距规整。
        /// </summary>
        private static RowColumnLattice FitColumnFirstLattice(double[] u, double[] v, int rows, int cols, double pitchUHint, double pitchVHint, double[]? scores = null)
        {
            int n = u.Length;
            double[] colU;
            double pitchU;
            if (cols == 2)
                FitTwoColumnCenterLine(u, scores, n, pitchUHint, out colU, out pitchU);
            else
                FitClusterCenterLine(u, cols, pitchUHint, out colU, out pitchU);
            FitClusterCenterLine(v, rows, pitchVHint, out double[] rowV, out double pitchV);

            var pointRow = new int[n];
            var pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowV, v[i]);
                pointCol[i] = NearestIndex1D(colU, u[i]);
            }

            return new RowColumnLattice
            {
                Rows = rows,
                Cols = cols,
                PitchU = pitchU,
                PitchV = pitchV,
                RowV = rowV,
                ColU = colU,
                PointRow = pointRow,
                PointCol = pointCol
            };
        }

        /// <summary>双列：按 u 最大间隙分两簇，列中心为 Score 加权中位（避免 KMeans 被列间误检拉偏）。</summary>
        private static void FitTwoColumnCenterLine(double[] u, double[]? scores, int n, double pitchHint, out double[] colU, out double pitch)
        {
            colU = new double[2];
            if (n == 0)
            {
                pitch = pitchHint > 1e-3 ? pitchHint : 1;
                return;
            }

            var sorted = Enumerable.Range(0, n).OrderBy(i => u[i]).ToArray();
            int splitAt = 1;
            double bestGap = 0;
            for (int j = 1; j < sorted.Length; j++)
            {
                double gap = u[sorted[j]] - u[sorted[j - 1]];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    splitAt = j;
                }
            }

            var groups = new[] { new List<int>(), new List<int>() };
            for (int j = 0; j < sorted.Length; j++)
                groups[j < splitAt ? 0 : 1].Add(sorted[j]);

            for (int ic = 0; ic < 2; ic++)
            {
                if (groups[ic].Count == 0)
                    continue;
                colU[ic] = WeightedMedian(groups[ic].Select(i =>
                {
                    double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                    return (u[i], w);
                }).ToList());
            }

            if (groups[0].Count == 0 || groups[1].Count == 0)
            {
                double uMed = WeightedMedian(sorted.Select(i =>
                {
                    double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                    return (u[i], w);
                }).ToList());
                pitch = pitchHint > 1e-3 ? pitchHint : 1;
                colU[0] = uMed - pitch * 0.5;
                colU[1] = uMed + pitch * 0.5;
            }

            if (colU[0] > colU[1])
                (colU[0], colU[1]) = (colU[1], colU[0]);
            pitch = EstimatePitchFromOrderedCenters(colU, pitchHint);
        }

        /// <summary>1D KMeans → 迭代中位修正；中心线跟数据走，不每轮强制拉成等间距（避免偏离模板中心）。</summary>
        private static void FitClusterCenterLine(double[] coords, int lineCount, double pitchHint, out double[] centers, out double pitch)
        {
            centers = new double[lineCount];
            if (lineCount <= 0)
            {
                pitch = pitchHint > 1e-3 ? pitchHint : 1;
                return;
            }

            if (coords.Length == 0)
            {
                pitch = pitchHint > 1e-3 ? pitchHint : 1;
                for (int i = 0; i < lineCount; i++)
                    centers[i] = i * pitch;
                return;
            }

            double[] seeds = KMeans1D(coords, lineCount);
            Array.Sort(seeds);
            for (int i = 0; i < lineCount; i++)
                centers[i] = seeds[Math.Min(i, seeds.Length - 1)];
            pitch = pitchHint > 1e-3 ? pitchHint : EstimatePitchFromOrderedCenters(centers, 1);

            for (int iter = 0; iter < 10; iter++)
            {
                var offsets = Enumerable.Range(0, lineCount).Select(_ => new List<double>()).ToArray();
                foreach (double x in coords)
                {
                    int idx = NearestIndex1D(centers, x);
                    offsets[idx].Add(x - centers[idx]);
                }

                for (int i = 0; i < lineCount; i++)
                {
                    if (offsets[i].Count > 0)
                        centers[i] += Median(offsets[i]);
                }
            }

            pitch = EstimatePitchFromOrderedCenters(centers, pitchHint);
        }

        private static double EstimatePitchFromOrderedCenters(double[] centers, double pitchHint)
        {
            if (centers.Length < 2)
                return pitchHint > 1e-3 ? pitchHint : 1;
            var sorted = centers.OrderBy(x => x).ToArray();
            var gaps = new List<double>();
            for (int i = 1; i < sorted.Length; i++)
            {
                double g = sorted[i] - sorted[i - 1];
                if (g > 1e-3)
                    gaps.Add(g);
            }
            if (gaps.Count == 0)
                return pitchHint > 1e-3 ? pitchHint : 1;
            return Median(gaps);
        }

        /// <summary>相邻间距直方图：取最集中一档间距的平均（抑制离群大/小间隙）。</summary>
        private static double DominantAdjacentGapAverage(IReadOnlyList<double> gaps)
        {
            if (gaps.Count == 0)
                return 0;
            if (gaps.Count == 1)
                return gaps[0];

            var sorted = gaps.Where(g => g > 1e-3).OrderBy(g => g).ToArray();
            if (sorted.Length == 0)
                return 0;
            if (sorted.Length == 1)
                return sorted[0];

            double med = MedianOfValues(sorted);
            double binW = Math.Max(6.0, med * 0.18);
            int bestCount = 0;
            double bestSum = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                double sum = 0;
                int cnt = 0;
                for (int j = i; j < sorted.Length && sorted[j] <= sorted[i] + binW + 1e-6; j++)
                {
                    sum += sorted[j];
                    cnt++;
                }
                if (cnt > bestCount)
                {
                    bestCount = cnt;
                    bestSum = sum;
                }
            }

            return bestCount > 0 ? bestSum / bestCount : med;
        }

        /// <summary>单列链上点：按链向排序后取相邻间距主簇平均 → 行距；再拟 RowV。</summary>
        private static (double PitchV, double[] RowV) EstimateColumnChainPitchFromIndices(
            int[] indices,
            double[] cols,
            double[] rows,
            double[] v,
            double chainDirectionDeg,
            int rowCount,
            string? tag,
            string columnLabel)
        {
            rowCount = Math.Max(1, rowCount);
            if (indices.Length < 2)
                return (0, Array.Empty<double>());

            int[] ordered = double.IsNaN(chainDirectionDeg)
                ? indices.OrderBy(i => rows[i]).ThenBy(i => i).ToArray()
                : indices.OrderBy(i => ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg)).ThenBy(i => i).ToArray();

            var gaps = new List<double>(ordered.Length - 1);
            for (int j = 1; j < ordered.Length; j++)
            {
                double a = double.IsNaN(chainDirectionDeg)
                    ? rows[ordered[j - 1]]
                    : ProjectAlongChainDeg(cols[ordered[j - 1]], rows[ordered[j - 1]], chainDirectionDeg);
                double b = double.IsNaN(chainDirectionDeg)
                    ? rows[ordered[j]]
                    : ProjectAlongChainDeg(cols[ordered[j]], rows[ordered[j]], chainDirectionDeg);
                gaps.Add(Math.Abs(b - a));
            }

            double pitch = DominantAdjacentGapAverage(gaps);
            if (pitch < 1e-3)
                pitch = EstimatePitchFromOrderedCenters(ordered.Select(i => v[i]).ToArray(), 1);

            var vSamples = ordered.Select(i => v[i]).ToArray();
            FitClusterCenterLine(vSamples, rowCount, pitch, out double[] rowV, out double pitchOut);

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string gapList = string.Join(", ", gaps.Select(g => g.ToString("F1", CultureInfo.InvariantCulture)));
                DiagLattice(tag, $"{columnLabel} 链向相邻间距=[{gapList}] → 主簇平均 pitchV≈{pitchOut:F1} (n={indices.Length})");
            }

            return (pitchOut, rowV);
        }

        /// <summary>条带格距：列0 用角度聚类 K 点；列1 同法聚类 K 点（均非全板）。分别算列内相邻间距主簇平均。</summary>
        private static RowColumnLattice BuildStripLatticeFromChainClusterPoints(
            int[] col0Indices,
            int[] col1Indices,
            double[] cols,
            double[] rows,
            double[] u,
            double[] v,
            double[]? scores,
            int effRows,
            int effCols,
            double chainDirectionDeg,
            string? tag)
        {
            double chainDeg = chainDirectionDeg;
            if (!double.IsNaN(chainDeg) && col0Indices.Length >= 2)
                chainDeg = CanonicalizeStripChainDirectionDeg(
                    EstimateChainDirectionAngleDeg(cols, rows, col0Indices), effRows, effCols);

            var (pitchV0, rowV0) = EstimateColumnChainPitchFromIndices(
                col0Indices, cols, rows, v, chainDeg, effRows, tag, "列0(角度K点)");
            var (pitchV1, rowV1) = EstimateColumnChainPitchFromIndices(
                col1Indices.Length >= 2 ? col1Indices : col0Indices,
                cols, rows, v, chainDeg, effRows, tag,
                col1Indices.Length >= 2 ? "列1(同法K点)" : "列1(回退列0)");

            double[] rowV = rowV0;
            if (rowV1.Length == rowV0.Length && rowV1.Length > 0)
            {
                rowV = new double[effRows];
                for (int ir = 0; ir < effRows; ir++)
                    rowV[ir] = 0.5 * (rowV0[ir] + rowV1[ir]);
                Array.Sort(rowV);
            }

            double pitchV = EstimatePitchFromOrderedCenters(rowV, 0.5 * (pitchV0 + pitchV1));

            static double MedU(int[] idx, double[] uArr, double[]? sc)
            {
                if (sc != null)
                    return WeightedMedian(idx.Select(i =>
                    {
                        double w = i < sc.Length ? Math.Max(0.01, sc[i]) : 1;
                        return (uArr[i], w);
                    }).ToList());
                return MedianOfValues(idx.Select(i => uArr[i]));
            }

            var colU = new double[2];
            colU[0] = col0Indices.Length > 0 ? MedU(col0Indices, u, scores) : 0;
            colU[1] = col1Indices.Length > 0 ? MedU(col1Indices, u, scores) : colU[0];
            if (colU[0] > colU[1])
                (colU[0], colU[1]) = (colU[1], colU[0]);
            double pitchU = Math.Max(1, colU[1] - colU[0]);

            int n = u.Length;
            var pointRow = new int[n];
            var pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowV, v[i]);
                pointCol[i] = NearestIndex1D(colU, u[i]);
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                DiagLattice(tag,
                    $"格距(聚类链点): pitchU={pitchU:F1} pitchV={pitchV:F1}, ColU=[{colU[0]:F1},{colU[1]:F1}], RowV=[{string.Join(",", rowV.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)))}]");

            return new RowColumnLattice
            {
                Rows = effRows,
                Cols = effCols,
                PitchU = pitchU,
                PitchV = pitchV,
                RowV = rowV,
                ColU = colU,
                PointRow = pointRow,
                PointCol = pointCol
            };
        }

        private static double WeightedMedian(List<(double value, double weight)> items)
        {
            if (items.Count == 0)
                return 0;
            items.Sort((a, b) => a.value.CompareTo(b.value));
            double total = items.Sum(x => x.weight);
            if (total < 1e-9)
                return items[items.Count / 2].value;
            double cum = 0;
            foreach (var it in items)
            {
                cum += it.weight;
                if (cum >= total * 0.5)
                    return it.value;
            }
            return items[^1].value;
        }

        /// <summary>用聚类分配后的候选点（Score 加权）预修正列中心线；行中心留给高分匹配专门拟合。</summary>
        private static RowColumnLattice RefineLatticeFromPointClusters(
            RowColumnLattice lat, double[] u, double[] v, double[]? scores)
        {
            int n = u.Length;
            var colPts = Enumerable.Range(0, lat.Cols).Select(_ => new List<(double val, double w)>()).ToArray();
            for (int i = 0; i < n; i++)
            {
                double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                int ic = ClampIndex(lat.PointCol[i], 0, lat.Cols - 1);
                colPts[ic].Add((u[i], w));
            }

            var colU = (double[])lat.ColU.Clone();
            for (int ic = 0; ic < lat.Cols; ic++)
                if (colPts[ic].Count > 0)
                    colU[ic] = WeightedMedian(colPts[ic]);

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = EstimatePitchFromOrderedCenters(colU, lat.PitchU),
                PitchV = lat.PitchV,
                RowV = lat.RowV,
                ColU = colU,
                PointRow = lat.PointRow,
                PointCol = lat.PointCol
            };
        }

        /// <summary>用已落格匹配点的 u/v（按 Score 加权）修正列/行中心线，使理论格点贴近模板中心。</summary>
        private static RowColumnLattice RefineLatticeFromCellMatches(
            RowColumnLattice lat,
            double[] u,
            double[] v,
            double[]? scores,
            Dictionary<(int ir, int ic), int> cellBest,
            HashSet<int>? onlyIndices)
        {
            var colU = (double[])lat.ColU.Clone();
            var rowV = (double[])lat.RowV.Clone();
            var colPts = Enumerable.Range(0, lat.Cols).Select(_ => new List<(double val, double w)>()).ToArray();
            var rowPts = Enumerable.Range(0, lat.Rows).Select(_ => new List<(double val, double w)>()).ToArray();

            foreach (var kv in cellBest)
            {
                int idx = kv.Value;
                if (onlyIndices != null && !onlyIndices.Contains(idx))
                    continue;
                double w = scores != null && idx < scores.Length ? Math.Max(0.01, scores[idx]) : 1;
                colPts[kv.Key.ic].Add((u[idx], w));
                rowPts[kv.Key.ir].Add((v[idx], w));
            }

            for (int ic = 0; ic < lat.Cols; ic++)
            {
                if (colPts[ic].Count > 0)
                    colU[ic] = WeightedMedian(colPts[ic]);
            }

            for (int ir = 0; ir < lat.Rows; ir++)
            {
                if (rowPts[ir].Count > 0)
                    rowV[ir] = FitRowCenterFromCellMatches(rowPts[ir]);
            }

            double pitchU = EstimatePitchFromOrderedCenters(colU, lat.PitchU);
            double pitchV = EstimatePitchFromOrderedCenters(rowV, lat.PitchV);

            int n = u.Length;
            var pointRow = new int[n];
            var pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowV, v[i]);
                pointCol[i] = NearestIndex1D(colU, u[i]);
            }

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = pitchU,
                PitchV = pitchV,
                RowV = rowV,
                ColU = colU,
                PointRow = pointRow,
                PointCol = pointCol
            };
        }

        /// <summary>列中心线收束为等间距；行中心线保留对模板匹配的贴合（不拉成等间距）。</summary>
        private static RowColumnLattice EnforceUniformColumnsPreserveRows(RowColumnLattice lat)
        {
            if (lat.Cols < 1 || lat.Rows < 1)
                return lat;

            var (u0, pitchU) = FitUniform1DLine(lat.ColU);
            var colU = new double[lat.Cols];
            for (int ic = 0; ic < lat.Cols; ic++)
                colU[ic] = u0 + ic * pitchU;

            double pitchV = EstimatePitchFromOrderedCenters(lat.RowV, lat.PitchV);

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = pitchU,
                PitchV = pitchV,
                RowV = (double[])lat.RowV.Clone(),
                ColU = colU,
                PointRow = lat.PointRow,
                PointCol = lat.PointCol
            };
        }

        /// <summary>双列：左右图像池各定 ColU[0/1]（不再 EnforceUniformColumns 覆盖列心），行中心保持链向拟合结果。</summary>
        private static RowColumnLattice AlignTwoColumnUFromImagePools(
            RowColumnLattice lat, double[] cols, double[] u, double[]? scores, int n)
        {
            if (lat.Cols < 2 || n <= 0)
                return lat;

            int[] pool = Enumerable.Range(0, n).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out int[] highPool, out _, out _))
                return lat;
            if (lowPool.Length < 2 || highPool.Length < 2)
                return lat;

            SplitPureImageColumnPools(cols, lowPool, highPool, out int[] pureLow, out int[] pureHigh, out _, out _);

            static double MedU(int[] idx, double[] uArr, double[]? sc)
            {
                if (sc != null)
                    return WeightedMedian(idx.Select(i =>
                    {
                        double w = i < sc.Length ? Math.Max(0.01, sc[i]) : 1;
                        return (uArr[i], w);
                    }).ToList());
                return MedianOfValues(idx.Select(i => uArr[i]));
            }

            var colU = new double[2];
            colU[0] = MedU(pureLow, u, scores);
            colU[1] = MedU(pureHigh, u, scores);
            if (colU[0] > colU[1])
                (colU[0], colU[1]) = (colU[1], colU[0]);

            double pitchU = Math.Max(1, colU[1] - colU[0]);

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = pitchU,
                PitchV = lat.PitchV,
                RowV = (double[])lat.RowV.Clone(),
                ColU = colU,
                PointRow = lat.PointRow,
                PointCol = lat.PointCol
            };
        }

        /// <summary>左右图像列池分别在 v 上聚 8 行，再合并行心，使投影点沿链向行距聚集（非全局等间距铺 RowV）。</summary>
        private static RowColumnLattice FitMergedRowVFromColumnPools(
            RowColumnLattice lat, double[] v, int[] lowPool, int[] highPool)
        {
            if (lat.Rows < 2 || lowPool.Length == 0 || highPool.Length == 0)
                return lat;

            var vLow = lowPool.Select(i => v[i]).ToArray();
            var vHigh = highPool.Select(i => v[i]).ToArray();
            FitClusterCenterLine(vLow, lat.Rows, lat.PitchV, out double[] rowVL, out double pitchVL);
            FitClusterCenterLine(vHigh, lat.Rows, lat.PitchV, out double[] rowVR, out double pitchVR);

            var rowV = new double[lat.Rows];
            for (int ir = 0; ir < lat.Rows; ir++)
                rowV[ir] = 0.5 * (rowVL[ir] + rowVR[ir]);
            Array.Sort(rowV);
            double pitchV = EstimatePitchFromOrderedCenters(rowV, 0.5 * (pitchVL + pitchVR));

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = lat.PitchU,
                PitchV = pitchV,
                RowV = rowV,
                ColU = lat.ColU,
                PointRow = lat.PointRow,
                PointCol = lat.PointCol
            };
        }

        /// <summary>snap 未覆盖的空格：在对应图像列内按分数最高、其次距 u/v 格心补点。</summary>
        private static int FillEmptyLatticeCellsScoreDistance(
            Dictionary<(int ir, int ic), int> cellBest,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            int effRows,
            int effCols,
            RowColumnLattice lattice,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            string? tag,
            double maxDistPx = double.PositiveInfinity)
        {
            var used = new HashSet<int>(cellBest.Values);
            int filled = 0;
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.ContainsKey((ir, ic)))
                        continue;

                    int best = -1;
                    double bestSc = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (used.Contains(i))
                            continue;
                        if (IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                            continue;
                        int pointCol = ResolveLatticeColumnIndex(i, cols, u, lattice, col0ImageCol, col1ImageCol, imageColMargin);
                        if (pointCol != ic)
                            continue;

                        double du = Math.Abs(u[i] - lattice.IdealU(ir, ic));
                        double dv = Math.Abs(v[i] - lattice.IdealV(ir));
                        double dist = Math.Sqrt(du * du + dv * dv);
                        double sc = scores != null && i < scores.Length ? scores[i] : 0;
                        if (dist < bestDist - 1e-9 || (Math.Abs(dist - bestDist) < 1e-9 && sc > bestSc + 1e-9))
                        {
                            bestSc = sc;
                            bestDist = dist;
                            best = i;
                        }
                    }

                    if (best < 0 || bestDist > maxDistPx)
                        continue;
                    cellBest[(ir, ic)] = best;
                    used.Add(best);
                    filled++;
                }
            }

            if (filled > 0 && !string.IsNullOrEmpty(tag))
                DiagLattice(tag, $"空格补全(分数+距格心≤{maxDistPx:F0}px): +{filled} 格");
            return filled;
        }

        /// <summary>空格补全：不限图像列，在剩余 Find 中按分数、距格心选点。</summary>
        private static int FillEmptyLatticeCellsGlobal(
            Dictionary<(int ir, int ic), int> cellBest,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            int effRows,
            int effCols,
            RowColumnLattice lattice,
            string? tag)
        {
            var used = new HashSet<int>(cellBest.Values);
            int filled = 0;
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.ContainsKey((ir, ic)))
                        continue;

                    int best = -1;
                    double bestSc = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (used.Contains(i))
                            continue;
                        double du = Math.Abs(u[i] - lattice.IdealU(ir, ic));
                        double dv = Math.Abs(v[i] - lattice.IdealV(ir));
                        double dist = Math.Sqrt(du * du + dv * dv);
                        double sc = scores != null && i < scores.Length ? scores[i] : 0;
                        if (sc > bestSc + 1e-9 || (Math.Abs(sc - bestSc) < 1e-9 && dist < bestDist))
                        {
                            bestSc = sc;
                            bestDist = dist;
                            best = i;
                        }
                    }

                    if (best < 0)
                        continue;
                    cellBest[(ir, ic)] = best;
                    used.Add(best);
                    filled++;
                }
            }

            if (filled > 0 && !string.IsNullOrEmpty(tag))
                DiagLattice(tag, $"空格补全(全板分数+距格心): +{filled} 格");
            return filled;
        }

        /// <summary>未入选的高分点：若距某格心更近且分数高于该格当前点，则替换占格（修 0.895 等同格/错列落选）。</summary>
        private static int BumpUnassignedHighScoresToNearestCell(
            Dictionary<(int ir, int ic), int> cellBest,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double minScore,
            string? tag,
            double[]? cols = null,
            double col0ImageCol = double.NaN,
            double col1ImageCol = double.NaN)
        {
            if (scores == null)
                return 0;
            var used = new HashSet<int>(cellBest.Values);
            int bumped = 0;
            bool skipGap = cols != null && !double.IsNaN(col0ImageCol) && !double.IsNaN(col1ImageCol);
            for (int i = 0; i < n; i++)
            {
                if (used.Contains(i))
                    continue;
                if (minScore > 0 && scores[i] < minScore)
                    continue;
                if (skipGap && IsImageColumnGapOutlier(i, cols!, col0ImageCol, col1ImageCol))
                    continue;

                FindNearestLatticeCell(u[i], v[i], lattice, out int ir, out int ic, out _);
                var key = (ir, ic);
                if (!cellBest.TryGetValue(key, out int cur))
                {
                    cellBest[key] = i;
                    used.Add(i);
                    bumped++;
                    if (!string.IsNullOrEmpty(tag) && scores[i] >= 0.89 && scores[i] <= 0.90)
                        DiagLattice(tag, $"高分占格[{ir},{ic}]: #{i} Score={scores[i]:F3}");
                    continue;
                }

                if (scores[i] <= scores[cur] + 1e-9)
                    continue;

                used.Remove(cur);
                cellBest[key] = i;
                used.Add(i);
                bumped++;
                if (!string.IsNullOrEmpty(tag) && (scores[i] >= 0.89 && scores[i] <= 0.90 || scores[cur] >= 0.89 && scores[cur] <= 0.90))
                    DiagLattice(tag, $"高分替换格[{ir},{ic}]: #{i} Score={scores[i]:F3} > #{cur} {scores[cur]:F3}");
            }

            if (bumped > 0 && !string.IsNullOrEmpty(tag))
                DiagLattice(tag, $"未占格补占/替换最近格: {bumped} 次");
            return bumped;
        }

        private readonly struct TwoColumnDeltaUvReport
        {
            public double DeltaU { get; init; }
            public double DeltaV { get; init; }
            public string Summary { get; init; }
        }

        /// <summary>诊断：对未入选点（尤其 0.84~0.86）打印落选原因，便于区分 snap/竞争/几何而非分数门槛。</summary>
        private static void LogUvGridPickRejectionReasons(
            string tag,
            double[] rows,
            double[] cols,
            double[]? scores,
            double[] u,
            double[] v,
            int n,
            RowColumnLattice lattice,
            double snapU,
            double snapV,
            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell,
            Dictionary<(int ir, int ic), int> cellBest,
            List<int> kept,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            bool hasTwoImgCols)
        {
            if (string.IsNullOrEmpty(tag) || !HalconShapeMatchGridDiagnostics.IsEnabled || scores == null || n == 0)
                return;

            var pickedSet = new HashSet<int>(kept);
            var candByIndex = perCell.Values
                .SelectMany(list => list)
                .ToDictionary(c => c.Index, c => c);

            for (int i = 0; i < n; i++)
            {
                if (scores[i] < 0.848 || scores[i] > 0.870)
                    continue;

                if (pickedSet.Contains(i) && cellBest.ContainsValue(i))
                {
                    var cell = cellBest.First(kv => kv.Value == i).Key;
                    double du = Math.Abs(u[i] - lattice.IdealU(cell.ir, cell.ic));
                    double dv = Math.Abs(v[i] - lattice.IdealV(cell.ir));
                    DiagLattice(tag,
                        $"Find#{i} Score={scores[i]:F3} → 入选 格[{cell.ir},{cell.ic}] Δu={du:F1} Δv={dv:F1}");
                    continue;
                }

                string reason;
                if (hasTwoImgCols && !double.IsNaN(col0ImageCol) &&
                    IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                {
                    reason = "列间隙(图像Col落在两列之间，不参与落格)";
                }
                else if (!candByIndex.ContainsKey(i))
                {
                    double rescueU = Math.Max(snapU, lattice.MeanPitchU * HighScoreRescuePitchFactor);
                    double rescueV = Math.Max(snapV, lattice.PitchV * 0.55);
                    if (hasTwoImgCols)
                    {
                        int ic = ResolveLatticeColumnIndex(i, cols, u, lattice, col0ImageCol, col1ImageCol, imageColMargin);
                        bool tight = TryFindBestSnapCellInColumn(u[i], v[i], ic, lattice, snapU, snapV, out _, out _);
                        bool loose = TryFindBestSnapCellInColumn(u[i], v[i], ic, lattice, rescueU, rescueV, out int ri, out double d);
                        reason = tight
                            ? "未进候选(异常)"
                            : loose
                                ? $"snap拒绝: 列{ic} 放宽snap仍无格 (rescueU={rescueU:F0} rescueV={rescueV:F0})"
                                : $"snap拒绝: 列{ic} |Δu|>{rescueU:F0} 或 |Δv|>{rescueV:F0}";
                    }
                    else
                    {
                        bool tight = TryFindBestSnapCell(u[i], v[i], lattice, snapU, snapV, out _, out _, out _);
                        bool loose = TryFindBestSnapCell(u[i], v[i], lattice, rescueU, rescueV, out _, out _, out double d);
                        reason = tight ? "未进候选(异常)" : loose ? $"snap拒绝(放宽后最近 dist={d:F1})" : "snap拒绝(无格在放宽容差内)";
                    }
                }
                else
                {
                    GridCellCandidate self = candByIndex[i];
                    var key = (self.Ir, self.Ic);
                    if (cellBest.TryGetValue(key, out int win) && win != i)
                    {
                        double winSc = win < scores.Length ? scores[win] : 0;
                        double winDist = candByIndex.TryGetValue(win, out var wc) ? wc.DistLattice : 0;
                        reason =
                            $"同格竞争: 格[{self.Ir},{self.Ic}] 选了 #{win} Score={winSc:F3} dist={winDist:F1}，本点 dist={self.DistLattice:F1}";
                    }
                    else
                        reason = $"候选格[{self.Ir},{self.Ic}] dist={self.DistLattice:F1}，但该格最终未输出(可能离群剔除或空格未补)";
                }

                DiagLattice(tag, $"Find#{i} Score={scores[i]:F3} → 落选: {reason}");
            }
        }

        /// <summary>诊断：全量 Find 在 u/v 轴上的投影、格心坐标与按 u、v 排序的分布。</summary>
        private static void LogFindPointsUvProjection(
            string tag,
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double latticeAngleDeg,
            Dictionary<(int ir, int ic), int>? cellBest)
        {
            if (string.IsNullOrEmpty(tag) || !HalconShapeMatchGridDiagnostics.IsEnabled || n == 0)
                return;

            DiagLattice(tag, "──────── u/v 轴投影（Find → u,v）────────");
            DiagLattice(tag, $"格网角 θ≈{latticeAngleDeg:F1}°  pitchU={lattice.PitchU:F1}  pitchV={lattice.PitchV:F1}");

            if (lattice.Cols >= 2)
            {
                double dU = lattice.ColU[1] - lattice.ColU[0];
                DiagLattice(tag, $"u 轴列心: ColU[0]={lattice.ColU[0]:F1}  ColU[1]={lattice.ColU[1]:F1}  (ΔU={dU:F1})");
            }
            else
                DiagLattice(tag, $"u 轴列心: ColU[0]={lattice.ColU[0]:F1}");

            DiagLattice(tag, "v 轴行心 RowV: " + string.Join("  ",
                Enumerable.Range(0, lattice.Rows).Select(ir => $"[{ir}]={lattice.RowV[ir]:F1}")));

            DiagLattice(tag, "--- 理论格心 (u, v) 8×" + lattice.Cols + " ---");
            for (int ic = 0; ic < lattice.Cols; ic++)
            {
                var parts = new List<string>(lattice.Rows);
                for (int ir = 0; ir < lattice.Rows; ir++)
                    parts.Add($"[{ir},{ic}]=({lattice.IdealU(ir, ic):F0},{lattice.IdealV(ir):F0})");
                DiagLattice(tag, $"  列{ic}: {string.Join("  ", parts)}");
            }

            int[] pool0 = Array.Empty<int>();
            int[] pool1 = Array.Empty<int>();
            double col0Img = 0;
            double col1Img = 0;
            bool hasPools = lattice.Cols >= 2 &&
                TryPartitionTwoImageColumns(cols, Enumerable.Range(0, n).ToArray(), out pool0, out pool1, out col0Img, out col1Img);

            DiagLattice(tag, "--- Find 全量：图像 (Row,Col) → u/v 投影 ---");
            DiagLattice(tag, "  #idx  Score    Row      Col      u        v     近格[ir,ic]  图列  入选");
            var order = Enumerable.Range(0, n)
                .OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0)
                .ThenBy(i => i);
            var picked = cellBest != null ? new HashSet<int>(cellBest.Values) : null;
            foreach (int i in order)
            {
                FindNearestLatticeCell(u[i], v[i], lattice, out int nir, out int nic, out _);
                string imgSide = "?";
                if (hasPools)
                    imgSide = pool1.Contains(i) ? "列1" : "列0";
                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                string pickMark = picked != null && picked.Contains(i) ? "是" : "";
                DiagLattice(tag,
                    $"  {i,3}  {sc:F3}  {rows[i],8:F1}  {cols[i],8:F1}  {u[i],8:F1}  {v[i],8:F1}  [{nir},{nic}]  {imgSide,3}  {pickMark}");
            }

            if (hasPools)
            {
                DiagLattice(tag, $"图像 Col median: 列0={col0Img:F0}  列1={col1Img:F0}");
                LogUvAxisDistribution(tag, "u 轴", pool0, pool1, u, i => u[i]);
                LogUvAxisDistribution(tag, "v 轴", pool0, pool1, v, i => v[i]);
                LogUvScatterByRow(tag, pool0, pool1, u, v, scores, lattice);
            }
        }

        private static void LogUvAxisDistribution(
            string tag, string axisName, int[] pool0, int[] pool1, double[] values, Func<int, double> getVal)
        {
            static string SummarizePool(string label, int[] pool, double[] all, Func<int, double> getVal)
            {
                if (pool.Length == 0)
                    return $"{label}: (空)";
                var xs = pool.Select(getVal).OrderBy(x => x).ToArray();
                string pts = string.Join(" ", pool.Select(i => $"#{i}={getVal(i):F0}"));
                return $"{label}: min={xs[0]:F1} med={MedianOfValues(xs):F1} max={xs[^1]:F1}  [{pts}]";
            }

            DiagLattice(tag, $"--- {axisName}投影（按图像列分池）---");
            DiagLattice(tag, "  " + SummarizePool("列0池", pool0, values, getVal));
            DiagLattice(tag, "  " + SummarizePool("列1池", pool1, values, getVal));
            double gap = MedianOfValues(pool1.Select(getVal)) - MedianOfValues(pool0.Select(getVal));
            DiagLattice(tag, $"  列1−列0 median({axisName}) = {gap:F1}");
        }

        private static void LogUvScatterByRow(
            string tag, int[] pool0, int[] pool1,
            double[] u, double[] v, double[]? scores, RowColumnLattice lattice)
        {
            DiagLattice(tag, "--- 各行的 u 投影（列0池 | 列1池 相对 RowV[ir]）---");
            for (int ir = 0; ir < lattice.Rows; ir++)
            {
                double rowV = lattice.IdealV(ir);
                string FmtPool(int[] pool, int ic)
                {
                    var items = pool
                        .Select(i => (i, du: Math.Abs(u[i] - lattice.IdealU(ir, ic)), dv: Math.Abs(v[i] - rowV)))
                        .Where(t => t.dv < lattice.PitchV * 0.65)
                        .OrderBy(t => t.du)
                        .Take(4)
                        .Select(t =>
                        {
                            double sc = scores != null && t.i < scores.Length ? scores[t.i] : 0;
                            return $"#{t.i}({sc:F2},u={u[t.i]:F0})";
                        });
                    return string.Join(" ", items);
                }

                DiagLattice(tag,
                    $"  行{ir} V*={rowV:F0}  U*0={lattice.IdealU(ir, 0):F0} U*1={lattice.IdealU(ir, 1):F0}  |  列0: {FmtPool(pool0, 0)}  |  列1: {FmtPool(pool1, 1)}");
            }
        }

        /// <summary>输出双列 ΔU/ΔV：格网列间距、v 向列偏移，及每列 Find 点相对格心的 |Δu|/|Δv| 与入选同行偏差。</summary>
        private static TwoColumnDeltaUvReport LogTwoColumnDeltaUvReport(
            string? tag,
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double latticeAngleDeg,
            Dictionary<(int ir, int ic), int>? cellBest)
        {
            double deltaU = lattice.Cols >= 2 ? lattice.ColU[1] - lattice.ColU[0] : double.NaN;
            double deltaV = double.NaN;
            string summary = lattice.Cols >= 2 ? $"ΔU={deltaU:F0}" : "";

            if (lattice.Cols < 2 || n == 0)
                return new TwoColumnDeltaUvReport { DeltaU = deltaU, DeltaV = deltaV, Summary = summary };

            if (!TryPartitionTwoImageColumns(cols, Enumerable.Range(0, n).ToArray(), out int[] pool0, out int[] pool1, out double col0Img, out double col1Img))
                return new TwoColumnDeltaUvReport { DeltaU = deltaU, DeltaV = deltaV, Summary = summary };

            double v0Med = MedianOfValues(pool0.Select(i => v[i]));
            double v1Med = MedianOfValues(pool1.Select(i => v[i]));
            deltaV = v1Med - v0Med;
            summary = $"ΔU={deltaU:F0} ΔV={deltaV:F0}";

            if (string.IsNullOrEmpty(tag) || !HalconShapeMatchGridDiagnostics.IsEnabled)
                return new TwoColumnDeltaUvReport { DeltaU = deltaU, DeltaV = deltaV, Summary = summary };

            LogFindPointsUvProjection(tag, rows, cols, u, v, scores, n, lattice, latticeAngleDeg, cellBest);

            DiagLattice(tag, "──────── 双列 ΔU / ΔV ────────");
            DiagLattice(tag, $"ΔU = ColU[1]-ColU[0] = {lattice.ColU[1]:F1} - {lattice.ColU[0]:F1} = {deltaU:F1} px  (pitchU={lattice.PitchU:F1})");
            DiagLattice(tag, $"ΔV = median(v|列1池) - median(v|列0池) = {v1Med:F1} - {v0Med:F1} = {deltaV:F1} px  (pitchV={lattice.PitchV:F1})");
            DiagLattice(tag, $"图像列 median: 列0={col0Img:F0}  列1={col1Img:F0}  gap={col1Img - col0Img:F0}");

            LogColumnPoolDeltaResiduals(tag, "列0", pool0, u, v, scores, lattice, ic: 0);
            LogColumnPoolDeltaResiduals(tag, "列1", pool1, u, v, scores, lattice, ic: 1);

            if (cellBest != null && cellBest.Count > 0)
            {
                DiagLattice(tag, "--- 入选点 同行 列1相对列0 (实测 Δu/Δv vs 理论 ΔU/ΔV) ---");
                for (int ir = 0; ir < lattice.Rows; ir++)
                {
                    if (!cellBest.TryGetValue((ir, 0), out int i0) || !cellBest.TryGetValue((ir, 1), out int i1))
                    {
                        DiagLattice(tag, $"  行{ir}: (缺列0或列1入选点)");
                        continue;
                    }

                    double duPair = u[i1] - u[i0];
                    double dvPair = v[i1] - v[i0];
                    double sc0 = scores != null && i0 < scores.Length ? scores[i0] : 0;
                    double sc1 = scores != null && i1 < scores.Length ? scores[i1] : 0;
                    DiagLattice(tag,
                        $"  行{ir}: #{i0}({sc0:F3})↔#{i1}({sc1:F3})  " +
                        $"Δu={duPair:F1} Δv={dvPair:F1}  |Δu-ΔU|={Math.Abs(duPair - deltaU):F1} |Δv-ΔV|={Math.Abs(dvPair - deltaV):F1}");
                }
            }

            DiagLattice(tag, $"双列摘要: {summary}");
            return new TwoColumnDeltaUvReport { DeltaU = deltaU, DeltaV = deltaV, Summary = summary };
        }

        private static void LogColumnPoolDeltaResiduals(
            string tag,
            string label,
            int[] pool,
            double[] u,
            double[] v,
            double[]? scores,
            RowColumnLattice lattice,
            int ic)
        {
            DiagLattice(tag, $"--- {label} Find (n={pool.Length}) u,v 与最近格心偏差 ---");
            foreach (int i in pool.OrderBy(ii => v[ii]))
            {
                int bestIr = 0;
                double bestDist = double.MaxValue;
                for (int ir = 0; ir < lattice.Rows; ir++)
                {
                    double du = Math.Abs(u[i] - lattice.IdealU(ir, ic));
                    double dv = Math.Abs(v[i] - lattice.IdealV(ir));
                    double d = Math.Sqrt(du * du + dv * dv);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIr = ir;
                    }
                }

                double duR = Math.Abs(u[i] - lattice.IdealU(bestIr, ic));
                double dvR = Math.Abs(v[i] - lattice.IdealV(bestIr));
                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                DiagLattice(tag,
                    $"  #{i,2} Score={sc:F3}  u={u[i],8:F1} v={v[i],8:F1}  →格[{bestIr},{ic}]  |Δu|={duR:F1} |Δv|={dvR:F1}");
            }
        }

        /// <summary>用模板角共识选出的 K 个最拟合点（如 2×8 的 8 连）分别拟合行中心与列中心，格网贴合模板。</summary>
        private static RowColumnLattice RefineLatticeFromConsensusPick(
            RowColumnLattice lat, double[] u, double[] v, double[]? scores, int[] pick, string? tag)
        {
            if (pick == null || pick.Length == 0)
                return lat;

            var rowV = (double[])lat.RowV.Clone();
            var colU = (double[])lat.ColU.Clone();
            FitRowCentersFromConsensusPick(rowV, lat.Rows, u, v, scores, pick);
            FitColCentersFromConsensusPick(colU, lat.Cols, u, scores, pick, lat.PitchU);

            int n = u.Length;
            var pointRow = new int[n];
            var pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowV, v[i]);
                pointCol[i] = NearestIndex1D(colU, u[i]);
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string rowList = string.Join(", ", rowV.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));
                string colList = string.Join(", ", colU.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));
                Diag(tag, $"共识拟合中心: RowV=[{rowList}], ColU=[{colList}]");
            }

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = EstimatePitchFromOrderedCenters(colU, lat.PitchU),
                PitchV = EstimatePitchFromOrderedCenters(rowV, lat.PitchV),
                RowV = rowV,
                ColU = colU,
                PointRow = pointRow,
                PointCol = pointCol
            };
        }

        /// <summary>8 连等：按 v 排序，每行中心落在对应模板点的 v 上。</summary>
        private static void FitRowCentersFromConsensusPick(
            double[] rowV, int rows, double[] u, double[] v, double[]? scores, int[] pick)
        {
            if (rows <= 0 || pick.Length == 0)
                return;

            if (pick.Length == rows)
            {
                var ordered = pick.OrderBy(i => v[i]).ThenBy(i => u[i]).ToArray();
                for (int k = 0; k < rows; k++)
                {
                    int idx = ordered[k];
                    double w = scores != null && idx < scores.Length ? scores[idx] : 1;
                    rowV[k] = FitRowCenterFromCellMatches(new List<(double, double)> { (v[idx], w) });
                }
                return;
            }

            var buckets = Enumerable.Range(0, rows).Select(_ => new List<(double val, double w)>()).ToArray();
            foreach (int idx in pick)
            {
                if (idx < 0 || idx >= v.Length)
                    continue;
                int ir = NearestIndex1D(rowV, v[idx]);
                double w = scores != null && idx < scores.Length ? scores[idx] : 1;
                buckets[ir].Add((v[idx], w));
            }
            for (int ir = 0; ir < rows; ir++)
            {
                if (buckets[ir].Count > 0)
                    rowV[ir] = FitRowCenterFromCellMatches(buckets[ir]);
            }
        }

        /// <summary>按 u 将共识点分到各列，列中心为 Score 加权中位；单簇时用 pitch 补第二列。</summary>
        private static void FitColCentersFromConsensusPick(
            double[] colU, int cols, double[] u, double[]? scores, int[] pick, double pitchUHint)
        {
            if (cols <= 0 || pick.Length == 0)
                return;

            var valid = pick.Where(i => i >= 0 && i < u.Length).ToArray();
            if (valid.Length == 0)
                return;

            if (cols == 1)
            {
                colU[0] = WeightedMedian(valid.Select(i =>
                {
                    double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                    return (u[i], w);
                }).ToList());
                return;
            }

            double uMin = valid.Min(i => u[i]);
            double uMax = valid.Max(i => u[i]);
            double span = uMax - uMin;
            double pitch = pitchUHint > 1e-3 ? pitchUHint : Math.Max(1, span);

            if (span < pitch * 0.28)
            {
                double uMed = WeightedMedian(valid.Select(i =>
                {
                    double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                    return (u[i], w);
                }).ToList());
                int icNear = NearestIndex1D(colU, uMed);
                colU[icNear] = uMed;
                int icOther = icNear == 0 ? 1 : cols - 1;
                colU[icOther] = colU[icNear] + (icOther > icNear ? pitch : -pitch);
                if (colU[0] > colU[1])
                    (colU[0], colU[1]) = (colU[1], colU[0]);
                return;
            }

            var sorted = valid.OrderBy(i => u[i]).ToArray();
            int splitAt = 1;
            double bestGap = 0;
            for (int j = 1; j < sorted.Length; j++)
            {
                double gap = u[sorted[j]] - u[sorted[j - 1]];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    splitAt = j;
                }
            }

            var groups = new List<int>[cols];
            for (int g = 0; g < cols; g++)
                groups[g] = new List<int>();

            if (cols == 2)
            {
                for (int j = 0; j < sorted.Length; j++)
                    groups[j < splitAt ? 0 : 1].Add(sorted[j]);
            }
            else
            {
                double[] seeds = KMeans1D(valid.Select(i => u[i]).ToArray(), cols);
                Array.Sort(seeds);
                foreach (int idx in valid)
                {
                    int g = NearestIndex1D(seeds, u[idx]);
                    groups[g].Add(idx);
                }
            }

            for (int ic = 0; ic < cols; ic++)
            {
                if (groups[ic].Count == 0)
                    continue;
                colU[ic] = WeightedMedian(groups[ic].Select(i =>
                {
                    double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                    return (u[i], w);
                }).ToList());
            }

            for (int ic = 0; ic < cols; ic++)
            {
                if (groups[ic].Count > 0)
                    continue;
                int neighbor = ic > 0 ? ic - 1 : ic + 1;
                if (neighbor >= 0 && neighbor < cols && groups[neighbor].Count > 0)
                    colU[ic] = colU[neighbor] + (ic > neighbor ? pitch : -pitch);
            }

            Array.Sort(colU);
        }

        /// <summary>行中心 = 该行模板匹配的 v（Score² 加权）；前序已卡分，此处不再按 max×0.88 剔点。</summary>
        private static RowColumnLattice RefineRowCentersFromTopMatches(
            RowColumnLattice lat, double[] u, double[] v, double[]? scores)
        {
            int n = u.Length;
            var rowV = (double[])lat.RowV.Clone();
            double[] colU = lat.ColU;

            for (int iter = 0; iter < 8; iter++)
            {
                var rowPts = Enumerable.Range(0, lat.Rows).Select(_ => new List<(double val, double w)>()).ToArray();
                for (int i = 0; i < n; i++)
                {
                    int ir = NearestIndex1D(rowV, v[i]);
                    double s = scores != null && i < scores.Length ? scores[i] : 1;
                    rowPts[ir].Add((v[i], s));
                }

                bool moved = false;
                for (int ir = 0; ir < lat.Rows; ir++)
                {
                    if (rowPts[ir].Count == 0)
                        continue;
                    double next = FitRowCenterFromCellMatches(rowPts[ir]);
                    if (Math.Abs(next - rowV[ir]) > 0.05)
                        moved = true;
                    rowV[ir] = next;
                }
                if (!moved)
                    break;
            }

            int[] pointRow = new int[n];
            int[] pointCol = new int[n];
            for (int i = 0; i < n; i++)
            {
                pointRow[i] = NearestIndex1D(rowV, v[i]);
                pointCol[i] = NearestIndex1D(colU, u[i]);
            }

            return new RowColumnLattice
            {
                Rows = lat.Rows,
                Cols = lat.Cols,
                PitchU = lat.PitchU,
                PitchV = EstimatePitchFromOrderedCenters(rowV, lat.PitchV),
                RowV = rowV,
                ColU = colU,
                PointRow = pointRow,
                PointCol = pointCol
            };
        }

        private static double ComputeHighScoreCutoff(double[]? scores, int n)
        {
            if (scores == null || n == 0)
                return 0;
            double max = 0;
            for (int i = 0; i < Math.Min(n, scores.Length); i++)
                max = Math.Max(max, scores[i]);
            return Math.Max(0.5, max * 0.88);
        }

        /// <summary>单行：优先取得分最高模板的 v；多匹配时用 Score² 加权平均。</summary>
        private static double FitRowCenterFromCellMatches(List<(double val, double w)> pts)
        {
            if (pts.Count == 0)
                return 0;
            if (pts.Count == 1)
                return pts[0].val;

            var ordered = pts.OrderByDescending(p => p.w).ToList();
            if (ordered.Count >= 2 && ordered[0].w - ordered[1].w >= 0.035)
                return ordered[0].val;

            double sumW = 0, sumV = 0;
            foreach (var (val, w) in pts)
            {
                double ww = w * w;
                sumW += ww;
                sumV += val * ww;
            }
            return sumW > 1e-9 ? sumV / sumW : ordered[0].val;
        }

        private static (double origin, double pitch) FitUniform1DLine(double[] centers)
        {
            int n = centers.Length;
            if (n == 0)
                return (0, 1);
            if (n == 1)
                return (centers[0], 1);

            double sumI = 0, sumII = 0, sumY = 0, sumIY = 0;
            for (int i = 0; i < n; i++)
            {
                sumI += i;
                sumII += i * i;
                sumY += centers[i];
                sumIY += i * centers[i];
            }

            double denom = n * sumII - sumI * sumI;
            double pitch = Math.Abs(denom) < 1e-9
                ? EstimatePitchFromOrderedCenters(centers, 1)
                : (n * sumIY - sumI * sumY) / denom;
            if (Math.Abs(pitch) < 1e-3)
                pitch = EstimatePitchFromOrderedCenters(centers, pitch);
            double origin = (sumY - pitch * sumI) / n;
            return (origin, pitch);
        }
        private static void ComputeLatticeSnapTolerance(RowColumnLattice lattice, double snapTolerancePx, out double snapU, out double snapV)

        {

            if (snapTolerancePx > 0)

            {

                snapU = snapV = snapTolerancePx;

                return;

            }



            snapU = Math.Max(12.0, 0.5 * lattice.MeanPitchU);

            snapV = Math.Max(12.0, 0.5 * lattice.PitchV);

        }



        private static void LogLatticeLines(string tag, RowColumnLattice lattice)

        {

            if (!HalconShapeMatchGridDiagnostics.IsEnabled)

                return;



            var colLine = lattice.ColU.Select(x => x.ToString("F1", CultureInfo.InvariantCulture));
            Diag(tag, $"列中心线 U (pitch={lattice.PitchU:F1}): [{string.Join(", ", colLine)}]");
            Diag(tag, $"行中心线 V (pitch={lattice.PitchV:F1}): [{string.Join(", ", lattice.RowV.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)))}]");

        }


        private static int ClampIndex(int value, int min, int max) => Math.Max(min, Math.Min(max, value));



        private static double Median(List<double> values)

        {

            if (values.Count == 0)

                return 0;

            var sorted = values.OrderBy(x => x).ToArray();

            return sorted[sorted.Length / 2];

        }



        private static void Diag(string tag, string message) =>

            HalconShapeMatchGridDiagnostics.Log($"[{tag}] {message}");
        /// <summary>格网 u 轴角 θ 下，v 轴在图像平面从 +Col 朝 +Row 的方位角（°）。</summary>
        private static double VAxisImageAngleColToRowDeg(double latticeUDeg)
        {
            double rad = latticeUDeg * Math.PI / 180.0;
            return NormalizeAngleDeg(Math.Atan2(Math.Cos(rad), -Math.Sin(rad)) * 180.0 / Math.PI);
        }

        private static double NormalizeAngleDeg(double deg)
        {
            double a = deg % 360.0;
            if (a > 180.0) a -= 360.0;
            if (a <= -180.0) a += 360.0;
            return a;
        }

        /// <summary>2×N 条带：u 沿纯左/纯右列中心连线（分列）；中间 Col 带不参与估角。</summary>
        private static bool TryEstimateStripColumnAxisDeg(
            double[] cols,
            double[] rows,
            int n,
            out double columnAxisDeg)
        {
            columnAxisDeg = double.NaN;
            if (n < 4)
                return false;
            int[] pool = Enumerable.Range(0, n).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out int[] highPool, out _, out _)
                || lowPool.Length < 2 || highPool.Length < 2)
                return false;

            SplitPureImageColumnPools(cols, lowPool, highPool, out int[] pureLow, out int[] pureHigh, out double c0Ref, out double c1Ref);

            double c0 = 0, r0 = 0;
            foreach (int i in pureLow)
            {
                c0 += cols[i];
                r0 += rows[i];
            }
            c0 /= pureLow.Length;
            r0 /= pureLow.Length;

            double c1 = 0, r1 = 0;
            foreach (int i in pureHigh)
            {
                c1 += cols[i];
                r1 += rows[i];
            }
            c1 /= pureHigh.Length;
            r1 /= pureHigh.Length;

            double dc = c1 - c0;
            double dr = r1 - r0;
            if (Math.Abs(dc) + Math.Abs(dr) < 1e-3)
                return false;

            columnAxisDeg = NormalizeAngleDeg(Math.Atan2(dr, dc) * 180.0 / Math.PI);
            return true;
        }

        private readonly struct StripUvOrientationScore
        {
            public double ThetaDeg { get; init; }
            public double ColumnSepU { get; init; }
            public double WithinColU { get; init; }
            public double ColumnLeakV { get; init; }
            public double RowSpreadV { get; init; }
            public int CrossUOverlap { get; init; }
            /// <summary>θ 下 8×2 近格 RMS（越小投影越聚团）。</summary>
            public double GridClusterRms { get; init; }
            public int Merit { get; init; }
        }

        /// <summary>剔除图像中间带，仅用左右「纯列」估计分列方向，避免 Col≈1124 拉偏 u 轴。</summary>
        private static void SplitPureImageColumnPools(
            double[] cols,
            int[] lowPool,
            int[] highPool,
            out int[] pureLow,
            out int[] pureHigh,
            out double col0Ref,
            out double col1Ref)
        {
            pureLow = lowPool;
            pureHigh = highPool;
            col0Ref = double.NaN;
            col1Ref = double.NaN;
            if (lowPool.Length == 0 || highPool.Length == 0)
                return;

            double c0 = MedianOfValues(lowPool.Select(i => cols[i]));
            double c1 = MedianOfValues(highPool.Select(i => cols[i]));
            col0Ref = c0;
            col1Ref = c1;
            double gap = Math.Max(80, c1 - c0);
            double margin = gap * 0.28;
            double leftMax = c0 + margin;
            double rightMin = c1 - margin;
            var pl = lowPool.Where(i => cols[i] <= leftMax).ToArray();
            var ph = highPool.Where(i => cols[i] >= rightMin).ToArray();
            if (pl.Length >= 2)
                pureLow = pl;
            if (ph.Length >= 2)
                pureHigh = ph;
        }

        private static StripUvOrientationScore ScoreStripUvTheta(
            double[] cols,
            double[] rows,
            int n,
            int[] lowPool,
            int[] highPool,
            double thetaDeg,
            int[]? scoreLowPool = null,
            int[]? scoreHighPool = null)
        {
            int[] uLowIdx = scoreLowPool ?? lowPool;
            int[] uHighIdx = scoreHighPool ?? highPool;
            double rad = thetaDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            var uLow = new List<double>(uLowIdx.Length);
            var uHigh = new List<double>(uHighIdx.Length);
            var vLow = new List<double>(lowPool.Length);
            var vHigh = new List<double>(highPool.Length);
            foreach (int i in uLowIdx)
            {
                uLow.Add(cols[i] * cos + rows[i] * sin);
            }
            foreach (int i in uHighIdx)
            {
                uHigh.Add(cols[i] * cos + rows[i] * sin);
            }
            foreach (int i in lowPool)
            {
                vLow.Add(-cols[i] * sin + rows[i] * cos);
            }
            foreach (int i in highPool)
            {
                vHigh.Add(-cols[i] * sin + rows[i] * cos);
            }

            double medLowU = Median(uLow);
            double medHighU = Median(uHigh);
            double columnSepU = Math.Abs(medHighU - medLowU);
            double withinColU = 0.5 * (StdDev(uLow) + StdDev(uHigh));
            double medLowV = Median(vLow);
            double medHighV = Median(vHigh);
            double columnLeakV = Math.Abs(medHighV - medLowV);
            double rowSpreadV = 0.5 * (StdDev(vLow) + StdDev(vHigh));

            double uTol = Math.Max(90, columnSepU * 0.16);
            int crossUOverlap = 0;
            foreach (double ul in uLow)
            {
                foreach (double uh in uHigh)
                {
                    if (Math.Abs(ul - uh) < uTol)
                        crossUOverlap++;
                }
            }

            int merit = 0;
            if (columnSepU > 180)
                merit += (int)Math.Min(80_000, columnSepU * 80);
            if (withinColU > 1e-3)
                merit += (int)Math.Min(50_000, (columnSepU / withinColU) * 5_000);
            if (rowSpreadV > 80)
                merit += (int)Math.Min(30_000, rowSpreadV * 120);
            if (columnLeakV < 60)
                merit += 18_000;
            else if (columnLeakV < 90)
                merit += 8_000;
            else
                merit -= (int)(columnLeakV * 50);
            merit -= crossUOverlap * 2_500;

            double gridRms = ScoreStripUvGridClusterRms(cols, rows, n, thetaDeg, 8, 2);

            return new StripUvOrientationScore
            {
                ThetaDeg = thetaDeg,
                ColumnSepU = columnSepU,
                WithinColU = withinColU,
                ColumnLeakV = columnLeakV,
                RowSpreadV = rowSpreadV,
                CrossUOverlap = crossUOverlap,
                GridClusterRms = gridRms,
                Merit = merit
            };
        }

        /// <summary>在候选 θ 下快速拟 8×2 格，返回各点到最近格心的 RMS。</summary>
        private static double ScoreStripUvGridClusterRms(
            double[] cols, double[] rows, int n, double thetaDeg, int gridRows, int gridCols)
        {
            if (n < gridRows || gridCols < 1)
                return double.MaxValue;
            double rad = thetaDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            var u = new double[n];
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                u[i] = cols[i] * cos + rows[i] * sin;
                v[i] = -cols[i] * sin + rows[i] * cos;
            }
            var lattice = FitColumnFirstLattice(u, v, gridRows, gridCols,
                EstimatePitchFromProjections(u, gridCols),
                EstimatePitchFromProjections(v, gridRows));
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                FindNearestLatticeCell(u[i], v[i], lattice, out int ir, out int ic, out double dist);
                sum += dist * dist;
            }
            return Math.Sqrt(sum / n);
        }

        private static bool PreferStripUvScore(StripUvOrientationScore cand, StripUvOrientationScore best, bool hasBest)
        {
            if (!hasBest)
                return true;
            if (cand.Merit != best.Merit)
                return cand.Merit > best.Merit;
            if (cand.CrossUOverlap != best.CrossUOverlap)
                return cand.CrossUOverlap < best.CrossUOverlap;
            if (Math.Abs(cand.GridClusterRms - best.GridClusterRms) > 3.0)
                return cand.GridClusterRms < best.GridClusterRms;
            double ratioC = cand.ColumnSepU / Math.Max(1, cand.WithinColU);
            double ratioB = best.ColumnSepU / Math.Max(1, best.WithinColU);
            return ratioC > ratioB + 0.05;
        }

        /// <summary>在索引子集内按 Col 分两池（用于顶分 K 点估角/评分）。</summary>
        private static void PartitionColumnPoolsWithinIndices(
            double[] cols,
            int[] indices,
            out int[] lowPool,
            out int[] highPool)
        {
            lowPool = Array.Empty<int>();
            highPool = Array.Empty<int>();
            if (indices.Length < 2)
                return;

            if (!TryPartitionTwoImageColumns(cols, indices, out lowPool, out highPool, out _, out _))
            {
                var sorted = indices.OrderBy(i => cols[i]).ToArray();
                int mid = sorted.Length / 2;
                lowPool = sorted.Take(mid).ToArray();
                highPool = sorted.Skip(mid).ToArray();
            }
        }

        private static StripUvOrientationScore ScoreStripUvThetaOnIndices(
            double[] cols,
            double[] rows,
            int[] indices,
            double thetaDeg)
        {
            if (indices.Length < 2)
                return default;

            PartitionColumnPoolsWithinIndices(cols, indices, out int[] lowPool, out int[] highPool);
            SplitPureImageColumnPools(cols, lowPool, highPool, out int[] pureLow, out int[] pureHigh, out _, out _);
            return ScoreStripUvTheta(cols, rows, cols.Length, lowPool, highPool, thetaDeg, pureLow, pureHigh);
        }

        private static StripUvOrientationScore FineTuneStripUvTheta(
            double[] cols,
            double[] rows,
            int[] indices,
            double centerDeg,
            double halfRangeDeg = 8.0,
            double stepDeg = 0.2)
        {
            StripUvOrientationScore best = default;
            bool any = false;
            for (double t = centerDeg - halfRangeDeg; t <= centerDeg + halfRangeDeg + 1e-6; t += stepDeg)
            {
                var sc = ScoreStripUvThetaOnIndices(cols, rows, indices, NormalizeAngleDeg(t));
                if (PreferStripUvScore(sc, best, any))
                {
                    best = sc;
                    any = true;
                }
            }
            return best;
        }

        /// <summary>在链向初值附近细调 θ_u；用全板双列池评分（单列顶分 K 无法分列）。</summary>
        private static StripUvOrientationScore FineTuneStripUvThetaOnFullBoard(
            double[] cols,
            double[] rows,
            int n,
            double centerDeg,
            double halfRangeDeg = 8.0,
            double stepDeg = 0.2)
        {
            int[] pool = Enumerable.Range(0, n).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out int[] highPool, out _, out _))
                return FineTuneStripUvTheta(cols, rows, pool, centerDeg, halfRangeDeg, stepDeg);

            SplitPureImageColumnPools(cols, lowPool, highPool, out int[] pureLow, out int[] pureHigh, out _, out _);

            StripUvOrientationScore best = default;
            bool any = false;
            for (double t = centerDeg - halfRangeDeg; t <= centerDeg + halfRangeDeg + 1e-6; t += stepDeg)
            {
                var sc = ScoreStripUvTheta(cols, rows, n, lowPool, highPool, NormalizeAngleDeg(t), pureLow, pureHigh);
                if (PreferStripUvScore(sc, best, any))
                {
                    best = sc;
                    any = true;
                }
            }
            return best;
        }

        /// <summary>单列内顶分 K（如 8 连）估链向，避免全板 Top-K 混入另一列。</summary>
        private static int[] SelectTopChainIndicesInStripColumn(
            double[] cols,
            double[]? scores,
            int inputCount,
            int gridRows,
            int gridCols,
            double[]? rows = null,
            string? tag = null)
        {
            int k = ConsensusChainWindowSize(gridRows, gridCols);
            if (gridCols == 2 && rows != null && rows.Length >= inputCount)
                return SelectBootstrapIndicesForTwoColumnStrip(cols, rows, scores, inputCount, k, tag);

            int[] pool = Enumerable.Range(0, inputCount).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out int[] highPool, out _, out _))
                return SelectTopIndicesByScore(scores, inputCount, k);

            static int[] TopKInPool(int[] columnPool, double[]? sc, int takeK)
            {
                if (columnPool.Length == 0)
                    return Array.Empty<int>();
                takeK = Math.Min(takeK, columnPool.Length);
                if (sc == null || sc.Length == 0)
                    return columnPool.Take(takeK).ToArray();
                return columnPool
                    .OrderByDescending(i => i < sc.Length ? sc[i] : 0)
                    .ThenBy(i => i)
                    .Take(takeK)
                    .ToArray();
            }

            int[] topLow = TopKInPool(lowPool, scores, k);
            int[] topHigh = TopKInPool(highPool, scores, k);
            if (topLow.Length < 2)
                return topHigh;
            if (topHigh.Length < 2)
                return topLow;

            double MeanScore(int[] idx)
            {
                double s = 0;
                foreach (int i in idx)
                    s += scores != null && i < scores.Length ? scores[i] : 0;
                return s / idx.Length;
            }

            return MeanScore(topLow) >= MeanScore(topHigh) ? topLow : topHigh;
        }

        /// <summary>单列顶分 K 点 PCA 链向 → u 轴 θ = 链向 − 90°。</summary>
        private static bool TryEstimateStripThetaFromTopScoreChain(
            double[] cols,
            double[] rows,
            double[]? scores,
            int inputCount,
            int gridRows,
            int gridCols,
            out double thetaUDeg,
            out double chainDeg,
            out int[] topIndices)
        {
            thetaUDeg = double.NaN;
            chainDeg = double.NaN;
            topIndices = SelectTopChainIndicesInStripColumn(cols, scores, inputCount, gridRows, gridCols, rows);
            if (topIndices.Length < 2)
                return false;

            chainDeg = CanonicalizeStripChainDirectionDeg(
                NormalizeAngleDeg(EstimateChainDirectionAngleDeg(cols, rows, topIndices)),
                gridRows, gridCols);
            thetaUDeg = NormalizeAngleDeg(chainDeg - 90.0);
            return true;
        }

        /// <summary>8×2 条带：链沿 Row，链向约定为图像 +90° 附近（避免 PCA 符号导致 θ_u≈±180°）。</summary>
        private static double CanonicalizeStripChainDirectionDeg(double chainDeg, int gridRows, int gridCols)
        {
            if (gridCols != 2 || gridRows <= gridCols || double.IsNaN(chainDeg))
                return chainDeg;
            double c = NormalizeAngleDeg(chainDeg);
            if (Math.Abs(NormalizeAngleDeg(c + 90.0)) < Math.Abs(NormalizeAngleDeg(c - 90.0)))
                c = NormalizeAngleDeg(c + 180.0);
            return c;
        }

        private static double StdDev(List<double> values)
        {
            if (values.Count < 2)
                return 0;
            double mean = values.Average();
            double sum = 0;
            foreach (double x in values)
            {
                double d = x - mean;
                sum += d * d;
            }
            return Math.Sqrt(sum / values.Count);
        }

        /// <summary>2×N 条带 u/v 定向：聚类取 K 点估链向，θ_u=链向−90°，仅在 K 点上细调 θ。</summary>
        private static OrientationChoice ResolveStripUvOrientation(
            double[] cols,
            double[] rows,
            int n,
            int gridRows,
            int gridCols,
            double[]? scores,
            double pcaBootstrapDeg,
            double consensusMatchDeg,
            string? tag)
        {
            if (!TryEstimateStripThetaFromTopScoreChain(cols, rows, scores, n, gridRows, gridCols,
                    out double thetaFromChain, out double chainDeg, out int[] topK))
            {
                if (!TryEstimateStripColumnAxisDeg(cols, rows, n, out double columnAxisDeg))
                    return new OrientationChoice
                    {
                        AngleRad = !double.IsNaN(pcaBootstrapDeg)
                            ? pcaBootstrapDeg * Math.PI / 180.0
                            : EstimatePrincipalAngleRad(cols, rows),
                        SwapUv = false
                    };
                thetaFromChain = columnAxisDeg;
                chainDeg = double.NaN;
                topK = Array.Empty<int>();
            }

            // 方向仅由聚类 K 点链向决定；不在全板或单列 K 点上再做 u/v 分列评分细调（会偏 θ）。
            double thetaU = thetaFromChain;
            if (topK.Length < 2)
            {
                var tuned = FineTuneStripUvThetaOnFullBoard(cols, rows, n, thetaFromChain, 8.0, 0.2);
                thetaU = tuned.ThetaDeg;
            }

            double vImg = VAxisImageAngleColToRowDeg(thetaU);
            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string topList = topK.Length > 0
                    ? string.Join(",", topK)
                    : "—";
                DiagLattice(tag,
                    $"条带 u/v 轴: u⊥分列 θ={thetaU:F1}°, v(图像 Col→Row)≈{vImg:F1}°, " +
                    $"(聚类K={topK.Length} 链向≈{chainDeg:F1}° θ_u=链向−90° 索引=[{topList}] PCA全板≈{pcaBootstrapDeg:F1}°)");
            }

            return new OrientationChoice { AngleRad = thetaU * Math.PI / 180.0, SwapUv = false };
        }

        private static bool ShouldUseStripUvGeometricOrientation(int gridRows, int gridCols) =>
            gridCols == 2 && gridRows > gridCols;

        private static void ProjectToUv(double[] cols, double[] rows, double angleRad, out double[] u, out double[] v)

        {

            int n = cols.Length;

            double cos = Math.Cos(angleRad);

            double sin = Math.Sin(angleRad);

            u = new double[n];

            v = new double[n];

            for (int i = 0; i < n; i++)

            {

                u[i] = cols[i] * cos + rows[i] * sin;

                v[i] = -cols[i] * sin + rows[i] * cos;

            }

        }

        /// <summary>u,v 格点坐标 → 图像 row/col（与 ProjectToUv 互逆）。</summary>
        private static void UvToImage(double u, double v, double angleRad, out double col, out double row)
        {
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            col = u * cos - v * sin;
            row = u * sin + v * cos;
        }

        /// <summary>格心一律由统一阵列 ColU/RowV 推算；仅 CellFound 区分是否命中，不用各匹配点坐标。</summary>
        private static void BuildCellGridGeometry(
            RowColumnLattice lattice,
            double angleRad,
            Dictionary<(int ir, int ic), int> cellBest,
            HashSet<int> keptIndices,
            double consensusMatchDeg,
            out double[] cellRow,
            out double[] cellCol,
            out bool[] cellFound,
            out double[] cellAngleDeg)
        {
            int rows = lattice.Rows;
            int cols = lattice.Cols;
            int n = rows * cols;
            cellRow = new double[n];
            cellCol = new double[n];
            cellFound = new bool[n];
            cellAngleDeg = new double[n];
            double boxAngle = double.IsNaN(consensusMatchDeg) ? angleRad * 180.0 / Math.PI : consensusMatchDeg;
            for (int ir = 0; ir < rows; ir++)
            {
                for (int ic = 0; ic < cols; ic++)
                {
                    int idx = ir * cols + ic;
                    bool found = cellBest.TryGetValue((ir, ic), out int matchIdx) && keptIndices.Contains(matchIdx);
                    cellFound[idx] = found;

                    double u = lattice.IdealU(ir, ic);
                    double v = lattice.IdealV(ir);
                    UvToImage(u, v, angleRad, out double col, out double row);
                    cellRow[idx] = row;
                    cellCol[idx] = col;
                    cellAngleDeg[idx] = boxAngle;
                }
            }
        }



        /// <summary>格网整片旋转 = 链向角；swap 使长边落在 v（链向），避免误检把 8 连当成 8 列。</summary>
        private static OrientationChoice ResolveOrientationAtChainAngle(
            double[] cols, double[] rows, double[]? scores, int gridRows, int gridCols, double chainDirectionDeg, string? tag)
        {
            double rad = chainDirectionDeg * Math.PI / 180.0;
            int longDim = Math.Max(gridRows, gridCols);
            bool swapUv = longDim == gridCols;
            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"链向格网: θ={chainDirectionDeg:F1}° swap={swapUv} (长边={longDim} 沿 v)");
            return new OrientationChoice { AngleRad = rad, SwapUv = swapUv };
        }

        /// <summary>匹配角集中时：阵列 u/v 仅由模板倾斜（加权圆均值）决定，不用 PCA 点阵主轴。</summary>
        private static OrientationChoice ResolveOrientationFromTemplateTilt(
            double[] cols, double[] rows, double[] angles, double[]? scores, int gridRows, int gridCols, double consensusMatchDeg, string tag)
        {
            double matchMeanDeg = consensusMatchDeg;
            double matchRad = matchMeanDeg * Math.PI / 180.0;
            var trials = new List<OrientationChoice>
            {
                new() { AngleRad = matchRad, SwapUv = false },
                new() { AngleRad = matchRad, SwapUv = true },
                new() { AngleRad = matchRad + Math.PI / 2.0, SwapUv = false },
                new() { AngleRad = matchRad + Math.PI / 2.0, SwapUv = true }
            };

            var best = PickBestOrientation(cols, rows, trials, gridRows, gridCols, matchMeanDeg, tag);
            Diag(tag, $"阵列方向取自模板匹配角聚类(浓度≥0.3): 共识≈{matchMeanDeg:F1}°, 选用 θ={best.AngleRad * 180.0 / Math.PI:F1}° swap={best.SwapUv}");
            return best;
        }

        private static OrientationChoice ResolveOrientationAuto(

            double[] cols, double[] rows, double[]? angles, double[]? scores, int gridRows, int gridCols, double consensusMatchDeg, string tag)

        {

            var trials = new List<OrientationChoice>();

            double pca = EstimatePrincipalAngleRad(cols, rows);

            double? matchMeanDeg = double.IsNaN(consensusMatchDeg) ? null : consensusMatchDeg;



            void Add(double angleRad)

            {

                trials.Add(new OrientationChoice { AngleRad = angleRad, SwapUv = false });

                trials.Add(new OrientationChoice { AngleRad = angleRad, SwapUv = true });

                trials.Add(new OrientationChoice { AngleRad = angleRad + Math.PI / 2.0, SwapUv = false });

                trials.Add(new OrientationChoice { AngleRad = angleRad + Math.PI / 2.0, SwapUv = true });

            }



            if (matchMeanDeg.HasValue)

            {

                double matchRad = matchMeanDeg.Value * Math.PI / 180.0;

                for (int k = 0; k < 4; k++)

                    Add(matchRad + k * (Math.PI / 2.0));

            }

            Add(pca);



            return PickBestOrientation(cols, rows, trials, gridRows, gridCols, matchMeanDeg, tag);

        }



        private static OrientationChoice ResolveOrientationWithFixedAngle(

            double[] cols, double[] rows, double[]? angles, double[]? scores, int gridRows, int gridCols, double gridAngleDeg, double consensusMatchDeg, string tag)

        {

            double angleRad = gridAngleDeg * Math.PI / 180.0;

            var trials = new List<OrientationChoice>

            {

                new() { AngleRad = angleRad, SwapUv = false },

                new() { AngleRad = angleRad, SwapUv = true }

            };

            double? matchMeanDeg = double.IsNaN(consensusMatchDeg) ? null : consensusMatchDeg;

            return PickBestOrientation(cols, rows, trials, gridRows, gridCols, matchMeanDeg, tag);

        }



        private static OrientationChoice PickBestOrientation(

            double[] cols, double[] rows, List<OrientationChoice> trials, int gridRows, int gridCols, double? consensusMatchDeg, string tag)

        {

            int bestScore = -1;

            var best = trials[0];
            double bestParallelDelta = double.MaxValue;

            foreach (var t in trials)

            {

                int effRows = t.SwapUv ? gridCols : gridRows;

                int effCols = t.SwapUv ? gridRows : gridCols;

                var m = EvaluateLatticeAlignment(cols, rows, t.AngleRad, effRows, effCols, gridRows, gridCols);

                int score = ScoreOrientation(m, gridRows, gridCols, t.SwapUv, t.AngleRad, consensusMatchDeg);
                double parallelDelta = consensusMatchDeg.HasValue
                    ? AngleDistanceDeg(t.AngleRad * 180.0 / Math.PI, consensusMatchDeg.Value)
                    : double.MaxValue;

                if (HalconShapeMatchGridDiagnostics.IsEnabled)

                {

                    string matchNote = consensusMatchDeg.HasValue
                        ? $" ∥模板={parallelDelta:F1}° ⊥={AngleDistanceDeg(t.AngleRad * 180.0 / Math.PI, consensusMatchDeg.Value + 90.0):F1}°"
                        : "";

                    Diag(tag, $"  试 θ={t.AngleRad * 180 / Math.PI:F1}° swap={t.SwapUv} cells={m.UniqueCells} snap={m.SnappedCount} total={score}{matchNote}");

                }

                if (score > bestScore
                    || (score == bestScore && parallelDelta < bestParallelDelta - 0.5))

                {

                    bestScore = score;

                    best = t;
                    bestParallelDelta = parallelDelta;

                }

            }



            return best;

        }

        /// <summary>条带定向：PCA 主轴 ±90° 试凑；2×N 条带禁止 swap（8 行须沿 v）。按落格数与 pitchV 选优。</summary>
        private static OrientationChoice ResolveOrientationFromPcaAxis(
            double[] cols,
            double[] rows,
            int gridRows,
            int gridCols,
            double pcaAxisDeg,
            double consensusMatchDeg,
            string? tag)
        {
            double pcaRad = pcaAxisDeg * Math.PI / 180.0;
            var trials = new List<OrientationChoice>
            {
                new() { AngleRad = pcaRad, SwapUv = false },
                new() { AngleRad = pcaRad, SwapUv = true },
                new() { AngleRad = pcaRad + Math.PI / 2.0, SwapUv = false },
                new() { AngleRad = pcaRad + Math.PI / 2.0, SwapUv = true }
            };

            if (gridCols == 2 && gridRows > gridCols)
                trials = trials.Where(t => !t.SwapUv).ToList();

            int target = gridRows * gridCols;
            int bestScore = -1;
            double bestPitchV = 0;
            var best = trials[0];
            foreach (var t in trials)
            {
                int effRows = t.SwapUv ? gridCols : gridRows;
                int effCols = t.SwapUv ? gridRows : gridCols;
                var m = EvaluateLatticeAlignment(cols, rows, t.AngleRad, effRows, effCols, gridRows, gridCols);
                int score = ScoreOrientation(m, gridRows, gridCols, t.SwapUv, t.AngleRad, null);
                ProjectToUv(cols, rows, t.AngleRad, out _, out var vProj);
                double pitchV = EstimatePitchFromProjections(vProj, effRows);
                if (pitchV < 25 && m.UniqueCells < target)
                    score -= 30_000;
                bool prefer = score > bestScore || (score == bestScore && pitchV > bestPitchV + 5);
                if (prefer)
                {
                    bestScore = score;
                    bestPitchV = pitchV;
                    best = t;
                }
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string matchNote = double.IsNaN(consensusMatchDeg) ? "" : $" (模板≈{consensusMatchDeg:F1}° 仅参考)";
                Diag(tag, $"定向(PCA): 主轴≈{pcaAxisDeg:F1}°{matchNote} → 格网 θ={best.AngleRad * 180.0 / Math.PI:F1}° swap={best.SwapUv} pitchV≈{bestPitchV:F1}");
            }
            return best;
        }

        private static int ScoreOrientation(AlignmentMetrics m, int gridRows, int gridCols, bool swapUv, double gridAngleRad, double? consensusMatchDeg)

        {

            int target = gridRows * gridCols;

            int filled = Math.Min(m.UniqueCells, target);

            // 须落满期望格数才给维度奖，避免「方向对但只 snap 少数点」压过整板 16 格
            int dimBonus = m.UniqueCells >= target ? 50_000 : 0;

            int swapPenalty = swapUv && gridRows != gridCols ? -2_000 : 0;

            int angleBonus = consensusMatchDeg.HasValue
                ? ScoreMatchAngleAlignmentBonus(gridAngleRad, consensusMatchDeg.Value)
                : 0;

            return dimBonus + swapPenalty + angleBonus + filled * 1_000 + m.SnappedCount + m.AspectBonus * 200;

        }

        /// <summary>阵列 u 轴与模板倾斜平行时高分；仅垂直（+90°）时低分，避免 PCA 主轴抢方向。</summary>
        private static int ScoreMatchAngleAlignmentBonus(double gridAngleRad, double matchDeg)

        {
            double gridDeg = gridAngleRad * 180.0 / Math.PI;
            double dParallel = AngleDistanceDeg(gridDeg, matchDeg);
            double dPerp = AngleDistanceDeg(gridDeg, matchDeg + 90.0);
            if (dParallel <= 6) return 80_000;
            if (dParallel <= 12) return 55_000;
            if (dParallel <= 25) return 25_000;
            if (dPerp <= 6) return 8_000;
            if (dPerp <= 12) return 3_000;
            double delta = Math.Min(dParallel, dPerp);
            if (delta <= 45) return 0;
            return (int)(-400 * (delta - 45));
        }

        private static double MinAngleDeltaToMatchDeg(double gridAngleRad, double matchDeg)
        {
            double gridDeg = gridAngleRad * 180.0 / Math.PI;
            double d0 = AngleDistanceDeg(gridDeg, matchDeg);
            double d90 = AngleDistanceDeg(gridDeg, matchDeg + 90.0);
            return Math.Min(d0, d90);
        }

        /// <summary>阵列 u 轴应与模板角或模板角+90° 一致；仅在偏差大时微调。</summary>
        private static double SnapGridAngleToMatchRad(double gridAngleRad, double matchDeg)
        {
            if (MinAngleDeltaToMatchDeg(gridAngleRad, matchDeg) <= 8.0)
                return gridAngleRad;

            double gridDeg = gridAngleRad * 180.0 / Math.PI;
            double bestCandDeg = gridDeg;
            double bestD = double.MaxValue;
            for (int k = -4; k <= 4; k++)
            {
                double candDeg = matchDeg + k * 90.0;
                double d = AngleDistanceDeg(gridDeg, candDeg);
                if (d < bestD)
                {
                    bestD = d;
                    bestCandDeg = candDeg;
                }
            }
            return bestCandDeg * Math.PI / 180.0;
        }

        /// <summary>阵列期望匹配数 = rows×cols（如 2×8 → 16）。</summary>
        private static int ExpectedConsensusMatchCount(int gridRows, int gridCols) =>
            Math.Max(1, gridRows * gridCols);

        /// <summary>2×N / N×2 条带：沿链向一列/行的焊盘数（如 2 列×8 行 → 8），用于共识/链向/bootstrap，非整板 16。</summary>
        private static int ConsensusChainWindowSize(int gridRows, int gridCols)
        {
            if (gridRows < 1 || gridCols < 1)
                return 0;
            if (gridRows == 2 || gridCols == 2)
                return Math.Max(gridRows, gridCols);
            return ExpectedConsensusMatchCount(gridRows, gridCols);
        }

        /// <summary>取 Score 最高的 K 个匹配索引。</summary>
        private static int[] SelectTopIndicesByScore(double[]? scores, int inputCount, int k)
        {
            k = Math.Min(Math.Max(0, k), inputCount);
            if (k <= 0)
                return Array.Empty<int>();

            var indices = Enumerable.Range(0, inputCount);
            if (scores == null || scores.Length == 0)
                return indices.Take(k).ToArray();

            return indices
                .OrderByDescending(i => i < scores.Length ? scores[i] : 0)
                .ThenBy(i => i)
                .Take(k)
                .ToArray();
        }

        /// <summary>取 Score 最高的 K 个匹配索引（K=ExpectedConsensusMatchCount）。</summary>
        private static int[] SelectTopMatchIndicesForLattice(double[]? scores, int inputCount, int gridRows, int gridCols)
        {
            int k = ExpectedConsensusMatchCount(gridRows, gridCols);
            return SelectTopIndicesByScore(scores, inputCount, k);
        }

        /// <summary>双列条带：取左列（纯列）池，供链向聚类。</summary>
        private static int[] GetStripPrimaryColumnPool(double[] cols, int inputCount)
        {
            int[] pool = Enumerable.Range(0, inputCount).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out _, out double lowMed, out _))
                return pool;

            double colMin = pool.Min(i => cols[i]);
            double colMax = pool.Max(i => cols[i]);
            double leftMax = lowMed + Math.Max(60, (colMax - colMin) * 0.22);
            int[] col0Pool = lowPool.Where(i => cols[i] <= leftMax).ToArray();
            return col0Pool.Length >= 2 ? col0Pool : lowPool;
        }

        /// <summary>单列池：链向 1D 聚类 K 行，每簇取最高分 1 点；仅用这 K 点估链向（不用全板）。</summary>
        private static int[] SelectStripChainIndicesByClusterFit(
            double[] cols,
            double[] rows,
            double[]? scores,
            int[] columnPool,
            int k,
            string? tag)
        {
            if (columnPool.Length == 0 || k <= 0)
                return Array.Empty<int>();
            k = Math.Min(k, columnPool.Length);
            if (columnPool.Length <= k)
                return columnPool.OrderBy(i => rows[i]).ThenBy(i => i).ToArray();

            int[] seedIdx = columnPool
                .OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0)
                .ThenBy(i => i)
                .Take(Math.Min(k, columnPool.Length))
                .ToArray();
            double chainDeg = EstimateChainDirectionAngleDeg(cols, rows, seedIdx);

            var chainCoord = new double[columnPool.Length];
            for (int j = 0; j < columnPool.Length; j++)
            {
                int i = columnPool[j];
                chainCoord[j] = double.IsNaN(chainDeg)
                    ? rows[i]
                    : ProjectAlongChainDeg(cols[i], rows[i], chainDeg);
            }

            double pitchHint = EstimatePitchFromProjections(chainCoord, k);
            FitClusterCenterLine(chainCoord, k, pitchHint, out double[] centers, out _);

            var clusterMembers = Enumerable.Range(0, k).Select(_ => new List<int>()).ToArray();
            for (int j = 0; j < columnPool.Length; j++)
            {
                int i = columnPool[j];
                int c = NearestIndex1D(centers, chainCoord[j]);
                clusterMembers[c].Add(i);
            }

            var picked = new List<int>(k);
            var used = new HashSet<int>();
            for (int c = 0; c < k; c++)
            {
                if (clusterMembers[c].Count == 0)
                    continue;
                int best = clusterMembers[c]
                    .OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0)
                    .ThenBy(i => i)
                    .First();
                picked.Add(best);
                used.Add(best);
            }

            if (picked.Count < k)
            {
                foreach (int i in columnPool.OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0))
                {
                    if (used.Contains(i))
                        continue;
                    picked.Add(i);
                    used.Add(i);
                    if (picked.Count >= k)
                        break;
                }
            }

            if (!double.IsNaN(chainDeg))
            {
                chainDeg = EstimateChainDirectionAngleDeg(cols, rows, picked.ToArray());
                picked = picked
                    .OrderBy(i => ProjectAlongChainDeg(cols[i], rows[i], chainDeg))
                    .ThenBy(i => i)
                    .ToList();
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled && picked.Count > 0)
            {
                double cd = EstimateChainDirectionAngleDeg(cols, rows, picked.ToArray());
                string scoreList = scores != null
                    ? string.Join(", ", picked.Select(i => i < scores.Length ? scores[i].ToString("F3", CultureInfo.InvariantCulture) : "?"))
                    : "";
                Diag(tag, $"链向聚类(列池={columnPool.Length}, K={k}): 链向≈{cd:F1}°, 索引=[{string.Join(",", picked)}], Score=[{scoreList}]");
            }

            return picked.ToArray();
        }

        /// <summary>双列条带引导：左列池内 KMeans 链向聚类取 K 个最吻合点估 θ（非全板、非滑窗顶分）。</summary>
        private static int[] SelectBootstrapIndicesForTwoColumnStrip(
            double[] cols,
            double[] rows,
            double[]? scores,
            int inputCount,
            int window,
            string? tag)
        {
            if (inputCount == 0 || window <= 0)
                return Array.Empty<int>();

            int[] col0Pool = GetStripPrimaryColumnPool(cols, inputCount);
            return SelectStripChainIndicesByClusterFit(cols, rows, scores, col0Pool, window, tag);
        }

        /// <summary>2×N 等：在 PCA 与 PCA+90° 下沿 u/v 试滑动窗，取得分高且横向散布小的连续 N 点（同一列/行 N 连）。</summary>
        private static int[] SelectIndicesAlongFirstLatticeLine(
            double[] cols,
            double[] rows,
            double[]? scores,
            int inputCount,
            int gridRows,
            int gridCols,
            string? tag)
        {
            int window = ConsensusChainWindowSize(gridRows, gridCols);
            if (inputCount == 0 || window <= 0)
                return Array.Empty<int>();
            if (gridCols == 2)
                return SelectBootstrapIndicesForTwoColumnStrip(cols, rows, scores, inputCount, window, tag);
            window = Math.Min(window, inputCount);

            double thetaPca = EstimatePrincipalAngleRad(cols, rows);

            int[] bestPick = Array.Empty<int>();
            double bestMetric = double.NegativeInfinity;
            string bestNote = "";

            foreach (double theta in new[] { thetaPca, thetaPca + Math.PI / 2.0 })
            {
                ProjectToUv(cols, rows, theta, out var u, out var v);
                foreach (bool alongU in new[] { true, false })
                {
                    double[] along = alongU ? u : v;
                    double[] cross = alongU ? v : u;
                    string axis = alongU ? "u" : "v";
                    var order = Enumerable.Range(0, inputCount).OrderBy(i => along[i]).ThenBy(i => i).ToArray();
                    double alongSpan = order.Length > 1
                        ? Math.Max(1, along[order[^1]] - along[order[0]])
                        : 1;
                    for (int s = 0; s + window <= inputCount; s++)
                    {
                        var pick = Enumerable.Range(0, window).Select(j => order[s + j]).ToArray();
                        double sum = 0;
                        foreach (int idx in pick)
                            sum += scores != null && idx < scores.Length ? scores[idx] : 1;
                        double crossSpan = pick.Max(i => cross[i]) - pick.Min(i => cross[i]);
                        // 垂向跨度远大于链向步距时多为「跨两列」窗，强惩罚（原 0.01 不足以排除 0.815 等误检）
                        double crossRatio = crossSpan / alongSpan;
                        if (scores != null && pick.Any(i => i >= scores.Length || scores[i] < 0.9))
                            continue;
                        double metric = sum * (1.0 + 1.0 / (1.0 + crossRatio * 4.0)) - crossSpan * 0.02;
                        if (metric > bestMetric)
                        {
                            bestMetric = metric;
                            bestPick = pick;
                            double cLo = pick.Min(i => along[i]);
                            double cHi = pick.Max(i => along[i]);
                            bestNote = $"θ={theta * 180 / Math.PI:F1}°沿{axis} {axis}∈[{cLo:F1},{cHi:F1}] 垂向跨度={crossSpan:F1}";
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled && bestPick.Length > 0)
            {
                string scoreList = scores != null
                    ? string.Join(", ", bestPick.Select(i => i < scores.Length ? scores[i].ToString("F3", CultureInfo.InvariantCulture) : "?"))
                    : "";
                Diag(tag, $"模板角共识候选: {bestNote}, Score=[{scoreList}]");
            }

            return bestPick;
        }

        /// <summary>已定格网角后：沿 v 取连续 window 个，u 散布最小且得分高（第一列 N 连）。</summary>
        private static int[] SelectChainIndicesAtUv(
            double[] u, double[] v, double[]? scores, int n, int window, string? tag) =>
            SelectChainIndicesFromPool(Enumerable.Range(0, n).ToArray(), u, v, scores, window, 0.9, tag);

        /// <summary>按图像 Col 间隙分成左右两列；三簇时取「最左簇 | 其余」，避免中间列并入列0。</summary>
        private static bool TryPartitionTwoImageColumns(
            double[] cols,
            int[] pool,
            out int[] lowColPool,
            out int[] highColPool,
            out double lowMed,
            out double highMed)
        {
            lowColPool = Array.Empty<int>();
            highColPool = Array.Empty<int>();
            lowMed = highMed = 0;
            if (pool.Length < 4)
                return false;

            var sorted = pool.OrderBy(i => cols[i]).ToArray();
            double span = cols[sorted[^1]] - cols[sorted[0]];
            double minGap = Math.Max(80, span * 0.15);
            double leftColMax = cols[sorted[0]] + span * 0.38;

            var candidates = new List<(int splitAt, double gap, double lowMed, double highMed)>();
            for (int j = 1; j < sorted.Length; j++)
            {
                double gap = cols[sorted[j]] - cols[sorted[j - 1]];
                if (gap < minGap)
                    continue;
                var low = sorted.Take(j).ToArray();
                var high = sorted.Skip(j).ToArray();
                if (low.Length < 2 || high.Length < 2)
                    continue;
                double lm = MedianOfValues(low.Select(i => cols[i]));
                double hm = MedianOfValues(high.Select(i => cols[i]));
                candidates.Add((j, gap, lm, hm));
            }

            if (candidates.Count == 0)
                return false;

            var chosen = candidates
                .Where(c => c.lowMed <= leftColMax)
                .OrderByDescending(c => c.gap)
                .FirstOrDefault();

            if (chosen.splitAt == 0)
                chosen = candidates.OrderByDescending(c => c.gap).First();

            lowColPool = sorted.Take(chosen.splitAt).ToArray();
            highColPool = sorted.Skip(chosen.splitAt).ToArray();
            lowMed = chosen.lowMed;
            highMed = chosen.highMed;
            return true;
        }

        /// <summary>双列条带列0：左列池 + 链向投影滑窗；θ≠±90° 时不能仅用 v 排序。</summary>
        private static int[] SelectColumn0ChainPick(
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            int window,
            double chainDirectionDeg,
            string? tag)
        {
            if (n == 0 || window <= 0)
                return Array.Empty<int>();
            window = Math.Min(window, n);

            int[] pool = Enumerable.Range(0, n).ToArray();
            int[] col0Pool = pool;
            if (TryPartitionTwoImageColumns(cols, pool, out int[] lowPool, out _, out double lowMed, out _))
            {
                double colMin = pool.Min(i => cols[i]);
                double colMax = pool.Max(i => cols[i]);
                double leftMax = lowMed + Math.Max(60, (colMax - colMin) * 0.22);
                col0Pool = lowPool.Where(i => cols[i] <= leftMax).ToArray();
                if (col0Pool.Length < window)
                    col0Pool = lowPool;
            }

            if (double.IsNaN(chainDirectionDeg) && col0Pool.Length >= 2)
            {
                int[] seed = scores != null
                    ? col0Pool.OrderByDescending(i => i < scores.Length ? scores[i] : 0).Take(Math.Min(8, col0Pool.Length)).ToArray()
                    : col0Pool.Take(Math.Min(8, col0Pool.Length)).ToArray();
                chainDirectionDeg = EstimateChainDirectionAngleDeg(cols, rows, seed);
            }

            foreach (double minSc in new[] { 0.88, 0.82, 0.75, 0.0 })
            {
                int[] pick = SelectChainIndicesFromPool(
                    col0Pool, u, v, scores, rows, cols, window, minSc, chainDirectionDeg,
                    useImageColCrossSpan: false, tag);
                if (pick.Length >= window)
                    return pick;
            }

            int[] fallback = SelectUniformlySpacedAlongChain(col0Pool, rows, cols, window, chainDirectionDeg);
            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled && fallback.Length > 0)
                Diag(tag, $"列0 链向 N 连 等距回退: 左列池={col0Pool.Length}, θ={chainDirectionDeg:F1}°");
            return fallback;
        }

        private static int[] SelectUniformlySpacedAlongChain(
            int[] pool, double[] rows, double[] cols, int count, double chainDirectionDeg)
        {
            if (pool.Length == 0 || count <= 0)
                return Array.Empty<int>();
            if (pool.Length <= count)
                return pool.ToArray();

            if (double.IsNaN(chainDirectionDeg))
                chainDirectionDeg = 90.0;

            var ordered = pool.OrderBy(i => ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg)).ThenBy(i => i).ToArray();
            var picked = new List<int>(count);
            for (int k = 0; k < count; k++)
            {
                double t = count == 1 ? 0.5 : (double)k / (count - 1);
                int j = (int)Math.Round(t * (ordered.Length - 1));
                int idx = ordered[j];
                if (!picked.Contains(idx))
                    picked.Add(idx);
            }

            if (picked.Count < count)
            {
                foreach (int i in ordered)
                {
                    if (picked.Contains(i))
                        continue;
                    picked.Add(i);
                    if (picked.Count >= count)
                        break;
                }
            }

            return picked.ToArray();
        }

        /// <summary>在指定索引池内滑窗选链向 N 连；列1 用图像列跨度衡量「跨列」，避免误用 u 惩罚同列高分点。</summary>
        private static int[] SelectChainIndicesFromPool(
            int[] pool,
            double[] u,
            double[] v,
            double[]? scores,
            int window,
            double minScore,
            string? tag) =>
            SelectChainIndicesFromPool(pool, u, v, scores, null, null, window, minScore, double.NaN, useImageColCrossSpan: false, tag);

        private static int[] SelectChainIndicesFromPool(
            int[] pool,
            double[] u,
            double[] v,
            double[]? scores,
            double[]? rows,
            double[]? cols,
            int window,
            double minScore,
            double chainDirectionDeg,
            bool useImageColCrossSpan,
            string? tag)
        {
            if (pool == null || pool.Length == 0 || window <= 0)
                return Array.Empty<int>();
            window = Math.Min(window, pool.Length);
            var order = !double.IsNaN(chainDirectionDeg) && rows != null && cols != null
                ? pool.OrderBy(i => ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg)).ThenBy(i => i).ToArray()
                : pool.OrderBy(i => v[i]).ThenBy(i => i).ToArray();
            double alongSpan = 1;
            if (window > 1)
            {
                if (!double.IsNaN(chainDirectionDeg) && rows != null && cols != null)
                {
                    alongSpan = Math.Max(1, Math.Abs(
                        ProjectAlongChainDeg(cols[order[window - 1]], rows[order[window - 1]], chainDirectionDeg)
                        - ProjectAlongChainDeg(cols[order[0]], rows[order[0]], chainDirectionDeg)));
                }
                else
                    alongSpan = Math.Max(1, v[order[window - 1]] - v[order[0]]);
            }

            int[] bestPick = Array.Empty<int>();
            double bestMetric = double.NegativeInfinity;
            for (int s = 0; s + window <= order.Length; s++)
            {
                var pick = Enumerable.Range(0, window).Select(j => order[s + j]).ToArray();
                double sum = 0;
                foreach (int idx in pick)
                    sum += scores != null && idx < scores.Length ? scores[idx] : 1;
                if (scores != null && pick.Any(i => i >= scores.Length || scores[i] < minScore))
                    continue;
                double crossSpan = useImageColCrossSpan && cols != null
                    ? pick.Max(i => cols[i]) - pick.Min(i => cols[i])
                    : pick.Max(i => u[i]) - pick.Min(i => u[i]);
                double crossRatio = crossSpan / Math.Max(1, alongSpan);
                double metric = sum * (1.0 + 1.0 / (1.0 + crossRatio * 4.0)) - crossSpan * 0.02;
                if (window >= 3)
                {
                    double[] projs = pick
                        .Select(i => !double.IsNaN(chainDirectionDeg) && rows != null && cols != null
                            ? ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg)
                            : v[i])
                        .OrderBy(x => x)
                        .ToArray();
                    var gaps = new List<double>();
                    for (int g = 1; g < projs.Length; g++)
                        gaps.Add(projs[g] - projs[g - 1]);
                    if (gaps.Count > 0)
                    {
                        double medGap = Median(gaps);
                        double minGap = gaps.Min();
                        double winSpan = projs[^1] - projs[0];
                        if (minGap < 0.52 * medGap)
                            metric -= (0.52 * medGap - minGap) * 4.0;
                        if (alongSpan > 1 && winSpan > 1)
                        {
                            double spanRatio = winSpan / alongSpan;
                            if (spanRatio > 0.88)
                                metric += sum * 0.08 * (spanRatio - 0.88);
                        }
                    }
                }
                if (metric > bestMetric)
                {
                    bestMetric = metric;
                    bestPick = pick;
                }
            }

            if (bestPick.Length == 0 && order.Length >= window)
            {
                double bestSum = double.NegativeInfinity;
                for (int s = 0; s + window <= order.Length; s++)
                {
                    var pick = Enumerable.Range(0, window).Select(j => order[s + j]).ToArray();
                    double sum = pick.Sum(idx => scores != null && idx < scores.Length ? scores[idx] : 1);
                    if (sum > bestSum)
                    {
                        bestSum = sum;
                        bestPick = pick;
                    }
                }
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled && bestPick.Length > 0)
            {
                string scoreList = scores != null
                    ? string.Join(", ", bestPick.Select(i => i < scores.Length ? scores[i].ToString("F3", CultureInfo.InvariantCulture) : "?"))
                    : "";
                Diag(tag, $"链向 N 连 池={pool.Length} 跨列度量={(useImageColCrossSpan ? "图像Col" : "u")}: Score=[{scoreList}]");
            }

            return bestPick;
        }

        /// <summary>列1 池：保留 v 落在「列0 中位 v + Δv」附近的点（含 0.921 等 u 靠列0 但图像在列1 的点）。</summary>
        private static int[] FilterCol1PoolByTargetVBand(int[] pool, double[] v, int[] col0Pick, double deltaV)
        {
            if (pool.Length == 0 || col0Pick.Length == 0)
                return pool;

            double col0VMed = MedianOfValues(col0Pick.Where(i => i >= 0 && i < v.Length).Select(i => v[i]));
            double targetV = col0VMed + deltaV;
            double band = Math.Max(90, Math.Abs(deltaV) * 0.38);
            var bandPool = pool.Where(i => Math.Abs(v[i] - targetV) <= band).ToArray();
            if (bandPool.Length >= 8)
                return bandPool;

            if (pool.Length <= 8)
                return pool;

            var sorted = pool.OrderBy(i => v[i]).ToArray();
            int splitAt = 1;
            double bestGap = 0;
            for (int j = 1; j < sorted.Length; j++)
            {
                double gap = v[sorted[j]] - v[sorted[j - 1]];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    splitAt = j;
                }
            }

            double minGap = Math.Max(80, Math.Abs(deltaV) * 0.12);
            if (bestGap < minGap)
                return pool;

            var low = sorted.Take(splitAt).ToArray();
            var high = sorted.Skip(splitAt).ToArray();
            double medLow = low.Length > 0 ? MedianOfValues(low.Select(i => v[i])) : 0;
            double medHigh = high.Length > 0 ? MedianOfValues(high.Select(i => v[i])) : 0;
            var chosen = medHigh >= medLow ? high : low;
            return Math.Abs(MedianOfValues(chosen.Select(i => v[i])) - targetV)
                <= Math.Abs(medLow - targetV)
                ? chosen
                : bandPool.Length > 0 ? bandPool : chosen;
        }

        /// <summary>候选索引 → 格坐标；同一索引若占多格，优先保留列 0（链向列）。</summary>
        private static Dictionary<int, (int ir, int ic)> BuildIndexToCellMap(
            Dictionary<(int ir, int ic), int> cellBest)
        {
            var map = new Dictionary<int, (int ir, int ic)>();
            foreach (var kv in cellBest)
            {
                if (!map.TryGetValue(kv.Value, out var prev) || (kv.Key.ic == 0 && prev.ic != 0))
                    map[kv.Value] = kv.Key;
            }
            return map;
        }

        /// <summary>双列条带：将链向 N 连按链向在图像上的投影排序后落入 (0..rows-1, ic)。</summary>
        private static void ApplyForcedChainColumnPicks(
            int[] linePick,
            double[] rows,
            double[] cols,
            double[] v,
            int effRows,
            int ic,
            Dictionary<(int ir, int ic), int> cellBest,
            HashSet<int>? forbidIndices,
            double chainDirectionDeg,
            string? tag)
        {
            var ordered = linePick
                .Where(i => i >= 0 && i < v.Length)
                .Where(i => forbidIndices == null || !forbidIndices.Contains(i))
                .OrderBy(i => ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg))
                .ThenBy(i => i)
                .ToArray();
            int n = Math.Min(effRows, ordered.Length);
            var used = new HashSet<int>();
            for (int ir = 0; ir < n; ir++)
            {
                int idx = ordered[ir];
                if (!used.Add(idx))
                    continue;
                if (forbidIndices != null && forbidIndices.Contains(idx))
                    continue;
                foreach (var key in cellBest.Where(kv => kv.Value == idx).Select(kv => kv.Key).ToList())
                    cellBest.Remove(key);
                cellBest[(ir, ic)] = idx;
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string idxList = string.Join(",", ordered.Take(n));
                string forbidNote = forbidIndices != null && forbidIndices.Count > 0
                    ? $", 已排除列0索引=[{string.Join(",", forbidIndices.OrderBy(x => x))}]"
                    : "";
                Diag(tag, $"强制链向列{ic}: 行0..{n - 1} ← 候选[{idxList}]{forbidNote}");
            }
        }

        private static HashSet<int> BuildColumn0ReservedIndices(int[] col0Pick, Dictionary<(int ir, int ic), int> cellBest)
        {
            var reserved = new HashSet<int>();
            if (col0Pick != null)
            {
                foreach (int i in col0Pick)
                    if (i >= 0)
                        reserved.Add(i);
            }
            foreach (var kv in cellBest.Where(kv => kv.Key.ic == 0))
                reserved.Add(kv.Value);
            return reserved;
        }

        /// <summary>排除列0 后，在剩余池内沿 v 取 N 连。</summary>
        private static int[] SelectSecondColumnChainAtUv(
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            HashSet<int> exclude,
            int window,
            string? tag)
        {
            if (n == 0 || window <= 0)
                return Array.Empty<int>();
            var pool = Enumerable.Range(0, n).Where(i => !exclude.Contains(i)).ToArray();
            if (pool.Length < window)
                return Array.Empty<int>();
            return SelectChainIndicesFromPool(pool, u, v, scores, window, 0.85, tag);
        }

        private sealed class SecondColumnClusterResult
        {
            public int[] Pool { get; init; } = Array.Empty<int>();
            public double Col1U { get; init; }
            public double Col0ImageCol { get; init; }
            public double Col1ImageCol { get; init; }
            public double DeltaV { get; init; }
            public double PitchV { get; init; }
        }

        private static double MedianOfValues(IEnumerable<double> values)
        {
            var a = values.OrderBy(x => x).ToArray();
            if (a.Length == 0)
                return 0;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) * 0.5;
        }

        /// <summary>排除列0 后：在剩余池上聚类 v 行距，并估计列1 相对列0 的 v/图像列偏移。</summary>
        private static SecondColumnClusterResult? ReclusterSecondColumnAfterExclude(
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            int[] col0Pick,
            HashSet<int> col0Reserved,
            int effRows,
            RowColumnLattice lattice,
            string? tag)
        {
            var pool = Enumerable.Range(0, n).Where(i => !col0Reserved.Contains(i)).ToArray();
            if (pool.Length < Math.Max(2, effRows / 2))
                return null;

            var vPool = pool.Select(i => v[i]).ToArray();
            double pitchVHint = lattice.PitchV > 1e-3 ? lattice.PitchV : 1;
            FitClusterCenterLine(vPool, effRows, pitchVHint, out _, out double pitchV);
            if (pitchV < 40)
            {
                var vSorted = vPool.OrderBy(x => x).ToArray();
                if (vSorted.Length >= 2)
                    pitchV = Math.Max(40, (vSorted[^1] - vSorted[0]) / Math.Max(1, effRows - 1));
            }

            double col0VMed = MedianOfValues(col0Pick.Where(i => i >= 0 && i < v.Length).Select(i => v[i]));
            double col1VMed = MedianOfValues(pool.Select(i => v[i]));
            double deltaV = col1VMed - col0VMed;

            double col0ImageCol = MedianOfValues(col0Pick.Where(i => i >= 0 && i < cols.Length).Select(i => cols[i]));
            double col1ImageCol = MedianOfValues(pool.Select(i => cols[i]));

            double col1U = WeightedMedian(pool.Select(i =>
            {
                double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                return (u[i], w);
            }).ToList());

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                Diag(tag, $"列1 重聚类: 池={pool.Length} (排除列0={col0Reserved.Count}), Δv={deltaV:F1}, " +
                     $"图像列 med col0={col0ImageCol:F0} col1={col1ImageCol:F0}, pitchV={pitchV:F1}");
            }

            return new SecondColumnClusterResult
            {
                Pool = pool,
                Col1U = col1U,
                Col0ImageCol = col0ImageCol,
                Col1ImageCol = col1ImageCol,
                DeltaV = deltaV,
                PitchV = pitchV
            };
        }

        /// <summary>列1：排除列0 后筛图像右列 + v 主簇，再链向 N 连按 v 落格（与列0 同构，保证共线）。</summary>
        private static int FillSecondColumnByChain(
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int effRows,
            int chainWindow,
            int[] col0Pick,
            SecondColumnClusterResult cluster,
            HashSet<int> col0Reserved,
            RowColumnLattice lattice,
            double chainDirectionDeg,
            Dictionary<(int ir, int ic), int> cellBest,
            string? tag)
        {
            double imageColMargin = Math.Max(30, Math.Abs(cluster.Col1ImageCol - cluster.Col0ImageCol) * 0.12);
            int[] pool = cluster.Pool
                .Where(i => IsOnSecondColumnInImage(i, cols, cluster.Col0ImageCol, cluster.Col1ImageCol, imageColMargin))
                .ToArray();
            pool = FilterCol1PoolByTargetVBand(pool, v, col0Pick, cluster.DeltaV);

            int w = Math.Min(chainWindow, effRows);
            int[] col1Pick = SelectChainIndicesFromPool(
                pool, u, v, scores, rows, cols, w, 0.82, chainDirectionDeg, useImageColCrossSpan: true, tag);
            if (col1Pick.Length > 0)
                ApplyForcedChainColumnPicks(col1Pick, rows, cols, v, effRows, 1, cellBest, col0Reserved, chainDirectionDeg, tag);

            AssignSecondColumnPerGridRow(
                rows, cols, u, v, scores, effRows, pool, col0Reserved, col0Pick,
                cluster.DeltaV, cluster.Col1U, lattice, chainDirectionDeg,
                cluster.Col0ImageCol, cluster.Col1ImageCol, imageColMargin, cellBest, tag);

            RefineSecondColumnPerRowAlongChain(
                rows, cols, v, scores, effRows, pool, col0Reserved,
                cluster.DeltaV, chainDirectionDeg, cluster.Col0ImageCol, cluster.Col1ImageCol,
                imageColMargin, cellBest, tag);

            RepairColumnChainSpacing(
                rows, cols, scores, effRows, 1, pool, col0Reserved, chainDirectionDeg,
                cluster.Col0ImageCol, cluster.Col1ImageCol, imageColMargin, cellBest, tag);

            int filled = Enumerable.Range(0, effRows).Count(ir => cellBest.ContainsKey((ir, 1)));
            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列1 链向落格: {filled}/{effRows} 行, 池={pool.Length}, v簇Δ={cluster.DeltaV:F0}");
            return filled;
        }

        /// <summary>列1 按行对齐列0：链向投影为主、u≈u(列0)+Δu 共线，避免仅按 Row 最近误选离线点。</summary>
        private static void AssignSecondColumnPerGridRow(
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int effRows,
            int[] pool,
            HashSet<int> col0Reserved,
            int[] col0Pick,
            double deltaV,
            double col1U,
            RowColumnLattice lattice,
            double chainDirectionDeg,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            Dictionary<(int ir, int ic), int> cellBest,
            string? tag)
        {
            foreach (var key in cellBest.Where(kv => kv.Key.ic == 1).Select(kv => kv.Key).ToList())
                cellBest.Remove(key);

            double deltaU = lattice.Cols >= 2
                ? lattice.ColU[1] - lattice.ColU[0]
                : col1U - MedianOfValues(col0Pick.Where(i => i >= 0 && i < u.Length).Select(i => u[i]));
            ComputeLatticeSnapTolerance(lattice, 0, out double snapU, out double snapV);
            double uSnap = Math.Max(36, Math.Min(snapU * 1.15, Math.Abs(deltaU) * 0.28));
            double vSnap = Math.Max(70, Math.Abs(deltaV) * 0.28);

            var chainAnchors = Enumerable.Range(0, effRows)
                .Where(ir => cellBest.TryGetValue((ir, 0), out int i0) && i0 >= 0 && i0 < cols.Length)
                .Select(ir => ProjectAlongChainDeg(cols[cellBest[(ir, 0)]], rows[cellBest[(ir, 0)]], chainDirectionDeg))
                .OrderBy(x => x)
                .ToArray();
            double snapChain = 80;
            if (chainAnchors.Length >= 2)
            {
                var gaps = chainAnchors.Zip(chainAnchors.Skip(1), (a, b) => Math.Abs(b - a)).ToList();
                snapChain = Math.Max(45, Median(gaps) * 0.62);
            }

            var used = new HashSet<int>();
            const double chainTiePx = 4.0;

            for (int ir = 0; ir < effRows; ir++)
            {
                if (!cellBest.TryGetValue((ir, 0), out int i0) || i0 < 0)
                    continue;

                double wantChain = ProjectAlongChainDeg(cols[i0], rows[i0], chainDirectionDeg);
                double wantV = v[i0] + deltaV;
                double wantU = u[i0] + deltaU;
                double minRowStep = 40;
                if (ir > 0 && cellBest.TryGetValue((ir - 1, 0), out int i0Prev) && cellBest.TryGetValue((ir - 1, 1), out int prev))
                    minRowStep = Math.Max(40, Math.Abs(rows[i0] - rows[i0Prev]) * 0.72);

                int best = -1;
                double bestRank = double.PositiveInfinity;
                double bestSc = -1;
                foreach (int i in pool)
                {
                    if (col0Reserved.Contains(i) || used.Contains(i))
                        continue;
                    if (!IsOnSecondColumnInImage(i, cols, col0ImageCol, col1ImageCol, imageColMargin))
                        continue;

                    double chainDist = Math.Abs(ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg) - wantChain);
                    if (chainDist > snapChain)
                        continue;
                    if (Math.Abs(v[i] - wantV) > vSnap)
                        continue;
                    if (Math.Abs(u[i] - wantU) > uSnap)
                        continue;
                    if (ir > 0 && cellBest.TryGetValue((ir - 1, 1), out int prevIdx)
                        && Math.Abs(rows[i] - rows[prevIdx]) < minRowStep)
                        continue;

                    double rank = chainDist
                        + Math.Abs(u[i] - wantU) * 0.22
                        + Math.Abs(rows[i] - rows[i0]) * 0.06;
                    double sc = scores != null && i < scores.Length ? scores[i] : 1;
                    bool prefer = best < 0
                        || rank < bestRank - chainTiePx
                        || (rank <= bestRank + chainTiePx && sc > bestSc + 0.012);
                    if (prefer)
                    {
                        bestRank = rank;
                        bestSc = sc;
                        best = i;
                    }
                }

                if (best < 0)
                    continue;
                cellBest[(ir, 1)] = best;
                used.Add(best);
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列1 按行对齐列0(链向+u): {used.Count}/{effRows} 行, snapChain={snapChain:F0}, uSnap={uSnap:F0}");
        }

        /// <summary>列1 顶行：在池内选 v≈v(列0[0])+Δv 且链向投影最接近列0[0] 的点（如 0.837 顶行）。</summary>
        private static void PromoteCol1TopRowFromPool(
            double[] rows,
            double[] cols,
            double[] v,
            double[]? scores,
            int effRows,
            int[] pool,
            int[] col0Pick,
            HashSet<int> col0Reserved,
            double deltaV,
            double chainDirectionDeg,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            Dictionary<(int ir, int ic), int> cellBest,
            string? tag)
        {
            if (!cellBest.TryGetValue((0, 0), out int i0) || i0 < 0 || i0 >= v.Length)
                return;

            double expectedV = v[i0] + deltaV;
            double vSnap = Math.Max(90, Math.Abs(deltaV) * 0.38);
            double anchor = ProjectAlongChainDeg(cols[i0], rows[i0], chainDirectionDeg);
            double snapChain = 80;

            int best = -1;
            double bestRank = double.PositiveInfinity;
            double bestSc = -1;
            foreach (int i in pool)
            {
                if (col0Reserved.Contains(i))
                    continue;
                if (!IsOnSecondColumnInImage(i, cols, col0ImageCol, col1ImageCol, imageColMargin))
                    continue;
                if (Math.Abs(v[i] - expectedV) > vSnap)
                    continue;
                double chainDist = Math.Abs(ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg) - anchor);
                if (chainDist > snapChain)
                    continue;
                double rank = chainDist + Math.Abs(v[i] - expectedV) * 0.15;
                double sc = scores != null && i < scores.Length ? scores[i] : 1;
                bool prefer = rank < bestRank - 1e-6 || (rank <= bestRank + 6 && sc > bestSc + 0.012);
                if (prefer)
                {
                    bestRank = rank;
                    bestSc = sc;
                    best = i;
                }
            }

            if (best < 0 || (cellBest.TryGetValue((0, 1), out int cur) && cur == best))
                return;

            foreach (var key in cellBest.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList())
                cellBest.Remove(key);
            cellBest[(0, 1)] = best;
            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                double sc = scores != null && best < scores.Length ? scores[best] : 0;
                Diag(tag, $"列1 顶行提升: #{best} Score={sc:F3}");
            }
        }

        /// <summary>列1 同行间距对齐列0：过密行（如 0.925）改选链向步长接近列0 的点（如 0.837 所在行）。</summary>
        private static void RepairColumnChainSpacing(
            double[] rows,
            double[] cols,
            double[]? scores,
            int effRows,
            int ic,
            int[] pool,
            HashSet<int> col0Reserved,
            double chainDirectionDeg,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            Dictionary<(int ir, int ic), int> cellBest,
            string? tag)
        {
            if (ic != 1 || double.IsNaN(chainDirectionDeg) || effRows < 2)
                return;

            double Proj(int idx) => ProjectAlongChainDeg(cols[idx], rows[idx], chainDirectionDeg);
            int repaired = 0;

            for (int ir = 0; ir < effRows; ir++)
            {
                if (!cellBest.TryGetValue((ir, 0), out int c0b))
                    continue;
                if (!cellBest.TryGetValue((ir, 1), out int cur))
                    continue;

                double wantRow = rows[c0b];
                double wantProj = Proj(c0b);
                if (ir > 0 && cellBest.TryGetValue((ir - 1, 0), out int c0a) && cellBest.TryGetValue((ir - 1, 1), out int prev))
                {
                    double targetRowGap = rows[c0b] - rows[c0a];
                    double targetProjGap = Proj(c0b) - Proj(c0a);
                    double actualRowGap = rows[cur] - rows[prev];
                    double actualProjGap = Proj(cur) - Proj(prev);
                    bool rowOk = Math.Abs(targetRowGap) < 20
                        || Math.Abs(actualRowGap) >= 0.72 * Math.Abs(targetRowGap);
                    bool projOk = Math.Abs(targetProjGap) < 20
                        || Math.Abs(actualProjGap) >= 0.72 * Math.Abs(targetProjGap);
                    if (rowOk && projOk)
                        continue;
                    wantRow = rows[prev] + targetRowGap;
                    wantProj = Proj(prev) + targetProjGap;
                }

                int best = -1;
                double bestRank = double.PositiveInfinity;
                double bestSc = -1;
                foreach (int i in pool)
                {
                    if (col0Reserved.Contains(i) || cellBest.Values.Contains(i))
                        continue;
                    if (!IsOnSecondColumnInImage(i, cols, col0ImageCol, col1ImageCol, imageColMargin))
                        continue;
                    if (ir > 0 && cellBest.TryGetValue((ir - 1, 1), out int prevIdx))
                    {
                        double targetRowGap = rows[c0b] - rows[cellBest[(ir - 1, 0)]];
                        double minRowStep = Math.Abs(targetRowGap) >= 20 ? 0.72 * Math.Abs(targetRowGap) : 40;
                        if (Math.Abs(rows[i] - rows[prevIdx]) < minRowStep)
                            continue;
                    }

                    double rank = Math.Abs(rows[i] - wantRow) + Math.Abs(Proj(i) - wantProj) * 0.2;
                    double sc = scores != null && i < scores.Length ? scores[i] : 1;
                    bool prefer = rank < bestRank - 1e-6
                        || (rank <= bestRank + 10 && sc > bestSc + 0.012);
                    if (prefer)
                    {
                        bestRank = rank;
                        bestSc = sc;
                        best = i;
                    }
                }

                if (best < 0 || best == cur)
                    continue;

                foreach (var key in cellBest.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList())
                    cellBest.Remove(key);
                cellBest[(ir, 1)] = best;
                repaired++;
            }

            if (repaired > 0 && !string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列1 链向间距修正(对齐列0): {repaired} 行");
        }

        /// <summary>列1 逐行 refine：图像列1 + v≈v(列0同行)+Δv + 链向投影与列0 同行；不用 u（θ≈±90° 时 u 沿 Row，不能表示左右列）。</summary>
        private static void RefineSecondColumnPerRowAlongChain(
            double[] rows,
            double[] cols,
            double[] v,
            double[]? scores,
            int effRows,
            int[] pool,
            HashSet<int> col0Reserved,
            double deltaV,
            double chainDirectionDeg,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            Dictionary<(int ir, int ic), int> cellBest,
            string? tag)
        {
            if (pool.Length == 0)
                return;

            var anchorChain = new double[effRows];
            for (int ir = 0; ir < effRows; ir++)
            {
                if (cellBest.TryGetValue((ir, 0), out int i0) && i0 >= 0 && i0 < cols.Length)
                    anchorChain[ir] = ProjectAlongChainDeg(cols[i0], rows[i0], chainDirectionDeg);
                else
                    anchorChain[ir] = double.NaN;
            }

            var chainAnchors = anchorChain.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            double snapChain = 80;
            if (chainAnchors.Length >= 2)
                snapChain = Math.Max(50, Median(chainAnchors.Zip(chainAnchors.Skip(1), (a, b) => Math.Abs(b - a)).ToList()) * 0.65);

            double vSnap = Math.Max(90, Math.Abs(deltaV) * 0.28);
            const double chainTiePx = 4.0;
            var usedCol1 = new HashSet<int>();
            int replaced = 0;
            for (int ir = 0; ir < effRows; ir++)
            {
                if (double.IsNaN(anchorChain[ir]))
                    continue;

                double expectedV = double.NaN;
                if (cellBest.TryGetValue((ir, 0), out int i0Row) && i0Row >= 0 && i0Row < v.Length)
                    expectedV = v[i0Row] + deltaV;

                int bestIdx = -1;
                double bestDist = double.PositiveInfinity;
                double bestSc = -1;
                foreach (int i in pool)
                {
                    if (col0Reserved.Contains(i))
                        continue;
                    if (!IsOnSecondColumnInImage(i, cols, col0ImageCol, col1ImageCol, imageColMargin))
                        continue;
                    if (!double.IsNaN(expectedV) && Math.Abs(v[i] - expectedV) > vSnap)
                        continue;
                    double dist = Math.Abs(ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg) - anchorChain[ir]);
                    if (dist > snapChain)
                        continue;
                    double sc = scores != null && i < scores.Length ? scores[i] : 1;
                    bool prefer = bestIdx < 0
                        || dist < bestDist - chainTiePx
                        || (dist <= bestDist + chainTiePx && sc > bestSc + 0.012)
                        || (Math.Abs(dist - bestDist) < 1e-6 && sc > bestSc);
                    if (prefer)
                    {
                        bestDist = dist;
                        bestSc = sc;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0)
                    continue;

                if (usedCol1.Contains(bestIdx))
                {
                    int alt = -1;
                    double altDist = double.PositiveInfinity;
                    double altSc = -1;
                    foreach (int i in pool)
                    {
                        if (col0Reserved.Contains(i) || usedCol1.Contains(i))
                            continue;
                        if (!IsOnSecondColumnInImage(i, cols, col0ImageCol, col1ImageCol, imageColMargin))
                            continue;
                        if (!double.IsNaN(expectedV) && Math.Abs(v[i] - expectedV) > vSnap)
                            continue;
                        double dist = Math.Abs(ProjectAlongChainDeg(cols[i], rows[i], chainDirectionDeg) - anchorChain[ir]);
                        if (dist > snapChain)
                            continue;
                        double sc = scores != null && i < scores.Length ? scores[i] : 1;
                        bool preferAlt = alt < 0
                            || dist < altDist - chainTiePx
                            || (dist <= altDist + chainTiePx && sc > altSc + 0.012)
                            || (Math.Abs(dist - altDist) < 1e-6 && sc > altSc);
                        if (preferAlt)
                        {
                            altDist = dist;
                            altSc = sc;
                            alt = i;
                        }
                    }
                    if (alt < 0)
                        continue;
                    bestIdx = alt;
                }

                if (cellBest.TryGetValue((ir, 1), out int prev) && prev == bestIdx)
                {
                    usedCol1.Add(bestIdx);
                    continue;
                }

                foreach (var key in cellBest.Where(kv => kv.Value == bestIdx).Select(kv => kv.Key).ToList())
                    cellBest.Remove(key);
                cellBest[(ir, 1)] = bestIdx;
                usedCol1.Add(bestIdx);
                replaced++;
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列1 链向同行 refine: {replaced} 行更新, snapChain={snapChain:F0}");
        }

        private static bool IsOnSecondColumnInImage(
            int i, double[] cols, double col0ImageCol, double col1ImageCol, double margin)
        {
            double d0 = Math.Abs(cols[i] - col0ImageCol);
            double d1 = Math.Abs(cols[i] - col1ImageCol);
            return d1 + margin < d0;
        }

        /// <summary>定左右列：图像 Col 明确时用 Col；两列之间模糊带用 u 离 ColU 更近的一侧（避免 0.895 等中间 Col 误归左列）。</summary>
        private static int ResolveLatticeColumnIndex(
            int i,
            double[] cols,
            double[] u,
            RowColumnLattice lattice,
            double col0ImageCol,
            double col1ImageCol,
            double margin)
        {
            double d0 = Math.Abs(cols[i] - col0ImageCol);
            double d1 = Math.Abs(cols[i] - col1ImageCol);
            double colGap = Math.Abs(col1ImageCol - col0ImageCol);
            if (colGap > 1e-3)
            {
                double rel = (cols[i] - col0ImageCol) / colGap;
                if (rel >= 0.58)
                    return 1;
                if (rel <= 0.32)
                    return 0;
            }
            if (d1 + margin < d0)
                return 1;
            if (d0 + margin < d1)
                return 0;
            double uMid = 0.5 * (lattice.ColU[0] + lattice.ColU[1]);
            return u[i] >= uMid ? 1 : 0;
        }

        /// <summary>按 (行,列) 顺序收集双列条带索引，避免 cellBest.Values 混入零散格或重复语义。</summary>
        private static List<int> CollectStripCellIndices(
            Dictionary<(int ir, int ic), int> cellBest,
            int effRows,
            int effCols)
        {
            var list = new List<int>(effRows * effCols);
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.TryGetValue((ir, ic), out int idx))
                        list.Add(idx);
                }
            }
            return list;
        }

        private static double ProjectAlongChainDeg(double col, double row, double chainDirectionDeg)
        {
            double rad = chainDirectionDeg * Math.PI / 180.0;
            return col * Math.Cos(rad) + row * Math.Sin(rad);
        }

        /// <summary>条带列共线容差：沿链向 v 落格、横向 u 对齐；比 snapV 更宽，避免 pitch 估偏时误删。</summary>
        private static double ComputeColumnCollinearityToleranceU(RowColumnLattice lattice, double snapU, double snapV)
        {
            double su = snapU > 1e-6 ? snapU : Math.Max(12.0, 0.5 * lattice.MeanPitchU);
            double sv = snapV > 1e-6 ? snapV : Math.Max(12.0, 0.5 * lattice.PitchV);
            return Math.Max(48.0, Math.Max(Math.Max(2.5 * su, 0.65 * lattice.MeanPitchU), 1.5 * sv));
        }

        private static double ComputeRowAlignmentToleranceV(RowColumnLattice lattice, double snapV) =>
            Math.Max(50.0, Math.Max(0.42 * lattice.PitchV, 0.38 * snapV));

        /// <summary>图像 Col 落在两列间隙或距任一列中心过远（如 0.864/0.852 在 Col≈1124），不宜占格。</summary>
        private static bool IsImageColumnGapOutlier(int i, double[] cols, double col0ImageCol, double col1ImageCol)
        {
            double gap = col1ImageCol - col0ImageCol;
            if (gap < 80 || i < 0 || i >= cols.Length)
                return false;
            double d0 = Math.Abs(cols[i] - col0ImageCol);
            double d1 = Math.Abs(cols[i] - col1ImageCol);
            double minD = Math.Min(d0, d1);
            if (minD > gap * 0.26)
                return true;
            double rel = (cols[i] - col0ImageCol) / gap;
            if (rel < 0.38 || rel > 0.52)
                return false;
            return minD > gap * 0.20;
        }

        /// <summary>从 cellBest 剔除 u/v/图像列离群占格，返回剔除格数。</summary>
        private static int PruneOutliersFromStripCellBest(
            Dictionary<(int ir, int ic), int> cellBest,
            double[] cols,
            double[] u,
            double[] v,
            RowColumnLattice lattice,
            double col0ImageCol,
            double col1ImageCol,
            double snapU,
            double snapV,
            string? tag)
        {
            if (cellBest.Count == 0)
                return 0;

            double uTol = ComputeColumnCollinearityToleranceU(lattice, snapU, snapV);
            double vTol = ComputeRowAlignmentToleranceV(lattice, snapV);
            var uMed = new Dictionary<int, double>();
            foreach (var grp in cellBest.GroupBy(kv => kv.Key.ic))
            {
                var idxs = grp.Select(kv => kv.Value).Where(i => i >= 0 && i < u.Length).ToList();
                if (idxs.Count > 0)
                    uMed[grp.Key] = MedianOfValues(idxs.Select(i => u[i]));
            }

            var remove = new List<(int ir, int ic)>();
            foreach (var kv in cellBest)
            {
                int i = kv.Value;
                (int ir, int ic) = kv.Key;
                if (i < 0 || i >= u.Length || i >= v.Length || i >= cols.Length)
                {
                    remove.Add(kv.Key);
                    continue;
                }
                if (IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                {
                    remove.Add(kv.Key);
                    continue;
                }
                if (Math.Abs(v[i] - lattice.IdealV(ir)) > vTol)
                {
                    remove.Add(kv.Key);
                    continue;
                }
                if (uMed.TryGetValue(ic, out double medU) && Math.Abs(u[i] - medU) > uTol)
                    remove.Add(kv.Key);
            }

            foreach (var key in remove)
                cellBest.Remove(key);

            if (remove.Count > 0 && !string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string detail = string.Join(", ", remove.Select(k => $"[{k.ir},{k.ic}]"));
                DiagLattice(tag, $"条带离群剔除: {remove.Count} 格 (|Δu|>{uTol:F0}, |Δv|>{vTol:F0}, 列间隙) {detail}");
            }

            return remove.Count;
        }

        /// <summary>按物理列在 u 方向与列中心线对齐（链向 v 上的共线），不改坐标。</summary>
        internal static int[] PrunePickIndicesByLatticeColumnUv(
            double[] u,
            double[] v,
            int[] pickIndices,
            IReadOnlyDictionary<int, (int ir, int ic)> indexToCell,
            double maxUDeviation,
            string? tag)
        {
            if (pickIndices == null || pickIndices.Length == 0)
                return Array.Empty<int>();
            if (pickIndices.Length < 2 || maxUDeviation <= 0 || u == null || v == null)
                return pickIndices;

            var kept = new HashSet<int>();
            int removed = 0;
            int colCount = 0;
            double maxKeptDev = 0;

            foreach (var grp in pickIndices
                         .Where(i => i >= 0 && i < u.Length && i < v.Length
                                     && indexToCell.TryGetValue(i, out var cell) && cell.ic >= 0)
                         .GroupBy(i => indexToCell[i].ic))
            {
                var idxs = grp.ToList();
                if (idxs.Count < 2)
                {
                    foreach (int i in idxs)
                        kept.Add(i);
                    continue;
                }

                colCount++;
                double medU = MedianOfValues(idxs.Select(i => u[i]));
                foreach (int i in idxs)
                {
                    double dev = Math.Abs(u[i] - medU);
                    if (dev <= maxUDeviation)
                    {
                        kept.Add(i);
                        maxKeptDev = Math.Max(maxKeptDev, dev);
                    }
                    else
                        removed++;
                }
            }

            foreach (int i in pickIndices)
            {
                if (!indexToCell.TryGetValue(i, out var cell) || cell.ic < 0)
                    kept.Add(i);
            }

            if (removed > 0 && !string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列共线筛选(u): {colCount} 列, 剔除 {removed} 个 |Δu|>{maxUDeviation:F1}, 保留最大|Δu|≈{maxKeptDev:F1}");

            return pickIndices.Where(i => kept.Contains(i)).ToArray();
        }

        /// <summary>列1：按同行列0 的 u+Δu 剔除明显离线的点（不改坐标）。</summary>
        private static int[] PruneCol1PickIndicesByRowAlignedU(
            double[] u,
            int[] pickIndices,
            IReadOnlyDictionary<int, (int ir, int ic)> indexToCell,
            Dictionary<(int ir, int ic), int> cellBest,
            double deltaU,
            double maxUDeviation,
            string? tag)
        {
            if (pickIndices.Length == 0 || maxUDeviation <= 0)
                return pickIndices;

            var kept = new HashSet<int>();
            int removed = 0;
            foreach (int i in pickIndices)
            {
                if (!indexToCell.TryGetValue(i, out var cell) || cell.ic != 1)
                {
                    kept.Add(i);
                    continue;
                }
                if (!cellBest.TryGetValue((cell.ir, 0), out int i0) || i0 < 0 || i0 >= u.Length)
                {
                    kept.Add(i);
                    continue;
                }
                double dev = Math.Abs(u[i] - (u[i0] + deltaU));
                if (dev <= maxUDeviation)
                    kept.Add(i);
                else
                    removed++;
            }

            if (removed > 0 && !string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列1 同行共线: 剔除 {removed} 个 |Δu|>{maxUDeviation:F1} (相对列0+Δu)");

            return pickIndices.Where(i => kept.Contains(i)).ToArray();
        }

        private static int[] PrunePickIndicesByColumnCollinearitySafe(
            double[] u,
            double[] v,
            int[] pickIndices,
            IReadOnlyDictionary<int, (int ir, int ic)> indexToCell,
            RowColumnLattice lattice,
            double snapU,
            double snapV,
            string? tag)
        {
            if (pickIndices.Length < 2)
                return pickIndices;

            double tol = ComputeColumnCollinearityToleranceU(lattice, snapU, snapV);
            int[] pruned = PrunePickIndicesByLatticeColumnUv(u, v, pickIndices, indexToCell, tol, tag);
            int minKeep = Math.Max(4, pickIndices.Length / 2);
            if (pruned.Length >= minKeep)
                return pruned;

            double loose = tol * 2.5;
            int[] loosePruned = PrunePickIndicesByLatticeColumnUv(u, v, pickIndices, indexToCell, loose, tag);
            if (loosePruned.Length >= minKeep)
            {
                if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                    Diag(tag, $"列共线筛选放宽: |Δu|≤{loose:F1} ({pickIndices.Length}→{loosePruned.Length})");
                return loosePruned;
            }

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                Diag(tag, $"列共线筛选过严({pickIndices.Length}→{loosePruned.Length})，保留落格结果");
            return pickIndices;
        }

        private static bool TryFitImageLine2D(
            IReadOnlyList<(double row, double col)> points,
            out double r0,
            out double c0,
            out double dr,
            out double dc)
        {
            r0 = c0 = dr = dc = 0;
            int n = points.Count;
            if (n == 0)
                return false;
            if (n == 1)
            {
                r0 = points[0].row;
                c0 = points[0].col;
                dr = 1;
                dc = 0;
                return true;
            }

            double mr = 0, mc = 0;
            foreach (var p in points)
            {
                mr += p.row;
                mc += p.col;
            }
            mr /= n;
            mc /= n;
            double srr = 0, scc = 0, src = 0;
            foreach (var p in points)
            {
                double ar = p.row - mr;
                double ac = p.col - mc;
                srr += ar * ar;
                scc += ac * ac;
                src += ar * ac;
            }

            double tr = srr + scc;
            double det = srr * scc - src * src;
            double disc = Math.Sqrt(Math.Max(0, tr * tr * 0.25 - det));
            double lam1 = tr * 0.5 + disc;
            if (Math.Abs(src) > 1e-9 || Math.Abs(lam1 - srr) > Math.Abs(lam1 - scc))
            {
                dr = src;
                dc = lam1 - srr;
            }
            else
            {
                dr = lam1 - scc;
                dc = src;
            }

            double len = Math.Sqrt(dr * dr + dc * dc);
            if (len < 1e-9)
            {
                dr = points[^1].row - points[0].row;
                dc = points[^1].col - points[0].col;
                len = Math.Sqrt(dr * dr + dc * dc);
            }
            if (len < 1e-9)
            {
                dr = 1;
                dc = 0;
                len = 1;
            }
            dr /= len;
            dc /= len;
            r0 = mr;
            c0 = mc;
            return true;
        }

        private static void ProjectOntoImageLine(
            double row, double col,
            double r0, double c0, double dr, double dc,
            out double projRow, out double projCol)
        {
            double t = (row - r0) * dr + (col - c0) * dc;
            projRow = r0 + t * dr;
            projCol = c0 + t * dc;
        }

        private static double PerpendicularDistanceToImageLine(
            double row, double col,
            double r0, double c0, double dr, double dc)
        {
            double t = (row - r0) * dr + (col - c0) * dc;
            double pr = r0 + t * dr;
            double pc = c0 + t * dc;
            return Math.Sqrt((row - pr) * (row - pr) + (col - pc) * (col - pc));
        }

        private static int[] ApplyTwoColumnStripPicks(
            int[] col0Pick,
            double[] rows,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            int effRows,
            RowColumnLattice lattice,
            int chainWindow,
            Dictionary<(int ir, int ic), int> cellBest,
            double latticeAngleDeg,
            string? tag)
        {
            double chainDirectionDeg = !double.IsNaN(latticeAngleDeg)
                ? latticeAngleDeg
                : col0Pick.Length >= 2
                    ? EstimateChainDirectionAngleDeg(cols, rows, col0Pick)
                    : double.NaN;
            if (double.IsNaN(chainDirectionDeg))
                chainDirectionDeg = 90.0;

            ApplyForcedChainColumnPicks(col0Pick, rows, cols, v, effRows, 0, cellBest, forbidIndices: null, chainDirectionDeg, tag);
            HashSet<int> col0Reserved = BuildColumn0ReservedIndices(col0Pick, cellBest);

            int col1Filled = 0;
            if (ReclusterSecondColumnAfterExclude(rows, cols, u, v, scores, n, col0Pick, col0Reserved, effRows, lattice, tag)
                is SecondColumnClusterResult cluster)
            {
                col1Filled = FillSecondColumnByChain(
                    rows, cols, u, v, scores, effRows, chainWindow, col0Pick, cluster, col0Reserved, lattice, chainDirectionDeg, cellBest, tag);
            }

            if (col1Filled < effRows)
            {
                int w = Math.Min(chainWindow, effRows);
                int[] col1Chain = SelectSecondColumnChainAtUv(u, v, scores, n, col0Reserved, w, tag);
                if (col1Chain.Length > 0)
                    ApplyForcedChainColumnPicks(col1Chain, rows, cols, v, effRows, 1, cellBest, col0Reserved, chainDirectionDeg, tag);
            }

            return Enumerable.Range(0, effRows)
                .Where(ir => cellBest.TryGetValue((ir, 1), out _))
                .Select(ir => cellBest[(ir, 1)])
                .ToArray();
        }

        /// <summary>由 N 个匹配点估计排布链在图像中的方向角（列→行，°）。</summary>
        private static double EstimateChainDirectionAngleDeg(double[] cols, double[] rows, int[] indices)
        {
            if (indices == null || indices.Length < 2)
                return double.NaN;

            double meanC = 0, meanR = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= cols.Length || i >= rows.Length)
                    continue;
                meanC += cols[i];
                meanR += rows[i];
            }
            meanC /= indices.Length;
            meanR /= indices.Length;

            double scc = 0, scr = 0, srr = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= cols.Length || i >= rows.Length)
                    continue;
                double dc = cols[i] - meanC;
                double dr = rows[i] - meanR;
                scc += dc * dc;
                scr += dc * dr;
                srr += dr * dr;
            }

            double theta = 0.5 * Math.Atan2(2.0 * scr, scc - srr);
            return theta * 180.0 / Math.PI;
        }

        private static int[] SelectIndicesForAngleConsensus(
            double[] cols,
            double[] rows,
            double[]? scores,
            int inputCount,
            int gridRows,
            int gridCols,
            string? tag) =>
            SelectIndicesAlongFirstLatticeLine(cols, rows, scores, inputCount, gridRows, gridCols, tag);

        /// <summary>仅用阵列期望数量的高分匹配计算模板角共识，排除多余误检。</summary>
        private static (double consensusDeg, double concentration, int[] pickedIndices) ComputeConsensusMatchAngle(
            double[] anglesDeg,
            double[]? scores,
            double[] cols,
            double[] rows,
            int inputCount,
            int gridRows,
            int gridCols,
            string? tag)
        {
            int expectK = ExpectedConsensusMatchCount(gridRows, gridCols);
            int[] pick = SelectIndicesForAngleConsensus(cols, rows, scores, inputCount, gridRows, gridCols, tag);
            if (pick.Length == 0)
                return (double.NaN, 0, Array.Empty<int>());

            double mean = WeightedCircularMeanDegrees(anglesDeg, scores, pick);
            double conc = MatchAngleConcentrationOnIndices(anglesDeg, pick);

            if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string scoreSpan = "";
                if (scores != null && pick.Length > 0)
                {
                    double lo = pick.Min(i => i < scores.Length ? scores[i] : 0);
                    double hi = pick.Max(i => i < scores.Length ? scores[i] : 0);
                    scoreSpan = $", Score∈[{lo:F3},{hi:F3}]";
                }
                string mode = "格点筛选";
                string angleList = string.Join(", ", pick.Select(i => i < anglesDeg.Length ? anglesDeg[i].ToString("F2", CultureInfo.InvariantCulture) : "?"));
                double chainDeg = EstimateChainDirectionAngleDeg(cols, rows, pick);
                string chainNote = double.IsNaN(chainDeg) ? "" : $", 链向≈{chainDeg:F1}°";
                Diag(tag, $"模板角共识({mode}): {pick.Length}/{inputCount} 个 (目标 {expectK}, 阵列 {gridRows}×{gridCols}){scoreSpan}, Angle=[{angleList}], 模板角≈{mean:F1}°, 浓度={conc:F3}{chainNote}");
            }

            return (mean, conc, pick);
        }

        private static double WeightedCircularMeanDegrees(double[] anglesDeg, double[]? scores, int count)
        {
            return WeightedCircularMeanDegrees(anglesDeg, scores, Enumerable.Range(0, Math.Min(count, anglesDeg.Length)).ToArray());
        }

        private static double WeightedCircularMeanDegrees(double[] anglesDeg, double[]? scores, int[] indices)
        {
            double sumSin = 0, sumCos = 0, wSum = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= anglesDeg.Length)
                    continue;
                double w = scores != null && i < scores.Length ? Math.Max(0.01, scores[i]) : 1;
                double rad = anglesDeg[i] * Math.PI / 180.0;
                sumSin += w * Math.Sin(rad);
                sumCos += w * Math.Cos(rad);
                wSum += w;
            }
            if (wSum < 1e-9)
                return 0;
            return Math.Atan2(sumSin / wSum, sumCos / wSum) * 180.0 / Math.PI;
        }



        private static AlignmentMetrics EvaluateLatticeAlignment(

            double[] cols, double[] rows, double angleRad, int effRows, int effCols, int userRows, int userCols)

        {

            int n = cols.Length;

            if (n == 0 || effRows < 1 || effCols < 1)

                return default;



            ProjectToUv(cols, rows, angleRad, out var u, out var v);

            double pitchU = EstimatePitchFromProjections(u, effCols);

            double pitchV = EstimatePitchFromProjections(v, effRows);

            if (pitchU < 1e-3 || pitchV < 1e-3)

                return default;



            var lattice = FitColumnFirstLattice(u, v, effRows, effCols, pitchU, pitchV);

            ComputeLatticeSnapTolerance(lattice, 0, out double snapU, out double snapV);



            int snapped = 0;

            var cells = new HashSet<(int, int)>();

            for (int i = 0; i < n; i++)

            {

                if (!TryFindBestSnapCell(u[i], v[i], lattice, snapU, snapV, out int ir, out int ic, out _))

                    continue;

                snapped++;

                cells.Add((ir, ic));

            }



            double spanU = lattice.MeanPitchU * Math.Max(0, effCols - 1);

            double spanV = lattice.PitchV * Math.Max(0, effRows - 1);

            bool uIsMajor = spanU >= spanV;

            bool colsAreMajor = effCols >= effRows;



            return new AlignmentMetrics

            {

                SnappedCount = snapped,

                UniqueCells = cells.Count,

                MatchesUserGridDims = effRows == userRows && effCols == userCols,

                AspectBonus = uIsMajor == colsAreMajor ? 1 : 0

            };

        }



        private static double MatchAngleConcentration(double[] anglesDeg) =>
            MatchAngleConcentrationOnIndices(anglesDeg, Enumerable.Range(0, anglesDeg.Length).ToArray());

        private static double MatchAngleConcentrationOnIndices(double[] anglesDeg, int[] indices)
        {
            if (indices.Length == 0)
                return 0;
            double sumSin = 0, sumCos = 0;
            int used = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= anglesDeg.Length)
                    continue;
                double rad = anglesDeg[i] * Math.PI / 180.0;
                sumSin += Math.Sin(rad);
                sumCos += Math.Cos(rad);
                used++;
            }
            if (used == 0)
                return 0;
            return Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / used;
        }
        private static double EstimatePrincipalAngleRad(double[] cols, double[] rows)

        {

            int n = cols.Length;

            double mx = cols.Average();

            double my = rows.Average();

            double sxx = 0, syy = 0, sxy = 0;

            for (int i = 0; i < n; i++)

            {

                double dx = cols[i] - mx;

                double dy = rows[i] - my;

                sxx += dx * dx;

                syy += dy * dy;

                sxy += dx * dy;

            }



            if (sxx + syy < 1e-6) return 0;

            return 0.5 * Math.Atan2(2 * sxy, sxx - syy);

        }



        private static double EstimatePitchFromProjections(double[] proj, int expectedCells)

        {

            if (proj.Length < 2) return 0;

            var sorted = proj.OrderBy(x => x).ToArray();

            var diffs = new List<double>();

            for (int i = 1; i < sorted.Length; i++)

            {

                double d = sorted[i] - sorted[i - 1];

                if (d > 1e-3) diffs.Add(d);

            }



            if (diffs.Count == 0) return 0;

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



        private static double[] KMeans1D(double[] values, int k)

        {

            k = Math.Max(1, Math.Min(k, values.Length));

            var sorted = values.OrderBy(x => x).ToArray();

            var centers = new double[k];

            for (int i = 0; i < k; i++)

            {

                double q = (i + 0.5) / k;

                int idx = (int)Math.Clamp(Math.Round(q * sorted.Length - 0.5), 0, sorted.Length - 1);

                centers[i] = sorted[idx];

            }



            for (int iter = 0; iter < 30; iter++)

            {

                var sums = new double[k];

                var counts = new int[k];

                foreach (double val in values)

                {

                    int b = NearestIndex1D(centers, val);

                    sums[b] += val;

                    counts[b]++;

                }



                bool moved = false;

                for (int j = 0; j < k; j++)

                {

                    if (counts[j] == 0) continue;

                    double next = sums[j] / counts[j];

                    if (Math.Abs(next - centers[j]) > 1e-4) moved = true;

                    centers[j] = next;

                }



                if (!moved) break;

            }



            Array.Sort(centers);

            return centers;

        }



        private static int NearestIndex1D(double[] centers, double value)

        {

            int best = 0;

            double dMin = Math.Abs(value - centers[0]);

            for (int k = 1; k < centers.Length; k++)

            {

                double d = Math.Abs(value - centers[k]);

                if (d < dMin)

                {

                    dMin = d;

                    best = k;

                }

            }



            return best;

        }

        internal static double StripChainAngleFromIndices(double[] cols, double[] rows, int[] indices) =>
            indices.Length >= 2 ? EstimateChainDirectionAngleDeg(cols, rows, indices) : double.NaN;

        internal static HalconLatticeStripBootstrapResult StripLatticeBootstrap(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "StripBoot" : diagnosticTag;
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return new HalconLatticeStripBootstrapResult { InputCount = 0 };

            double[] r = rows!;
            double[] c = cols!;
            int kBoot = ConsensusChainWindowSize(gridRows, gridCols);
            int[] bootIdx = SelectTopChainIndicesInStripColumn(c, scores, n, gridRows, gridCols, r, tag);
            if (bootIdx.Length < 2)
                bootIdx = Enumerable.Range(0, Math.Min(n, Math.Max(2, kBoot))).ToArray();

            double pcaDeg = EstimatePrincipalAngleRad(c, r) * 180.0 / Math.PI;

            double consensusDeg = double.NaN;
            double matchConc = 0;
            if (angles != null && angles.Length >= n && bootIdx.Length > 0)
            {
                consensusDeg = WeightedCircularMeanDegrees(angles, scores, bootIdx);
                matchConc = MatchAngleConcentrationOnIndices(angles, bootIdx);
            }

            double latticeDeg = double.NaN;
            double vAxisDeg = double.NaN;
            if (ShouldUseStripUvGeometricOrientation(gridRows, gridCols))
            {
                var orient = ResolveStripUvOrientation(c, r, n, gridRows, gridCols, scores, pcaDeg, consensusDeg, tag);
                latticeDeg = orient.AngleRad * 180.0 / Math.PI;
                vAxisDeg = VAxisImageAngleColToRowDeg(latticeDeg);
            }
            else if (!double.IsNaN(pcaDeg))
            {
                latticeDeg = pcaDeg;
                vAxisDeg = VAxisImageAngleColToRowDeg(latticeDeg);
            }

            DiagLattice(tag,
                $"引导: K={bootIdx.Length}, 格网u轴θ≈{latticeDeg:F1}°, v≈{vAxisDeg:F1}°, PCA链向≈{pcaDeg:F1}°, HALCON模板≈{consensusDeg:F1}° 浓度={matchConc:F3}");
            return new HalconLatticeStripBootstrapResult
            {
                InputCount = n,
                BootstrapPickIndices = bootIdx,
                LatticeAngleDeg = latticeDeg,
                VAxisImageAngleDeg = vAxisDeg,
                PcaAxisAngleDeg = pcaDeg,
                ConsensusMatchAngleDeg = consensusDeg,
                MatchConcentration = matchConc
            };
        }

        internal static HalconLatticeStripContext? StripLatticeBuildOrient(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            HalconLatticeStripBootstrapResult? bootstrap,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "StripOrient" : diagnosticTag;
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return null;

            double[] r = rows!;
            double[] c = cols!;
            var boot = bootstrap ?? StripLatticeBootstrap(r, c, angles, scores, gridRows, gridCols, tag);
            if (boot.InputCount != n)
                boot = StripLatticeBootstrap(r, c, angles, scores, gridRows, gridCols, tag);

            double pcaDeg = boot.PcaAxisAngleDeg;
            if (double.IsNaN(pcaDeg))
                pcaDeg = EstimatePrincipalAngleRad(c, r) * 180.0 / Math.PI;
            double consensusPre = boot.ConsensusMatchAngleDeg;

            OrientationChoice orientation;
            if (ShouldUseStripUvGeometricOrientation(gridRows, gridCols))
            {
                if (!double.IsNaN(boot.LatticeAngleDeg))
                {
                    double thetaU = NormalizeAngleDeg(boot.LatticeAngleDeg);
                    orientation = new OrientationChoice { AngleRad = thetaU * Math.PI / 180.0, SwapUv = false };
                    DiagLattice(tag, $"定向: 沿用引导 u轴 θ≈{thetaU:F1}°, v≈{VAxisImageAngleColToRowDeg(thetaU):F1}°");
                }
                else
                    orientation = ResolveStripUvOrientation(c, r, n, gridRows, gridCols, scores, boot.PcaAxisAngleDeg, consensusPre, tag);
            }
            else if (!double.IsNaN(boot.PcaAxisAngleDeg))
                orientation = new OrientationChoice { AngleRad = boot.PcaAxisAngleDeg * Math.PI / 180.0, SwapUv = false };
            else
                orientation = ResolveOrientationFromPcaAxis(c, r, gridRows, gridCols, pcaDeg, consensusPre, tag);

            double angleRad = orientation.AngleRad;
            bool swapUv = orientation.SwapUv;
            int effRows = swapUv ? gridCols : gridRows;
            int effCols = swapUv ? gridRows : gridCols;
            double latticeAngleDeg = angleRad * 180.0 / Math.PI;

            ProjectToUv(c, r, angleRad, out var u, out var v);
            double pitchU = EstimatePitchFromProjections(u, effCols);
            double pitchV = EstimatePitchFromProjections(v, effRows);
            if (pitchU < 1e-3 || pitchV < 1e-3)
            {
                DiagLattice(tag, "格距估计失败，无法落格");
                return null;
            }

            var lattice = FitColumnFirstLattice(u, v, effRows, effCols, pitchU, pitchV, scores);
            int chainWindow = ConsensusChainWindowSize(gridRows, gridCols);
            DiagLattice(tag, $"定向: θ≈{latticeAngleDeg:F1}° swap={swapUv}, 物理 {effRows}×{effCols}, pitchU={pitchU:F1}, pitchV={pitchV:F1}");

            return new HalconLatticeStripContext
            {
                InputCount = n,
                UserGridRows = gridRows,
                UserGridCols = gridCols,
                EffRows = effRows,
                EffCols = effCols,
                AxesSwapped = swapUv,
                LatticeAngleDeg = latticeAngleDeg,
                PcaAxisDeg = pcaDeg,
                AngleRad = angleRad,
                PitchU = pitchU,
                PitchV = pitchV,
                ChainWindow = chainWindow,
                U = u,
                V = v,
                Lattice = lattice,
                BootstrapPickIndices = boot.BootstrapPickIndices,
                DiagnosticTag = tag
            };
        }

        internal static HalconLatticeStripContext StripLatticePickColumn0(
            HalconLatticeStripContext context,
            double[] rows,
            double[] cols,
            double[]? scores,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? context.DiagnosticTag ?? "StripCol0" : diagnosticTag;
            int n = context.InputCount;
            double[] r = rows!;
            double[] c = cols!;
            var u = context.U;
            var v = context.V;
            var lattice = context.Lattice;
            int effRows = context.EffRows;
            int effCols = context.EffCols;
            int chainWindow = context.ChainWindow;
            int gridRows = context.UserGridRows;
            int gridCols = context.UserGridCols;

            double chainDegForPick = context.LatticeAngleDeg;
            if (double.IsNaN(chainDegForPick))
                chainDegForPick = context.PcaAxisDeg;
            int wPick = Math.Min(chainWindow, effRows);
            int[] linePick = Array.Empty<int>();
            if (chainWindow > 0 && chainWindow < ExpectedConsensusMatchCount(gridRows, gridCols))
            {
                linePick = effCols == 2
                    ? SelectColumn0ChainPick(r, c, u, v, scores, n, wPick, chainDegForPick, tag)
                    : SelectChainIndicesFromPool(
                        Enumerable.Range(0, n).ToArray(), u, v, scores, r, c, wPick, 0.88, chainDegForPick,
                        useImageColCrossSpan: false, tag);
            }

            double snapU = 0, snapV = 0;
            if (linePick.Length > 0)
            {
                lattice = RefineLatticeFromConsensusPick(lattice, u, v, scores, linePick, tag);
                ComputeLatticeSnapTolerance(lattice, 0, out snapU, out snapV);
                ProjectToUv(c, r, context.AngleRad, out var uNew, out var vNew);
                u = uNew;
                v = vNew;
            }

            double chainDir = chainDegForPick;
            if (linePick.Length >= 2 && HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                double geom = EstimateChainDirectionAngleDeg(c, r, linePick);
                if (AngleDistanceDeg(geom, chainDir) > 2.0)
                    DiagLattice(tag, $"列0链向 N={linePick.Length}: 格网θ≈{chainDir:F1}° (列0八点几何≈{geom:F1}°)");
                else
                    DiagLattice(tag, $"列0链向 N={linePick.Length}, 格网θ≈{chainDir:F1}°");
            }
            else
                DiagLattice(tag, $"列0链向 N={linePick.Length}, 格网θ≈{chainDir:F1}°");

            return context.WithColumn0(linePick, lattice, snapU, snapV, u, v);
        }

        internal static LatticePickResult StripLatticeFill(
            HalconLatticeStripContext context,
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            double minScoreKeep,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? context.DiagnosticTag ?? "StripFill" : diagnosticTag;
            int n = context.InputCount;
            double[] r = rows!;
            double[] c = cols!;
            int effRows = context.EffRows;
            int effCols = context.EffCols;
            bool swapUv = context.AxesSwapped;
            double latticeAngleDeg = context.LatticeAngleDeg;
            int[] linePick = context.Column0PickIndices;
            var lattice = context.Lattice;
            ProjectToUv(c, r, context.AngleRad, out var u, out var v);
            int gridRows = context.UserGridRows;
            int gridCols = context.UserGridCols;
            int targetCells = gridRows * gridCols;
            int chainWindow = context.ChainWindow;

            double snapU = context.SnapU;
            double snapV = context.SnapV;
            if (linePick.Length > 0 && (snapU < 1e-6 || snapV < 1e-6))
                ComputeLatticeSnapTolerance(lattice, 0, out snapU, out snapV);

            var perCell = BuildLatticeCandidates(u, v, scores, n, lattice, snapU, snapV, 0, out int rejectedSnap, out int rescued);
            DiagLattice(tag, $"落格: {perCell.Count}/{targetCells} 格有候选, 拒绝={rejectedSnap}, snap放宽={rescued}");

            var cellBest = SelectOnePerCellLatticeFirst(perCell, tag);
            int[] col1ChainPick = Array.Empty<int>();
            if (linePick.Length > 0 && effCols == 2)
                col1ChainPick = ApplyTwoColumnStripPicks(linePick, r, c, u, v, scores, n, effRows, lattice, chainWindow, cellBest, latticeAngleDeg, tag);

            List<int> kept = effCols == 2
                ? CollectStripCellIndices(cellBest, effRows, effCols)
                : new List<int>(cellBest.Values);
            var indexToCell = BuildIndexToCellMap(cellBest);
            kept.Sort((a, b) =>
            {
                if (!indexToCell.TryGetValue(a, out var ka))
                    return 1;
                if (!indexToCell.TryGetValue(b, out var kb))
                    return -1;
                int c0 = ka.ir.CompareTo(kb.ir);
                return c0 != 0 ? c0 : ka.ic.CompareTo(kb.ic);
            });

            int[] pickIndices = kept.ToArray();

            // 列1 已由链向/按行对齐选取；|Δu| 同行 prune 在 θ≈-57° 时会误删全部 8 个列1（日志：剔除 8 个）
            if (pickIndices.Length >= 2 && effCols >= 1)
                pickIndices = PrunePickIndicesByColumnCollinearitySafe(u, v, pickIndices, indexToCell, lattice, snapU, snapV, tag);

            double consensusDeg = double.NaN;
            double matchConc = 0;
            if (angles != null && pickIndices.Length > 0)
            {
                consensusDeg = WeightedCircularMeanDegrees(angles, scores, pickIndices);
                matchConc = MatchAngleConcentrationOnIndices(angles, pickIndices);
            }

            double chainDirectionDeg = latticeAngleDeg;

            int m = pickIndices.Length;
            var outR = new double[m];
            var outC = new double[m];
            var outA = new double[m];
            var outS = new double[m];
            var outGr = new int[m];
            var outGc = new int[m];
            for (int k = 0; k < m; k++)
            {
                int i = pickIndices[k];
                outR[k] = r[i];
                outC[k] = c[i];
                outA[k] = angles != null && i < angles.Length ? angles[i] : 0;
                outS[k] = scores != null && i < scores.Length ? scores[i] : 0;
                if (indexToCell.TryGetValue(i, out var cell))
                {
                    outGr[k] = cell.ir;
                    outGc[k] = cell.ic;
                }
                else
                {
                    outGr[k] = -1;
                    outGc[k] = -1;
                }
            }

            TwoColumnDeltaUvReport stripDelta = default;
            if (effCols == 2)
            {
                stripDelta = LogTwoColumnDeltaUvReport(tag, r, c, u, v, scores, n, lattice, latticeAngleDeg, cellBest);
                LogTwoColumnStripDetail(tag, r, c, scores, angles, u, v, n, effRows, effCols, lattice, swapUv,
                    latticeAngleDeg, linePick, col1ChainPick, cellBest, pickIndices, outGr, outGc, perCell);
            }
            else if (HalconShapeMatchGridDiagnostics.IsEnabled)
            {
                string col0 = string.Join(",", Enumerable.Range(0, m).Where(k => outGc[k] == 0).Select(k => pickIndices[k]));
                string col1 = string.Join(",", Enumerable.Range(0, m).Where(k => outGc[k] == 1).Select(k => pickIndices[k]));
                int nCol0 = Enumerable.Range(0, m).Count(k => outGc[k] == 0);
                int nCol1 = Enumerable.Range(0, m).Count(k => outGc[k] == 1);
                Diag(tag, $"输出 {m}/{targetCells} (列0={nCol0}, 列1={nCol1}), θ≈{latticeAngleDeg:F1}°, 列0=[{col0}], 列1=[{col1}]");
            }

            return new LatticePickResult
            {
                InputCount = n,
                Rows = outR,
                Cols = outC,
                Angles = outA,
                Scores = outS,
                GridRow = outGr,
                GridCol = outGc,
                KeptIndices = pickIndices,
                ChainPickIndices = linePick.Length > 0 ? linePick : pickIndices,
                LatticeRows = effRows,
                LatticeCols = effCols,
                AxesSwapped = swapUv,
                LatticeAngleDeg = latticeAngleDeg,
                ChainDirectionAngleDeg = chainDirectionDeg,
                ConsensusMatchAngleDeg = consensusDeg,
                MatchConcentration = matchConc,
                PitchRow = lattice.PitchV,
                PitchCol = lattice.MeanPitchU,
                TwoColumnDeltaU = stripDelta.DeltaU,
                TwoColumnDeltaV = stripDelta.DeltaV,
                TwoColumnDeltaSummary = stripDelta.Summary
            };
        }

        /// <summary>2 列×N 行格点筛选：链向定向 + 每格最高分，输出最多 rows×cols 个匹配（如 16）。</summary>
        internal sealed class LatticePickResult
        {
            public int InputCount { get; init; }
            public double[] Rows { get; init; } = Array.Empty<double>();
            public double[] Cols { get; init; } = Array.Empty<double>();
            public double[] Angles { get; init; } = Array.Empty<double>();
            public double[] Scores { get; init; } = Array.Empty<double>();
            public int[] GridRow { get; init; } = Array.Empty<int>();
            public int[] GridCol { get; init; } = Array.Empty<int>();
            public int[] KeptIndices { get; init; } = Array.Empty<int>();
            /// <summary>沿链向一列/行的 N 连索引（如 8），用于显示/链向；与整板 KeptIndices 不同。</summary>
            public int[] ChainPickIndices { get; init; } = Array.Empty<int>();
            public int LatticeRows { get; init; }
            public int LatticeCols { get; init; }
            public bool AxesSwapped { get; init; }
            public double LatticeAngleDeg { get; init; }
            public double ChainDirectionAngleDeg { get; init; } = double.NaN;
            public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
            public double MatchConcentration { get; init; }
            public double PitchRow { get; init; }
            public double PitchCol { get; init; }
            /// <summary>双列格网列心间距 ColU[1]-ColU[0]（px）。</summary>
            public double TwoColumnDeltaU { get; init; } = double.NaN;
            /// <summary>双列 v 池中位差 median(v|列1)-median(v|列0)（px）。</summary>
            public double TwoColumnDeltaV { get; init; } = double.NaN;
            public string TwoColumnDeltaSummary { get; init; } = "";
        }

        private static string FormatDeg(double deg) =>
            double.IsNaN(deg) ? "—" : $"{deg:F1}°";

        /// <summary>引导后：用格网 u 轴 θ 建 u/v；全板按同格分数最高落满 rows×cols（如 26→16）。</summary>
        internal static LatticePickResult PickLatticeGridInUv(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double latticeAngleHintDeg,
            int[]? bootstrapPickIndices,
            double minScoreKeep,
            double snapTolerancePx,
            string? diagnosticTag,
            string? uvProjectionSvgPath = null,
            double pcaChainAngleDeg = double.NaN)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "UvGridPick" : diagnosticTag;
            string? svgOut = !string.IsNullOrWhiteSpace(uvProjectionSvgPath)
                ? uvProjectionSvgPath
                : Environment.GetEnvironmentVariable("XV_GRID_FILTER_SVG");
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return new LatticePickResult { InputCount = 0 };

            double[] r = rows!;
            double[] c = cols!;
            int targetCells = gridRows * gridCols;

            double consensusPre = double.NaN;
            double matchConc = 0;
            int[] refinePick = bootstrapPickIndices ?? Array.Empty<int>();
            if (angles != null && angles.Length >= n)
            {
                if (refinePick.Length == 0)
                {
                    (consensusPre, matchConc, refinePick) =
                        ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);
                }
                else
                {
                    consensusPre = WeightedCircularMeanDegrees(angles, scores, refinePick);
                    matchConc = MatchAngleConcentrationOnIndices(angles, refinePick);
                }
            }

            double pcaChainDeg = !double.IsNaN(pcaChainAngleDeg)
                ? pcaChainAngleDeg
                : EstimatePrincipalAngleRad(c, r) * 180.0 / Math.PI;

            DiagLattice(tag,
                $"u/v 落格 {gridRows}×{gridCols}（目标 {targetCells}）, 输入 {n}, 引导θ={FormatDeg(latticeAngleHintDeg)}, PCA链向≈{pcaChainDeg:F1}°");

            OrientationChoice orientation;
            if (ShouldUseStripUvGeometricOrientation(gridRows, gridCols))
            {
                if (!double.IsNaN(latticeAngleHintDeg))
                {
                    double thetaU = NormalizeAngleDeg(latticeAngleHintDeg);
                    orientation = new OrientationChoice { AngleRad = thetaU * Math.PI / 180.0, SwapUv = false };
                    DiagLattice(tag, $"定向: 沿用引导 u轴 θ≈{thetaU:F1}°, v≈{VAxisImageAngleColToRowDeg(thetaU):F1}°");
                }
                else
                    orientation = ResolveStripUvOrientation(c, r, n, gridRows, gridCols, scores, pcaChainDeg, consensusPre, tag);
            }
            else if (!double.IsNaN(latticeAngleHintDeg))
                orientation = new OrientationChoice { AngleRad = latticeAngleHintDeg * Math.PI / 180.0, SwapUv = false };
            else
                orientation = ResolveOrientationFromPcaAxis(c, r, gridRows, gridCols, pcaChainDeg, consensusPre, tag);

            double angleRad = orientation.AngleRad;
            bool swapUv = orientation.SwapUv;
            int effRows = swapUv ? gridCols : gridRows;
            int effCols = swapUv ? gridRows : gridCols;
            double latticeAngleDeg = angleRad * 180.0 / Math.PI;

            ProjectToUv(c, r, angleRad, out var u, out var v);
            double chainDirectionDeg = VAxisImageAngleColToRowDeg(latticeAngleDeg);
            int[] angleIndices = refinePick.Length >= 2 ? refinePick : Array.Empty<int>();

            RowColumnLattice lattice;
            if (effCols == 2 && angleIndices.Length >= 2)
            {
                int[] col0Idx = angleIndices;
                int[] col1Idx = Array.Empty<int>();
                if (TryPartitionTwoImageColumns(c, angleIndices, out int[] lowPick, out int[] highPick, out _, out _)
                    && lowPick.Length >= 2 && highPick.Length >= 2)
                {
                    col0Idx = lowPick;
                    col1Idx = highPick;
                }
                else if (TryPartitionTwoImageColumns(c, Enumerable.Range(0, n).ToArray(), out _, out int[] highPool, out _, out _))
                {
                    int k = ConsensusChainWindowSize(gridRows, gridCols);
                    col1Idx = SelectStripChainIndicesByClusterFit(c, r, scores, highPool, k,
                        string.IsNullOrEmpty(tag) ? null : $"{tag}/Col1Pitch");
                }

                lattice = BuildStripLatticeFromChainClusterPoints(
                    col0Idx, col1Idx, c, r, u, v, scores, effRows, effCols, chainDirectionDeg, tag);
            }
            else
            {
                double pitchU = angleIndices.Length >= 2
                    ? EstimatePitchFromProjections(angleIndices.Select(i => u[i]).ToArray(), effCols)
                    : EstimatePitchFromProjections(u, effCols);
                double pitchV = angleIndices.Length >= 2
                    ? EstimatePitchFromProjections(angleIndices.Select(i => v[i]).ToArray(), effRows)
                    : EstimatePitchFromProjections(v, effRows);
                if (pitchU < 1e-3 || pitchV < 1e-3)
                {
                    DiagLattice(tag, "格距估计失败");
                    return new LatticePickResult { InputCount = n, LatticeRows = effRows, LatticeCols = effCols };
                }

                if (angleIndices.Length >= 2)
                {
                    lattice = BuildStripLatticeFromChainClusterPoints(
                        angleIndices, Array.Empty<int>(), c, r, u, v, scores, effRows, effCols, chainDirectionDeg, tag);
                }
                else
                {
                    lattice = FitColumnFirstLattice(u, v, effRows, effCols, pitchU, pitchV, scores);
                    if (refinePick.Length >= 2)
                        lattice = RefineLatticeFromConsensusPick(lattice, u, v, scores, refinePick, tag);
                    else
                    {
                        lattice = RefineRowCentersFromTopMatches(lattice, u, v, scores);
                        lattice = EnforceUniformColumnsPreserveRows(lattice);
                    }
                }
            }

            if (lattice.PitchU < 1e-3 || lattice.PitchV < 1e-3)
            {
                DiagLattice(tag, "格距估计失败");
                return new LatticePickResult { InputCount = n, LatticeRows = effRows, LatticeCols = effCols };
            }

            ComputeLatticeSnapTolerance(lattice, snapTolerancePx, out double snapU, out double snapV);
            if (effCols == 2)
            {
                snapU = Math.Max(snapU, lattice.MeanPitchU * 0.52);
                snapV = Math.Max(snapV, lattice.PitchV * 0.42);
            }

            DiagLattice(tag, $"格网 θ≈{latticeAngleDeg:F1}° swap={swapUv}, pitchU×pitchV={lattice.PitchU:F0}×{lattice.PitchV:F0}, snapU={snapU:F1}, snapV={snapV:F1}");

            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell;
            int rejectedSnap = 0;
            int rescuedHigh = 0;
            double col0Img = double.NaN;
            double col1Img = double.NaN;
            double imgMargin = Math.Max(40, lattice.MeanPitchU * 0.12);
            bool hasTwoImgCols = effCols == 2 &&
                TryPartitionTwoImageColumns(c, Enumerable.Range(0, n).ToArray(), out _, out _, out col0Img, out col1Img);

            if (hasTwoImgCols)
            {
                perCell = BuildLatticeCandidatesTwoColumn(
                    c, u, v, scores, n, lattice, snapU, snapV, 0,
                    col0Img, col1Img, imgMargin, out rejectedSnap, out rescuedHigh);
                int addedSnap = AddNearestColumnCandidatesForUnassigned(
                    perCell, c, u, v, scores, n, lattice, col0Img, col1Img, imgMargin, 0, tag);
                DiagLattice(tag,
                    $"落格(双列·snap): {perCell.Count}/{targetCells} 格, snap拒绝={rejectedSnap}, snap放宽={rescuedHigh}, 列内补候选={addedSnap}");
            }
            else
            {
                perCell = BuildLatticeCandidates(u, v, scores, n, lattice, snapU, snapV, 0,
                    out rejectedSnap, out rescuedHigh);
                DiagLattice(tag,
                    $"落格(snap): {perCell.Count}/{targetCells} 格, snap拒绝={rejectedSnap}, snap放宽={rescuedHigh}");
            }

            LogCandidateDisposition(tag, r, c, scores, u, v, n, lattice, snapU, snapV, perCell);

            var cellBest = SelectOnePerCellLatticeFirst(perCell, tag, distanceFirst: hasTwoImgCols);
            if (cellBest.Count < targetCells)
            {
                if (hasTwoImgCols)
                    FillEmptyLatticeCellsScoreDistance(cellBest, c, u, v, scores, n, effRows, effCols, lattice,
                        col0Img, col1Img, imgMargin, tag);
                else
                    FillEmptyLatticeCellsGlobal(cellBest, u, v, scores, n, effRows, effCols, lattice, tag);
            }

            double col0Bump = double.NaN;
            double col1Bump = double.NaN;
            if (hasTwoImgCols)
                BumpUnassignedHighScoresToNearestCell(cellBest, u, v, scores, n, lattice, 0, tag, c, col0Img, col1Img);
            else
                BumpUnassignedHighScoresToNearestCell(cellBest, u, v, scores, n, lattice, 0, tag);

            if (hasTwoImgCols)
            {
                int pruned = PruneOutliersFromStripCellBest(
                    cellBest, c, u, v, lattice, col0Img, col1Img, snapU, snapV, tag);
                if (pruned > 0)
                    FillEmptyLatticeCellsScoreDistance(cellBest, c, u, v, scores, n, effRows, effCols, lattice,
                        col0Img, col1Img, imgMargin, tag);
            }

            DiagLattice(tag,
                hasTwoImgCols
                    ? $"每格选取: 列内 snap + 距格心优先, 命中 {cellBest.Count}/{targetCells} 格 (列间隙/离列远不占格)"
                    : $"每格选取: snap 候选内分数优先, 命中 {cellBest.Count}/{targetCells} 格 (不按分筛选)");

            var kept = new List<int>(targetCells);
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.TryGetValue((ir, ic), out int idx))
                        kept.Add(idx);
                }
            }

            LogUvGridPickRejectionReasons(tag, r, c, scores, u, v, n, lattice, snapU, snapV, perCell, cellBest, kept,
                col0Img, col1Img, imgMargin, hasTwoImgCols);

            double consensusDeg = consensusPre;
            if (angles != null && kept.Count > 0 && double.IsNaN(consensusDeg))
                consensusDeg = WeightedCircularMeanDegrees(angles, scores, kept.ToArray());

            TwoColumnDeltaUvReport colDelta = default;
            if (effCols == 2)
                colDelta = LogTwoColumnDeltaUvReport(tag, r, c, u, v, scores, n, lattice, latticeAngleDeg, cellBest);
            else if (!string.IsNullOrEmpty(tag) && HalconShapeMatchGridDiagnostics.IsEnabled)
                LogFindPointsUvProjection(tag, r, c, u, v, scores, n, lattice, latticeAngleDeg, cellBest);

            if (!string.IsNullOrWhiteSpace(svgOut))
            {
                try
                {
                    HalconShapeMatchUvProjectionSvg.Write(
                        svgOut, tag, r, c, scores, u, v, lattice, effRows, effCols, cellBest, kept,
                        latticeAngleDeg, VAxisImageAngleColToRowDeg(latticeAngleDeg),
                        pcaChainDeg,
                        colDelta.DeltaU, colDelta.DeltaV, colDelta.Summary);
                    DiagLattice(tag, $"u/v 投影图已写入: {svgOut}");
                }
                catch (Exception ex)
                {
                    DiagLattice(tag, $"u/v 投影图写入失败: {ex.Message}");
                }
            }

            double vAxisDeg = VAxisImageAngleColToRowDeg(latticeAngleDeg);
            return PackLatticePickFromIndices(
                r, c, angles, scores, kept, cellBest, lattice, effRows, effCols, swapUv,
                latticeAngleDeg, vAxisDeg, consensusDeg, matchConc, lattice.PitchV, lattice.PitchU, n,
                colDelta.DeltaU, colDelta.DeltaV, colDelta.Summary);
        }

        private static LatticePickResult PackLatticePickFromIndices(
            double[] r,
            double[] c,
            double[]? angles,
            double[]? scores,
            List<int> kept,
            Dictionary<(int ir, int ic), int> cellBest,
            RowColumnLattice lattice,
            int effRows,
            int effCols,
            bool swapUv,
            double latticeAngleDeg,
            double chainDirectionDeg,
            double consensusDeg,
            double matchConc,
            double pitchV,
            double pitchU,
            int inputCount,
            double twoColumnDeltaU = double.NaN,
            double twoColumnDeltaV = double.NaN,
            string? twoColumnDeltaSummary = null)
        {
            int m = kept.Count;
            var outR = new double[m];
            var outC = new double[m];
            var outA = new double[m];
            var outS = new double[m];
            var outGr = new int[m];
            var outGc = new int[m];
            var indexToCell = cellBest.ToDictionary(kv => kv.Value, kv => kv.Key);

            for (int k = 0; k < m; k++)
            {
                int i = kept[k];
                outR[k] = r[i];
                outC[k] = c[i];
                outA[k] = angles != null && i < angles.Length ? angles[i] : 0;
                outS[k] = scores != null && i < scores.Length ? scores[i] : 0;
                if (indexToCell.TryGetValue(i, out var cell))
                {
                    outGr[k] = cell.ir;
                    outGc[k] = cell.ic;
                }
                else
                {
                    outGr[k] = -1;
                    outGc[k] = -1;
                }
            }

            return new LatticePickResult
            {
                InputCount = inputCount,
                Rows = outR,
                Cols = outC,
                Angles = outA,
                Scores = outS,
                GridRow = outGr,
                GridCol = outGc,
                KeptIndices = kept.ToArray(),
                ChainPickIndices = kept.ToArray(),
                LatticeRows = effRows,
                LatticeCols = effCols,
                AxesSwapped = swapUv,
                LatticeAngleDeg = latticeAngleDeg,
                ChainDirectionAngleDeg = chainDirectionDeg,
                ConsensusMatchAngleDeg = consensusDeg,
                MatchConcentration = matchConc,
                PitchRow = pitchV,
                PitchCol = pitchU,
                TwoColumnDeltaU = twoColumnDeltaU,
                TwoColumnDeltaV = twoColumnDeltaV,
                TwoColumnDeltaSummary = twoColumnDeltaSummary ?? ""
            };
        }

        internal static LatticePickResult PickLatticeGridMatches(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double minScoreKeep,
            string? diagnosticTag)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "LatticePick" : diagnosticTag;
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return new LatticePickResult { InputCount = 0 };

            int targetCells = gridRows * gridCols;
            DiagLattice(tag, $"格点筛选 {gridRows}×{gridCols}（目标 {targetCells}）, 输入 {n}");

            var boot = StripLatticeBootstrap(rows!, cols!, angles, scores, gridRows, gridCols, tag);
            var ctx = StripLatticeBuildOrient(rows!, cols!, angles, scores, gridRows, gridCols, boot, tag);
            if (ctx == null)
                return new LatticePickResult { InputCount = n };

            ctx = StripLatticePickColumn0(ctx, rows!, cols!, scores, tag);
            return StripLatticeFill(ctx, rows!, cols!, angles, scores, minScoreKeep, tag);
        }

    }
}
