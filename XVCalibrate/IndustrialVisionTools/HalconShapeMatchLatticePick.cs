using System;
using System.Collections.Generic;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    /// <summary>格网 (0,0) 在图像上的角（Row 向下、Col 向右）。默认左上，避免链向翻 180° 时编号从左上跳到右下。</summary>
    public enum LatticeOriginCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>落格输出 / 焊接遍历顺序（相对格 (0,0) 与行列编号）。main 的 flow_loop 与轨迹拼接均跟此顺序。</summary>
    public enum LatticeWeldOrder
    {
        /// <summary>先沿列（行增大），再下一列。默认。</summary>
        ColMajor,
        /// <summary>先沿行（列增大），再下一行。</summary>
        RowMajor,
        /// <summary>按列蛇形：偶数列行↑，奇数列行↓。</summary>
        SnakeCol,
        /// <summary>按行蛇形：偶数行列→，奇数行列←。</summary>
        SnakeRow
    }

    /// <summary>焊接间隔遍数：在基础顺序上每隔 N 取一点，共 N 遍；是否奇偶遍反向由参数控制。</summary>
    public static class LatticeWeldSequence
    {
        /// <summary>
        /// 间隔 N：先按基础序排好，再分 N 遍。第 k 遍取下标 k, k+N, k+2N…；
        /// <paramref name="alternatePassReverse"/> 为 true 时奇数遍（第 2、4…遍）反向。N≤1 时原样返回。
        /// </summary>
        public static List<T> ApplyIntervalPasses<T>(
            IReadOnlyList<T> baseOrder,
            int interval,
            bool alternatePassReverse = true)
        {
            if (baseOrder == null || baseOrder.Count == 0)
                return new List<T>();
            int n = Math.Clamp(interval, 1, baseOrder.Count);
            if (n <= 1)
                return baseOrder is List<T> asList ? new List<T>(asList) : baseOrder.ToList();

            var result = new List<T>(baseOrder.Count);
            for (int pass = 0; pass < n; pass++)
            {
                var sub = new List<T>((baseOrder.Count + n - 1) / n);
                for (int i = pass; i < baseOrder.Count; i += n)
                    sub.Add(baseOrder[i]);
                if (alternatePassReverse && (pass & 1) == 1)
                    sub.Reverse();
                result.AddRange(sub);
            }
            return result;
        }
    }

    /// <summary>阵列格点筛选结果：每格一个最高分匹配，如 2×8 → 最多 16 个模板。</summary>
    public sealed class HalconShapeMatchLatticePickResult
    {
        public int InputCount { get; init; }
        public int KeptCount { get; init; }
        public double[] Rows { get; init; } = System.Array.Empty<double>();
        public double[] Cols { get; init; } = System.Array.Empty<double>();
        public double[] Angles { get; init; } = System.Array.Empty<double>();
        public double[] Scores { get; init; } = System.Array.Empty<double>();
        public int[] GridRow { get; init; } = System.Array.Empty<int>();
        public int[] GridCol { get; init; } = System.Array.Empty<int>();
        public int[] KeptIndices { get; init; } = System.Array.Empty<int>();
        public int LatticeRows { get; init; }
        public int LatticeCols { get; init; }
        public bool AxesSwapped { get; init; }
        /// <summary>落格 u/v 用的格网角(°)，显示格框须与此角一致。</summary>
        public double LatticeAngleDeg { get; init; } = double.NaN;
        public double ChainDirectionAngleDeg { get; init; } = double.NaN;
        public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
        public double PitchRow { get; init; }
        public double PitchCol { get; init; }
        public double TwoColumnDeltaU { get; init; } = double.NaN;
        public double TwoColumnDeltaV { get; init; } = double.NaN;
        public string TwoColumnDeltaSummary { get; init; } = "";
    }

    /// <summary>链向·引导结果（与流程 bootstrap 节点一致）。</summary>
    public sealed class HalconLatticeStripBootstrapInfo
    {
        public int InputCount { get; init; }
        public int[] BootstrapPickIndices { get; init; } = System.Array.Empty<int>();
        /// <summary>u/v 格网 u 轴 θ（°），接 ChainBootstrapAngle / LatticeAngle。</summary>
        public double LatticeAngleDeg { get; init; } = double.NaN;
        public double VAxisImageAngleDeg { get; init; } = double.NaN;
        /// <summary>全板 PCA 链向（°），非落格 θ。</summary>
        public double PcaChainAngleDeg { get; init; } = double.NaN;
        public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
        public double MatchConcentration { get; init; }
    }

    /// <summary>链向定向后按列/行聚类落格，每格取最拟合模板（输出整板 rows×cols 点）。</summary>
    public static class HalconShapeMatchLatticePick
    {
        /// <summary>条带引导：估格网 θ、高分 K 点、模板角共识（供 u/v 落格）。</summary>
        public static HalconLatticeStripBootstrapInfo StripBootstrap(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            string? diagnosticTag = null)
        {
            var b = HalconShapeMatchLatticeStrip.Bootstrap(rows, cols, angles, scores, gridRows, gridCols, diagnosticTag);
            return new HalconLatticeStripBootstrapInfo
            {
                InputCount = b.InputCount,
                BootstrapPickIndices = b.BootstrapPickIndices,
                LatticeAngleDeg = b.LatticeAngleDeg,
                VAxisImageAngleDeg = b.VAxisImageAngleDeg,
                PcaChainAngleDeg = b.PcaAxisAngleDeg,
                ConsensusMatchAngleDeg = b.ConsensusMatchAngleDeg,
                MatchConcentration = b.MatchConcentration
            };
        }

        /// <summary>接「链向·引导」：用引导输出的格网 u 轴 θ 落格，每格最高分，输出 rows×cols 点（如 16）。</summary>
        public static HalconShapeMatchLatticePickResult PickUvGridAfterBootstrap(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double latticeAngleHintDeg,
            int[]? bootstrapPickIndices = null,
            double minScoreKeep = 0,
            double snapTolerancePx = 0,
            string? diagnosticTag = null,
            string? uvProjectionSvgPath = null,
            double pcaChainAngleDeg = double.NaN,
            LatticeOriginCorner originCorner = LatticeOriginCorner.TopLeft,
            LatticeWeldOrder weldOrder = LatticeWeldOrder.ColMajor,
            int weldInterval = 1,
            bool weldIntervalReverse = true)
        {
            var inner = HalconShapeMatchGridFilter.PickLatticeGridInUv(
                rows, cols, angles, scores, gridRows, gridCols,
                latticeAngleHintDeg, bootstrapPickIndices, minScoreKeep, snapTolerancePx, diagnosticTag,
                uvProjectionSvgPath, pcaChainAngleDeg, originCorner, weldOrder, weldInterval, weldIntervalReverse);
            return ToPickResult(inner);
        }

        /// <summary>落格并导出 u/v 投影 SVG（可用浏览器打开）。</summary>
        public static HalconShapeMatchLatticePickResult PickUvGridAndExportProjectionSvg(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double latticeAngleHintDeg,
            string outputSvgPath,
            int[]? bootstrapPickIndices = null,
            string? diagnosticTag = null) =>
            PickUvGridAfterBootstrap(
                rows, cols, angles, scores, gridRows, gridCols,
                latticeAngleHintDeg, bootstrapPickIndices, 0, 0, diagnosticTag, outputSvgPath);

        public static HalconShapeMatchLatticePickResult Pick(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double minScoreKeep = 0,
            string? diagnosticTag = null,
            LatticeOriginCorner originCorner = LatticeOriginCorner.TopLeft,
            LatticeWeldOrder weldOrder = LatticeWeldOrder.ColMajor,
            int weldInterval = 1,
            bool weldIntervalReverse = true)
        {
            var inner = HalconShapeMatchGridFilter.PickLatticeGridMatches(
                rows, cols, angles, scores, gridRows, gridCols, minScoreKeep, diagnosticTag, originCorner, weldOrder, weldInterval, weldIntervalReverse);
            return ToPickResult(inner);
        }

        /// <summary>RANSAC 鲁棒格网：输入 Find 全点，输出 rows×cols 落格结果，可直接接显示。</summary>
        public static HalconShapeMatchLatticePickResult PickRansac(
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
            int ransacIterations = 500,
            double inlierSnapFactor = 0.45,
            LatticeOriginCorner originCorner = LatticeOriginCorner.TopLeft,
            LatticeWeldOrder weldOrder = LatticeWeldOrder.ColMajor,
            int weldInterval = 1,
            bool weldIntervalReverse = true)
        {
            var inner = HalconShapeMatchGridFilter.PickLatticeRansac(
                rows, cols, angles, scores, gridRows, gridCols,
                minScoreKeep, snapTolerancePx, diagnosticTag, uvProjectionSvgPath,
                ransacIterations, inlierSnapFactor, originCorner, weldOrder, weldInterval, weldIntervalReverse);
            return ToPickResult(inner);
        }

        private static HalconShapeMatchLatticePickResult ToPickResult(
            HalconShapeMatchGridFilter.LatticePickResult inner) =>
            new()
            {
                InputCount = inner.InputCount,
                KeptCount = inner.Rows.Length,
                Rows = inner.Rows,
                Cols = inner.Cols,
                Angles = inner.Angles,
                Scores = inner.Scores,
                GridRow = inner.GridRow,
                GridCol = inner.GridCol,
                KeptIndices = inner.KeptIndices,
                LatticeRows = inner.LatticeRows,
                LatticeCols = inner.LatticeCols,
                AxesSwapped = inner.AxesSwapped,
                LatticeAngleDeg = inner.LatticeAngleDeg,
                ChainDirectionAngleDeg = inner.ChainDirectionAngleDeg,
                ConsensusMatchAngleDeg = inner.ConsensusMatchAngleDeg,
                PitchRow = inner.PitchRow,
                PitchCol = inner.PitchCol,
                TwoColumnDeltaU = inner.TwoColumnDeltaU,
                TwoColumnDeltaV = inner.TwoColumnDeltaV,
                TwoColumnDeltaSummary = inner.TwoColumnDeltaSummary
            };
    }
}
