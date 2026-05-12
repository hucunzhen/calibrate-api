#if HALCON_ENABLED
using System;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    internal static partial class HalconDlSegBridge
    {
        /// <summary>
        /// 对单张图像运行分割模型，返回与输入同尺寸的类别索引图（像素含义与训练时 segmentation_image 一致）。
        /// </summary>
        public static CalibImage RunSegmentationInference(string modelHdlPath, string imagePath)
        {
            if (string.IsNullOrWhiteSpace(modelHdlPath) || !System.IO.File.Exists(modelHdlPath))
                throw new ArgumentException("模型文件无效。", nameof(modelHdlPath));
            if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
                throw new ArgumentException("图像路径无效。", nameof(imagePath));

            HObject? hoRead = null;
            HObject? hoScaled = null;
            HObject? segObj = null;
            try
            {
                HOperatorSet.ReadImage(out hoRead, imagePath);
                var model = new HDlModel();
                model.ReadDlModel(modelHdlPath);

                int modelCh = TryGetDlModelInputChannels(model, null);
                hoRead = PrepareDlSampleInputImage(hoRead, modelCh, imagePath);

                HTuple dims = model.GetDlModelParam("image_dimensions");
                if (dims == null || dims.Length < 2)
                    throw new InvalidOperationException("无法从模型读取 image_dimensions；请确认模型类型为 segmentation。");
                int targetH = dims[0].I;
                int targetW = dims[1].I;
                if (targetW <= 1 || targetH <= 1)
                    throw new InvalidOperationException($"无效的 image_dimensions: {targetW}x{targetH}");

                HOperatorSet.ZoomImageSize(hoRead, out hoScaled, targetW, targetH, "constant");
                hoRead.Dispose();
                hoRead = null;

                var sample = new HDict();
                sample.CreateDict();
                sample.SetDictObject(hoScaled, "image");

                HDict[] batchOut = model.ApplyDlModel(new[] { sample }, new HTuple("segmentation_image"));
                if (batchOut == null || batchOut.Length < 1)
                    throw new InvalidOperationException("apply_dl_model 未返回结果。");

                segObj = batchOut[0].GetDictObject("segmentation_image");
                CalibImage calib = HalconFlowBridge.ToCalibGray(segObj);
                return calib;
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "HALCON 推理失败。若与样本字典键或预处理有关，请在 HDevelop 中参考官方语义分割推理示例（gen_dl_samples_from_images、preprocess_dl_samples 后再 apply_dl_model）。详情: "
                    + ex.Message, ex);
            }
            finally
            {
                hoRead?.Dispose();
                hoScaled?.Dispose();
                segObj?.Dispose();
            }
        }
    }
}
#endif
