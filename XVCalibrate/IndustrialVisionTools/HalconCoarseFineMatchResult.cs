#if HALCON_ENABLED
using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>刚性粗定位 + 可变形精匹配结果。</summary>
    public sealed class HalconCoarseFineMatchResult
    {
        public static HalconCoarseFineMatchResult Empty { get; } = new HalconCoarseFineMatchResult();

        public double[] CoarseRows { get; init; } = Array.Empty<double>();
        public double[] CoarseCols { get; init; } = Array.Empty<double>();
        public double[] CoarseAngles { get; init; } = Array.Empty<double>();
        public double[] CoarseScales { get; init; } = Array.Empty<double>();
        public double[] CoarseScores { get; init; } = Array.Empty<double>();

        /// <summary>精匹配位置（失败时该候选不出现）。</summary>
        public double[] FineRows { get; init; } = Array.Empty<double>();
        public double[] FineCols { get; init; } = Array.Empty<double>();
        /// <summary>精匹配角度：沿用对应粗匹配角度（可变形 API 不单独返回角度）。</summary>
        public double[] FineAngles { get; init; } = Array.Empty<double>();
        public double[] FineScores { get; init; } = Array.Empty<double>();
        public double[] FineScales { get; init; } = Array.Empty<double>();

        /// <summary>与 Fine* 一一对应的变形轮廓（图像坐标）。</summary>
        public HalconXldContourBundle? DeformedXld { get; init; }

        public int FineCount => FineRows.Length;
        public int CoarseCount => CoarseRows.Length;
    }
}
#endif
