#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    /// <summary>HALCON TrainDlModelBatch 微调参数（HalconDlSeg 页面）。</summary>
    internal sealed class HalconDlSegTrainOptions
    {
        public string ExportRoot { get; set; } = "";
        public string PretrainedHdlPath { get; set; } = "";
        public string OutputHdlPath { get; set; } = "";
        public int Epochs { get; set; } = 5;
        public int BatchSize { get; set; } = 2;
        public double LearningRate { get; set; } = 0.001;
        public bool UseGpu { get; set; } = true;

        /// <summary>
        /// 历史选项：当前训练路径固定为 real image + uint2 分割（见 HalconDlSegTrainSampleBuilder），此字段保留供 UI 持久化兼容。
        /// </summary>
        public bool NormalizeTrainingImageToReal01 { get; set; }
    }
}
#endif
