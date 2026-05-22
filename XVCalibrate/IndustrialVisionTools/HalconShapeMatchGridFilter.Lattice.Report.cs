using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CalibOperatorCLI_Example
{
    internal static partial class HalconShapeMatchGridFilter
    {
        private static string Fmt(double x) => x.ToString("F1", CultureInfo.InvariantCulture);
        private static string FmtSc(double[]? scores, int i) =>
            scores != null && i >= 0 && i < scores.Length ? scores[i].ToString("F3", CultureInfo.InvariantCulture) : "?";

        /// <summary>双列条带逐格输入/输出明细，写入 grid-filter.log。</summary>
        private static void LogTwoColumnStripDetail(
            string tag,
            double[] rows,
            double[] cols,
            double[]? scores,
            double[]? angles,
            double[] u,
            double[] v,
            int n,
            int effRows,
            int effCols,
            RowColumnLattice lattice,
            bool swapUv,
            double latticeAngleDeg,
            int[] col0ChainPick,
            int[] col1ChainPick,
            Dictionary<(int ir, int ic), int> cellBest,
            int[] outputIndices,
            int[] outputGridRow,
            int[] outputGridCol,
            Dictionary<(int ir, int ic), List<GridCellCandidate>>? perCell)
        {
            if (!HalconShapeMatchGridDiagnostics.IsEnabled)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"──────── [{tag}] 双列条带明细 8×2 ────────");
            sb.AppendLine($"格网 θ={Fmt(latticeAngleDeg)}° swap={swapUv}  effRows={effRows} effCols={effCols}");
            sb.AppendLine($"列中心 U=[{string.Join(", ", lattice.ColU.Select(Fmt))}] pitchU={Fmt(lattice.PitchU)}");
            sb.AppendLine($"行中心 V=[{string.Join(", ", lattice.RowV.Select(Fmt))}] pitchV={Fmt(lattice.PitchV)}");
            sb.AppendLine();

            sb.AppendLine($"=== Find 全量输入 ({n} 个，按 Score 降序) ===");
            sb.AppendLine("  #idx   Score    Row      Col      u        v");
            foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => scores != null && i < scores.Length ? scores[i] : 0).ThenBy(i => i))
            {
                sb.AppendLine($"  {i,4}  {FmtSc(scores, i),6}  {Fmt(rows[i]),7}  {Fmt(cols[i]),7}  {Fmt(u[i]),7}  {Fmt(v[i]),7}");
            }
            sb.AppendLine();

            LogChainPickBlock(sb, "列0 链向 N 连（输入）", col0ChainPick, rows, cols, scores, u, v);
            LogChainPickBlock(sb, "列1 链向 N 连（输入）", col1ChainPick, rows, cols, scores, u, v);

            if (perCell != null)
            {
                sb.AppendLine("=== 落格候选（强制列前，每格最高分）===");
                for (int ic = 0; ic < effCols; ic++)
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (perCell.TryGetValue((ir, ic), out var list) && list.Count > 0)
                    {
                        var best = list.OrderByDescending(c => c.Score).First();
                        sb.AppendLine($"  格[{ir},{ic}] → #{best.Index} Score={FmtSc(scores, best.Index)} dist={Fmt(best.DistLattice)}");
                    }
                    else
                        sb.AppendLine($"  格[{ir},{ic}] → (无候选)");
                }
                sb.AppendLine();
            }

            sb.AppendLine("=== 最终输出（按格 [行,列]）===");
            sb.AppendLine("  格      #idx   Score    Row      Col      u        v     说明");
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    if (cellBest.TryGetValue((ir, ic), out int idx))
                    {
                        string note = "";
                        if (col0ChainPick.Contains(idx) && ic == 1)
                            note = "※也在列0链";
                        if (ic == 0 && !col0ChainPick.Contains(idx))
                            note = "※非列0链入选";
                        if (Math.Abs(scores?[idx] ?? 0 - 0.914) < 0.001)
                            note += " ←0.914";
                        sb.AppendLine($"  [{ir},{ic}]   {idx,4}  {FmtSc(scores, idx),6}  {Fmt(rows[idx]),7}  {Fmt(cols[idx]),7}  {Fmt(u[idx]),7}  {Fmt(v[idx]),7}  {note}");
                    }
                    else
                        sb.AppendLine($"  [{ir},{ic}]    —     —        —        —        —        —   空缺");
                }
            }
            sb.AppendLine();

            sb.AppendLine($"=== 显示用 ConsensusPickIndices ({outputIndices.Length} 个，顺序=先列后行) ===");
            sb.AppendLine("  序   #idx   Score    格[行,列]   Row      Col");
            for (int k = 0; k < outputIndices.Length; k++)
            {
                int i = outputIndices[k];
                int gr = k < outputGridRow.Length ? outputGridRow[k] : -1;
                int gc = k < outputGridCol.Length ? outputGridCol[k] : -1;
                sb.AppendLine($"  {k,3}  {i,4}  {FmtSc(scores, i),6}  [{gr},{gc}]     {Fmt(rows[i]),7}  {Fmt(cols[i]),7}");
            }

            var missingCol1 = Enumerable.Range(0, effRows).Where(ir => !cellBest.ContainsKey((ir, 1))).ToList();
            if (missingCol1.Count > 0)
                sb.AppendLine($"警告: 列1 空缺行: {string.Join(",", missingCol1)}");

            var col0Only = outputIndices.Where(i => col0ChainPick.Contains(i) && !cellBest.Any(kv => kv.Key.ic == 1 && kv.Value == i)).ToList();
            var col1Only = outputIndices.Where(i => cellBest.Any(kv => kv.Key.ic == 1 && kv.Value == i)).ToList();
            sb.AppendLine($"汇总: 列0格={Enumerable.Range(0, effRows).Count(ir => cellBest.ContainsKey((ir, 0)))}, " +
                          $"列1格={Enumerable.Range(0, effRows).Count(ir => cellBest.ContainsKey((ir, 1)))}, " +
                          $"显示索引列0链={col0ChainPick.Length}, 显示总数={outputIndices.Length}");
            for (int k = 0; k < outputIndices.Length; k++)
            {
                if (Math.Abs((scores?[outputIndices[k]] ?? 0) - 0.914) < 0.001)
                {
                    int gr = k < outputGridRow.Length ? outputGridRow[k] : -1;
                    int gc = k < outputGridCol.Length ? outputGridCol[k] : -1;
                    sb.AppendLine($"提示: Score=0.914 对应 Find#{outputIndices[k]} 在输出序[{k}] 格[{gr},{gc}]（列0链={col0ChainPick.Contains(outputIndices[k])}）");
                }
            }

            HalconShapeMatchGridDiagnostics.Log(sb.ToString());
        }

        private static void LogChainPickBlock(
            StringBuilder sb,
            string title,
            int[] pick,
            double[] rows,
            double[] cols,
            double[]? scores,
            double[] u,
            double[] v)
        {
            sb.AppendLine($"=== {title} ({pick.Length} 个) ===");
            if (pick.Length == 0)
            {
                sb.AppendLine("  (空)");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("  序   #idx   Score    Row      Col      u        v");
            var ordered = pick.OrderBy(i => v[i]).ThenBy(i => i).ToArray();
            for (int k = 0; k < ordered.Length; k++)
            {
                int i = ordered[k];
                sb.AppendLine($"  {k,3}  {i,4}  {FmtSc(scores, i),6}  {Fmt(rows[i]),7}  {Fmt(cols[i]),7}  {Fmt(u[i]),7}  {Fmt(v[i]),7}");
            }
            sb.AppendLine($"  Score=[{string.Join(", ", pick.Select(i => FmtSc(scores, i)))}]");
            sb.AppendLine();
        }

        internal static void LogDisplayPickSubset(
            string tag,
            double[] rows,
            double[] cols,
            double[]? scores,
            int[] pickIndices,
            int[]? gridCol,
            int totalFindCount)
        {
            if (!HalconShapeMatchGridDiagnostics.IsEnabled || pickIndices == null || pickIndices.Length == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"──────── [{tag}] 显示筛选 ────────");
            sb.AppendLine($"Find 总数={totalFindCount} → 显示 {pickIndices.Length} 个");
            sb.AppendLine("  序   #idx   Score    GridCol   Row      Col");
            for (int k = 0; k < pickIndices.Length; k++)
            {
                int i = pickIndices[k];
                int gc = gridCol != null && k < gridCol.Length ? gridCol[k] : -1;
                if (i < 0 || i >= rows.Length)
                    continue;
                sb.AppendLine($"  {k,3}  {i,4}  {FmtSc(scores, i),6}  {gc,7}  {Fmt(rows[i]),7}  {Fmt(cols[i]),7}");
            }
            int c0 = gridCol?.Count(ic => ic == 0) ?? 0;
            int c1 = gridCol?.Count(ic => ic == 1) ?? 0;
            sb.AppendLine($"显示列统计: 列0={c0}, 列1={c1}");
            HalconShapeMatchGridDiagnostics.Log(sb.ToString());
        }
    }
}
