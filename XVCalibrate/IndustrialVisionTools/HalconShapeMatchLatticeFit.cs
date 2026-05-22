namespace CalibOperatorCLI_Example
{
    /// <summary>阵列聚类拟合结果（列/行中心线、方向、格距等）。</summary>
    public sealed class HalconShapeMatchLatticeFitResult
    {
        public int InputCount { get; init; }
        public double EstimatedAngleDeg { get; init; }
        public bool AxesSwapped { get; init; }
        public int LatticeRows { get; init; }
        public int LatticeCols { get; init; }
        public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
        /// <summary>N 连（如 2×8 取 8 颗）在图像中的排布方向角（°），非 HALCON 模板旋转角。</summary>
        public double ChainDirectionAngleDeg { get; init; } = double.NaN;
        public double MatchConcentration { get; init; }
        public double PitchCol { get; init; }
        public double PitchRow { get; init; }
        public double SnapU { get; init; }
        public double SnapV { get; init; }
        public double[] ColCenterU { get; init; } = System.Array.Empty<double>();
        public double[] RowCenterV { get; init; } = System.Array.Empty<double>();
        public double[] CellRow { get; init; } = System.Array.Empty<double>();
        public double[] CellCol { get; init; } = System.Array.Empty<double>();
        public bool[] CellFound { get; init; } = System.Array.Empty<bool>();
        public double[] CellAngle { get; init; } = System.Array.Empty<double>();
        public int[] PointGridRow { get; init; } = System.Array.Empty<int>();
        public int[] PointGridCol { get; init; } = System.Array.Empty<int>();
    }

    /// <summary>阵列聚类拟合可选上游链向/共识（由「链向角估计」算子提供）。</summary>
    public sealed class HalconShapeMatchLatticeFitOptions
    {
        public double? ChainDirectionAngleDeg { get; init; }
        public double? ConsensusMatchAngleDeg { get; init; }
        public int[]? ConsensusPickIndices { get; init; }
    }

    /// <summary>HALCON 形状匹配阵列聚类拟合（定向 + 列/行中心线聚类）。</summary>
    public static class HalconShapeMatchLatticeFit
    {
        public static HalconShapeMatchLatticeFitResult Fit(
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
            HalconShapeMatchLatticeFitOptions? chainInputs = null) =>
            ToPublic(HalconShapeMatchGridFilter.FitLattice(
                rows, cols, angles, scores,
                gridRows, gridCols,
                pitchRow, pitchCol,
                gridAngleDeg,
                snapTolerancePx,
                diagnosticTag,
                chainInputs?.ChainDirectionAngleDeg,
                chainInputs?.ConsensusMatchAngleDeg,
                chainInputs?.ConsensusPickIndices));

        internal static HalconShapeMatchLatticeFitResult ToPublic(HalconShapeMatchGridFilter.LatticeFitResult inner) =>
            new()
            {
                InputCount = inner.InputCount,
                EstimatedAngleDeg = inner.EstimatedAngleDeg,
                AxesSwapped = inner.AxesSwapped,
                LatticeRows = inner.Lattice.Rows,
                LatticeCols = inner.Lattice.Cols,
                ConsensusMatchAngleDeg = inner.ConsensusMatchAngleDeg,
                ChainDirectionAngleDeg = inner.ChainDirectionAngleDeg,
                MatchConcentration = inner.MatchConcentration,
                PitchCol = inner.PitchCol,
                PitchRow = inner.PitchRow,
                SnapU = inner.SnapU,
                SnapV = inner.SnapV,
                ColCenterU = inner.Lattice.ColU,
                RowCenterV = inner.Lattice.RowV,
                CellRow = inner.CellRow,
                CellCol = inner.CellCol,
                CellFound = inner.CellFound,
                CellAngle = inner.CellAngleDeg,
                PointGridRow = inner.PointGridRow,
                PointGridCol = inner.PointGridCol
            };
    }
}
