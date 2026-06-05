using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>CalibImage 几何变换（旋转、翻转），供流程算子调用。</summary>
    internal static class CalibImageTransform
    {
        public static CalibImage Flip(CalibImage src, string flipMode)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            var n = src.GetNativeStruct();
            if (n.data == IntPtr.Zero || n.width <= 0 || n.height <= 0)
                throw new InvalidOperationException("翻转: 图像无有效数据");

            string mode = (flipMode ?? "horizontal").Trim().ToLowerInvariant();
            bool flipX = mode is "horizontal" or "h" or "x" or "左右" or "水平";
            bool flipY = mode is "vertical" or "v" or "y" or "上下" or "垂直";
            if (mode is "both" or "xy" or "hv" or "双向")
            {
                flipX = true;
                flipY = true;
            }
            else if (!flipX && !flipY)
                throw new InvalidOperationException($"翻转: 未知模式 '{flipMode}'（支持 horizontal / vertical / both）");

            if (flipX && flipY)
                return RotateRightAngle(src, 180);

            int w = n.width, h = n.height, ch = n.channels;
            int rowBytes = w * ch;
            int bytes = rowBytes * h;
            var buf = new byte[bytes];
            Marshal.Copy(n.data, buf, 0, bytes);

            var dst = new CalibImage(w, h, ch);
            var dn = dst.GetNativeStruct();
            var outBuf = new byte[bytes];

            for (int y = 0; y < h; y++)
            {
                int sy = flipY ? h - 1 - y : y;
                for (int x = 0; x < w; x++)
                {
                    int sx = flipX ? w - 1 - x : x;
                    int srcOff = sy * rowBytes + sx * ch;
                    int dstOff = y * rowBytes + x * ch;
                    for (int c = 0; c < ch; c++)
                        outBuf[dstOff + c] = buf[srcOff + c];
                }
            }

            Marshal.Copy(outBuf, 0, dn.data, bytes);
            return dst;
        }

        /// <param name="angleDeg">顺时针角度（90/180/270 走快速路径）。</param>
        /// <param name="expandCanvas">非直角旋转时 true=扩大画布容纳整图；false=保持原尺寸（中心裁剪）。</param>
        public static CalibImage Rotate(CalibImage src, double angleDeg, bool expandCanvas)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            double a = NormalizeClockwiseAngle(angleDeg);
            int snapped = ((int)Math.Round(a) % 360 + 360) % 360;

            if (snapped == 0)
                return CalibAPI.DuplicateImage(src);

            CalibImage rotated = snapped switch
            {
                90 => RotateRightAngle(src, 90),
                180 => RotateRightAngle(src, 180),
                270 => RotateRightAngle(src, 270),
                _ => RotateArbitrary(src, a, expandCanvas)
            };

            if (!expandCanvas && (rotated.Width != src.Width || rotated.Height != src.Height))
            {
                CalibImage cropped = CenterCropToSize(rotated, src.Width, src.Height);
                if (!ReferenceEquals(cropped, rotated))
                    rotated.Dispose();
                return cropped;
            }

            return rotated;
        }

        /// <summary>中心对齐裁剪/贴到目标尺寸，不足区域填黑。</summary>
        private static CalibImage CenterCropToSize(CalibImage src, int targetW, int targetH)
        {
            int sw = src.Width;
            int sh = src.Height;
            if (sw == targetW && sh == targetH)
                return CalibAPI.DuplicateImage(src);

            int ch = src.GetNativeStruct().channels;
            var dst = new CalibImage(targetW, targetH, ch);
            var dn = dst.GetNativeStruct();
            int dstBytes = targetW * targetH * ch;
            var outBuf = new byte[dstBytes];
            Array.Clear(outBuf, 0, outBuf.Length);

            int copyW = Math.Min(sw, targetW);
            int copyH = Math.Min(sh, targetH);
            int srcX0 = Math.Max(0, (sw - copyW) / 2);
            int srcY0 = Math.Max(0, (sh - copyH) / 2);
            int dstX0 = Math.Max(0, (targetW - copyW) / 2);
            int dstY0 = Math.Max(0, (targetH - copyH) / 2);

            var sn = src.GetNativeStruct();
            int srcRow = sw * ch;
            int dstRow = targetW * ch;
            var srcBuf = new byte[sw * sh * ch];
            Marshal.Copy(sn.data, srcBuf, 0, srcBuf.Length);

            for (int y = 0; y < copyH; y++)
            {
                int sy = srcY0 + y;
                int dy = dstY0 + y;
                int srcOff = sy * srcRow + srcX0 * ch;
                int dstOff = dy * dstRow + dstX0 * ch;
                Array.Copy(srcBuf, srcOff, outBuf, dstOff, copyW * ch);
            }

            Marshal.Copy(outBuf, 0, dn.data, outBuf.Length);
            return dst;
        }

        private static double NormalizeClockwiseAngle(double angleDeg)
        {
            double a = angleDeg % 360.0;
            if (a < 0) a += 360.0;
            return a;
        }

        private static CalibImage RotateRightAngle(CalibImage src, int angleCw)
        {
            var n = src.GetNativeStruct();
            int w = n.width, h = n.height, ch = n.channels;
            int rowBytes = w * ch;
            var buf = new byte[rowBytes * h];
            Marshal.Copy(n.data, buf, 0, buf.Length);

            int newW, newH;
            if (angleCw == 90 || angleCw == 270)
            {
                newW = h;
                newH = w;
            }
            else
            {
                newW = w;
                newH = h;
            }

            var dst = new CalibImage(newW, newH, ch);
            var dn = dst.GetNativeStruct();
            var outBuf = new byte[newW * newH * ch];
            int newRowBytes = newW * ch;

            for (int y = 0; y < newH; y++)
            {
                for (int x = 0; x < newW; x++)
                {
                    int sx, sy;
                    switch (angleCw)
                    {
                        case 90:
                            sx = y;
                            sy = w - 1 - x;
                            break;
                        case 180:
                            sx = w - 1 - x;
                            sy = h - 1 - y;
                            break;
                        case 270:
                            sx = h - 1 - y;
                            sy = x;
                            break;
                        default:
                            sx = x;
                            sy = y;
                            break;
                    }

                    int srcOff = sy * rowBytes + sx * ch;
                    int dstOff = y * newRowBytes + x * ch;
                    for (int c = 0; c < ch; c++)
                        outBuf[dstOff + c] = buf[srcOff + c];
                }
            }

            Marshal.Copy(outBuf, 0, dn.data, outBuf.Length);
            return dst;
        }

        private static CalibImage RotateArbitrary(CalibImage src, double angleCwDeg, bool expandCanvas)
        {
            int channels = src.GetNativeStruct().channels;
            using Bitmap? srcBmpRaw = src.ToBitmap();
            if (srcBmpRaw == null)
                throw new InvalidOperationException("旋转: 无法转换为位图");

            using var srcBmp = CloneToGdiSafeBitmap(srcBmpRaw);

            double rad = angleCwDeg * Math.PI / 180.0;
            int srcW = srcBmp.Width, srcH = srcBmp.Height;
            int dstW, dstH;
            if (expandCanvas)
            {
                double absCos = Math.Abs(Math.Cos(rad));
                double absSin = Math.Abs(Math.Sin(rad));
                dstW = (int)Math.Ceiling(srcW * absCos + srcH * absSin);
                dstH = (int)Math.Ceiling(srcW * absSin + srcH * absCos);
                dstW = Math.Max(1, dstW);
                dstH = Math.Max(1, dstH);
            }
            else
            {
                dstW = srcW;
                dstH = srcH;
            }

            using var dstRgb = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dstRgb))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.None;
                g.TranslateTransform(dstW / 2f, dstH / 2f);
                g.RotateTransform((float)angleCwDeg);
                g.TranslateTransform(-srcW / 2f, -srcH / 2f);
                g.DrawImage(srcBmp, 0, 0, srcW, srcH);
            }

            if (channels == 1)
                return FromBitmapGray(RgbBitmapToGray8(dstRgb));

            return FromBitmapBgr(dstRgb);
        }

        private static bool IsIndexedPixelFormat(PixelFormat fmt) =>
            (fmt & PixelFormat.Indexed) != 0;

        /// <summary>GDI+ 不能在索引格式位图上创建 Graphics；先转到 24bpp RGB。</summary>
        private static Bitmap CloneToGdiSafeBitmap(Bitmap bmp)
        {
            if (!IsIndexedPixelFormat(bmp.PixelFormat))
                return (Bitmap)bmp.Clone();

            var rgb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(rgb))
            {
                g.Clear(Color.Black);
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            }

            return rgb;
        }

        private static Bitmap RgbBitmapToGray8(Bitmap rgb)
        {
            var gray = new Bitmap(rgb.Width, rgb.Height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = gray.Palette;
            for (int i = 0; i < 256; i++)
                pal.Entries[i] = Color.FromArgb(i, i, i);
            gray.Palette = pal;

            var rect = new Rectangle(0, 0, rgb.Width, rgb.Height);
            BitmapData srcBd = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            BitmapData dstBd = gray.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                int w = rgb.Width;
                int h = rgb.Height;
                int srcStride = Math.Abs(srcBd.Stride);
                int dstStride = Math.Abs(dstBd.Stride);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int si = y * srcStride + x * 3;
                        byte b = Marshal.ReadByte(srcBd.Scan0, si);
                        byte gch = Marshal.ReadByte(srcBd.Scan0, si + 1);
                        byte r = Marshal.ReadByte(srcBd.Scan0, si + 2);
                        byte lum = (byte)(0.299 * r + 0.587 * gch + 0.114 * b);
                        Marshal.WriteByte(dstBd.Scan0, y * dstStride + x, lum);
                    }
                }
            }
            finally
            {
                rgb.UnlockBits(srcBd);
                gray.UnlockBits(dstBd);
            }

            return gray;
        }

        private static CalibImage FromBitmap(Bitmap bmp)
        {
            if (bmp == null) throw new ArgumentNullException(nameof(bmp));

            if (bmp.PixelFormat == PixelFormat.Format8bppIndexed)
                return FromBitmapGray(bmp);

            using var rgb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(rgb))
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            return FromBitmapBgr(rgb);
        }

        private static CalibImage FromBitmapGray(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed);
            try
            {
                int w = bmp.Width, h = bmp.Height;
                int stride = Math.Abs(bd.Stride);
                var dst = new CalibImage(w, h, 1);
                var dn = dst.GetNativeStruct();
                var outBuf = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    IntPtr srcRow = IntPtr.Add(bd.Scan0, y * stride);
                    Marshal.Copy(srcRow, outBuf, y * w, w);
                }
                Marshal.Copy(outBuf, 0, dn.data, outBuf.Length);
                return dst;
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
        }

        private static CalibImage FromBitmapBgr(Bitmap rgbBmp)
        {
            var rect = new Rectangle(0, 0, rgbBmp.Width, rgbBmp.Height);
            var bd = rgbBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = rgbBmp.Width, h = rgbBmp.Height;
                int stride = Math.Abs(bd.Stride);
                var dst = new CalibImage(w, h, 3);
                var dn = dst.GetNativeStruct();
                var bgr = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                {
                    IntPtr srcRow = IntPtr.Add(bd.Scan0, y * stride);
                    for (int x = 0; x < w; x++)
                    {
                        int si = y * stride + x * 3;
                        int di = (y * w + x) * 3;
                        byte r = Marshal.ReadByte(srcRow, x * 3 + 0);
                        byte g = Marshal.ReadByte(srcRow, x * 3 + 1);
                        byte b = Marshal.ReadByte(srcRow, x * 3 + 2);
                        bgr[di + 0] = b;
                        bgr[di + 1] = g;
                        bgr[di + 2] = r;
                    }
                }
                Marshal.Copy(bgr, 0, dn.data, bgr.Length);
                return dst;
            }
            finally
            {
                rgbBmp.UnlockBits(bd);
            }
        }

        public static double ParseAngleDegrees(string? raw, double defaultDeg = 90)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultDeg;
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && !double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out a))
                throw new InvalidOperationException($"旋转: 无效角度 '{raw}'");
            return a;
        }

        /// <summary>缩放 CalibImage。mode: factor / absolute / max_side。</summary>
        public static CalibImage Resize(
            CalibImage src,
            string mode,
            double scale,
            int targetWidth,
            int targetHeight,
            int maxSide,
            bool keepAspect,
            string interpolation)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            var n = src.GetNativeStruct();
            if (n.data == IntPtr.Zero || n.width <= 0 || n.height <= 0)
                throw new InvalidOperationException("图像缩放: 图像无有效数据");

            ResolveResizeOutputSize(
                mode, scale, targetWidth, targetHeight, maxSide, keepAspect,
                n.width, n.height, out int newW, out int newH);

            if (newW == n.width && newH == n.height)
                return CalibAPI.DuplicateImage(src);

            using Bitmap? srcBmpRaw = src.ToBitmap()
                ?? throw new InvalidOperationException("图像缩放: 无法转换为位图");
            using var srcBmp = CloneToGdiSafeBitmap(srcBmpRaw);

            using var dstRgb = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dstRgb))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = ParseInterpolation(interpolation);
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(srcBmp, new Rectangle(0, 0, newW, newH), new Rectangle(0, 0, srcBmp.Width, srcBmp.Height), GraphicsUnit.Pixel);
            }

            if (n.channels == 1)
                return FromBitmapGray(RgbBitmapToGray8(dstRgb));

            return FromBitmapBgr(dstRgb);
        }

        private static void ResolveResizeOutputSize(
            string? modeRaw,
            double scale,
            int targetWidth,
            int targetHeight,
            int maxSide,
            bool keepAspect,
            int srcW,
            int srcH,
            out int dstW,
            out int dstH)
        {
            string mode = (modeRaw ?? "factor").Trim().ToLowerInvariant();
            if (mode is "max_side" or "maxside" or "fit" or "最长边")
                mode = "max_side";
            else if (mode is "absolute" or "size" or "绝对" or "指定尺寸")
                mode = "absolute";
            else
                mode = "factor";

            if (mode == "factor")
            {
                if (scale <= 0)
                    throw new InvalidOperationException("图像缩放: scale 须 > 0");
                dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                return;
            }

            if (mode == "max_side")
            {
                if (maxSide <= 0)
                    throw new InvalidOperationException("图像缩放: max_side 模式须填写 maxSide > 0");
                int ms = maxSide;
                if (srcW >= srcH)
                {
                    dstW = ms;
                    dstH = Math.Max(1, (int)Math.Round((double)srcH * ms / srcW));
                }
                else
                {
                    dstH = ms;
                    dstW = Math.Max(1, (int)Math.Round((double)srcW * ms / srcH));
                }
                return;
            }

            if (targetWidth <= 0 && targetHeight <= 0)
                throw new InvalidOperationException("图像缩放: absolute 模式须指定 width 或 height > 0");

            if (targetWidth > 0 && targetHeight > 0)
            {
                dstW = targetWidth;
                dstH = targetHeight;
                return;
            }

            if (targetWidth > 0)
            {
                dstW = targetWidth;
                dstH = keepAspect
                    ? Math.Max(1, (int)Math.Round((double)srcH * targetWidth / srcW))
                    : srcH;
                return;
            }

            dstH = targetHeight;
            dstW = keepAspect
                ? Math.Max(1, (int)Math.Round((double)srcW * targetHeight / srcH))
                : srcW;
        }

        private static InterpolationMode ParseInterpolation(string? raw)
        {
            string s = (raw ?? "linear").Trim().ToLowerInvariant();
            return s switch
            {
                "nearest" or "近邻" => InterpolationMode.NearestNeighbor,
                "bicubic" or "双三次" => InterpolationMode.HighQualityBicubic,
                _ => InterpolationMode.HighQualityBilinear,
            };
        }

        public static double ParseScale(string? raw, double defaultScale = 1.0)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultScale;
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && !double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                throw new InvalidOperationException($"图像缩放: 无效 scale '{raw}'");
            return v;
        }

        public static int ParsePositiveOrZeroInt(string? raw, int defaultValue = 0)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                throw new InvalidOperationException($"图像缩放: 无效整数 '{raw}'");
            return Math.Max(0, v);
        }
    }
}
