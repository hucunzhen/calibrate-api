#if HALCON_ENABLED
using System;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HALCON 深度学习语义分割：模型通道推断、训练前图像/标签预处理与推理入口。
    /// 托管侧微调见 <see cref="HalconDlSegTrainRunner"/>；完整官方流程仍以 HDevelop 为准。
    /// </summary>
    internal static partial class HalconDlSegBridge
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
    }
}
#endif
