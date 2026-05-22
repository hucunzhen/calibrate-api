using System;

using System.Collections.Generic;

using System.Globalization;

using System.Linq;



namespace CalibOperatorCLI_Example

{

    /// <summary>

    /// 规则阵列过滤：行/列聚类 + 等间距格点拟合，每格选最贴合理论格点的匹配（整齐度优先，分数次之）。

    /// </summary>

    internal static partial class HalconShapeMatchGridFilter

    {

        public sealed class Result

        {

            public double[] Rows { get; init; } = Array.Empty<double>();

            public double[] Cols { get; init; } = Array.Empty<double>();

            public double[] Angles { get; init; } = Array.Empty<double>();

            public double[] Scores { get; init; } = Array.Empty<double>();

            public int InputCount { get; init; }

            public double EstimatedPitchCol { get; init; }

            public double EstimatedPitchRow { get; init; }

            public double EstimatedAngleDeg { get; init; }

            public bool AxesSwapped { get; init; }

            /// <summary>阵列格行索引（0-based），与 Rows 等长；未落格为 -1。</summary>
            public int[] GridRow { get; init; } = Array.Empty<int>();

            /// <summary>阵列格列索引（0-based），与 Rows 等长；未落格为 -1。</summary>
            public int[] GridCol { get; init; } = Array.Empty<int>();

            /// <summary>阵列行数（与 CellRow 等长 = LatticeRows×LatticeCols）。</summary>
            public int LatticeRows { get; init; }

            /// <summary>阵列列数。</summary>
            public int LatticeCols { get; init; }

            /// <summary>全部理论格中心 Row（行优先 ir×cols+ic），图像坐标。</summary>
            public double[] CellRow { get; init; } = Array.Empty<double>();

            /// <summary>全部理论格中心 Column，图像坐标。</summary>
            public double[] CellCol { get; init; } = Array.Empty<double>();

            /// <summary>该格是否在最终输出中命中。</summary>
            public bool[] CellFound { get; init; } = Array.Empty<bool>();

            /// <summary>各格绘制框用的角度(°)，与 CellRow 等长；缺格为阵列共识匹配角。</summary>
            public double[] CellAngleDeg { get; init; } = Array.Empty<double>();

            /// <summary>模板匹配角共识值(°)，用于阵列 u/v 投影。</summary>
            public double ConsensusMatchAngleDeg { get; init; }

            /// <summary>N 连排布链向角(°)，用于绘制格框旋转。</summary>
            public double ChainDirectionAngleDeg { get; init; } = double.NaN;

            public double[] ColCenterU { get; init; } = Array.Empty<double>();

            public double[] RowCenterV { get; init; } = Array.Empty<double>();

        }






        private sealed class GridCellCandidate

        {

            public int Index { get; }

            public int Ir { get; }

            public int Ic { get; }

            public double DistLattice { get; }

            public double Score { get; }



            public GridCellCandidate(int index, int ir, int ic, double distLattice, double score)

            {

                Index = index;

                Ir = ir;

                Ic = ic;

                DistLattice = distLattice;

                Score = score;

            }

        }



        public static Result Filter(

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

            double pitchToleranceRatio,

            int minNeighborVotes,

            double minScoreKeep = 0,

            double maxAngleDeviationDeg = 0,

            string? diagnosticTag = null,

            LatticeFitResult? precomputedLattice = null)

        {

            string tag = string.IsNullOrEmpty(diagnosticTag) ? "GridFilter" : diagnosticTag;

            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);

            if (n == 0)

            {

                Diag(tag, "输入为空，跳过");

                return new Result { Rows = Array.Empty<double>(), Cols = Array.Empty<double>() };

            }



            if (gridRows < 1 || gridCols < 1)

                throw new ArgumentException("gridRows / gridCols 至少为 1");



            double[] r = rows!;

            double[] c = cols!;



            Diag(tag, $"参数: 阵列 {gridRows}×{gridCols}, 输入 {n}, pitchRow={pitchRow}, pitchCol={pitchCol}, " +

                $"gridAngle={(gridAngleDeg.HasValue ? gridAngleDeg.Value.ToString("F1", CultureInfo.InvariantCulture) : "auto")}, " +

                $"snapPx={snapTolerancePx}, minVotes={minNeighborVotes}, minScoreKeep={minScoreKeep}");



            var fit = precomputedLattice ?? FitLattice(
                r, c, angles, scores, gridRows, gridCols, pitchRow, pitchCol, gridAngleDeg, snapTolerancePx, tag);

            double angleRad = fit.AngleRad;
            bool swapUv = fit.AxesSwapped;
            var u = fit.U;
            var v = fit.V;
            double consensusMatchDeg = fit.ConsensusMatchAngleDeg;
            double chainDirectionDeg = fit.ChainDirectionAngleDeg;
            var lattice = fit.Lattice;
            double snapU = fit.SnapU;
            double snapV = fit.SnapV;

            if (lattice.ColU.Length == 0 || lattice.RowV.Length == 0
                || (fit.PitchCol < 1e-3 && pitchCol <= 0) || (fit.PitchRow < 1e-3 && pitchRow <= 0))
            {
                Diag(tag, "阵列聚类/格距失败，原样输出");
                return PassThrough(r, c, angles, scores, n, fit.PitchRow, fit.PitchCol, fit.EstimatedAngleDeg, swapUv);
            }

            if (precomputedLattice == null)
                Diag(tag, $"阵列聚类: pitchU={lattice.PitchU:F1}, pitchV={lattice.PitchV:F1}, snapU={snapU:F1}, snapV={snapV:F1}");

            var perCell = BuildLatticeCandidates(u, v, scores, n, lattice, snapU, snapV, 0, out int rejectedSnap, out int rescuedHighScore);
            if (rescuedHighScore > 0)
                Diag(tag, $"snap 放宽落格: {rescuedHighScore} 个 (≈{2.6 * lattice.MeanPitchU:F0}px)");

            Diag(tag, $"落格: {perCell.Count} 格有候选, 拒绝(距格点过远)={rejectedSnap}");

            LogCandidateDisposition(tag, r, c, scores, u, v, n, lattice, snapU, snapV, perCell);



            var cellBest = SelectOnePerCellLatticeFirst(perCell, tag);

            int effRows = swapUv ? gridCols : gridRows;
            int effCols = swapUv ? gridRows : gridCols;
            int chainWindow = ConsensusChainWindowSize(gridRows, gridCols);
            double chainDegForPick = !double.IsNaN(chainDirectionDeg) ? chainDirectionDeg : angleRad * 180.0 / Math.PI;
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
            int[] col1ChainPick = Array.Empty<int>();
            if (linePick.Length > 0 && effCols == 2)
                col1ChainPick = ApplyTwoColumnStripPicks(linePick, r, c, u, v, scores, n, effRows, lattice, chainWindow, cellBest, chainDegForPick, tag);

            var kept = effCols == 2
                ? CollectStripCellIndices(cellBest, effRows, effCols)
                : new List<int>(cellBest.Values);



            if (minNeighborVotes > 0)

            {

                int before = kept.Count;

                kept = kept.Where(i => cellBest.ContainsValue(i) &&

                    HasLatticeNeighbors(i, cellBest, u, v, lattice, pitchToleranceRatio, minNeighborVotes)).ToList();

                if (kept.Count != before)

                    Diag(tag, $"邻格一致性: {before}→{kept.Count}");

            }



            if (maxAngleDeviationDeg > 0 && angles != null && angles.Length > 0 && kept.Count > 0)

            {

                double refDeg = EstimateReferenceMatchAngleDeg(angles, scores, kept);

                int before = kept.Count;

                kept = kept.Where(i => i < angles.Length && AngleDistanceDeg(angles[i], refDeg) <= maxAngleDeviationDeg).ToList();

                if (kept.Count != before)

                    Diag(tag, $"角度过滤 ref={refDeg:F1}°: {before}→{kept.Count}");

            }



            LogKeptCells(tag, r, c, scores, kept, cellBest, perCell);

            kept.Sort((a, b) =>

            {

                var ca = perCell.Values.SelectMany(x => x).First(x => x.Index == a);

                var cb = perCell.Values.SelectMany(x => x).First(x => x.Index == b);

                int cmp = ca.Ir.CompareTo(cb.Ir);

                return cmp != 0 ? cmp : ca.Ic.CompareTo(cb.Ic);

            });



            var keptSet = new HashSet<int>(kept);
            int[] consensusPick = Array.Empty<int>();
            if (angles != null && angles.Length >= n)
                (_, _, consensusPick) = ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);

            if (consensusPick.Length > 0)
                lattice = RefineLatticeFromConsensusPick(lattice, u, v, scores, consensusPick, tag);

            lattice = RefineLatticeFromCellMatches(lattice, u, v, scores, cellBest, keptSet);

            if (consensusPick.Length == 0)
                lattice = EnforceUniformColumnsPreserveRows(lattice);

            Diag(tag, consensusPick.Length > 0
                ? "已用共识高分点+落格匹配修正行/列中心（保留模板贴合）"
                : "已用落格匹配修正行/列中心：列等间距、行跟模板");

            if (double.IsNaN(consensusMatchDeg) && angles != null && angles.Length >= n)
                (consensusMatchDeg, _, _) = ComputeConsensusMatchAngle(angles, scores, c, r, n, gridRows, gridCols, tag);

            var result = Pack(r, c, angles, scores, kept, cellBest, lattice, angleRad, n, lattice.PitchV, lattice.MeanPitchU,
                angleRad * 180.0 / Math.PI, swapUv, consensusMatchDeg, chainDirectionDeg);

            Diag(tag, $"输出 {result.Rows.Length} / 期望 {gridRows * gridCols}");

            return result;

        }




        /// <summary>在全部格点中找满足 snap 且欧氏距离最小的格，避免 Round 分到错列。</summary>
        private static bool TryFindBestSnapCell(
            double ui, double vi, RowColumnLattice lattice, double snapU, double snapV,
            out int ir, out int ic, out double dist)
        {
            ir = ic = 0;
            dist = double.MaxValue;
            bool found = false;
            for (int r = 0; r < lattice.Rows; r++)
            {
                for (int c = 0; c < lattice.Cols; c++)
                {
                    double du = Math.Abs(ui - lattice.IdealU(r, c));
                    double dv = Math.Abs(vi - lattice.IdealV(r));
                    if (du > snapU || dv > snapV)
                        continue;
                    double d = Math.Sqrt(du * du + dv * dv);
                    if (d < dist)
                    {
                        dist = d;
                        ir = r;
                        ic = c;
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>仅在指定列内找满足 snap 且距离最小的行格，避免右列点因 u 偏差占左列格。</summary>
        private static bool TryFindBestSnapCellInColumn(
            double ui, double vi, int ic, RowColumnLattice lattice, double snapU, double snapV,
            out int ir, out double dist)
        {
            ir = 0;
            dist = double.MaxValue;
            bool found = false;
            ic = ClampIndex(ic, 0, lattice.Cols - 1);
            for (int r = 0; r < lattice.Rows; r++)
            {
                double du = Math.Abs(ui - lattice.IdealU(r, ic));
                double dv = Math.Abs(vi - lattice.IdealV(r));
                if (du > snapU || dv > snapV)
                    continue;
                double d = Math.Sqrt(du * du + dv * dv);
                if (d < dist)
                {
                    dist = d;
                    ir = r;
                    found = true;
                }
            }
            return found;
        }

        private static void FindNearestLatticeCell(
            double ui, double vi, RowColumnLattice lattice,
            out int ir, out int ic, out double dist)
        {
            ir = ic = 0;
            dist = double.MaxValue;
            for (int r = 0; r < lattice.Rows; r++)
            {
                for (int c = 0; c < lattice.Cols; c++)
                {
                    double du = ui - lattice.IdealU(r, c);
                    double dv = vi - lattice.IdealV(r);
                    double d = Math.Sqrt(du * du + dv * dv);
                    if (d < dist)
                    {
                        dist = d;
                        ir = r;
                        ic = c;
                    }
                }
            }
        }

        private const double HighScoreRescuePitchFactor = 2.6;

        private static Dictionary<(int ir, int ic), List<GridCellCandidate>> BuildLatticeCandidates(

            double[] u, double[] v, double[]? scores, int n,

            RowColumnLattice lattice, double snapU, double snapV, double scoreRescueMin,
            out int rejectedSnap, out int rescuedHighScore)

        {

            var perCell = new Dictionary<(int ir, int ic), List<GridCellCandidate>>();

            rejectedSnap = 0;
            rescuedHighScore = 0;
            double rescueSnapU = Math.Max(snapU, lattice.MeanPitchU * HighScoreRescuePitchFactor);
            double rescueSnapV = Math.Max(snapV, lattice.PitchV * 0.55);



            for (int i = 0; i < n; i++)

            {
                double sc = scores != null && i < scores.Length ? scores[i] : 0;

                if (!TryFindBestSnapCell(u[i], v[i], lattice, snapU, snapV, out int ir, out int ic, out double dist))
                {
                    if (TryFindBestSnapCell(u[i], v[i], lattice, rescueSnapU, rescueSnapV, out ir, out ic, out dist))
                        rescuedHighScore++;
                    else
                    {
                        rejectedSnap++;
                        continue;
                    }
                }



                var key = (ir, ic);

                if (!perCell.TryGetValue(key, out var list))

                {

                    list = new List<GridCellCandidate>();

                    perCell[key] = list;

                }



                list.Add(new GridCellCandidate(i, ir, ic, dist, sc));

            }



            return perCell;

        }

        /// <summary>双列 u/v 落格：先按图像 Col 定列，再在该列内 snap 到行格。</summary>
        private static Dictionary<(int ir, int ic), List<GridCellCandidate>> BuildLatticeCandidatesTwoColumn(
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double snapU,
            double snapV,
            double scoreRescueMin,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            out int rejectedSnap,
            out int rescuedHighScore)
        {
            var perCell = new Dictionary<(int ir, int ic), List<GridCellCandidate>>();
            rejectedSnap = 0;
            rescuedHighScore = 0;
            double rescueSnapU = Math.Max(snapU, lattice.MeanPitchU * HighScoreRescuePitchFactor);
            double rescueSnapV = Math.Max(snapV, lattice.PitchV * 0.55);

            for (int i = 0; i < n; i++)
            {
                if (IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                {
                    rejectedSnap++;
                    continue;
                }

                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                int ic = ResolveLatticeColumnIndex(i, cols, u, lattice, col0ImageCol, col1ImageCol, imageColMargin);

                if (!TryFindBestSnapCellInColumn(u[i], v[i], ic, lattice, snapU, snapV, out int ir, out double dist))
                {
                    if (TryFindBestSnapCellInColumn(u[i], v[i], ic, lattice, rescueSnapU, rescueSnapV, out ir, out dist))
                        rescuedHighScore++;
                    else
                    {
                        rejectedSnap++;
                        continue;
                    }
                }

                var key = (ir, ic);
                if (!perCell.TryGetValue(key, out var list))
                {
                    list = new List<GridCellCandidate>();
                    perCell[key] = list;
                }

                list.Add(new GridCellCandidate(i, ir, ic, dist, sc));
            }

            return perCell;
        }

        private static Dictionary<(int ir, int ic), List<GridCellCandidate>> BuildLatticeCandidatesNearest(
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice)
        {
            var perCell = new Dictionary<(int ir, int ic), List<GridCellCandidate>>();
            for (int i = 0; i < n; i++)
            {
                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                FindNearestLatticeCell(u[i], v[i], lattice, out int ir, out int ic, out double dist);
                var key = (ir, ic);
                if (!perCell.TryGetValue(key, out var list))
                {
                    list = new List<GridCellCandidate>();
                    perCell[key] = list;
                }

                list.Add(new GridCellCandidate(i, ir, ic, dist, sc));
            }

            return perCell;
        }

        /// <summary>u/v 落格：每点在本列内按距格心最近行入候选（无 snap 门槛），列归属用 ResolveLatticeColumnIndex。</summary>
        private static Dictionary<(int ir, int ic), List<GridCellCandidate>> BuildLatticeCandidatesTwoColumnNearest(
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin)
        {
            var perCell = new Dictionary<(int ir, int ic), List<GridCellCandidate>>();
            for (int i = 0; i < n; i++)
            {
                if (IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                    continue;

                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                int ic = ResolveLatticeColumnIndex(i, cols, u, lattice, col0ImageCol, col1ImageCol, imageColMargin);
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

                var key = (bestIr, ic);
                if (!perCell.TryGetValue(key, out var list))
                {
                    list = new List<GridCellCandidate>();
                    perCell[key] = list;
                }

                list.Add(new GridCellCandidate(i, bestIr, ic, bestDist, sc));
            }

            return perCell;
        }

        /// <summary>snap 未通过的高分点：在本图像列内找距格心最近的行，加入候选（与同格仍按分数优先竞争）。</summary>
        private static int AddNearestColumnCandidatesForUnassigned(
            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell,
            double[] cols,
            double[] u,
            double[] v,
            double[]? scores,
            int n,
            RowColumnLattice lattice,
            double col0ImageCol,
            double col1ImageCol,
            double imageColMargin,
            double minScore,
            string? tag)
        {
            var already = new HashSet<int>(perCell.Values.SelectMany(list => list).Select(c => c.Index));
            int added = 0;
            for (int i = 0; i < n; i++)
            {
                if (already.Contains(i))
                    continue;
                if (IsImageColumnGapOutlier(i, cols, col0ImageCol, col1ImageCol))
                    continue;
                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                if (minScore > 0 && sc < minScore)
                    continue;

                int ic = ResolveLatticeColumnIndex(i, cols, u, lattice, col0ImageCol, col1ImageCol, imageColMargin);
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

                var key = (bestIr, ic);
                if (!perCell.TryGetValue(key, out var list))
                {
                    list = new List<GridCellCandidate>();
                    perCell[key] = list;
                }

                list.Add(new GridCellCandidate(i, bestIr, ic, bestDist, sc));
                already.Add(i);
                added++;
            }

            if (added > 0 && !string.IsNullOrEmpty(tag))
                Diag(tag, $"snap 外补候选(本列最近格): +{added} 点");
            return added;
        }

        private static int NearestLatticeIndex(double value, double origin, double pitch, int count)

        {

            if (count <= 1)

                return 0;

            int ic = (int)Math.Round((value - origin) / pitch);

            return ClampIndex(ic, 0, count - 1);

        }



        private static Dictionary<(int ir, int ic), int> SelectOnePerCellLatticeFirst(
            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell,
            string? tag,
            double maxDistLattice = double.PositiveInfinity,
            bool distanceFirst = false)
        {
            var assignment = new Dictionary<(int ir, int ic), int>();
            int skippedFar = 0;
            foreach (var kv in perCell)
            {
                List<GridCellCandidate> pool = double.IsFinite(maxDistLattice) && maxDistLattice > 0
                    ? kv.Value.Where(c => c.DistLattice <= maxDistLattice).ToList()
                    : kv.Value;
                if (pool.Count == 0)
                {
                    skippedFar++;
                    continue;
                }

                GridCellCandidate best = distanceFirst
                    ? pool.OrderBy(c => c.DistLattice).ThenByDescending(c => c.Score).ThenBy(c => c.Index).First()
                    : pool.OrderByDescending(c => c.Score).ThenBy(c => c.DistLattice).ThenBy(c => c.Index).First();
                assignment[kv.Key] = best.Index;
            }

            if (skippedFar > 0 && !string.IsNullOrEmpty(tag))
                Diag(tag, $"每格选取: 距格心>{maxDistLattice:F0}px 无候选，跳过 {skippedFar} 格");
            if (!string.IsNullOrEmpty(tag))
                Diag(tag, distanceFirst
                    ? $"每格选取(距格心优先、其次分数): {assignment.Count} 格"
                    : $"每格选取(分数优先、其次距格点): {assignment.Count} 格");
            return assignment;
        }



        private static bool HasLatticeNeighbors(

            int i,

            Dictionary<(int ir, int ic), int> assignment,

            double[] u, double[] v,

            RowColumnLattice lattice,

            double pitchTolRatio,

            int minVotes)

        {

            var cell = assignment.First(kv => kv.Value == i).Key;

            int ir = cell.ir;

            int ic = cell.ic;

            int votes = 0;

            // 按格拓扑计数：高分放宽落格后几何间距可能偏离 pitch，不再用 u/v 差做硬约束
            void Check(int dirR, int dirC)

            {

                var nk = (ir + dirR, ic + dirC);

                if (assignment.ContainsKey(nk))

                    votes++;

            }



            Check(-1, 0);

            Check(1, 0);

            Check(0, -1);

            Check(0, 1);

            return votes >= minVotes;

        }




        private static void LogCandidateDisposition(

            string tag, double[] r, double[] c, double[]? scores,

            double[] u, double[] v, int n,

            RowColumnLattice lattice, double snapU, double snapV,

            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell)

        {

            if (!HalconShapeMatchGridDiagnostics.IsEnabled)

                return;



            var assigned = new HashSet<int>(perCell.Values.SelectMany(x => x).Select(x => x.Index));

            for (int i = 0; i < n; i++)

            {

                double sc = scores != null && i < scores.Length ? scores[i] : 0;

                if (sc <= 0)
                    continue;



                if (assigned.Contains(i))

                {

                    var cand = perCell.Values.SelectMany(x => x).First(x => x.Index == i);

                    Diag(tag, $"候选[{i}] Score={sc:F3} → 格[{cand.Ir},{cand.Ic}] 距格点={cand.DistLattice:F1}px");

                    continue;

                }



                int roundIr = lattice.PointRow[i];

                int roundIc = lattice.PointCol[i];

                double roundDu = Math.Abs(u[i] - lattice.IdealU(roundIr, roundIc));

                double roundDv = Math.Abs(v[i] - lattice.IdealV(roundIr));

                FindNearestLatticeCell(u[i], v[i], lattice, out int nearIr, out int nearIc, out double nearDist);

                if (TryFindBestSnapCell(u[i], v[i], lattice, snapU, snapV, out int snapIr, out int snapIc, out double snapDist))

                    Diag(tag, $"候选[{i}] Score={sc:F3} 未落格: 最近可落格[{snapIr},{snapIc}] dist={snapDist:F1} (Round→[{roundIr},{roundIc}] du={roundDu:F1})");

                else

                    Diag(tag, $"候选[{i}] Score={sc:F3} 未落格: 最近格[{nearIr},{nearIc}] dist={nearDist:F1}>{snapU:F1} (Round→[{roundIr},{roundIc}] du={roundDu:F1})");

            }

        }



        private static void LogKeptCells(

            string tag, double[] r, double[] c, double[]? scores, List<int> kept,

            Dictionary<(int ir, int ic), int> cellBest,

            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell)

        {

            if (!HalconShapeMatchGridDiagnostics.IsEnabled)

                return;



            foreach (var kv in cellBest.OrderBy(k => k.Key.ir).ThenBy(k => k.Key.ic))

            {

                var cand = perCell[kv.Key].First(c => c.Index == kv.Value);

                double sc = scores != null && kv.Value < scores.Length ? scores[kv.Value] : 0;

                Diag(tag, $"  格[{kv.Key.ir},{kv.Key.ic}] 保留 dist={cand.DistLattice:F1} Score={sc:F3}");

            }

        }









        private static double EstimateReferenceMatchAngleDeg(double[] anglesDeg, double[]? scores, List<int> keptIndices)

        {

            if (keptIndices.Count == 0) return 0;

            if (scores != null && scores.Length > 0)

            {

                var top = keptIndices

                    .Where(i => i < anglesDeg.Length)

                    .OrderByDescending(i => i < scores.Length ? scores[i] : 0)

                    .Take(Math.Max(1, Math.Min(5, keptIndices.Count)))

                    .Select(i => anglesDeg[i])

                    .ToArray();

                if (top.Length > 0)

                    return CircularMeanDegrees(top);

            }



            return CircularMeanDegrees(keptIndices.Where(i => i < anglesDeg.Length).Select(i => anglesDeg[i]).ToArray());

        }



        private static double CircularMeanDegrees(double[] degAngles)

        {

            if (degAngles.Length == 0) return 0;

            double sumSin = 0, sumCos = 0;

            foreach (double a in degAngles)

            {

                double rad = a * Math.PI / 180.0;

                sumSin += Math.Sin(rad);

                sumCos += Math.Cos(rad);

            }



            return Math.Atan2(sumSin / degAngles.Length, sumCos / degAngles.Length) * 180.0 / Math.PI;

        }



        private static double AngleDistanceDeg(double a, double b)

        {

            double d = Math.Abs(a - b) % 360.0;

            return d > 180.0 ? 360.0 - d : d;

        }






        private static Result PassThrough(double[] r, double[] c, double[]? angles, double[]? scores, int n, double pitchV, double pitchU, double angleDeg, bool swapUv)

        {

            return new Result

            {

                Rows = (double[])r.Clone(),

                Cols = (double[])c.Clone(),

                Angles = angles != null && angles.Length >= n ? angles.Take(n).ToArray() : Array.Empty<double>(),

                Scores = scores != null && scores.Length >= n ? scores.Take(n).ToArray() : Array.Empty<double>(),

                GridRow = Enumerable.Repeat(-1, n).ToArray(),

                GridCol = Enumerable.Repeat(-1, n).ToArray(),

                LatticeRows = 0,

                LatticeCols = 0,

                CellRow = Array.Empty<double>(),

                CellCol = Array.Empty<double>(),

                CellFound = Array.Empty<bool>(),

                InputCount = n,

                EstimatedPitchRow = pitchV,

                EstimatedPitchCol = pitchU,

                EstimatedAngleDeg = angleDeg,

                AxesSwapped = swapUv

            };

        }



        private static Result Pack(
            double[] r, double[] c, double[]? angles, double[]? scores, List<int> kept,
            Dictionary<(int ir, int ic), int> cellBest,
            RowColumnLattice lattice,
            double angleRad,
            int inputCount, double pitchV, double pitchU,             double angleDeg, bool swapUv,
            double consensusMatchDeg,
            double chainDirectionDeg)

        {
            var keptSet = new HashSet<int>(kept);
            BuildCellGridGeometry(lattice, angleRad, cellBest, keptSet, consensusMatchDeg,
                out double[] cellRow, out double[] cellCol, out bool[] cellFound, out double[] cellAngleDeg);

            var outR = new double[kept.Count];

            var outC = new double[kept.Count];

            var outA = new double[kept.Count];

            var outS = new double[kept.Count];

            var outGr = new int[kept.Count];

            var outGc = new int[kept.Count];

            var indexToCell = cellBest.ToDictionary(kv => kv.Value, kv => kv.Key);

            if (kept.Count >= 2 && lattice.Cols >= 1)
            {
                ComputeLatticeSnapTolerance(lattice, 0, out double snapU, out double snapV);
                ProjectToUv(c, r, angleRad, out var uPr, out var vPr);
                kept = PrunePickIndicesByColumnCollinearitySafe(uPr, vPr, kept.ToArray(), indexToCell, lattice, snapU, snapV, null).ToList();
                outR = new double[kept.Count];
                outC = new double[kept.Count];
                outA = new double[kept.Count];
                outS = new double[kept.Count];
                outGr = new int[kept.Count];
                outGc = new int[kept.Count];
            }

            for (int k = 0; k < kept.Count; k++)

            {

                int i = kept[k];

                outR[k] = r[i];

                outC[k] = c[i];

                outA[k] = angles != null && angles.Length > i ? angles[i] : 0;

                outS[k] = scores != null && scores.Length > i ? scores[i] : 0;

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

            return new Result

            {

                Rows = outR,

                Cols = outC,

                Angles = outA,

                Scores = outS,

                GridRow = outGr,

                GridCol = outGc,

                LatticeRows = lattice.Rows,

                LatticeCols = lattice.Cols,

                CellRow = cellRow,

                CellCol = cellCol,

                CellFound = cellFound,

                CellAngleDeg = cellAngleDeg,

                ConsensusMatchAngleDeg = double.IsNaN(consensusMatchDeg) ? angleDeg : consensusMatchDeg,

                ChainDirectionAngleDeg = chainDirectionDeg,

                InputCount = inputCount,

                EstimatedPitchRow = pitchV,

                EstimatedPitchCol = pitchU,

                EstimatedAngleDeg = angleDeg,

                AxesSwapped = swapUv,

                ColCenterU = lattice.ColU,

                RowCenterV = lattice.RowV

            };

        }

    }

}


