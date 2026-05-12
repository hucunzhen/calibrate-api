#if HALCON_ENABLED
using System;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>训练前模型与样本张量的诊断输出。</summary>
    internal static class HalconDlSegTrainDiagnostics
    {
        internal static void DescribeImage(string tag, HObject img, Action<string> log)
        {
            try
            {
                HOperatorSet.CountChannels(img, out HTuple nc);
                HOperatorSet.GetImageType(img, out HTuple typ);
                string ts = typ.Length >= 1 ? typ[0].S : typ.S;
                HOperatorSet.GetImageSize(img, out HTuple iw, out HTuple ih);
                log($"[diag] {tag}: {iw.I}x{ih.I}, channels={nc.I}, pixel_type={ts}");
            }
            catch (Exception ex)
            {
                log($"[diag] {tag}: 读取失败 ({ex.Message})");
            }
        }

        /// <summary>从已构建的 DLSample 读取三张图并打印类型（用于首步核对 batch 内每张样本）。</summary>
        internal static void DescribeDlSample(HDict sample, string tag, Action<string> log)
        {
            foreach (string key in new[] { "image", "segmentation_image", "weight_image" })
            {
                HObject? ho = null;
                try
                {
                    ho = sample.GetDictObject(key);
                    DescribeImage($"{tag} · {key}", ho, log);
                }
                catch (Exception ex)
                {
                    log($"[diag] {tag} · {key}: 读取失败 ({ex.Message})");
                }
                finally
                {
                    ho?.Dispose();
                }
            }
        }

        /// <summary>
        /// train_dl_model_batch 要求的元组长度为 batch_size × batch_size_multiplier（见官方文档）。
        /// </summary>
        internal static int TryGetBatchSizeMultiplier(HDlModel model, Action<string> log)
        {
            try
            {
                HTuple t = model.GetDlModelParam("batch_size_multiplier");
                if (t != null && t.Length >= 1)
                {
                    int m = t[0].I;
                    if (m >= 1)
                    {
                        if (m > 1)
                            log($"[diag] batch_size_multiplier={m}：每步须提交 UI batch_size×{m} 个 DLSample。");
                        return m;
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[diag] batch_size_multiplier 读取失败，按 1 处理: {ex.Message}");
            }

            return 1;
        }

        internal static void LogDlModelDiagnostics(HDlModel model, Action<string> log)
        {
            foreach (string key in new[] { "type", "image_type", "pix_type", "num_channels", "batch_size_multiplier", "intensity_range", "class_ids" })
            {
                try
                {
                    HTuple t = model.GetDlModelParam(key);
                    log($"[diag] get_dl_model_param('{key}') = {t}");
                }
                catch (Exception ex)
                {
                    log($"[diag] get_dl_model_param('{key}') 不可用: {ex.Message}");
                }
            }
        }
    }
}
#endif
