using System;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>每个粗候选一张填充区域 Mask（非轮廓线），精匹配对每张各执行一次。</summary>
    public sealed class HalconCoarseMaskBatch
    {
        /// <summary>生成 Mask 时的原图（精匹配内对其 reduce_domain）。</summary>
        public CalibImage? SourceImage { get; init; }

        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }

        /// <summary>填充区域二值 Mask（区域内 255），与 <see cref="CoarseIndices"/> 等长。</summary>
        public CalibImage[] Masks { get; init; } = Array.Empty<CalibImage>();

        /// <summary>对应原始粗定位数组下标。</summary>
        public int[] CoarseIndices { get; init; } = Array.Empty<int>();

        public double[] CoarseRows { get; init; } = Array.Empty<double>();
        public double[] CoarseCols { get; init; } = Array.Empty<double>();
        public double[] CoarseAngles { get; init; } = Array.Empty<double>();
        public double[]? CoarseScores { get; init; }

        public int Count => Masks.Length;
    }
}
