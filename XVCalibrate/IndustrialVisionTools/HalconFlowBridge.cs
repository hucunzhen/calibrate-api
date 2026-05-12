#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// CalibImage ↔ HALCON HObject（灰度 / BGR 字节图）转换，供 Flow HALCON 算子使用。
    /// </summary>
    internal static class HalconFlowBridge
    {
        /// <summary>经临时 BMP + ReadImage 导入，避免 GenImage1 与指针在 HalconDotNet 下的 Tuple 互操作问题。</summary>
        public static HObject CalibToHObject(CalibImage img)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            using Bitmap? bmp = img.ToBitmap();
            if (bmp == null)
                throw new InvalidOperationException("HALCON: 无法转换为位图");

            string tmp = Path.Combine(Path.GetTempPath(), "halcon_flow_" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                bmp.Save(tmp, ImageFormat.Bmp);
                HOperatorSet.ReadImage(out HObject himg, tmp);
                return himg;
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignored
                }
            }
        }

        /// <summary>避免 HTuple 与 IntPtr 隐式混用触发 “Illegal operation on Tuple / HTupleString”。</summary>
        private static int TupleFirstInt(HTuple t)
        {
            if (t == null || t.TupleLength() <= 0) return 0;
            return t[0].I;
        }

        private static void EnsureSameImageSize(CalibImage a, CalibImage b, string opName)
        {
            if (a == null || b == null) return;
            a.RefreshProperties();
            b.RefreshProperties();
            var na = a.GetNativeStruct();
            var nb = b.GetNativeStruct();
            if (na.width != nb.width || na.height != nb.height)
                throw new InvalidOperationException($"HALCON {opName}: 两幅图须同尺寸 {na.width}x{na.height} 与 {nb.width}x{nb.height}");
        }

        /// <summary>
        /// CALIB_LoadImageFile 会把 BMP 统一成 <b>3 通道 BGR</b>（见 CalibOperator.cpp MatToImageBGR），
        /// 而 SetDarkBinary / SetMask / ApplyMask 等按 <b>单通道 width×height</b> 逐像素使用缓冲区。
        /// 因此 HALCON 结果在回写 CalibImage 后必须再压成 <b>1 通道</b>，才能与原有算子衔接。
        /// </summary>
        public static CalibImage ToCalibGray(HObject ho)
        {
            if (ho == null)
                throw new InvalidOperationException("HALCON: 输出图像无效");

            // 经临时文件导出再加载，避免 GetImagePointer1 在 HalconDotNet 下的 Tuple/指针互操作异常
            string tmp = Path.Combine(Path.GetTempPath(), "halcon_flow_gray_" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                HOperatorSet.WriteImage(ho, "bmp", 0, tmp);
                using CalibImage loaded = CalibAPI.LoadImage(tmp);
                return ToSingleChannelGray(loaded);
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignored
                }
            }
        }

        /// <summary>与 FlowPage.FillGrayBytesFromCalib 使用相同的 BGR→灰度权值，保证与灰度混合等算子一致。</summary>
        public static CalibImage ToSingleChannelGray(CalibImage img)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            img.RefreshProperties();
            if (img.Channels == 1)
                return DuplicateSingleChannel(img);

            if (img.Channels != 3)
                throw new InvalidOperationException($"HALCON: CalibImage 通道数 {img.Channels}，仅支持 1 或 3");

            var n = img.GetNativeStruct();
            int w = n.width, h = n.height, nPix = w * h;
            if (n.data == IntPtr.Zero || nPix <= 0)
                throw new InvalidOperationException("HALCON: 源 CalibImage 无有效数据");

            var bgr = new byte[nPix * 3];
            Marshal.Copy(n.data, bgr, 0, nPix * 3);
            var gray = new byte[nPix];
            for (int p = 0; p < nPix; p++)
            {
                int b = bgr[p * 3], g = bgr[p * 3 + 1], r = bgr[p * 3 + 2];
                gray[p] = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
            }

            var dst = new CalibImage(w, h, 1);
            var dn = dst.GetNativeStruct();
            Marshal.Copy(gray, 0, dn.data, nPix);
            return dst;
        }

        private static CalibImage DuplicateSingleChannel(CalibImage src)
        {
            var n = src.GetNativeStruct();
            int nPix = n.width * n.height;
            if (n.data == IntPtr.Zero || nPix <= 0)
                throw new InvalidOperationException("HALCON: 源 CalibImage 无有效数据");
            var dst = new CalibImage(n.width, n.height, 1);
            var dn = dst.GetNativeStruct();
            var buf = new byte[nPix];
            Marshal.Copy(n.data, buf, 0, nPix);
            Marshal.Copy(buf, 0, dn.data, nPix);
            return dst;
        }

        /// <summary>若为 RGB/BGR 三通道则转灰度，否则返回原对象（调用方负责 Dispose）。</summary>
        public static HObject EnsureGray(HObject ho)
        {
            HOperatorSet.CountChannels(ho, out HTuple cn);
            int nch = TupleFirstInt(cn);
            if (nch <= 1)
                return ho;
            if (nch != 3)
                throw new InvalidOperationException($"HALCON: Rgb1ToGray 需要 3 通道，当前 CountChannels={nch}");

            HOperatorSet.Rgb1ToGray(ho, out HObject gray);
            ho.Dispose();
            return gray;
        }

        /// <summary>Threshold + RegionToBin，输出与 Flow 一致的 byte 灰度二值图。</summary>
        public static CalibImage ThresholdToCalibGray(CalibImage inImg, double minGray, double maxGray)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.Threshold(ho, out HObject region, minGray, maxGray);
                try
                {
                    // 宽高使用独立 HTuple，避免把 GetImageSize 返回的 tuple 直接传入 RegionToBin 触发类型歧义
                    HOperatorSet.RegionToBin(region, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>ScaleImage：线性缩放 Gray&apos; = Mult * Gray + Add，对比度/亮度调节。</summary>
        public static CalibImage ScaleImageToCalib(CalibImage inImg, double mult, double add)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.ScaleImage(ho, out HObject outHo, mult, add);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>ScaleImageMax：按最大值拉伸动态范围（整幅归一化）。</summary>
        public static CalibImage ScaleImageMaxToCalib(CalibImage inImg)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.ScaleImageMax(ho, out HObject outHo);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>Illuminate：局部亮度校正（大视野光照不均时常用）。</summary>
        public static CalibImage IlluminateToCalib(CalibImage inImg, int maskWidth, int maskHeight, double factor)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskWidth = Math.Max(3, maskWidth | 1);
            maskHeight = Math.Max(3, maskHeight | 1);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.Illuminate(ho, out HObject outHo, maskWidth, maskHeight, factor);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>MeanImage：均值平滑，可作 DynThreshold 的参考图。</summary>
        public static CalibImage MeanImageToCalib(CalibImage inImg, int maskWidth, int maskHeight)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskWidth = Math.Max(1, maskWidth);
            maskHeight = Math.Max(1, maskHeight);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.MeanImage(ho, out HObject outHo, maskWidth, maskHeight);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>GaussFilter：高斯平滑；Size 为奇数。</summary>
        public static CalibImage GaussFilterToCalib(CalibImage inImg, int size)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if ((size & 1) == 0) size++;
            size = Math.Max(3, size);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GaussFilter(ho, out HObject outHo, size);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>GrayClosingRect：灰度闭运算，连接暗条、填小孔。</summary>
        public static CalibImage GrayClosingRectToCalib(CalibImage inImg, int maskHeight, int maskWidth)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskHeight = Math.Max(1, maskHeight);
            maskWidth = Math.Max(1, maskWidth);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GrayClosingRect(ho, out HObject outHo, maskHeight, maskWidth);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>Emphasize：强调图像细节（与既有 Flow 节点一致，经 HOperatorSet）。</summary>
        public static CalibImage EmphasizeToCalib(CalibImage inImg, int maskWidth, int maskHeight, double factor)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskWidth = Math.Max(1, maskWidth);
            maskHeight = Math.Max(1, maskHeight);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.Emphasize(ho, out HObject outHo, maskWidth, maskHeight, factor);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>GrayOpeningRect：灰度开运算。</summary>
        public static CalibImage GrayOpeningRectToCalib(CalibImage inImg, int maskHeight, int maskWidth)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskHeight = Math.Max(1, maskHeight);
            maskWidth = Math.Max(1, maskWidth);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GrayOpeningRect(ho, out HObject outHo, maskHeight, maskWidth);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>BinaryThreshold：自动阈值（如 max_separability），再 RegionToBin。</summary>
        public static CalibImage BinaryThresholdToCalibGray(CalibImage inImg, string method, string lightDark)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(method)) method = "max_separability";
            if (string.IsNullOrWhiteSpace(lightDark)) lightDark = "dark";

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.BinaryThreshold(ho, out HObject region, new HTuple(method), new HTuple(lightDark), out HTuple _);
                try
                {
                    HOperatorSet.RegionToBin(region, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>DynThreshold：局部自适应阈值（需原始图 + 参考图，例如 MeanImage 输出）。</summary>
        public static CalibImage DynThresholdToCalibGray(CalibImage original, CalibImage reference, double offset, string lightDark)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (string.IsNullOrWhiteSpace(lightDark)) lightDark = "dark";

            HObject ho = CalibToHObject(original);
            HObject href = CalibToHObject(reference);
            try
            {
                ho = EnsureGray(ho);
                href = EnsureGray(href);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.DynThreshold(ho, href, out HObject region, offset, lightDark);
                try
                {
                    HOperatorSet.RegionToBin(region, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
                href.Dispose();
            }
        }

        /// <summary>VarThreshold：基于局部灰度方差的自适应阈值（单图；HalconDotNet 为 6 个标量/元组参数 + LightDark）。</summary>
        public static CalibImage VarThresholdToCalibGray(CalibImage inImg, int maskWidth, int maskHeight, double stdDevScale, double absThreshold, string lightDark)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(lightDark)) lightDark = "dark";
            maskWidth = Math.Max(1, maskWidth | 1);
            maskHeight = Math.Max(1, maskHeight | 1);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.VarThreshold(ho, out HObject region, maskWidth, maskHeight, stdDevScale, absThreshold, lightDark);
                try
                {
                    HOperatorSet.RegionToBin(region, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>MedianImage：中值滤波，抑制椒盐噪点。</summary>
        public static CalibImage MedianImageToCalib(CalibImage inImg, string maskType, int radius, string margin)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(maskType)) maskType = "circle";
            if (string.IsNullOrWhiteSpace(margin)) margin = "mirrored";
            radius = Math.Max(1, radius);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.MedianImage(ho, out HObject outHo, maskType, radius, margin);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>InvertImage：灰度取反。</summary>
        public static CalibImage InvertImageToCalib(CalibImage inImg)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.InvertImage(ho, out HObject outHo);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>AbsDiffImage：|Image1−Image2|×Mult。</summary>
        public static CalibImage AbsDiffImageToCalib(CalibImage image1, CalibImage image2, double mult)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(AbsDiffImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.AbsDiffImage(h1, h2, out HObject outHo, mult);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>SubImage：Out = (Image1−Image2)×Mult + Add。</summary>
        public static CalibImage SubImageToCalib(CalibImage image1, CalibImage image2, double mult, double add)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(SubImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.SubImage(h1, h2, out HObject outHo, mult, add);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>AddImage：Out = Mult×Image1 + Image2 + Add。</summary>
        public static CalibImage AddImageToCalib(CalibImage image1, CalibImage image2, double mult, double add)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(AddImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.AddImage(h1, h2, out HObject outHo, mult, add);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>MinImage / MaxImage：逐像素最小/最大。</summary>
        public static CalibImage MinImageToCalib(CalibImage image1, CalibImage image2)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(MinImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.MinImage(h1, h2, out HObject outHo);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>MaxImage：逐像素最大。</summary>
        public static CalibImage MaxImageToCalib(CalibImage image1, CalibImage image2)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(MaxImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.MaxImage(h1, h2, out HObject outHo);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>MultImage：Out = Image1×Image2×Mult + Add。</summary>
        public static CalibImage MultImageToCalib(CalibImage image1, CalibImage image2, double mult, double add)
        {
            if (image1 == null) throw new ArgumentNullException(nameof(image1));
            if (image2 == null) throw new ArgumentNullException(nameof(image2));
            EnsureSameImageSize(image1, image2, nameof(MultImageToCalib));

            HObject h1 = CalibToHObject(image1);
            HObject h2 = CalibToHObject(image2);
            try
            {
                h1 = EnsureGray(h1);
                h2 = EnsureGray(h2);
                HOperatorSet.MultImage(h1, h2, out HObject outHo, mult, add);
                h1.Dispose();
                h1 = null!;
                h2.Dispose();
                h2 = null!;
                h1 = outHo;
                return ToCalibGray(h1);
            }
            finally
            {
                h1?.Dispose();
                h2?.Dispose();
            }
        }

        /// <summary>SobelAmp：边缘幅值，如 FilterType=sum_abs, Size=3。</summary>
        public static CalibImage SobelAmpToCalib(CalibImage inImg, string filterType, int size)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(filterType)) filterType = "sum_abs";
            if ((size & 1) == 0) size++;
            size = Math.Max(3, size);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.SobelAmp(ho, out HObject outHo, filterType, size);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>SmoothImage：如 Filter=gauss, Alpha=3.0。</summary>
        public static CalibImage SmoothImageToCalib(CalibImage inImg, string filter, double alpha)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(filter)) filter = "gauss";
            if (alpha < 0.0) alpha = 1.0;

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.SmoothImage(ho, out HObject outHo, filter, alpha);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>GrayErosionRect：灰度腐蚀。</summary>
        public static CalibImage GrayErosionRectToCalib(CalibImage inImg, int maskHeight, int maskWidth)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskHeight = Math.Max(1, maskHeight);
            maskWidth = Math.Max(1, maskWidth);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GrayErosionRect(ho, out HObject outHo, maskHeight, maskWidth);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>GrayDilationRect：灰度膨胀。</summary>
        public static CalibImage GrayDilationRectToCalib(CalibImage inImg, int maskHeight, int maskWidth)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            maskHeight = Math.Max(1, maskHeight);
            maskWidth = Math.Max(1, maskWidth);
            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GrayDilationRect(ho, out HObject outHo, maskHeight, maskWidth);
                ho.Dispose();
                ho = outHo;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>AutoThreshold：直方图多类分割后取<strong>面积最大</strong>的一类区域并二值化（Sigma 为直方图平滑）。</summary>
        public static CalibImage AutoThresholdToCalibGray(CalibImage inImg, double sigma)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            sigma = Math.Max(0.0, sigma);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.AutoThreshold(ho, out HObject regions, sigma);
                try
                {
                    HOperatorSet.Connection(regions, out HObject connected);
                    try
                    {
                        HOperatorSet.CountObj(connected, out HTuple nT);
                        int n = TupleFirstInt(nT);
                        if (n <= 0)
                        {
                            var emptyMask = new CalibImage(iw, ih, 1);
                            var zn = emptyMask.GetNativeStruct();
                            byte[] zeros = new byte[iw * ih];
                            Marshal.Copy(zeros, 0, zn.data, zeros.Length);
                            return emptyMask;
                        }

                        double maxArea = -1.0;
                        int bestIdx = -1;
                        for (int i = 1; i <= n; i++)
                        {
                            HOperatorSet.SelectObj(connected, out HObject obj, i);
                            try
                            {
                                HOperatorSet.AreaCenter(obj, out HTuple area, out HTuple _, out HTuple _);
                                double a = area.TupleLength() > 0 ? area[0].D : 0.0;
                                if (a > maxArea)
                                {
                                    maxArea = a;
                                    bestIdx = i;
                                }
                            }
                            finally
                            {
                                obj.Dispose();
                            }
                        }

                        if (bestIdx < 1)
                            throw new InvalidOperationException("HALCON: AutoThreshold 未得到有效区域");

                        HOperatorSet.SelectObj(connected, out HObject best, bestIdx);
                        try
                        {
                            HOperatorSet.RegionToBin(best, out HObject bin, 255.0, 0.0, iw, ih);
                            try
                            {
                                return ToCalibGray(bin);
                            }
                            finally
                            {
                                bin.Dispose();
                            }
                        }
                        finally
                        {
                            best.Dispose();
                        }
                    }
                    finally
                    {
                        connected.Dispose();
                    }
                }
                finally
                {
                    regions.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// 二值图 → Region 上矩形结构元开/闭/腐蚀/膨胀，再 RegionToBin。
        /// </summary>
        public static CalibImage BinaryMorphRect(CalibImage binaryImage, string op, int width, int height, int iterations)
        {
            if (binaryImage == null) throw new ArgumentNullException(nameof(binaryImage));
            op = (op ?? "open").Trim().ToLowerInvariant();
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            iterations = Math.Max(1, iterations);

            HObject ho = CalibToHObject(binaryImage);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);

                HOperatorSet.Threshold(ho, out HObject region, 1.0, 255.0);
                ho.Dispose();
                ho = null!;
                HObject cur = region;
                try
                {
                    for (int k = 0; k < iterations; k++)
                    {
                        HObject next;
                        if (op == "close" || op == "closing")
                            HOperatorSet.ClosingRectangle1(cur, out next, width, height);
                        else if (op == "dilate" || op == "dilation")
                            HOperatorSet.DilationRectangle1(cur, out next, width, height);
                        else if (op == "erode" || op == "erosion")
                            HOperatorSet.ErosionRectangle1(cur, out next, width, height);
                        else
                            HOperatorSet.OpeningRectangle1(cur, out next, width, height);
                        cur.Dispose();
                        cur = next;
                    }

                    HOperatorSet.RegionToBin(cur, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    cur.Dispose();
                }
            }
            finally
            {
                ho?.Dispose();
            }
        }

        /// <summary>
        /// 单通道灰度/二值图 → Threshold → Connection → <c>GenContourRegionXld</c>，每条连通域一条轮廓。
        /// </summary>
        public static HalconXldContourBundle XldContoursFromBinaryGray(CalibImage inImg, double minGray, double maxGray, string genContourMode, int minContourPoints)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(genContourMode)) genContourMode = "border";
            minContourPoints = Math.Max(2, minContourPoints);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.Threshold(ho, out HObject region, minGray, maxGray);
                try
                {
                    HOperatorSet.Connection(region, out HObject connected);
                    try
                    {
                        HOperatorSet.CountObj(connected, out HTuple numObj);
                        int n = TupleFirstInt(numObj);
                        var list = new List<Point2D[]>(Math.Max(0, n));
                        for (int idx = 1; idx <= n; idx++)
                        {
                            HOperatorSet.SelectObj(connected, out HObject regPart, idx);
                            try
                            {
                                HOperatorSet.GenContourRegionXld(regPart, out HObject contour, genContourMode);
                                try
                                {
                                    Point2D[] pts = ContourXldToPointArray(contour);
                                    if (pts.Length >= minContourPoints)
                                        list.Add(pts);
                                }
                                finally
                                {
                                    contour.Dispose();
                                }
                            }
                            finally
                            {
                                regPart.Dispose();
                            }
                        }

                        return new HalconXldContourBundle
                        {
                            Width = iw,
                            Height = ih,
                            Contours = list
                        };
                    }
                    finally
                    {
                        connected.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// 对已有 XLD 折线逐条 <c>GenContourPolygonXld</c> + <c>SegmentContoursXld</c>（直线/圆弧分段）。
        /// </summary>
        public static HalconXldContourBundle SegmentXldBundle(HalconXldContourBundle src, string mode, int smoothCont, double maxLineDist1, double maxLineDist2)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Contours == null || src.Contours.Count == 0)
                return new HalconXldContourBundle { Width = src.Width, Height = src.Height, Contours = new List<Point2D[]>() };
            if (string.IsNullOrWhiteSpace(mode)) mode = "lines_circles";

            var outList = new List<Point2D[]>();
            foreach (var poly in src.Contours)
            {
                if (poly == null || poly.Length < 2)
                    continue;

                double[] ry = poly.Select(p => p.Y).ToArray();
                double[] cx = poly.Select(p => p.X).ToArray();
                var row = new HTuple(ry);
                var col = new HTuple(cx);
                HOperatorSet.GenContourPolygonXld(out HObject xldPoly, row, col);
                try
                {
                    HOperatorSet.SegmentContoursXld(xldPoly, out HObject split, mode, smoothCont, maxLineDist1, maxLineDist2);
                    try
                    {
                        HOperatorSet.CountObj(split, out HTuple ns);
                        int cnt = TupleFirstInt(ns);
                        for (int i = 1; i <= cnt; i++)
                        {
                            HOperatorSet.SelectObj(split, out HObject piece, i);
                            try
                            {
                                Point2D[] pts = ContourXldToPointArray(piece);
                                if (pts.Length >= 2)
                                    outList.Add(pts);
                            }
                            finally
                            {
                                piece.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        split.Dispose();
                    }
                }
                finally
                {
                    xldPoly.Dispose();
                }
            }

            return new HalconXldContourBundle
            {
                Width = src.Width,
                Height = src.Height,
                Contours = outList
            };
        }

        private static Point2D[] ContourXldToPointArray(HObject contourXld)
        {
            using var xld = new HXLDCont(contourXld);
            xld.GetContourXld(out HTuple row, out HTuple col);
            int len = row.TupleLength();
            if (len <= 0)
                return Array.Empty<Point2D>();
            var arr = new Point2D[len];
            for (int i = 0; i < len; i++)
                arr[i] = new Point2D(col[i].D, row[i].D);
            return arr;
        }

        /// <summary>
        /// 对应「二值化找最大连通域 → Mask」：Gauss → Threshold → Connection → 面积最大的 Region → RegionToBin。
        /// </summary>
        public static CalibImage LargestBlobMaskFromGray(CalibImage inImg, int gaussSize, double threshMin, double threshMax)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                if (gaussSize >= 3)
                {
                    if ((gaussSize & 1) == 0) gaussSize++;
                    HOperatorSet.GaussFilter(ho, out HObject sm, gaussSize);
                    ho.Dispose();
                    ho = sm;
                }

                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.Threshold(ho, out HObject region, threshMin, threshMax);
                try
                {
                    HOperatorSet.Connection(region, out HObject connected);
                    try
                    {
                        HOperatorSet.CountObj(connected, out HTuple nT);
                        int n = TupleFirstInt(nT);
                        if (n <= 0)
                        {
                            var emptyMask = new CalibImage(iw, ih, 1);
                            var zn = emptyMask.GetNativeStruct();
                            byte[] zeros = new byte[iw * ih];
                            Marshal.Copy(zeros, 0, zn.data, zeros.Length);
                            return emptyMask;
                        }

                        double maxArea = -1.0;
                        int bestIdx = -1;
                        for (int i = 1; i <= n; i++)
                        {
                            HOperatorSet.SelectObj(connected, out HObject obj, i);
                            try
                            {
                                HOperatorSet.AreaCenter(obj, out HTuple area, out HTuple _, out HTuple _);
                                double a = area.TupleLength() > 0 ? area[0].D : 0.0;
                                if (a > maxArea)
                                {
                                    maxArea = a;
                                    bestIdx = i;
                                }
                            }
                            finally
                            {
                                obj.Dispose();
                            }
                        }

                        if (bestIdx < 1)
                            throw new InvalidOperationException("HALCON: 未选出最大连通域");

                        HOperatorSet.SelectObj(connected, out HObject best, bestIdx);
                        try
                        {
                            HOperatorSet.RegionToBin(best, out HObject bin, 255.0, 0.0, iw, ih);
                            try
                            {
                                return ToCalibGray(bin);
                            }
                            finally
                            {
                                bin.Dispose();
                            }
                        }
                        finally
                        {
                            best.Dispose();
                        }
                    }
                    finally
                    {
                        connected.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// 最大轮廓实心 Mask：阈值连通域中按 <paramref name="rankBy"/> 选取一块后 <c>FillUp</c> 填满内部空洞，再 <c>RegionToBin</c>。
        /// <paramref name="rankBy"/>：area＝面积最大；perimeter＝外轮廓 <c>LengthXld</c> 最大（均先 FillUp，保证 Mask 无孔）。
        /// </summary>
        public static CalibImage LargestContourMaskFromGray(CalibImage inImg, int gaussSize, double threshMin, double threshMax, string genContourMode, string rankBy, int minContourPoints)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (string.IsNullOrWhiteSpace(genContourMode)) genContourMode = "border";
            string r = (rankBy ?? "area").Trim().ToLowerInvariant();
            bool byArea = r is "area" or "a";
            bool byPerimeter = r is "perimeter" or "length" or "p" or "l";
            if (!byArea && !byPerimeter)
                byArea = true;
            minContourPoints = Math.Max(2, minContourPoints);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                if (gaussSize >= 3)
                {
                    if ((gaussSize & 1) == 0) gaussSize++;
                    HOperatorSet.GaussFilter(ho, out HObject sm, gaussSize);
                    ho.Dispose();
                    ho = sm;
                }

                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);
                if (iw <= 0 || ih <= 0)
                    throw new InvalidOperationException("HALCON: GetImageSize 无效");

                HOperatorSet.Threshold(ho, out HObject region, threshMin, threshMax);
                try
                {
                    HOperatorSet.Connection(region, out HObject connected);
                    try
                    {
                        HOperatorSet.CountObj(connected, out HTuple nT);
                        int n = TupleFirstInt(nT);
                        if (n <= 0)
                        {
                            var emptyMask = new CalibImage(iw, ih, 1);
                            var zn = emptyMask.GetNativeStruct();
                            byte[] zeros = new byte[iw * ih];
                            Marshal.Copy(zeros, 0, zn.data, zeros.Length);
                            return emptyMask;
                        }

                        double bestMetric = -1.0;
                        int bestIdx = -1;
                        for (int i = 1; i <= n; i++)
                        {
                            HOperatorSet.SelectObj(connected, out HObject regPart, i);
                            try
                            {
                                if (byArea)
                                {
                                    HOperatorSet.AreaCenter(regPart, out HTuple area, out HTuple _, out HTuple _);
                                    double a = area.TupleLength() > 0 ? area[0].D : 0.0;
                                    if (a > bestMetric)
                                    {
                                        bestMetric = a;
                                        bestIdx = i;
                                    }
                                }
                                else
                                {
                                    HOperatorSet.GenContourRegionXld(regPart, out HObject contour, genContourMode);
                                    try
                                    {
                                        Point2D[] pts = ContourXldToPointArray(contour);
                                        if (pts.Length < minContourPoints)
                                            continue;

                                        HOperatorSet.LengthXld(contour, out HTuple lenT);
                                        double len = lenT.TupleLength() > 0 ? lenT[0].D : 0.0;
                                        if (len > bestMetric)
                                        {
                                            bestMetric = len;
                                            bestIdx = i;
                                        }
                                    }
                                    finally
                                    {
                                        contour.Dispose();
                                    }
                                }
                            }
                            finally
                            {
                                regPart.Dispose();
                            }
                        }

                        if (bestIdx < 1)
                        {
                            var emptyMask = new CalibImage(iw, ih, 1);
                            var zn = emptyMask.GetNativeStruct();
                            byte[] zeros = new byte[iw * ih];
                            Marshal.Copy(zeros, 0, zn.data, zeros.Length);
                            return emptyMask;
                        }

                        HOperatorSet.SelectObj(connected, out HObject best, bestIdx);
                        try
                        {
                            HOperatorSet.FillUp(best, out HObject solid);
                            try
                            {
                                HOperatorSet.RegionToBin(solid, out HObject bin, 255.0, 0.0, iw, ih);
                                try
                                {
                                    return ToCalibGray(bin);
                                }
                                finally
                                {
                                    bin.Dispose();
                                }
                            }
                            finally
                            {
                                solid.Dispose();
                            }
                        }
                        finally
                        {
                            best.Dispose();
                        }
                    }
                    finally
                    {
                        connected.Dispose();
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// 对应 CLAHE：HALCON 无同名算子时，用 Illuminate + Emphasize 做局部对比度增强。
        /// </summary>
        public static CalibImage LocalContrastEnhance(CalibImage inImg, int illumW, int illumH, double illumFactor, int empW, int empH, double empFactor)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            illumW = Math.Max(3, illumW | 1);
            illumH = Math.Max(3, illumH | 1);
            empW = Math.Max(1, empW);
            empH = Math.Max(1, empH);

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.Illuminate(ho, out HObject il, illumW, illumH, illumFactor);
                ho.Dispose();
                ho = il;
                HOperatorSet.Emphasize(ho, out HObject em, empW, empH, empFactor);
                ho.Dispose();
                ho = em;
                return ToCalibGray(ho);
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// 对应 apply_mask：Mask 像素落在 [maskMin,maskMax] 时保留 Image 灰度，否则为 0。
        /// （部分 HalconDotNet 无 GrayMask，用语义等价的单通道像素运算实现。）
        /// </summary>
        public static CalibImage GrayMaskApply(CalibImage image, CalibImage mask, double maskMin, double maskMax)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (mask == null) throw new ArgumentNullException(nameof(mask));

            CalibImage gi = ToSingleChannelGray(image);
            CalibImage gm = ToSingleChannelGray(mask);
            try
            {
                var ni = gi.GetNativeStruct();
                var nm = gm.GetNativeStruct();
                if (ni.width != nm.width || ni.height != nm.height)
                    throw new InvalidOperationException($"HALCON GrayMask: 图像与 Mask 须同尺寸 {ni.width}x{ni.height} vs {nm.width}x{nm.height}");

                int nPix = ni.width * ni.height;
                var bufI = new byte[nPix];
                var bufM = new byte[nPix];
                Marshal.Copy(ni.data, bufI, 0, nPix);
                Marshal.Copy(nm.data, bufM, 0, nPix);

                int mmn = (int)Math.Clamp(Math.Round(maskMin), 0, 255);
                int mmx = (int)Math.Clamp(Math.Round(maskMax), 0, 255);
                var outb = new byte[nPix];
                for (int i = 0; i < nPix; i++)
                {
                    int m = bufM[i];
                    outb[i] = (m >= mmn && m <= mmx) ? bufI[i] : (byte)0;
                }

                var dst = new CalibImage(ni.width, ni.height, 1);
                var nd = dst.GetNativeStruct();
                Marshal.Copy(outb, 0, nd.data, nPix);
                return dst;
            }
            finally
            {
                gi.Dispose();
                gm.Dispose();
            }
        }

        /// <summary>
        /// 二值 Region 上做 OpeningCircle / ClosingCircle，再 RegionToBin（对应 morphology 清理）。
        /// </summary>
        public static CalibImage BinaryMorphCircle(CalibImage binaryImage, string op, double radius, int iterations)
        {
            if (binaryImage == null) throw new ArgumentNullException(nameof(binaryImage));
            op = (op ?? "open").Trim().ToLowerInvariant();
            if (radius < 0.5) radius = 1.0;
            iterations = Math.Max(1, iterations);

            HObject ho = CalibToHObject(binaryImage);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.GetImageSize(ho, out HTuple wT, out HTuple hT);
                int iw = TupleFirstInt(wT);
                int ih = TupleFirstInt(hT);

                HOperatorSet.Threshold(ho, out HObject region, 1.0, 255.0);
                ho.Dispose();
                ho = null!;
                HObject cur = region;
                try
                {
                    for (int k = 0; k < iterations; k++)
                    {
                        HObject next;
                        if (op == "close" || op == "closing")
                            HOperatorSet.ClosingCircle(cur, out next, radius);
                        else
                            HOperatorSet.OpeningCircle(cur, out next, radius);
                        cur.Dispose();
                        cur = next;
                    }

                    HOperatorSet.RegionToBin(cur, out HObject bin, 255.0, 0.0, iw, ih);
                    try
                    {
                        return ToCalibGray(bin);
                    }
                    finally
                    {
                        bin.Dispose();
                    }
                }
                finally
                {
                    cur.Dispose();
                }
            }
            finally
            {
                ho?.Dispose();
            }
        }

        /// <summary>
        /// 沿 XLD 折线按弧长间距采样，合并为点列（供显示）；可选只取前 maxBars 条最长轮廓。
        /// </summary>
        public static Point2D[] SamplePointsFromXldBundle(HalconXldContourBundle bundle, double spacing, int maxBars)
        {
            if (bundle?.Contours == null || bundle.Contours.Count == 0)
                return Array.Empty<Point2D>();
            spacing = Math.Max(1.0, spacing);
            maxBars = maxBars <= 0 ? int.MaxValue : maxBars;

            var indexed = new List<(double Len, Point2D[] Pts)>();
            foreach (var c in bundle.Contours)
            {
                if (c == null || c.Length < 2)
                    continue;
                double len = PolylineLength(c);
                indexed.Add((len, c));
            }

            indexed.Sort((a, b) => b.Len.CompareTo(a.Len));
            var outPts = new List<Point2D>();
            int taken = 0;
            foreach (var (_, pts) in indexed)
            {
                if (taken >= maxBars)
                    break;
                ResamplePolyline(pts, spacing, outPts);
                taken++;
            }

            return outPts.ToArray();
        }

        private static double PolylineLength(Point2D[] pts)
        {
            double s = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                double dx = pts[i].X - pts[i - 1].X;
                double dy = pts[i].Y - pts[i - 1].Y;
                s += Math.Sqrt(dx * dx + dy * dy);
            }
            return s;
        }

        private static void ResamplePolyline(Point2D[] pts, double spacing, List<Point2D> sink)
        {
            if (pts.Length == 0)
                return;
            double total = PolylineLength(pts);
            if (total < spacing)
            {
                sink.Add(pts[0]);
                if (pts.Length > 1)
                    sink.Add(pts[^1]);
                return;
            }

            sink.Add(pts[0]);
            double walked = 0;
            double nextAt = spacing;
            for (int i = 1; i < pts.Length; i++)
            {
                Point2D a = pts[i - 1];
                Point2D b = pts[i];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double seg = Math.Sqrt(dx * dx + dy * dy);
                if (seg < 1e-12)
                    continue;
                double segStart = walked;
                walked += seg;
                while (nextAt <= walked + 1e-9)
                {
                    double local = nextAt - segStart;
                    double t = local / seg;
                    sink.Add(new Point2D(a.X + t * dx, a.Y + t * dy));
                    nextAt += spacing;
                }
            }
        }
    }
}
#endif
