#if HALCON_ENABLED
using System;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>从磁盘路径构建 train_dl_model_batch 所需的单张 DLSample。</summary>
    internal static class HalconDlSegTrainSampleBuilder
    {
        /// <summary>构建单张 DLSample：image、segmentation_image、weight_image。</summary>
        /// <remarks>
        /// 类型组合：image 为 real [0,1]；segmentation_image 为 uint2；weight_image 为 real 1.0；均 FullDomain。
        /// </remarks>
        internal static HDict BuildSample(
            string imgPath,
            string segPath,
            int targetW,
            int targetH,
            int modelChannels,
            Action<string>? diagLog)
        {
            HObject? hoWork = null;
            HObject? hoSeg = null;
            HObject? imgZ = null;
            HObject? segZ = null;
            HObject? wimg = null;

            try
            {
                HOperatorSet.ReadImage(out HObject hoRead, imgPath);
                if (diagLog != null)
                {
                    diagLog($"[diag] 首张样本文件: image={imgPath}");
                    diagLog($"[diag] 首张样本文件: seg={segPath}");
                    diagLog($"[diag] 模型期望输入通道数 modelChannels={modelChannels}, 目标缩放 {targetW}x{targetH}");
                    HalconDlSegTrainDiagnostics.DescribeImage("① ReadImage 后（原始输入）", hoRead, diagLog);
                }

                hoWork = HalconDlSegBridge.PrepareDlSampleInputImage(hoRead, modelChannels, imgPath);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("② 对齐通道后（将送入 Zoom）", hoWork, diagLog);

                HOperatorSet.ReadImage(out hoSeg, segPath);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("③ ReadImage 分割标签（原始）", hoSeg, diagLog);

                hoSeg = HalconDlSegBridge.EnsureDlSegmentationSingleChannel(hoSeg, segPath);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("④ 分割标签（强制单通道后）", hoSeg, diagLog);

                HOperatorSet.ZoomImageSize(hoWork, out imgZ, targetW, targetH, "constant");
                hoWork.Dispose();
                hoWork = null;
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("⑤ image Zoom 后（byte，将转 real）", imgZ, diagLog);

                HOperatorSet.ZoomImageSize(hoSeg, out segZ, targetW, targetH, "nearest_neighbor");
                hoSeg.Dispose();
                hoSeg = null;
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("⑥ segmentation Zoom 后（byte，将转 uint2）", segZ, diagLog);

                segZ = HalconDlSegBridge.ConvertDlSegmentationToUint2(segZ);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("⑥b segmentation → uint2", segZ, diagLog);

                imgZ = HalconDlSegBridge.ConvertDlTrainingByteImageToReal01(imgZ);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("⑦ image → real [0,1]", imgZ, diagLog);

                wimg = HalconDlSegBridge.CreateDlTrainingUniformWeightRealAlways(targetW, targetH);
                if (diagLog != null)
                    HalconDlSegTrainDiagnostics.DescribeImage("⑧ weight_image（real 1.0）", wimg, diagLog);

                imgZ = HalconDlSegBridge.EnsureDlTrainingFullDomain(imgZ);
                segZ = HalconDlSegBridge.EnsureDlTrainingFullDomain(segZ);
                wimg = HalconDlSegBridge.EnsureDlTrainingFullDomain(wimg);
                if (diagLog != null)
                {
                    HalconDlSegTrainDiagnostics.DescribeImage("⑨ image FullDomain 后（写入字典）", imgZ, diagLog);
                    HalconDlSegTrainDiagnostics.DescribeImage("⑩ segmentation_image FullDomain 后（写入字典）", segZ, diagLog);
                    HalconDlSegTrainDiagnostics.DescribeImage("⑪ weight_image FullDomain 后（写入字典）", wimg, diagLog);
                }

                var sample = new HDict();
                sample.CreateDict();
                sample.SetDictObject(imgZ, "image");
                sample.SetDictObject(segZ, "segmentation_image");
                sample.SetDictObject(wimg, "weight_image");
                imgZ = segZ = wimg = null;

                return sample;
            }
            finally
            {
                hoWork?.Dispose();
                hoSeg?.Dispose();
                imgZ?.Dispose();
                segZ?.Dispose();
                wimg?.Dispose();
            }
        }

        internal static void DisposeSamples(HDict[] batch)
        {
            foreach (HDict? d in batch)
            {
                try
                {
                    d?.Dispose();
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}
#endif
