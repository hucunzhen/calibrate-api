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
        public double[] CoarseScales { get; init; } = Array.Empty<double>();
        public double[]? CoarseScores { get; init; }

        /// <summary>送入批构建的粗候选个数（压缩/回退前）。</summary>
        public int InputCandidateCount { get; init; }

        /// <summary>轮廓填充失败、改用位姿矩形回退的张数。这些轮仍保留，不从循环中丢掉。</summary>
        public int FallbackMaskCount { get; init; }

        public int Count => Masks.Length;
    }
}
