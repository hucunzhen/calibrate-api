#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// CalibImage ↔ HALCON HObject（灰度 / BGR 字节图）转换，供 Flow HALCON 算子使用。
    /// </summary>
    internal static partial class HalconFlowBridge
    {
        static HalconFlowBridge() => HalconRuntimeSettings.EnsureApplied();

        /// <summary>单通道灰度经 GenImage1 导入 HALCON（无临时 BMP）。</summary>
        private static HObject CalibGrayToHObject(CalibImage grayImg)
        {
            var n = grayImg.GetNativeStruct();
            int w = n.width, h = n.height, nPix = w * h;
            if (w <= 0 || h <= 0 || n.data == IntPtr.Zero || nPix <= 0)
                throw new InvalidOperationException("HALCON: CalibImage 无有效灰度数据");

            var buf = new byte[nPix];
            Marshal.Copy(n.data, buf, 0, nPix);
            GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                HOperatorSet.GenImage1(out HObject himg, "byte", w, h, handle.AddrOfPinnedObject());
                return himg;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>CalibImage → HObject。单/三通道走内存直传；其余仍经临时 BMP。</summary>
        public static HObject CalibToHObject(CalibImage img)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            img.RefreshProperties();
            if (img.Channels == 1)
                return CalibGrayToHObject(img);
            if (img.Channels == 3)
            {
                using CalibImage gray = ToSingleChannelGray(img);
                return CalibGrayToHObject(gray);
            }

            return CalibToHObjectViaTempBmp(img);
        }

        /// <summary>经临时 BMP + ReadImage（非 1/3 通道时的兜底）。</summary>
        private static HObject CalibToHObjectViaTempBmp(CalibImage img)
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
                                    Point2D[] pts = EnsureClosedContourPoints(ContourXldToPointArray(contour));
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

        /// <summary>根据 ROI 内直方图，在 [0,t] / [t,255] 中选更可能对应“目标”的灰度区间（避免整块 ROI 落入同一类）。</summary>
        public static (double MinGray, double MaxGray) SuggestGrayRangeInRegion(
            byte[] grayPixels,
            int width,
            int height,
            int otsuThreshold,
            Func<int, int, bool> isInsideRoi)
        {
            int low = 0;
            int high = 0;
            int total = 0;
            int t = Math.Clamp(otsuThreshold, 1, 254);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!isInsideRoi(x, y))
                        continue;
                    int g = grayPixels[y * width + x];
                    total++;
                    if (g <= t) low++;
                    else high++;
                }
            }

            if (total <= 0)
                return (0, t);

            double fracLow = (double)low / total;
            double fracHigh = (double)high / total;

            // 某一侧占满 ROI：目标一般在另一侧
            if (fracLow > 0.88)
                return (t + 1, 255);
            if (fracHigh > 0.88)
                return (0, t);

            // 两侧都有：取像素较少的一侧作为目标
            return low <= high ? (0, t) : (t + 1, 255);
        }

        /// <summary>
        /// ROI 内提轮廓：用户阈值 → 互补阈值 → BinaryThreshold，并剔除“整块 ROI”连通域。
        /// </summary>
        public static HalconXldContourBundle XldContoursFromBinaryGrayInRegion(
            CalibImage inImg,
            HObject domainRegion,
            double minGray,
            double maxGray,
            string genContourMode,
            int minContourPoints,
            double maxRegionAreaRatio = 0.92)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));
            if (domainRegion == null || !domainRegion.IsInitialized())
                throw new ArgumentException("domain 无效", nameof(domainRegion));

            int mid = (int)Math.Clamp(Math.Round((minGray + maxGray) / 2.0), 1, 254);
            var attempts = new List<(double Min, double Max, string Tag)>
            {
                (minGray, maxGray, "manual"),
                (0, mid, "low"),
                (mid + 1, 255, "high")
            };

            // 若用户区间已覆盖一侧，去重
            HalconXldContourBundle? best = null;
            string bestTag = "";
            foreach (var (min, max, tag) in attempts.DistinctBy(a => $"{a.Min:F0}_{a.Max:F0}"))
            {
                if (max <= min) continue;
                var bundle = XldContoursFromGrayRangeInRegionCore(
                    inImg, domainRegion, min, max, genContourMode, minContourPoints, maxRegionAreaRatio);
                if (bundle.Contours != null && bundle.Contours.Count > 0)
                {
                    best = bundle;
                    bestTag = tag;
                    break;
                }
            }

            if (best != null && best.Contours!.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[XldInRegion] used {bestTag}, contours={best.Contours.Count}");
                return best;
            }

            var auto = XldContoursFromBinaryThresholdInRegion(
                inImg, domainRegion, genContourMode, minContourPoints, maxRegionAreaRatio);
            if (auto.Contours != null && auto.Contours.Count > 0)
                return auto;

            return new HalconXldContourBundle
            {
                Width = inImg.Width,
                Height = inImg.Height,
                Contours = new List<Point2D[]>()
            };
        }

        private static HalconXldContourBundle XldContoursFromGrayRangeInRegionCore(
            CalibImage inImg,
            HObject domainRegion,
            double minGray,
            double maxGray,
            string genContourMode,
            int minContourPoints,
            double maxRegionAreaRatio)
        {
            if (string.IsNullOrWhiteSpace(genContourMode)) genContourMode = "border";
            minContourPoints = Math.Max(2, minContourPoints);

            HOperatorSet.AreaCenter(domainRegion, out HTuple domainAreaT, out HTuple _, out HTuple _);
            double domainArea = domainAreaT.Length > 0 ? domainAreaT[0].D : 0;

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.Threshold(ho, out HObject threshReg, minGray, maxGray);
                try
                {
                    HOperatorSet.Intersection(threshReg, domainRegion, out HObject clipped);
                    try
                    {
                        return ContoursFromClippedRegion(
                            clipped, domainArea, inImg.Width, inImg.Height,
                            genContourMode, minContourPoints, maxRegionAreaRatio);
                    }
                    finally
                    {
                        clipped.Dispose();
                    }
                }
                finally
                {
                    threshReg.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        private static HalconXldContourBundle XldContoursFromBinaryThresholdInRegion(
            CalibImage inImg,
            HObject domainRegion,
            string genContourMode,
            int minContourPoints,
            double maxRegionAreaRatio)
        {
            HOperatorSet.AreaCenter(domainRegion, out HTuple domainAreaT, out HTuple _, out HTuple _);
            double domainArea = domainAreaT.Length > 0 ? domainAreaT[0].D : 0;

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.ReduceDomain(ho, domainRegion, out HObject reduced);
                ho.Dispose();
                ho = reduced;

                foreach (string lightDark in new[] { "dark", "light" })
                {
                    try
                    {
                        HOperatorSet.BinaryThreshold(ho, out HObject region, "max_separability", lightDark, out HTuple _);
                        try
                        {
                            HOperatorSet.Intersection(region, domainRegion, out HObject clipped);
                            try
                            {
                                var bundle = ContoursFromClippedRegion(
                                    clipped, domainArea, inImg.Width, inImg.Height,
                                    genContourMode, minContourPoints, maxRegionAreaRatio);
                                if (bundle.Contours != null && bundle.Contours.Count > 0)
                                    return bundle;
                            }
                            finally
                            {
                                clipped.Dispose();
                            }
                        }
                        finally
                        {
                            region.Dispose();
                        }
                    }
                    catch
                    {
                        // 尝试下一极性
                    }
                }

                return new HalconXldContourBundle
                {
                    Width = inImg.Width,
                    Height = inImg.Height,
                    Contours = new List<Point2D[]>()
                };
            }
            finally
            {
                ho.Dispose();
            }
        }

        private static HalconXldContourBundle ContoursFromClippedRegion(
            HObject clipped,
            double domainArea,
            int iw,
            int ih,
            string genContourMode,
            int minContourPoints,
            double maxRegionAreaRatio)
        {
            HOperatorSet.Connection(clipped, out HObject connected);
            try
            {
                HOperatorSet.CountObj(connected, out HTuple numObj);
                int n = TupleFirstInt(numObj);
                var list = new List<Point2D[]>(Math.Max(0, n));
                Point2D[]? largestRejected = null;
                int largestRejectedLen = 0;

                for (int idx = 1; idx <= n; idx++)
                {
                    HOperatorSet.SelectObj(connected, out HObject regPart, idx);
                    try
                    {
                        double partArea = 0;
                        if (domainArea > 0)
                        {
                            HOperatorSet.AreaCenter(regPart, out HTuple partAreaT, out HTuple _, out HTuple _);
                            partArea = partAreaT.Length > 0 ? partAreaT[0].D : 0;
                            if (partArea >= domainArea * maxRegionAreaRatio)
                            {
                                foreach (var ptsTry in ContourXldToPointArraysFromRegion(regPart, genContourMode))
                                {
                                    if (ptsTry.Length > largestRejectedLen)
                                    {
                                        largestRejectedLen = ptsTry.Length;
                                        largestRejected = ptsTry;
                                    }
                                }
                                continue;
                            }
                        }

                        foreach (Point2D[] pts in ContourXldToPointArraysFromRegion(regPart, genContourMode))
                        {
                            Point2D[] closed = EnsureClosedContourPoints(pts);
                            if (closed.Length >= minContourPoints)
                                list.Add(closed);
                        }
                    }
                    finally
                    {
                        regPart.Dispose();
                    }
                }

                // 若全部被当成“整块 ROI”剔除，对掩膜轻微腐蚀后再试一次
                if (list.Count == 0 && domainArea > 0)
                {
                    HOperatorSet.ErosionRectangle1(clipped, out HObject eroded, 3, 3);
                    try
                    {
                        var retry = ContoursFromClippedRegion(
                            eroded, domainArea, iw, ih, genContourMode, minContourPoints, 0.98);
                        if (retry.Contours != null && retry.Contours.Count > 0)
                            return retry;
                    }
                    finally
                    {
                        eroded.Dispose();
                    }
                }

                if (list.Count == 0 && largestRejected != null && largestRejected.Length >= minContourPoints)
                    list.Add(EnsureClosedContourPoints(largestRejected));

                return new HalconXldContourBundle { Width = iw, Height = ih, Contours = list };
            }
            finally
            {
                connected.Dispose();
            }
        }

        private static IEnumerable<Point2D[]> ContourXldToPointArraysFromRegion(HObject regPart, string genContourMode)
        {
            HOperatorSet.GenContourRegionXld(regPart, out HObject contour, genContourMode);
            try
            {
                foreach (Point2D[] pts in ContourXldToPointArrays(contour))
                    yield return EnsureClosedContourPoints(pts);
            }
            finally
            {
                contour.Dispose();
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

        /// <summary>HALCON 法向等距偏移 XLD（<c>gen_parallel_contour_xld</c>）。</summary>
        public static HalconXldContourBundle OffsetXldBundle(HalconXldContourBundle src, double offset, string mode)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Contours == null || src.Contours.Count == 0)
                return new HalconXldContourBundle { Width = src.Width, Height = src.Height, Contours = new List<Point2D[]>() };

            var polys = src.Contours.Where(c => c != null && c.Length >= 2).ToList();
            if (polys.Count == 0)
                return new HalconXldContourBundle { Width = src.Width, Height = src.Height, Contours = new List<Point2D[]>() };

            string parallelMode = ResolveGenParallelContourMode(mode);
            var outList = new List<Point2D[]>();

            foreach (var poly in polys)
            {
                HObject ho = XldBundleToHObject(new List<Point2D[]> { poly });
                try
                {
                    HOperatorSet.GenParallelContourXld(ho, out HObject hoOut, parallelMode, offset);
                    try
                    {
                        outList.AddRange(ContourXldToPointArrays(hoOut));
                    }
                    finally
                    {
                        hoOut.Dispose();
                    }
                }
                finally
                {
                    ho.Dispose();
                }
            }

            return new HalconXldContourBundle
            {
                Width = src.Width,
                Height = src.Height,
                Contours = outList
            };
        }

        /// <summary>gen_parallel_contour_xld 的 Mode：gradient / contour_normal / regression_normal。</summary>
        static string ResolveGenParallelContourMode(string? mode)
        {
            string m = (mode ?? "regression_normal").Trim().ToLowerInvariant();
            return m switch
            {
                "gradient" => "gradient",
                "contour_normal" => "contour_normal",
                "regression_normal" => "regression_normal",
                // 旧 UI（OffsetContoursXld）兼容 → 折线轮廓用回归法向最稳
                "round" or "original" or "rectangular" => "regression_normal",
                _ => "regression_normal"
            };
        }

        /// <summary>逐条 XLD 提取点列（GenContourRegionXld 等可能返回多对象，不可对整包直接 GetContourXld）。</summary>
        public static List<Point2D[]> ContourXldToPointArrays(HObject contourXld)
        {
            var list = new List<Point2D[]>();
            if (contourXld == null || !contourXld.IsInitialized())
                return list;

            int n = 1;
            try
            {
                HOperatorSet.CountObj(contourXld, out HTuple num);
                n = Math.Max(1, TupleFirstInt(num));
            }
            catch
            {
                n = 1;
            }

            for (int i = 1; i <= n; i++)
            {
                HObject one = n == 1 ? contourXld : contourXld.SelectObj(i);
                try
                {
                    Point2D[] pts = ContourXldSingleToPointArray(one);
                    if (pts.Length >= 2)
                        list.Add(pts);
                }
                finally
                {
                    if (n > 1)
                        one.Dispose();
                }
            }

            return list;
        }

        public static Point2D[] ContourXldToPointArray(HObject contourXld)
        {
            List<Point2D[]> parts = ContourXldToPointArrays(contourXld);
            if (parts.Count == 0)
                return Array.Empty<Point2D>();
            return parts.OrderByDescending(p => p.Length).First();
        }

        /// <summary>
        /// HALCON 轮廓点列通常不含重复起点；补终点使折线含闭合边。
        /// <paramref name="forceClose"/> 为 true 时除首尾几乎重合外一律补起点（避免容差过大误判为已闭合）。
        /// </summary>
        public static Point2D[] EnsureClosedContourPoints(Point2D[]? pts, double tolerancePx = 0.5, bool forceClose = false)
        {
            if (pts == null || pts.Length < 2)
                return pts ?? Array.Empty<Point2D>();

            double dx = pts[0].X - pts[^1].X;
            double dy = pts[0].Y - pts[^1].Y;
            double tol = forceClose ? 1e-6 : Math.Max(tolerancePx, 1e-6);
            if (dx * dx + dy * dy <= tol * tol)
                return pts;

            var closed = new Point2D[pts.Length + 1];
            Array.Copy(pts, closed, pts.Length);
            closed[^1] = pts[0];
            return closed;
        }

        private static Point2D[] ContourXldSingleToPointArray(HObject singleContourXld)
        {
            using var xld = new HXLDCont(singleContourXld);
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
        public static CalibImage GrayMaskApply(
            CalibImage image,
            CalibImage mask,
            double maskMin,
            double maskMax,
            bool keepInsideMask = true)
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
                    bool inside = m >= mmn && m <= mmx;
                    outb[i] = keepInsideMask
                        ? (inside ? bufI[i] : (byte)0)
                        : (inside ? (byte)0 : bufI[i]);
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
        /// 沿 XLD 折线按弧长间距采样，合并为点列；可选只取前 maxBars 条轮廓。
        /// <paramref name="BarIds"/> 与 <paramref name="Points"/> 等长，同一条轮廓上的点共享同一编号（0…），供「轮廓转焊道路径」等按条分包。
        /// </summary>
        /// <param name="sortContoursByLengthDescending">false=按 <see cref="HalconXldContourBundle.Contours"/> 列表顺序输出；true=按周长从长到短排序后再输出（兼容旧行为）。</param>
        public static (Point2D[] Points, int[] BarIds) SamplePointsFromXldBundle(
            HalconXldContourBundle bundle,
            double spacing,
            int maxBars,
            bool sortContoursByLengthDescending)
        {
            if (bundle?.Contours == null || bundle.Contours.Count == 0)
                return (Array.Empty<Point2D>(), Array.Empty<int>());
            spacing = Math.Max(1.0, spacing);
            maxBars = maxBars <= 0 ? int.MaxValue : maxBars;

            var contours = new List<Point2D[]>();
            foreach (var c in bundle.Contours)
            {
                if (c == null || c.Length < 2)
                    continue;
                contours.Add(c);
            }

            if (sortContoursByLengthDescending)
                contours.Sort((a, b) => PolylineLength(b).CompareTo(PolylineLength(a)));

            var outPts = new List<Point2D>();
            var outIds = new List<int>();
            int taken = 0;
            foreach (var pts in contours)
            {
                if (taken >= maxBars)
                    break;
                int barId = taken;
                int n0 = outPts.Count;
                ResamplePolyline(pts, spacing, outPts);
                for (int k = n0; k < outPts.Count; k++)
                    outIds.Add(barId);
                taken++;
            }

            return (outPts.ToArray(), outIds.ToArray());
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

            // 等弧长步进常漏掉真实终点，导致每条轨迹首尾采样偏短
            if (pts.Length > 1)
            {
                Point2D end = pts[^1];
                Point2D tail = sink[sink.Count - 1];
                double edx = end.X - tail.X;
                double edy = end.Y - tail.Y;
                if (edx * edx + edy * edy > 1e-6)
                    sink.Add(end);
            }
        }

        // ================================================================
        // HALCON 形状模板匹配
        // ================================================================

        /// <summary>按选项过滤 XLD 包（最小点数、仅保留最长轮廓）。</summary>
        public static HalconXldContourBundle FilterXldBundle(HalconXldContourBundle bundle, int minContourPoints, bool largestOnly)
        {
            if (bundle.Contours == null || bundle.Contours.Count == 0)
                return bundle;

            var list = bundle.Contours
                .Where(c => c != null && c.Length >= Math.Max(2, minContourPoints))
                .ToList();

            if (largestOnly && list.Count > 1)
            {
                Point2D[] best = list.OrderByDescending(c => c.Length).First();
                list = new List<Point2D[]> { best };
            }

            return new HalconXldContourBundle
            {
                Width = bundle.Width,
                Height = bundle.Height,
                Contours = list
            };
        }

        /// <summary>EdgesSubPix (canny) 提取 XLD，可选 domain 区域裁剪。</summary>
        public static HalconXldContourBundle XldContoursFromEdgesSubPix(
            CalibImage inImg,
            HObject? domainRegion,
            double alpha,
            double low,
            double high,
            int minContourPoints)
        {
            if (inImg == null) throw new ArgumentNullException(nameof(inImg));

            HObject ho = CalibToHObject(inImg);
            try
            {
                ho = EnsureGray(ho);
                if (domainRegion != null && domainRegion.IsInitialized())
                {
                    HOperatorSet.ReduceDomain(ho, domainRegion, out HObject reduced);
                    ho.Dispose();
                    ho = reduced;
                }

                HOperatorSet.EdgesSubPix(ho, out HObject edges, "canny", alpha, low, high);
                ho.Dispose();
                ho = edges;

                int n = ho.CountObj();
                var outList = new List<Point2D[]>();
                for (int i = 1; i <= n; i++)
                {
                    HObject one = ho.SelectObj(i);
                    try
                    {
                        Point2D[] pts = ContourXldToPointArray(one);
                        if (pts.Length >= Math.Max(2, minContourPoints))
                            outList.Add(pts);
                    }
                    finally
                    {
                        one.Dispose();
                    }
                }

                return new HalconXldContourBundle
                {
                    Width = inImg.Width,
                    Height = inImg.Height,
                    Contours = outList
                };
            }
            finally
            {
                ho.Dispose();
            }
        }

        public static HObject GenRegionRectangle(double row1, double col1, double row2, double col2)
        {
            HOperatorSet.GenRectangle1(out HObject reg, row1, col1, row2, col2);
            return reg;
        }

        public static HObject GenRegionPolygonFilled(IReadOnlyList<Point2D> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                throw new ArgumentException("多边形至少需要 3 个点", nameof(polygon));

            var rows = polygon.Select(p => p.Y).ToArray();
            var cols = polygon.Select(p => p.X).ToArray();
            HOperatorSet.GenRegionPolygonFilled(out HObject reg, new HTuple(rows), new HTuple(cols));
            return reg;
        }

        /// <summary>环形区域：外多边形减内多边形（点坐标 X=列, Y=行）。</summary>
        public static HObject GenRegionRingFromPolygons(IReadOnlyList<Point2D> outer, IReadOnlyList<Point2D> inner)
        {
            if (outer == null || outer.Count < 3)
                throw new ArgumentException("外圈多边形至少需要 3 个点", nameof(outer));
            if (inner == null || inner.Count < 3)
                throw new ArgumentException("内圈多边形至少需要 3 个点", nameof(inner));

            HObject outerReg = GenRegionPolygonFilled(outer);
            try
            {
                HObject innerReg = GenRegionPolygonFilled(inner);
                try
                {
                    HOperatorSet.Difference(outerReg, innerReg, out HObject ring);
                    outerReg.Dispose();
                    return ring;
                }
                finally
                {
                    innerReg.Dispose();
                }
            }
            catch
            {
                outerReg.Dispose();
                throw;
            }
        }

        /// <summary>实心圆区域（row/col 为 HALCON 图像坐标）。</summary>
        public static HObject GenRegionCircle(double row, double col, double radius)
        {
            if (radius <= 0)
                throw new ArgumentException("半径必须大于 0", nameof(radius));
            HOperatorSet.GenCircle(out HObject reg, row, col, radius);
            return reg;
        }

        /// <summary>圆环区域：外圆减内圆（row/col 为 HALCON 图像坐标）。</summary>
        public static HObject GenRegionAnnulus(double row, double col, double innerRadius, double outerRadius)
        {
            if (outerRadius <= 0)
                throw new ArgumentException("外半径必须大于 0", nameof(outerRadius));
            if (innerRadius < 0)
                innerRadius = 0;
            if (innerRadius >= outerRadius)
                throw new ArgumentException("内半径必须小于外半径");

            HOperatorSet.GenCircle(out HObject outer, row, col, outerRadius);
            try
            {
                if (innerRadius <= 1e-6)
                    return outer;

                HOperatorSet.GenCircle(out HObject inner, row, col, innerRadius);
                try
                {
                    HOperatorSet.Difference(outer, inner, out HObject ring);
                    outer.Dispose();
                    return ring;
                }
                finally
                {
                    inner.Dispose();
                }
            }
            catch
            {
                outer.Dispose();
                throw;
            }
        }

        /// <summary>区域边界折线（图像坐标）。带孔区域请用 <paramref name="genContourMode"/> = border_holes。</summary>
        public static List<Point2D[]> RegionToBoundaryContours(HObject region, string genContourMode = "border")
        {
            string mode = string.IsNullOrWhiteSpace(genContourMode) ? "border" : genContourMode.Trim();
            HOperatorSet.GenContourRegionXld(region, out HObject contour, mode);
            try
            {
                return ContourXldToPointArrays(contour)
                    .Where(p => p != null && p.Length >= 2)
                    .Select(p => EnsureClosedContourPoints(p))
                    .ToList();
            }
            finally
            {
                contour.Dispose();
            }
        }

        /// <summary>环形/带孔区域：外边界 + 内孔边界。</summary>
        public static List<Point2D[]> RegionRingBoundaryContours(HObject ringRegion)
        {
            var list = RegionToBoundaryContours(ringRegion, "border_holes");
            if (list.Count > 0)
                return list;
            return RegionToBoundaryContours(ringRegion, "border");
        }

        /// <summary>将多条闭合多边形转为 XLD 轮廓列表（用于环形 ROI 的 PolygonXld）。</summary>
        public static List<Point2D[]> PolygonsToClosedContourList(params IReadOnlyList<Point2D>[] polygons)
        {
            var list = new List<Point2D[]>();
            if (polygons == null)
                return list;
            foreach (IReadOnlyList<Point2D>? poly in polygons)
            {
                if (poly == null || poly.Count < 3)
                    continue;
                list.Add(EnsureClosedContourPoints(poly.ToArray()));
            }

            return list;
        }

        /// <summary>区域外边界中最长的一条折线。</summary>
        public static Point2D[] RegionToBoundaryPoints(HObject region)
        {
            List<Point2D[]> parts = RegionToBoundaryContours(region);
            if (parts.Count == 0)
                return Array.Empty<Point2D>();
            return parts.OrderByDescending(p => p.Length).First();
        }

        private static HTuple ToNumLevelsTuple(int numLevels) =>
            numLevels > 0 ? new HTuple(numLevels) : new HTuple("auto");

        private static HTuple ToAngleStepTuple(double angleStepDeg) =>
            angleStepDeg > 0
                ? new HTuple(angleStepDeg * Math.PI / 180.0)
                : new HTuple("auto");

        private static HTuple ToScaleStepTuple(double scaleStep) =>
            scaleStep > 0 ? new HTuple(scaleStep) : new HTuple("auto");

        private static HTuple ToContrastTuple(string? contrast, int contrastFallback)
        {
            if (string.IsNullOrWhiteSpace(contrast) ||
                contrast.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return new HTuple("auto");
            if (int.TryParse(contrast, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0)
                return new HTuple(c);
            if (contrastFallback > 0)
                return new HTuple(contrastFallback);
            return new HTuple("auto");
        }

        private static int ResolveMinContrast(HalconShapeModelCreateOptions opt) =>
            opt.MinContrast > 0 ? opt.MinContrast : 5;

        /// <summary>统一创建入口：XLD 或 ROI 灰度图。</summary>

        /// <summary>CreateShapeModel：基于 XLD 轮廓（兼容旧参数）。</summary>

        /// <summary>CreateShapeModel / CreateScaledShapeModel：基于 XLD。</summary>

        /// <summary>CreateShapeModel / CreateScaledShapeModel：ROI 内灰度图。</summary>

        /// <summary>
        /// numMatches：&gt;0 直接用；否则按阵列行×列；仍无则 <paramref name="fallbackWhenUnset"/>（避免 0=全部匹配导致内存暴涨）。
        /// </summary>
        public static int ResolveFindNumMatches(int rawNumMatches, int latticeRows, int latticeCols, int fallbackWhenUnset = 100)
        {
            if (rawNumMatches > 0)
                return rawNumMatches;
            if (latticeRows > 0 && latticeCols > 0)
                return latticeRows * latticeCols;
            return Math.Max(1, fallbackWhenUnset);
        }

        /// <summary>
        /// FindShapeModel：在图像中查找形状模板（兼容旧签名，不返回 scale）。
        /// </summary>
        public static (double[] rows, double[] cols, double[] angles, double[] scores) FindShapeModel(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            string subPixel,
            int numLevels,
            double greediness,
            double scaleMin = 1.0,
            double scaleMax = 1.0)
        {
            var result = FindShapeModelWithScale(
                inImg, modelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness,
                scaleMin, scaleMax);
            return (result.rows, result.cols, result.angles, result.scores);
        }

        /// <summary>FindShapeModel / FindScaledShapeModel；返回 rows/cols/angles/scales/scores。</summary>
        public static (double[] rows, double[] cols, double[] angles, double[] scales, double[] scores) FindShapeModelWithScale(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            string subPixel,
            int numLevels,
            double greediness,
            double scaleMin = 1.0,
            double scaleMax = 1.0)
        {
            long shapeId = ResolveRegisteredShapeModelId(modelId);
            if (shapeId < 0)
                throw new InvalidOperationException(
                    $"形状模板 ModelId={modelId} 无效：须为 create/load_shape_model 的 .shm，勿接可变形模型或已释放的 ID");
            HShapeModel shapeModel = HalconShapeModelRegistry.Get(shapeId);
            HObject hoImage = CalibToHObject(inImg);
            HImage hImg = new HImage(hoImage);
            try
            {
                string subPix = string.IsNullOrWhiteSpace(subPixel) || subPixel.Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? "least_squares"
                    : subPixel;

                HTuple hv_Row, hv_Column, hv_Angle, hv_Scale, hv_Score;
                bool useScaled = Math.Abs(scaleMin - 1.0) > 1e-9 || Math.Abs(scaleMax - 1.0) > 1e-9;
                if (!useScaled)
                {
                    shapeModel.FindShapeModel(
                        hImg,
                        angleStartDeg * Math.PI / 180.0,
                        angleExtentDeg * Math.PI / 180.0,
                        minScore,
                        numMatches,
                        maxOverlap,
                        subPix,
                        numLevels,
                        greediness,
                        out hv_Row,
                        out hv_Column,
                        out hv_Angle,
                        out hv_Score);
                    hv_Scale = new HTuple();
                    for (int i = 0; i < hv_Row.Length; i++)
                        hv_Scale = hv_Scale.TupleConcat(1.0);
                }
                else
                {
                    double sMin = Math.Max(0.01, Math.Min(scaleMin, scaleMax));
                    double sMax = Math.Max(sMin, Math.Max(scaleMin, scaleMax));
                    try
                    {
                        shapeModel.FindScaledShapeModel(
                            hImg,
                            angleStartDeg * Math.PI / 180.0,
                            angleExtentDeg * Math.PI / 180.0,
                            sMin,
                            sMax,
                            minScore,
                            numMatches,
                            maxOverlap,
                            subPix,
                            numLevels,
                            greediness,
                            out hv_Row,
                            out hv_Column,
                            out hv_Angle,
                            out hv_Scale,
                            out hv_Score);
                    }
                    catch (HOperatorException ex)
                    {
                        throw new InvalidOperationException(
                            $"粗匹配缩放搜索失败：当前模型可能不是 ScaledShape（scaleMin={sMin:G4}, scaleMax={sMax:G4}）。请用 ScaledShape 重新建模或将 scale 设回 1。HALCON: {ex.Message}",
                            ex);
                    }
                }

                double[] rows = new double[hv_Row.Length];
                double[] cols = new double[hv_Column.Length];
                double[] angles = new double[hv_Angle.Length];
                double[] scales = new double[hv_Row.Length];
                double[] scores = new double[hv_Score.Length];
                for (int i = 0; i < hv_Row.Length; i++)
                {
                    rows[i] = hv_Row[i].D;
                    cols[i] = hv_Column[i].D;
                    angles[i] = hv_Angle[i].D * 180.0 / Math.PI;
                    scales[i] = hv_Scale != null && hv_Scale.Length > i ? hv_Scale[i].D : 1.0;
                    scores[i] = hv_Score[i].D;
                }
                return (rows, cols, angles, scales, scores);
            }
            finally
            {
                hImg.Dispose();
                hoImage.Dispose();
            }
        }

        /// <summary>查找形状模板；若无结果则用更低 MinScore / Greediness 再试一次。</summary>
        public static (double[] rows, double[] cols, double[] angles, double[] scores) FindShapeModelWithFallback(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            string subPixel,
            int numLevels,
            double greediness,
            double scaleMin = 1.0,
            double scaleMax = 1.0,
            CancellationToken cancellationToken = default)
        {
            var result = FindShapeModelWithScaleWithFallback(
                inImg, modelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness,
                scaleMin, scaleMax, cancellationToken);
            return (result.rows, result.cols, result.angles, result.scores);
        }

        /// <summary>查找形状模板（含 scale）；若无结果则用更低 MinScore / Greediness 再试一次。</summary>
        public static (double[] rows, double[] cols, double[] angles, double[] scales, double[] scores) FindShapeModelWithScaleWithFallback(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            string subPixel,
            int numLevels,
            double greediness,
            double scaleMin = 1.0,
            double scaleMax = 1.0,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = FindShapeModelWithScale(
                inImg, modelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness,
                scaleMin, scaleMax);
            if (first.rows.Length > 0)
                return first;

            cancellationToken.ThrowIfCancellationRequested();

            double retryScore = Math.Max(0.2, minScore * 0.65);
            double retryGreed = Math.Max(0.5, greediness * 0.85);
            if (Math.Abs(retryScore - minScore) < 1e-6 && Math.Abs(retryGreed - greediness) < 1e-6)
                return first;

            return FindShapeModelWithScale(
                inImg, modelId, angleStartDeg, angleExtentDeg, retryScore, numMatches, maxOverlap, subPixel, numLevels, retryGreed,
                scaleMin, scaleMax);
        }

        /// <summary>将已创建的模型写入 .shm 文件。</summary>
        public static void WriteShapeModelToFile(long modelId, string filePath)
        {
            if (modelId < 0)
                throw new ArgumentOutOfRangeException(nameof(modelId));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            HalconShapeModelRegistry.Get(modelId).WriteShapeModel(filePath);
        }

        /// <summary>从 .shm 文件加载形状模型并注册，返回 ModelId。</summary>
        public static long LoadShapeModelFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("形状模型文件不存在", filePath);

            var model = new HShapeModel();
            model.ReadShapeModel(filePath);
            return HalconShapeModelRegistry.Register(model);
        }

        /// <summary>读取已加载模型的参数摘要（用于界面显示）。</summary>
        public static string GetShapeModelParamsSummary(long modelId)
        {
            HShapeModel shapeModel = HalconShapeModelRegistry.Get(modelId);
            int numLevels = shapeModel.GetShapeModelParams(
                out double angleStart,
                out double angleExtent,
                out double angleStep,
                out double scaleMin,
                out double scaleMax,
                out double scaleStep,
                out string metric,
                out int minContrast);

            double angleStartDeg = angleStart * 180.0 / Math.PI;
            double angleExtentDeg = angleExtent * 180.0 / Math.PI;
            string metricStr = metric ?? "";
            bool scaled = Math.Abs(scaleMax - scaleMin) > 1e-6 && (Math.Abs(scaleMax - 1) > 1e-6 || Math.Abs(scaleMin - 1) > 1e-6);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ModelID: {modelId}");
            sb.AppendLine($"类型: {(scaled ? "Scaled" : "Standard")}");
            sb.AppendLine($"角度: [{angleStartDeg:F1}°, 范围 {angleExtentDeg:F1}°], 步长 {angleStep * 180.0 / Math.PI:F2}°");
            if (scaled)
                sb.AppendLine($"缩放: [{scaleMin:F3}, {scaleMax:F3}], 步长 {scaleStep:F3}");
            sb.AppendLine($"NumLevels: {numLevels}, MinContrast: {minContrast}");
            if (!string.IsNullOrEmpty(metricStr))
                sb.AppendLine($"Metric: {metricStr}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>释放 HALCON 形状模型句柄。</summary>
        public static void ClearShapeModel(long modelId)
        {
            if (modelId < 0) return;
            HalconShapeModelRegistry.Release(modelId);
        }

        /// <summary>获取形状模型轮廓点（模型坐标系：X=列, Y=行），用于叠加显示。</summary>
        public static Point2D[][] GetShapeModelContourPoints(long modelId, int level = 1) =>
            HalconRuntimeSettings.RunGeometrySafe(() => GetShapeModelContourPointsCore(modelId, level));

        private static Point2D[][] GetShapeModelContourPointsCore(long modelId, int level = 1)
        {
            if (!HalconShapeModelRegistry.TryGet(modelId, out HShapeModel shapeModel))
                return Array.Empty<Point2D[]>();
            using HXLDCont xld = shapeModel.GetShapeModelContours(level);
            int n = xld.CountObj();
            if (n <= 0)
                return Array.Empty<Point2D[]>();

            var list = new List<Point2D[]>(n);
            for (int i = 1; i <= n; i++)
            {
                HObject one = xld.SelectObj(i);
                try
                {
                    Point2D[] pts = ContourXldToPointArray(one);
                    if (pts.Length >= 2)
                        list.Add(EnsureClosedContourPoints(pts, 0.5, forceClose: true));
                }
                finally
                {
                    one.Dispose();
                }
            }

            return list.ToArray();
        }

        /// <summary>形状模型点（X=列,Y=行）按匹配位姿变换到图像坐标。</summary>
        public static Point2D TransformShapeModelPointToImage(
            Point2D modelPt,
            double matchRow,
            double matchCol,
            double angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            double row = modelPt.Y;
            double col = modelPt.X;
            double rowImg = row * c - col * s + matchRow;
            double colImg = row * s + col * c + matchCol;
            return new Point2D(colImg, rowImg);
        }

        /// <summary>将单条模型轮廓折线变换到图像坐标。</summary>
        public static Point2D[] TransformShapeModelContourToImage(
            Point2D[] modelContour,
            double matchRow,
            double matchCol,
            double angleDeg)
        {
            if (modelContour == null || modelContour.Length == 0)
                return Array.Empty<Point2D>();

            var imgPts = new Point2D[modelContour.Length];
            for (int i = 0; i < modelContour.Length; i++)
                imgPts[i] = TransformShapeModelPointToImage(modelContour[i], matchRow, matchCol, angleDeg);
            return imgPts;
        }

        /// <summary>从模型轮廓集合中取变换后面积最大的外形折线（与区域过滤一致）。</summary>
        public static Point2D[] GetOuterTransformedShapeContour(
            Point2D[][] modelContours,
            double matchRow,
            double matchCol,
            double angleDeg)
        {
            if (modelContours == null || modelContours.Length == 0)
                return Array.Empty<Point2D>();

            Point2D[]? best = null;
            double bestArea = 0;
            int bestLen = 0;
            foreach (Point2D[] contour in modelContours)
            {
                if (contour == null || contour.Length < 2)
                    continue;

                Point2D[] img = TransformShapeModelContourToImage(contour, matchRow, matchCol, angleDeg);
                double area = Math.Abs(ShapeContourSignedArea(img));
                if (area >= 4 && img.Length >= 3 && area > bestArea)
                {
                    bestArea = area;
                    best = img;
                }
                else if (best == null && img.Length > bestLen)
                {
                    bestLen = img.Length;
                    best = img;
                }
            }

            if (best == null || best.Length < 2)
                return Array.Empty<Point2D>();

            return EnsureClosedContourPoints(best, 0.5, forceClose: true);
        }

        private static double ShapeContourSignedArea(IReadOnlyList<Point2D> polygon)
        {
            if (polygon.Count < 3)
                return 0;
            double a = 0;
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a += polygon[i].X * polygon[j].Y - polygon[j].X * polygon[i].Y;
            }

            return a * 0.5;
        }

        /// <summary>单个 Find 实例对应的填充区域（HALCON Region），用于 TestRegionPoint。</summary>
        public sealed class ShapeMatchRegionMask : IDisposable
        {
            internal HRegion? Region { get; set; }

            public void Dispose()
            {
                Region?.Dispose();
                Region = null;
            }
        }

        /// <summary>
        /// 将形状模型轮廓按匹配位姿变换到图像，生成填充 Region（最大轮廓为外，较小为孔洞）。
        /// </summary>

        /// <summary>合并所有有效匹配区域（并集）。</summary>
        public static HRegion? UnionShapeMatchRegions(ShapeMatchRegionMask[] masks)
        {
            if (masks == null || masks.Length == 0)
                return null;

            HRegion? acc = null;
            foreach (ShapeMatchRegionMask? m in masks)
            {
                HRegion? r = m?.Region;
                if (r == null || !r.IsInitialized())
                    continue;

                if (acc == null)
                {
                    acc = r.CopyObj(1, -1);
                    continue;
                }

                HRegion merged = acc.Union2(r);
                acc.Dispose();
                acc = merged;
            }

            return acc;
        }

        /// <summary>填充 Region → 单通道 Mask 图（区域内 255，外 0）。</summary>
        public static CalibImage RegionToMaskCalibImage(HRegion region, int width, int height)
        {
            if (region == null || !region.IsInitialized())
                throw new ArgumentException("Region 无效", nameof(region));
            if (width <= 0 || height <= 0)
                throw new ArgumentException("图像尺寸无效");

            HOperatorSet.RegionToBin(region, out HObject bin, 255.0, 0.0, width, height);
            try
            {
                return ToCalibGray(bin);
            }
            finally
            {
                bin.Dispose();
            }
        }

        public sealed class ShapeMatchImageMaskResult
        {
            public CalibImage MaskedImage { get; init; } = null!;
            public CalibImage Mask { get; init; } = null!;
            /// <summary>参与生成 Mask 的匹配个数（经 maxMatchCount 截取后）。</summary>
            public int MatchCount { get; init; }
            /// <summary>输入 Row/Column 总数（截取前）。</summary>
            public int InputMatchCount { get; init; }
            public int ValidRegionCount { get; init; }
            public bool KeepInsideMask { get; init; } = true;
        }

        /// <summary>解析 maskRegionMode：保留/keep → true；去掉/remove → false。</summary>
        public static bool ParseMaskRegionKeepInside(string? maskRegionMode)
        {
            string m = (maskRegionMode ?? "保留mask区域").Trim();
            if (m.Length == 0)
                return true;
            string lower = m.ToLowerInvariant();
            return lower is "remove" or "exclude" or "cut" or "挖空" or "去掉" or "去掉mask区域" or "排除"
                ? false
                : true;
        }

        /// <summary>截取前 <paramref name="maxMatchCount"/> 个匹配（0=全部）。</summary>
        public static (double[] Rows, double[] Cols, double[]? Angles, int UsedCount, int TotalCount) SliceShapeMatchPoseArrays(
            double[] rows,
            double[] cols,
            double[]? angles,
            int maxMatchCount)
        {
            int total = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (total == 0)
                return (Array.Empty<double>(), Array.Empty<double>(), angles, 0, 0);

            int used = maxMatchCount > 0 ? Math.Min(maxMatchCount, total) : total;
            double[] r = new double[used];
            double[] c = new double[used];
            Array.Copy(rows!, 0, r, 0, used);
            Array.Copy(cols!, 0, c, 0, used);
            double[]? a = null;
            if (angles != null && angles.Length > 0)
            {
                int alen = Math.Min(angles.Length, used);
                a = new double[used];
                Array.Copy(angles, 0, a, 0, alen);
            }

            return (r, c, a, used, total);
        }

        /// <summary>
        /// 用 FindShapeModel 位姿 + 模板轮廓生成区域，对原图做 Mask。
        /// keepInsideMask=true：区域内保留原图、区域外置 0；false：挖空匹配区域、区域外保留。
        /// </summary>
        public static ShapeMatchImageMaskResult MaskCalibImageByShapeMatch(
            CalibImage image,
            long modelId,
            double[] rows,
            double[] cols,
            double[]? angles,
            int contourLevel,
            double erosionInsetPx,
            double maskMin,
            double maskMax,
            bool preserveColor,
            bool keepInsideMask = true,
            int maxMatchCount = 0)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (modelId < 0)
                throw new ArgumentException("ModelId 无效", nameof(modelId));

            var sliced = SliceShapeMatchPoseArrays(rows, cols, angles, maxMatchCount);
            rows = sliced.Rows;
            cols = sliced.Cols;
            angles = sliced.Angles;
            int n = sliced.UsedCount;
            if (n == 0)
                throw new InvalidOperationException("形状匹配 Mask: Row/Column 为空，请先连接 Find 结果");

            image.RefreshProperties();
            int w = image.Width;
            int h = image.Height;

            ShapeMatchRegionMask[] masks = BuildShapeMatchFilledRegions(
                modelId, rows, cols, angles, scales: null, contourLevel, erosionInsetPx);
            try
            {
                int valid = 0;
                foreach (ShapeMatchRegionMask m in masks)
                {
                    if (m?.Region != null && m.Region.IsInitialized())
                        valid++;
                }

                if (valid == 0)
                    throw new InvalidOperationException(
                        $"形状匹配 Mask: 无法从 {n} 个匹配生成有效区域（输入共{sliced.TotalCount}个），请检查 ModelId/contourLevel={contourLevel}");

                using HRegion? union = UnionShapeMatchRegions(masks);
                if (union == null || !union.IsInitialized())
                    throw new InvalidOperationException("形状匹配 Mask: 区域合并失败");

                CalibImage mask = RegionToMaskCalibImage(union, w, h);
                try
                {
                    CalibImage masked = ApplyMaskToCalibImage(image, mask, maskMin, maskMax, preserveColor, keepInsideMask);
                    return new ShapeMatchImageMaskResult
                    {
                        MaskedImage = masked,
                        Mask = mask,
                        MatchCount = n,
                        InputMatchCount = sliced.TotalCount,
                        ValidRegionCount = valid,
                        KeepInsideMask = keepInsideMask
                    };
                }
                catch
                {
                    mask.Dispose();
                    throw;
                }
            }
            finally
            {
                foreach (ShapeMatchRegionMask m in masks)
                    m?.Dispose();
            }
        }

        /// <summary>
        /// keepInsideMask=true：Mask 在 [maskMin,maskMax] 内保留原图像素，否则为 0；
        /// false：该范围内置 0、范围外保留。支持 1/3 通道。
        /// </summary>
        public static CalibImage ApplyMaskToCalibImage(
            CalibImage image,
            CalibImage mask,
            double maskMin,
            double maskMax,
            bool preserveColor,
            bool keepInsideMask = true)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (mask == null)
                throw new ArgumentNullException(nameof(mask));

            image.RefreshProperties();
            if (!preserveColor || image.Channels == 1)
            {
                CalibImage gray = ToSingleChannelGray(image);
                try
                {
                    return GrayMaskApply(gray, mask, maskMin, maskMax, keepInsideMask);
                }
                finally
                {
                    gray.Dispose();
                }
            }

            if (image.Channels != 3)
                throw new InvalidOperationException($"HALCON Mask: 不支持 {image.Channels} 通道");

            CalibImage gm = ToSingleChannelGray(mask);
            try
            {
                var ni = image.GetNativeStruct();
                var nm = gm.GetNativeStruct();
                if (ni.width != nm.width || ni.height != nm.height)
                    throw new InvalidOperationException(
                        $"HALCON Mask: 图像与 Mask 须同尺寸 {ni.width}x{ni.height} vs {nm.width}x{nm.height}");

                int nPix = ni.width * ni.height;
                var bgr = new byte[nPix * 3];
                var bufM = new byte[nPix];
                Marshal.Copy(ni.data, bgr, 0, nPix * 3);
                Marshal.Copy(nm.data, bufM, 0, nPix);

                int mmn = (int)Math.Clamp(Math.Round(maskMin), 0, 255);
                int mmx = (int)Math.Clamp(Math.Round(maskMax), 0, 255);
                for (int p = 0; p < nPix; p++)
                {
                    bool inside = bufM[p] >= mmn && bufM[p] <= mmx;
                    bool zero = keepInsideMask ? !inside : inside;
                    if (zero)
                    {
                        bgr[p * 3] = 0;
                        bgr[p * 3 + 1] = 0;
                        bgr[p * 3 + 2] = 0;
                    }
                }

                var dst = new CalibImage(ni.width, ni.height, 3);
                var nd = dst.GetNativeStruct();
                Marshal.Copy(bgr, 0, nd.data, nPix * 3);
                return dst;
            }
            finally
            {
                gm.Dispose();
            }
        }

        /// <summary>点 (row,col) 是否落在任一区域实例内。</summary>
        public static int FindShapeMatchRegionIndex(ShapeMatchRegionMask[] masks, double row, double col)
        {
            if (masks == null || masks.Length == 0)
                return -1;

            for (int i = 0; i < masks.Length; i++)
            {
                HRegion? r = masks[i]?.Region;
                if (r == null || !r.IsInitialized())
                    continue;
                int isInside = r.TestRegionPoint(row, col);
                if (isInside == 1)
                    return i;
            }

            return -1;
        }


        /// <summary>
        /// use_polarity / ignore_global_polarity 需要轮廓带 edge_direction；阈值/多边形轮廓应使用 ignore_local_polarity。
        /// </summary>
        private static string ResolveShapeModelXldMetric(HObject conts, string metric)
        {
            string m = (metric ?? "").Trim();
            if (string.IsNullOrEmpty(m))
                return "ignore_local_polarity";

            if (!m.Equals("use_polarity", StringComparison.OrdinalIgnoreCase) &&
                !m.Equals("ignore_global_polarity", StringComparison.OrdinalIgnoreCase))
                return m;

            try
            {
                HOperatorSet.GetContourAttribXld(conts, "edge_direction", out HTuple attrib);
                if (attrib != null && attrib.Length > 0)
                    return m;
            }
            catch
            {
                // 无 edge_direction 属性
            }

            return "ignore_local_polarity";
        }

        /// <summary>
        /// 将 Point2D[] 列表转换为 HObject XLD（每条轮廓单独 GenContourPolygonXld 后 ConcatObj）
        /// </summary>
        private static HObject XldBundleToHObject(List<Point2D[]> contours)
        {
            if (contours == null || contours.Count == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }

            HObject acc = new HObject();
            HOperatorSet.GenEmptyObj(out acc);
            int added = 0;

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                if (contour == null || contour.Length < 2)
                    continue;

                var rowsList = new List<double>();
                var colsList = new List<double>();
                foreach (var pt in contour)
                {
                    double row = pt.Y;
                    double col = pt.X;
                    if (double.IsNaN(row) || double.IsNaN(col) ||
                        double.IsInfinity(row) || double.IsInfinity(col))
                        continue;
                    rowsList.Add(row);
                    colsList.Add(col);
                }

                if (rowsList.Count < 2)
                    continue;

                HOperatorSet.GenContourPolygonXld(
                    out HObject one,
                    new HTuple(rowsList.ToArray()),
                    new HTuple(colsList.ToArray()));

                if (added == 0)
                {
                    acc.Dispose();
                    acc = one;
                }
                else
                {
                    HOperatorSet.ConcatObj(acc, one, out HObject merged);
                    acc.Dispose();
                    one.Dispose();
                    acc = merged;
                }

                added++;
            }

            if (added == 0)
            {
                acc.Dispose();
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }

            return acc;
        }
    }
}
#endif
