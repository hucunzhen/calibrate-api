namespace CalibOperatorCLI_Example
{
    /// <summary>2×N 链向条带筛选的分步 API（与 PickLatticeGridMatches 同核心，便于流程拆分调试）。</summary>
    internal static class HalconShapeMatchLatticeStrip
    {
        public static HalconLatticeStripBootstrapResult Bootstrap(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            string? diagnosticTag = null) =>
            HalconShapeMatchGridFilter.StripLatticeBootstrap(rows, cols, angles, scores, gridRows, gridCols, diagnosticTag);

        public static HalconLatticeStripContext? BuildOrient(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            HalconLatticeStripBootstrapResult? bootstrap,
            string? diagnosticTag = null) =>
            HalconShapeMatchGridFilter.StripLatticeBuildOrient(rows, cols, angles, scores, gridRows, gridCols, bootstrap, diagnosticTag);

        public static double ChainAngleFromIndices(double[] cols, double[] rows, int[] indices) =>
            HalconShapeMatchGridFilter.StripChainAngleFromIndices(cols, rows, indices);

        public static HalconLatticeStripContext PickColumn0(
            HalconLatticeStripContext context,
            double[] rows,
            double[] cols,
            double[]? scores,
            string? diagnosticTag = null) =>
            HalconShapeMatchGridFilter.StripLatticePickColumn0(context, rows, cols, scores, diagnosticTag);

        public static HalconShapeMatchChainDirectionResult FillStrip(
            HalconLatticeStripContext context,
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            double minScoreKeep = 0,
            string? diagnosticTag = null)
        {
            var inner = HalconShapeMatchGridFilter.StripLatticeFill(
                context, rows, cols, angles, scores, minScoreKeep, diagnosticTag);
            return ToChainDirectionResult(inner);
        }

        /// <summary>列0 链选 + 条带落格输出（= PickColumn0 + FillStrip）。</summary>
        public static (HalconLatticeStripContext Context, HalconShapeMatchChainDirectionResult Result) PickColumn0AndFill(
            HalconLatticeStripContext context,
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            double minScoreKeep = 0,
            string? diagnosticTag = null)
        {
            var ctx = PickColumn0(context, rows, cols, scores, diagnosticTag);
            var result = FillStrip(ctx, rows, cols, angles, scores, minScoreKeep, diagnosticTag);
            return (ctx, result);
        }

        private static HalconShapeMatchChainDirectionResult ToChainDirectionResult(
            HalconShapeMatchGridFilter.LatticePickResult inner) =>
            new()
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

        public static HalconShapeMatchChainDirectionResult RunFull(
            double[] rows,
            double[] cols,
            double[]? angles,
            double[]? scores,
            int gridRows,
            int gridCols,
            double minScoreKeep = 0,
            string? diagnosticTag = null)
        {
            var boot = Bootstrap(rows, cols, angles, scores, gridRows, gridCols, diagnosticTag);
            var ctx = BuildOrient(rows, cols, angles, scores, gridRows, gridCols, boot, diagnosticTag);
            if (ctx == null)
                return new HalconShapeMatchChainDirectionResult { InputCount = boot.InputCount };
            ctx = PickColumn0(ctx, rows, cols, scores, diagnosticTag);
            return FillStrip(ctx, rows, cols, angles, scores, minScoreKeep, diagnosticTag);
        }
    }
}
