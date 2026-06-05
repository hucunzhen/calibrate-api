#if HALCON_ENABLED
using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>创建模型时缓存的参数，用于生成编码文件名。</summary>
    internal sealed class HalconShapeModelMetaDocument
    {
        public DateTime CreatedUtc { get; set; }
        public int Epoch { get; set; }
        public double MinGray { get; set; }
        public double MaxGray { get; set; } = 255;
        public string? ResolvedMetric { get; set; }
        public HalconShapeModelCreateOptions CreateOptions { get; set; } = new();
    }
}
#endif
