using System;
using System.Collections.Generic;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    internal static partial class HalconShapeMatchGridFilter
    {
        /// <summary>
        /// RANSAC / 鲁棒拟合：由 FindShapeModel 全点估计 u/v 格网，每格取最高分，输出 rows×cols 点供显示。
        /// </summary>
        internal static LatticePickResult PickLatticeRansac(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double minScoreKeep = 0,
            double snapTolerancePx = 0,
            string? diagnosticTag = null,
            string? uvProjectionSvgPath = null,
            int maxIterations = 500,
            double inlierSnapFactor = 0.45)
        {
            string tag = string.IsNullOrEmpty(diagnosticTag) ? "RansacLattice" : diagnosticTag;
            string? svgOut = !string.IsNullOrWhiteSpace(uvProjectionSvgPath)
                ? uvProjectionSvgPath
                : Environment.GetEnvironmentVariable("XV_GRID_FILTER_SVG");
            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0)
                return new LatticePickResult { InputCount = 0 };

            double[] r = rows!;
            double[] c = cols!;
            int targetCells = gridRows * gridCols;
            maxIterations = Math.Clamp(maxIterations, 80, 2000);
            inlierSnapFactor = Math.Clamp(inlierSnapFactor, 0.25, 0.85);

            double consensusPre = double.NaN;
            double matchConc = 0;
            if (angles != null && angles.Length >= n)
            {
                (consensusPre, matchConc, _) =
                    ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);
            }

            double pcaChainDeg = EstimatePrincipalAngleRad(c, r) * 180.0 / Math.PI;
            var boot = StripLatticeBootstrap(r, c, angles, scores, gridRows, gridCols, tag);
            double seedThetaDeg = boot.LatticeAngleDeg;
            if (double.IsNaN(seedThetaDeg))
                seedThetaDeg = pcaChainDeg - 90.0;

            DiagLattice(tag,
                $"RANSAC 鲁棒格网 {gridRows}×{gridCols}（目标 {targetCells}）, 输入 {n}, 种子θ≈{seedThetaDeg:F1}°, iter={maxIterations}");

            bool swapUv = false;
            int effRows = gridRows;
            int effCols = gridCols;
            if (!ShouldUseStripUvGeometricOrientation(gridRows, gridCols))
            {
                var orient = ResolveOrientationFromPcaAxis(c, r, gridRows, gridCols, pcaChainDeg, consensusPre, tag);
                swapUv = orient.SwapUv;
                effRows = swapUv ? gridCols : gridRows;
                effCols = swapUv ? gridRows : gridCols;
                if (!double.IsNaN(orient.AngleRad))
                    seedThetaDeg = orient.AngleRad * 180.0 / Math.PI;
            }

            int chainWindow = ConsensusChainWindowSize(gridRows, gridCols);
            var rng = Random.Shared;
            long bestScore = long.MinValue;
            double bestThetaRad = seedThetaDeg * Math.PI / 180.0;
            RowColumnLattice? bestLattice = null;
            int bestFilled = 0;

            int thetaTrials = Math.Min(100, maxIterations / 5);
            int threePtTrials = Math.Max(200, maxIterations - thetaTrials - 40);
            int refineTrials = Math.Max(0, maxIterations - thetaTrials - threePtTrials);

            void ConsiderHypothesis(double thetaRad, RowColumnLattice lat)
            {
                if (lat.PitchU < 1e-3 || lat.PitchV < 1e-3)
                    return;
                ProjectToUv(c, r, thetaRad, out var u, out var v);
                ComputeLatticeSnapTolerance(lat, snapTolerancePx, out double snapU, out double snapV);
                if (effCols == 2)
                {
                    snapU = Math.Max(snapU * inlierSnapFactor, lat.MeanPitchU * 0.48);
                    snapV = Math.Max(snapV * inlierSnapFactor, lat.PitchV * 0.38);
                }
                else
                {
                    snapU *= inlierSnapFactor;
                    snapV *= inlierSnapFactor;
                }

                long sc = ScoreRansacLatticeAssignment(u, v, c, scores, n, lat, effRows, effCols, snapU, snapV, out int filled);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    bestThetaRad = thetaRad;
                    bestLattice = lat;
                    bestFilled = filled;
                }
            }

            for (int iter = 0; iter < thetaTrials; iter++)
            {
                double jitter = (iter % 19 - 9) * 0.32 + (rng.NextDouble() - 0.5) * 0.55;
                double thetaDeg = NormalizeAngleDeg(seedThetaDeg + jitter);
                double thetaRad = thetaDeg * Math.PI / 180.0;
                ProjectToUv(c, r, thetaRad, out var u, out var v);
                double chainDeg = VAxisImageAngleColToRowDeg(thetaDeg);
                int[] bootIdx = SelectBootstrapIndicesForTwoColumnStrip(c, r, scores, n, chainWindow, $"{tag}/θ{iter}");
                if (bootIdx.Length < 2)
                    bootIdx = boot.BootstrapPickIndices;
                if (bootIdx.Length < 2)
                    continue;

                int[] col0 = bootIdx;
                int[] col1 = Array.Empty<int>();
                if (TryPartitionTwoImageColumns(c, bootIdx, out int[] low, out int[] high, out _, out _)
                    && low.Length >= 2 && high.Length >= 2)
                {
                    col0 = low;
                    col1 = high;
                }
                else if (TryPartitionTwoImageColumns(c, Enumerable.Range(0, n).ToArray(), out _, out int[] highPool, out _, out _))
                {
                    col1 = SelectStripChainIndicesByClusterFit(c, r, scores, highPool, chainWindow, null);
                }

                var lat = BuildStripLatticeFromChainClusterPoints(
                    col0, col1, c, r, u, v, scores, effRows, effCols, chainDeg, null);
                ConsiderHypothesis(thetaRad, lat);
            }

            for (int iter = 0; iter < threePtTrials; iter++)
            {
                if (!TrySampleThreePointIndices(c, r, scores, n, rng, out int i0, out int i1, out int i2))
                    continue;
                if (!TryBuildLatticeFromThreePoints(i0, i1, i2, r, c, effRows, effCols, out double thetaRad, out var lat))
                    continue;
                ConsiderHypothesis(thetaRad, lat);
            }

            if (bestLattice != null)
            {
                for (int iter = 0; iter < refineTrials; iter++)
                {
                    double jitter = (rng.NextDouble() - 0.5) * 0.35;
                    double thetaDeg = NormalizeAngleDeg(bestThetaRad * 180.0 / Math.PI + jitter);
                    double thetaRad = thetaDeg * Math.PI / 180.0;
                    ProjectToUv(c, r, thetaRad, out var u, out var v);
                    double chainDeg = VAxisImageAngleColToRowDeg(thetaDeg);
                    int[] bootIdx = boot.BootstrapPickIndices.Length >= 2
                        ? boot.BootstrapPickIndices
                        : SelectBootstrapIndicesForTwoColumnStrip(c, r, scores, n, chainWindow, tag);
                    int[] col0 = bootIdx;
                    int[] col1 = Array.Empty<int>();
                    if (TryPartitionTwoImageColumns(c, bootIdx, out int[] low, out int[] high, out _, out _)
                        && low.Length >= 2 && high.Length >= 2)
                    {
                        col0 = low;
                        col1 = high;
                    }

                    var lat = BuildStripLatticeFromChainClusterPoints(
                        col0, col1, c, r, u, v, scores, effRows, effCols, chainDeg, null);
                    if (bestLattice != null && iter % 3 == 0)
                        lat = RefineLatticeFromConsensusPick(lat, u, v, scores, bootIdx, tag);
                    ConsiderHypothesis(thetaRad, lat);
                }
            }

            if (bestLattice == null)
            {
                DiagLattice(tag, "RANSAC 未找到有效格网，回退 u/v 落格");
                return PickLatticeGridInUv(
                    rows, cols, angles, scores, gridRows, gridCols,
                    seedThetaDeg, boot.BootstrapPickIndices, minScoreKeep, snapTolerancePx,
                    tag, svgOut, pcaChainDeg);
            }

            DiagLattice(tag, $"RANSAC 最优: 命中≈{bestFilled}/{targetCells} 格, θ≈{bestThetaRad * 180.0 / Math.PI:F1}°");
            return FinalizeRansacLatticePick(
                r, c, angles, scores, n, gridRows, gridCols, effRows, effCols, swapUv,
                bestThetaRad, bestLattice, minScoreKeep, snapTolerancePx, inlierSnapFactor,
                consensusPre, matchConc, pcaChainDeg, tag, svgOut);
        }

        private static long ScoreRansacLatticeAssignment(
            double[] u, double[] v, double[] cols, double[]? scores, int n,
            RowColumnLattice lattice, int effRows, int effCols,
            double snapU, double snapV, out int filledCells)
        {
            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell;
            if (effCols == 2 && TryPartitionTwoImageColumns(cols, Enumerable.Range(0, n).ToArray(), out _, out _, out double col0, out double col1))
            {
                double margin = Math.Max(40, lattice.MeanPitchU * 0.12);
                perCell = BuildLatticeCandidatesTwoColumnNearest(cols, u, v, scores, n, lattice, col0, col1, margin);
            }
            else
                perCell = BuildLatticeCandidatesNearest(u, v, scores, n, lattice);

            var cellBest = SelectOnePerCellLatticeFirst(perCell, "");
            filledCells = cellBest.Count;
            int target = effRows * effCols;
            double sumScore = 0;
            double sumDist = 0;
            foreach (var kv in cellBest)
            {
                int idx = kv.Value;
                if (scores != null && idx < scores.Length)
                    sumScore += scores[idx];
                FindNearestLatticeCell(u[idx], v[idx], lattice, out _, out _, out double dist);
                sumDist += dist;
            }

            long cellBonus = filledCells * 1_000_000L;
            long scoreBonus = (long)(sumScore * 50_000);
            long distPenalty = (long)(sumDist * 8);
            long missPenalty = (target - filledCells) * 25_000L;
            return cellBonus + scoreBonus - distPenalty - missPenalty;
        }

        private static bool TrySampleThreePointIndices(
            double[] cols, double[] rows, double[]? scores, int n, Random rng,
            out int i0, out int i1, out int i2)
        {
            i0 = i1 = i2 = -1;
            if (n < 3)
                return false;

            int[] pool = Enumerable.Range(0, n).ToArray();
            if (!TryPartitionTwoImageColumns(cols, pool, out int[] low, out int[] high, out _, out _)
                || low.Length < 2 || high.Length < 1)
                return false;

            i0 = low[rng.Next(low.Length)];
            i1 = low[rng.Next(low.Length)];
            if (i0 == i1)
            {
                int i0Local = i0;
                i1 = low.OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0)
                    .FirstOrDefault(i => i != i0Local);
                if (i1 < 0 || i1 == i0)
                    return false;
            }

            double colSpan = cols.Max() - cols.Min();
            double rowSpan = rows.Max() - rows.Min();
            if (Math.Abs(cols[i0] - cols[i1]) > Math.Max(12, colSpan * 0.22))
                return false;
            if (Math.Abs(rows[i0] - rows[i1]) < Math.Max(8, rowSpan * 0.12))
                return false;

            i2 = high[rng.Next(high.Length)];
            if (Math.Abs(cols[i2] - cols[i0]) < Math.Max(10, colSpan * 0.18))
                return false;
            return true;
        }

        private static bool TryBuildLatticeFromThreePoints(
            int i0, int i1, int i2,
            double[] rows, double[] cols,
            int effRows, int effCols,
            out double thetaRad,
            out RowColumnLattice lattice)
        {
            thetaRad = 0;
            lattice = null!;
            double chainDeg = EstimateChainDirectionAngleDeg(cols, rows, new[] { i0, i1 });
            chainDeg = CanonicalizeStripChainDirectionDeg(chainDeg, effRows, effCols);
            double thetaDeg = NormalizeAngleDeg(chainDeg - 90.0);
            thetaRad = thetaDeg * Math.PI / 180.0;

            ProjectToUv(cols, rows, thetaRad, out var uAll, out var vAll);
            double u0 = uAll[i0], v0 = vAll[i0];
            double u1 = uAll[i1], v1 = vAll[i1];
            double u2 = uAll[i2], v2 = vAll[i2];

            double vLo = Math.Min(v0, v1);
            double vHi = Math.Max(v0, v1);
            double pitchV = effRows > 1 ? (vHi - vLo) / (effRows - 1) : Math.Max(1, Math.Abs(v1 - v0));
            if (pitchV < 1e-3)
                return false;

            var rowV = new double[effRows];
            for (int ir = 0; ir < effRows; ir++)
                rowV[ir] = vLo + ir * pitchV;

            var colU = new double[effCols];
            colU[0] = u0;
            if (effCols >= 2)
                colU[1] = u2;
            if (colU[0] > colU[effCols - 1])
            {
                for (int ic = 0; ic < effCols; ic++)
                    colU[ic] = colU[effCols - 1 - ic];
            }

            double pitchU = effCols > 1 ? Math.Max(1, colU[effCols - 1] - colU[0]) : 1;

            lattice = new RowColumnLattice
            {
                Rows = effRows,
                Cols = effCols,
                PitchU = pitchU,
                PitchV = pitchV,
                RowV = rowV,
                ColU = colU,
                PointRow = Array.Empty<int>(),
                PointCol = Array.Empty<int>()
            };
            return true;
        }

        private static LatticePickResult FinalizeRansacLatticePick(
            double[] r, double[] c, double[]? angles, double[]? scores, int n,
            int gridRows, int gridCols, int effRows, int effCols, bool swapUv,
            double bestThetaRad, RowColumnLattice lattice,
            double minScoreKeep, double snapTolerancePx, double inlierSnapFactor,
            double consensusPre, double matchConc, double pcaChainDeg,
            string tag, string? svgOut)
        {
            int targetCells = effRows * effCols;
            double latticeAngleDeg = bestThetaRad * 180.0 / Math.PI;
            double chainDirectionDeg = VAxisImageAngleColToRowDeg(latticeAngleDeg);

            ProjectToUv(c, r, bestThetaRad, out var u, out var v);
            ComputeLatticeSnapTolerance(lattice, snapTolerancePx, out double snapU, out double snapV);
            if (effCols == 2)
            {
                snapU = Math.Max(snapU, lattice.MeanPitchU * 0.52);
                snapV = Math.Max(snapV, lattice.PitchV * 0.42);
            }
            else
            {
                snapU = Math.Max(snapU * inlierSnapFactor, lattice.MeanPitchU * 0.4);
                snapV = Math.Max(snapV * inlierSnapFactor, lattice.PitchV * 0.35);
            }

            DiagLattice(tag,
                $"RANSAC 落格 θ≈{latticeAngleDeg:F1}°, pitchU×pitchV={lattice.PitchU:F0}×{lattice.PitchV:F0}, snapU={snapU:F1}, snapV={snapV:F1}");

            double col0Img = double.NaN;
            double col1Img = double.NaN;
            bool twoColRansac = effCols == 2 &&
                TryPartitionTwoImageColumns(c, Enumerable.Range(0, n).ToArray(), out _, out _, out col0Img, out col1Img);

            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell;
            if (twoColRansac)
            {
                double imgMargin = Math.Max(40, lattice.MeanPitchU * 0.12);
                perCell = BuildLatticeCandidatesTwoColumnNearest(c, u, v, scores, n, lattice, col0Img, col1Img, imgMargin);
            }
            else
                perCell = BuildLatticeCandidatesNearest(u, v, scores, n, lattice);

            var cellBest = SelectOnePerCellLatticeFirst(perCell, tag, distanceFirst: twoColRansac);
            if (cellBest.Count < targetCells)
                FillEmptyLatticeCellsGlobal(cellBest, u, v, scores, n, effRows, effCols, lattice, tag);

            double col0Bump = twoColRansac ? col0Img : double.NaN;
            double col1Bump = twoColRansac ? col1Img : double.NaN;
            if (twoColRansac)
                BumpUnassignedHighScoresToNearestCell(cellBest, u, v, scores, n, lattice, 0, tag, c, col0Bump, col1Bump);
            else
                BumpUnassignedHighScoresToNearestCell(cellBest, u, v, scores, n, lattice, 0, tag);

            if (effCols == 2 && !double.IsNaN(col0Bump))
            {
                int pruned = PruneOutliersFromStripCellBest(
                    cellBest, c, u, v, lattice, col0Bump, col1Bump, snapU, snapV, tag);
                if (pruned > 0)
                    FillEmptyLatticeCellsGlobal(cellBest, u, v, scores, n, effRows, effCols, lattice, tag);
            }

            var kept = new List<int>(targetCells);
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.TryGetValue((ir, ic), out int idx))
                        kept.Add(idx);
                }
            }

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
    }
}
