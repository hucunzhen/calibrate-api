#if HALCON_ENABLED
using System;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HALCON 深度学习语义分割推理（需已训练的 .hdl 模型）。
    /// 托管侧微调可使用 HalconDlSegTrainRunner（train_dl_model_batch）；完整官方预处理仍以 HDevelop 为准。
    /// </summary>
    internal static class HalconDlSegBridge
    {
        /// <summary>从模型参数推断输入通道数（常见语义分割为 3）；失败时默认 3（RGB）。</summary>
        internal static int TryGetDlModelInputChannels(HDlModel model, Action<string>? log)
        {
            foreach (string key in new[] { "num_channels", "number_of_channels", "image_num_channels", "channels" })
            {
                try
                {
                    HTuple t = model.GetDlModelParam(key);
                    if (t == null || t.Length < 1)
                        continue;
                    int c = t[0].I;
                    if (c == 1 || c == 3)
                    {
                        log?.Invoke($"模型输入通道数 {key}={c}");
                        return c;
                    }
                }
                catch
                {
                    /* try next key */
                }
            }

            log?.Invoke("[warn] 无法读取模型输入通道数，默认使用 3（RGB）。若为单通道模型仍报错，请在 HDevelop 中查看 get_dl_model_param 可用键。");
            return 3;
        }

        /// <summary>
        /// 将 ReadImage 得到的图像调整为 <paramref name="modelChannels"/> 通道。
        /// 若返回新图像则会 dispose 原 <paramref name="hoRead"/>；若无需转换则返回同一引用。
        /// </summary>
        internal static HObject PrepareDlSampleInputImage(HObject hoRead, int modelChannels, string imagePath)
        {
            HOperatorSet.CountChannels(hoRead, out HTuple nch);
            int src = nch.I;
            if (src == modelChannels)
                return hoRead;

            if (src == 4 && modelChannels == 3)
            {
                HOperatorSet.AccessChannel(hoRead, out HObject r, 1);
                HOperatorSet.AccessChannel(hoRead, out HObject g, 2);
                HOperatorSet.AccessChannel(hoRead, out HObject b, 3);
                hoRead.Dispose();
                HOperatorSet.Compose3(r, g, b, out HObject rgb);
                r.Dispose();
                g.Dispose();
                b.Dispose();
                return rgb;
            }

            if (src == 3 && modelChannels == 1)
            {
                HOperatorSet.Rgb1ToGray(hoRead, out HObject gray);
                hoRead.Dispose();
                return gray;
            }

            if (src == 1 && modelChannels == 3)
            {
                HOperatorSet.CopyImage(hoRead, out HObject r);
                HOperatorSet.CopyImage(hoRead, out HObject g);
                HOperatorSet.CopyImage(hoRead, out HObject b);
                hoRead.Dispose();
                HOperatorSet.Compose3(r, g, b, out HObject rgb);
                r.Dispose();
                g.Dispose();
                b.Dispose();
                return rgb;
            }

            hoRead.Dispose();
            throw new InvalidOperationException(
                $"图像通道数 {src} 无法转换为模型需要的 {modelChannels}: {imagePath}");
        }

        /// <summary>
        /// 语义分割标签须为单通道；彩色 PNG 会先转灰度。
        /// </summary>
        internal static HObject EnsureDlSegmentationSingleChannel(HObject hoSeg, string segPath)
        {
            HOperatorSet.CountChannels(hoSeg, out HTuple snch);
            int c = snch.I;
            if (c == 1)
                return hoSeg;
            if (c == 3)
            {
                HOperatorSet.Rgb1ToGray(hoSeg, out HObject g);
                hoSeg.Dispose();
                return g;
            }

            hoSeg.Dispose();
            throw new InvalidOperationException($"分割标签应为单通道（或 RGB 可降灰度），当前 {c} 通道: {segPath}");
        }

        /// <summary>将缩放后的 byte 多通道图转为 real，灰度按 /255 映射到约 [0,1]。</summary>
        internal static HObject ConvertDlTrainingByteImageToReal01(HObject imgByte)
        {
            HOperatorSet.ConvertImageType(imgByte, out HObject imgReal, "real");
            imgByte.Dispose();
            HOperatorSet.ScaleImage(imgReal, out HObject scaled, new HTuple(1.0 / 255.0), new HTuple(0));
            imgReal.Dispose();
            return scaled;
        }

        /// <summary>与 gen_dl_segmentation_weights 一致，像素权重为 real。</summary>
        internal static HObject CreateDlTrainingUniformWeightReal(int width, int height)
        {
            HOperatorSet.GenImageConst(out HObject w0, new HTuple("real"), new HTuple(width), new HTuple(height));
            HOperatorSet.ScaleImage(w0, out HObject w1, new HTuple(0.0), new HTuple(1.0));
            w0.Dispose();
            return w1;
        }

        /// <summary>均匀类别权重时常为 byte（与部分官方预处理输出一致）；若仍 #9001 可对照 gen_dl_segmentation_weights。</summary>
        internal static HObject CreateDlTrainingUniformWeightByte(int width, int height)
        {
            HOperatorSet.GenImageConst(out HObject w0, new HTuple("byte"), new HTuple(width), new HTuple(height));
            HOperatorSet.ScaleImage(w0, out HObject w1, new HTuple(0), new HTuple(255));
            w0.Dispose();
            return w1;
        }

        /// <summary>训练算子对非全域 domain 敏感；Zoom 后仍调用 FullDomain 可避免 #9001。</summary>
        internal static HObject EnsureDlTrainingFullDomain(HObject img)
        {
            HOperatorSet.FullDomain(img, out HObject full);
            img.Dispose();
            return full;
        }

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
