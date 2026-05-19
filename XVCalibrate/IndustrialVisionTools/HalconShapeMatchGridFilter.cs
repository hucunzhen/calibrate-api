using System;

using System.Collections.Generic;

using System.Globalization;

using System.Linq;



namespace CalibOperatorCLI_Example

{

    /// <summary>

    /// 规则阵列过滤：行/列聚类 + 等间距格点拟合，每格选最贴合理论格点的匹配（整齐度优先，分数次之）。

    /// </summary>

    internal static class HalconShapeMatchGridFilter

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

        }



        /// <summary>先按 v 聚类分行，再在每行内对 u 做等间距列拟合。</summary>
        private sealed class RowColumnLattice
        {
            public int Rows { get; init; }
            public int Cols { get; init; }
            public double PitchV { get; init; }
            public double MeanPitchU { get; init; }
            public double[] RowV { get; init; } = Array.Empty<double>();
            public double[] RowU0 { get; init; } = Array.Empty<double>();
            public double[] RowPitchU { get; init; } = Array.Empty<double>();
            public int[] PointRow { get; init; } = Array.Empty<int>();

            public double IdealU(int ir, int ic) => RowU0[ir] + ic * RowPitchU[ir];
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

            string? diagnosticTag = null)

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



            bool userFixedAngle = gridAngleDeg.HasValue && !double.IsNaN(gridAngleDeg.Value);

            var orientation = userFixedAngle

                ? ResolveOrientationWithFixedAngle(c, r, angles, gridRows, gridCols, gridAngleDeg!.Value, tag)

                : ResolveOrientationAuto(c, r, angles, gridRows, gridCols, tag);



            double angleRad = orientation.AngleRad;

            bool swapUv = orientation.SwapUv;

            int effRows = swapUv ? gridCols : gridRows;

            int effCols = swapUv ? gridRows : gridCols;



            ProjectToUv(c, r, angleRad, out var u, out var v);



            double pitchU = pitchCol > 0 ? pitchCol : EstimatePitchFromProjections(u, effCols);

            double pitchV = pitchRow > 0 ? pitchRow : EstimatePitchFromProjections(v, effRows);

            if (pitchU < 1e-3 || pitchV < 1e-3)

            {

                Diag(tag, "格距估计失败，原样输出");

                return PassThrough(r, c, angles, scores, n, pitchV, pitchU, angleRad * 180.0 / Math.PI, swapUv);

            }



            var lattice = FitRowColumnLattice(u, v, effRows, effCols, pitchU, pitchV);

            ComputeLatticeSnapTolerance(lattice, snapTolerancePx, out double snapU, out double snapV);

            Diag(tag, $"分行+行内等间距: pitchV={lattice.PitchV:F1}, 平均pitchU={lattice.MeanPitchU:F1}, snapU={snapU:F1}, snapV={snapV:F1}");

            LogLatticeLines(tag, lattice);



            double scoreRescueMin = Math.Max(0.9, minScoreKeep);
            var perCell = BuildLatticeCandidates(u, v, scores, n, lattice, snapU, snapV, scoreRescueMin, out int rejectedSnap, out int rescuedHighScore);
            if (rescuedHighScore > 0)
                Diag(tag, $"高分放宽落格: {rescuedHighScore} 个 (score>={scoreRescueMin:F2}, snap≈{2.6 * lattice.MeanPitchU:F0}px)");

            Diag(tag, $"落格: {perCell.Count} 格有候选, 拒绝(距格点过远)={rejectedSnap}");

            LogCandidateDisposition(tag, r, c, scores, u, v, n, lattice, snapU, snapV, perCell);



            var cellBest = SelectOnePerCellLatticeFirst(perCell, tag);

            var kept = new List<int>(cellBest.Values);



            if (minNeighborVotes > 0)

            {

                int before = kept.Count;

                kept = kept.Where(i => cellBest.ContainsValue(i) &&

                    HasLatticeNeighbors(i, cellBest, u, v, lattice, pitchToleranceRatio, minNeighborVotes)).ToList();

                if (kept.Count != before)

                    Diag(tag, $"邻格一致性: {before}→{kept.Count}");

            }



            if (minScoreKeep > 0 && scores != null)

            {

                int before = kept.Count;

                kept = kept.Where(i => i < scores.Length && scores[i] >= minScoreKeep).ToList();

                if (kept.Count != before)

                    Diag(tag, $"minScoreKeep: {before}→{kept.Count}");

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



            var result = Pack(r, c, angles, scores, kept, n, lattice.PitchV, lattice.MeanPitchU, angleRad * 180.0 / Math.PI, swapUv);

            Diag(tag, $"输出 {result.Rows.Length} / 期望 {gridRows * gridCols}");

            return result;

        }



        /// <summary>1) v 方向 KMeans 分行；2) 每行内 u 方向 KMeans + 等间距列格点。</summary>
        private static RowColumnLattice FitRowColumnLattice(double[] u, double[] v, int rows, int cols, double pitchUHint, double pitchVHint)
        {
            int n = u.Length;
            var pointRow = new int[n];

            double[] rowCenters = KMeans1D(v, rows);
            Array.Sort(rowCenters);
            double pitchV = pitchVHint > 1e-3 ? pitchVHint : (rows > 1 ? (rowCenters[^1] - rowCenters[0]) / (rows - 1) : 1);
            var rowV = new double[rows];
            for (int ir = 0; ir < rows; ir++)
                rowV[ir] = rows > 1 ? rowCenters[0] + ir * pitchV : rowCenters[0];

            for (int i = 0; i < n; i++)
                pointRow[i] = NearestIndex1D(rowCenters, v[i]);

            RefineRowLines(v, pointRow, rows, ref rowV, ref pitchV);

            var rowU0 = new double[rows];
            var rowPitchU = new double[rows];
            var rowLists = Enumerable.Range(0, rows).Select(_ => new List<double>()).ToArray();
            for (int i = 0; i < n; i++)
                rowLists[pointRow[i]].Add(u[i]);

            for (int ir = 0; ir < rows; ir++)
            {
                FitRowColumnLine(rowLists[ir], cols, pitchUHint, out rowU0[ir], out rowPitchU[ir]);
            }

            var validPitches = rowPitchU.Where((p, ir) => rowLists[ir].Count > 0 && p > 1e-3).ToList();
            double meanPitchU = validPitches.Count > 0 ? Median(validPitches) : (pitchUHint > 1e-3 ? pitchUHint : 1);
            for (int ir = 0; ir < rows; ir++)
            {
                if (rowLists[ir].Count > 0)
                    rowPitchU[ir] = meanPitchU;
            }

            return new RowColumnLattice
            {
                Rows = rows,
                Cols = cols,
                PitchV = pitchV,
                MeanPitchU = meanPitchU,
                RowV = rowV,
                RowU0 = rowU0,
                RowPitchU = rowPitchU,
                PointRow = pointRow
            };
        }

        private static void RefineRowLines(double[] v, int[] pointRow, int rows, ref double[] rowV, ref double pitchV)
        {
            for (int iter = 0; iter < 4; iter++)
            {
                var offsets = Enumerable.Range(0, rows).Select(_ => new List<double>()).ToArray();
                for (int i = 0; i < v.Length; i++)
                {
                    int ir = pointRow[i];
                    offsets[ir].Add(v[i] - rowV[ir]);
                }

                for (int ir = 0; ir < rows; ir++)
                {
                    if (offsets[ir].Count == 0)
                        continue;
                    rowV[ir] += Median(offsets[ir]);
                }
            }

            if (rows > 1)
            {
                var sorted = rowV.OrderBy(x => x).ToArray();
                pitchV = (sorted[^1] - sorted[0]) / (rows - 1);
                double v0 = sorted[0];
                for (int ir = 0; ir < rows; ir++)
                    rowV[ir] = v0 + ir * pitchV;
            }
        }

        private static void FitRowColumnLine(List<double> uInRow, int cols, double pitchUHint, out double u0, out double pitchU)
        {
            if (uInRow.Count == 0)
            {
                u0 = 0;
                pitchU = pitchUHint > 1e-3 ? pitchUHint : 1;
                return;
            }

            var arr = uInRow.ToArray();
            double[] colCenters = KMeans1D(arr, cols);
            Array.Sort(colCenters);
            pitchU = pitchUHint > 1e-3 ? pitchUHint : (cols > 1 ? (colCenters[^1] - colCenters[0]) / (cols - 1) : 1);
            u0 = colCenters[0];

            for (int iter = 0; iter < 6; iter++)
            {
                var offsets = new List<double>();
                foreach (double uv in arr)
                {
                    int ic = ClampIndex((int)Math.Round((uv - u0) / pitchU), 0, cols - 1);
                    offsets.Add(uv - ic * pitchU);
                }

                u0 = Median(offsets);
            }
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
                    if (sc >= scoreRescueMin &&
                        TryFindBestSnapCell(u[i], v[i], lattice, rescueSnapU, rescueSnapV, out ir, out ic, out dist))
                    {
                        rescuedHighScore++;
                    }
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



        private static int NearestLatticeIndex(double value, double origin, double pitch, int count)

        {

            if (count <= 1)

                return 0;

            int ic = (int)Math.Round((value - origin) / pitch);

            return ClampIndex(ic, 0, count - 1);

        }



        private static Dictionary<(int ir, int ic), int> SelectOnePerCellLatticeFirst(

            Dictionary<(int ir, int ic), List<GridCellCandidate>> perCell, string tag)

        {

            var assignment = new Dictionary<(int ir, int ic), int>();

            foreach (var kv in perCell)

            {

                var best = kv.Value

                    .OrderByDescending(c => c.Score)

                    .ThenBy(c => c.DistLattice)

                    .First();

                assignment[kv.Key] = best.Index;

            }



            Diag(tag, $"每格选取(分数优先、其次距格点): {assignment.Count} 格");

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



            Diag(tag, $"行线 V: [{string.Join(", ", lattice.RowV.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)))}]");

            for (int ir = 0; ir < lattice.Rows; ir++)

            {

                var uLine = Enumerable.Range(0, lattice.Cols)

                    .Select(ic => lattice.IdealU(ir, ic).ToString("F1", CultureInfo.InvariantCulture));

                Diag(tag, $"  行{ir} 列线 U (u0={lattice.RowU0[ir]:F1}, pitch={lattice.RowPitchU[ir]:F1}): [{string.Join(", ", uLine)}]");

            }

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

                if (sc < 0.88)

                    continue;



                if (assigned.Contains(i))

                {

                    var cand = perCell.Values.SelectMany(x => x).First(x => x.Index == i);

                    Diag(tag, $"候选[{i}] Score={sc:F3} → 格[{cand.Ir},{cand.Ic}] 距格点={cand.DistLattice:F1}px");

                    continue;

                }



                int roundIr = lattice.PointRow[i];

                int roundIc = ClampIndex((int)Math.Round((u[i] - lattice.RowU0[roundIr]) / lattice.RowPitchU[roundIr]), 0, lattice.Cols - 1);

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



        private static OrientationChoice ResolveOrientationAuto(

            double[] cols, double[] rows, double[]? angles, int gridRows, int gridCols, string tag)

        {

            var trials = new List<OrientationChoice>();

            double pca = EstimatePrincipalAngleRad(cols, rows);



            void Add(double angleRad)

            {

                trials.Add(new OrientationChoice { AngleRad = angleRad, SwapUv = false });

                trials.Add(new OrientationChoice { AngleRad = angleRad, SwapUv = true });

                trials.Add(new OrientationChoice { AngleRad = angleRad + Math.PI / 2.0, SwapUv = false });

                trials.Add(new OrientationChoice { AngleRad = angleRad + Math.PI / 2.0, SwapUv = true });

            }



            Add(pca);

            if (angles != null && angles.Length >= cols.Length && MatchAngleConcentration(angles) >= 0.55)

                Add(CircularMeanDegrees(angles) * Math.PI / 180.0);



            return PickBestOrientation(cols, rows, trials, gridRows, gridCols, tag);

        }



        private static OrientationChoice ResolveOrientationWithFixedAngle(

            double[] cols, double[] rows, double[]? angles, int gridRows, int gridCols, double gridAngleDeg, string tag)

        {

            double angleRad = gridAngleDeg * Math.PI / 180.0;

            var trials = new List<OrientationChoice>

            {

                new() { AngleRad = angleRad, SwapUv = false },

                new() { AngleRad = angleRad, SwapUv = true }

            };

            return PickBestOrientation(cols, rows, trials, gridRows, gridCols, tag);

        }



        private static OrientationChoice PickBestOrientation(

            double[] cols, double[] rows, List<OrientationChoice> trials, int gridRows, int gridCols, string tag)

        {

            int bestScore = -1;

            var best = trials[0];

            foreach (var t in trials)

            {

                int effRows = t.SwapUv ? gridCols : gridRows;

                int effCols = t.SwapUv ? gridRows : gridCols;

                var m = EvaluateLatticeAlignment(cols, rows, t.AngleRad, effRows, effCols, gridRows, gridCols);

                int score = ScoreOrientation(m, gridRows, gridCols, t.SwapUv);

                if (HalconShapeMatchGridDiagnostics.IsEnabled)

                    Diag(tag, $"  试 θ={t.AngleRad * 180 / Math.PI:F1}° swap={t.SwapUv} cells={m.UniqueCells} snap={m.SnappedCount} total={score}");

                if (score > bestScore)

                {

                    bestScore = score;

                    best = t;

                }

            }



            return best;

        }



        private static int ScoreOrientation(AlignmentMetrics m, int gridRows, int gridCols, bool swapUv)

        {

            int target = gridRows * gridCols;

            int filled = Math.Min(m.UniqueCells, target);

            int dimBonus = m.MatchesUserGridDims ? 50_000 : 0;

            int swapPenalty = swapUv && gridRows != gridCols ? -5_000 : 0;

            return dimBonus + swapPenalty + filled * 1_000 + m.SnappedCount + m.AspectBonus * 200;

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



            var lattice = FitRowColumnLattice(u, v, effRows, effCols, pitchU, pitchV);

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



        private static double MatchAngleConcentration(double[] anglesDeg)

        {

            if (anglesDeg.Length == 0) return 0;

            double sumSin = 0, sumCos = 0;

            foreach (double a in anglesDeg)

            {

                double rad = a * Math.PI / 180.0;

                sumSin += Math.Sin(rad);

                sumCos += Math.Cos(rad);

            }



            return Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / anglesDeg.Length;

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



        private static Result PassThrough(double[] r, double[] c, double[]? angles, double[]? scores, int n, double pitchV, double pitchU, double angleDeg, bool swapUv)

        {

            return new Result

            {

                Rows = (double[])r.Clone(),

                Cols = (double[])c.Clone(),

                Angles = angles != null && angles.Length >= n ? angles.Take(n).ToArray() : Array.Empty<double>(),

                Scores = scores != null && scores.Length >= n ? scores.Take(n).ToArray() : Array.Empty<double>(),

                InputCount = n,

                EstimatedPitchRow = pitchV,

                EstimatedPitchCol = pitchU,

                EstimatedAngleDeg = angleDeg,

                AxesSwapped = swapUv

            };

        }



        private static Result Pack(double[] r, double[] c, double[]? angles, double[]? scores, List<int> kept, int inputCount, double pitchV, double pitchU, double angleDeg, bool swapUv)

        {

            var outR = new double[kept.Count];

            var outC = new double[kept.Count];

            var outA = new double[kept.Count];

            var outS = new double[kept.Count];

            for (int k = 0; k < kept.Count; k++)

            {

                int i = kept[k];

                outR[k] = r[i];

                outC[k] = c[i];

                outA[k] = angles != null && angles.Length > i ? angles[i] : 0;

                outS[k] = scores != null && scores.Length > i ? scores[i] : 0;

            }



            return new Result

            {

                Rows = outR,

                Cols = outC,

                Angles = outA,

                Scores = outS,

                InputCount = inputCount,

                EstimatedPitchRow = pitchV,

                EstimatedPitchCol = pitchU,

                EstimatedAngleDeg = angleDeg,

                AxesSwapped = swapUv

            };

        }

    }

}


