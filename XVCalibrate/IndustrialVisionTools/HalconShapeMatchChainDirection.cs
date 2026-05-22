namespace CalibOperatorCLI_Example
{
    /// <summary>由格点筛选得到的链向角与模板角共识（与 Pick 结果一致）。</summary>
    public sealed class HalconShapeMatchChainDirectionResult
    {
        public int InputCount { get; init; }
        public double ChainDirectionAngleDeg { get; init; } = double.NaN;
        public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
        public double MatchConcentration { get; init; }
        /// <summary>整板格点筛选索引（2×8 最多 16 个）；显示请接此端口。</summary>
        public int[] ConsensusPickIndices { get; init; } = System.Array.Empty<int>();
        /// <summary>第一列链向 N 连（如 8 个），用于链向角/模板角共识。</summary>
        public int[] ChainColumn0Indices { get; init; } = System.Array.Empty<int>();
        /// <summary>与 ConsensusPickIndices 同序的格行列号。</summary>
        public int[] GridRow { get; init; } = System.Array.Empty<int>();
        public int[] GridCol { get; init; } = System.Array.Empty<int>();
        /// <summary>与 ConsensusPickIndices 同序；为 Find 原始坐标（未投影）。</summary>
        public double[] Rows { get; init; } = System.Array.Empty<double>();
        public double[] Cols { get; init; } = System.Array.Empty<double>();
    }

    /// <summary>从 FindShapeModel 结果做格点筛选并估计链向（与「阵列格点筛选」相同核心）。</summary>
    public static class HalconShapeMatchChainDirection
    {
        public static HalconShapeMatchChainDirectionResult Estimate(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            string? diagnosticTag = null)
        {
            var inner = HalconShapeMatchGridFilter.PickLatticeGridMatches(
                rows, cols, angles, scores, gridRows, gridCols, 0, diagnosticTag);

            return new HalconShapeMatchChainDirectionResult
            {
                InputCount = inner.InputCount,
                ChainDirectionAngleDeg = inner.ChainDirectionAngleDeg,
                ConsensusMatchAngleDeg = inner.ConsensusMatchAngleDeg,
                MatchConcentration = inner.MatchConcentration,
                ConsensusPickIndices = inner.KeptIndices,
                ChainColumn0Indices = inner.ChainPickIndices,
                GridRow = inner.GridRow,
                GridCol = inner.GridCol,
                Rows = inner.Rows,
                Cols = inner.Cols
            };
        }
    }
}
