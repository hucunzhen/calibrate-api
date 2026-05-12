#if HALCON_ENABLED
using System;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    internal static partial class HalconDlSegBridge
    {
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

        /// <summary>
        /// 将分割标签转为 uint2（类别索引图）；byte 像素值直接类型转换，不做值缩放。
        /// </summary>
        internal static HObject ConvertDlSegmentationToUint2(HObject hoSeg)
        {
            HOperatorSet.GetImageType(hoSeg, out HTuple typ);
            string ts = typ.Length >= 1 ? typ[0].S : typ.S;
            if (ts == "uint2")
                return hoSeg;
            HOperatorSet.ConvertImageType(hoSeg, out HObject segU2, "uint2");
            hoSeg.Dispose();
            return segU2;
        }

        /// <summary>均匀权重图：real 1.0。</summary>
        internal static HObject CreateDlTrainingUniformWeightRealAlways(int width, int height)
        {
            HOperatorSet.GenImageConst(out HObject w0, new HTuple("real"), new HTuple(width), new HTuple(height));
            HOperatorSet.ScaleImage(w0, out HObject w1, new HTuple(0.0), new HTuple(1.0));
            w0.Dispose();
            return w1;
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

        /// <summary>均匀类别权重时常为 byte（与部分官方预处理输出一致）。</summary>
        internal static HObject CreateDlTrainingUniformWeightByte(int width, int height)
        {
            HOperatorSet.GenImageConst(out HObject w0, new HTuple("byte"), new HTuple(width), new HTuple(height));
            HOperatorSet.ScaleImage(w0, out HObject w1, new HTuple(0), new HTuple(255));
            w0.Dispose();
            return w1;
        }

        /// <summary>训练算子对非全域 domain 敏感；Zoom 后仍调用 FullDomain。</summary>
        internal static HObject EnsureDlTrainingFullDomain(HObject img)
        {
            HOperatorSet.FullDomain(img, out HObject full);
            img.Dispose();
            return full;
        }
    }
}
#endif
