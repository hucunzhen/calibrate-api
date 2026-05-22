namespace CalibOperatorCLI_Example
{
    /// <summary>2×N 链向条带筛选的中间状态，在链向子算子间传递。</summary>
    internal sealed class HalconLatticeStripContext
    {
        public int InputCount { get; init; }
        public int UserGridRows { get; init; }
        public int UserGridCols { get; init; }
        public int EffRows { get; init; }
        public int EffCols { get; init; }
        public bool AxesSwapped { get; init; }
        public double LatticeAngleDeg { get; init; }
        /// <summary>引导步 PCA 主轴（°），非点列几何链向。</summary>
        public double PcaAxisDeg { get; init; } = double.NaN;
        public double AngleRad { get; init; }
        public double PitchU { get; init; }
        public double PitchV { get; init; }
        public double SnapU { get; init; }
        public double SnapV { get; init; }
        public int ChainWindow { get; init; }
        public double[] U { get; init; } = System.Array.Empty<double>();
        public double[] V { get; init; } = System.Array.Empty<double>();
        internal HalconShapeMatchGridFilter.RowColumnLattice Lattice { get; init; } =
            new HalconShapeMatchGridFilter.RowColumnLattice();
        public int[] BootstrapPickIndices { get; init; } = System.Array.Empty<int>();
        public int[] Column0PickIndices { get; init; } = System.Array.Empty<int>();
        internal string? DiagnosticTag { get; init; }

        internal HalconLatticeStripContext WithColumn0(int[] col0, HalconShapeMatchGridFilter.RowColumnLattice lattice,
            double snapU, double snapV, double[]? u = null, double[]? v = null) =>
            new()
            {
                InputCount = InputCount,
                UserGridRows = UserGridRows,
                UserGridCols = UserGridCols,
                EffRows = EffRows,
                EffCols = EffCols,
                AxesSwapped = AxesSwapped,
                LatticeAngleDeg = LatticeAngleDeg,
                PcaAxisDeg = PcaAxisDeg,
                AngleRad = AngleRad,
                PitchU = PitchU,
                PitchV = PitchV,
                SnapU = snapU,
                SnapV = snapV,
                ChainWindow = ChainWindow,
                U = u ?? U,
                V = v ?? V,
                Lattice = lattice,
                BootstrapPickIndices = BootstrapPickIndices,
                Column0PickIndices = col0,
                DiagnosticTag = DiagnosticTag
            };
    }

    internal sealed class HalconLatticeStripBootstrapResult
    {
        public int InputCount { get; init; }
        public int[] BootstrapPickIndices { get; init; } = System.Array.Empty<int>();
        /// <summary>u/v 格网 u 轴角 θ（°）：几何分列 + 格聚评分，使投影点在 u/v 尽量聚团。</summary>
        public double LatticeAngleDeg { get; init; } = double.NaN;
        /// <summary>流程端口 ChainBootstrapAngle：与 <see cref="LatticeAngleDeg"/> 相同。</summary>
        public double ChainBootstrapAngleDeg => LatticeAngleDeg;
        /// <summary>v 轴在图像平面从 +Col 朝 +Row 的方位角（°）。</summary>
        public double VAxisImageAngleDeg { get; init; } = double.NaN;
        /// <summary>全板点云 PCA 主轴（°），HALCON 条带链向参考，非落格 θ。</summary>
        public double PcaAxisAngleDeg { get; init; } = double.NaN;
        public double ConsensusMatchAngleDeg { get; init; } = double.NaN;
        public double MatchConcentration { get; init; }
    }
}
