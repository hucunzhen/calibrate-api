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

            if (Math.Abs(a) < 1e-6)
                return CalibAPI.DuplicateImage(src);
            if (Math.Abs(a - 90) < 1e-6)
                return RotateRightAngle(src, 90);
            if (Math.Abs(a - 180) < 1e-6)
                return RotateRightAngle(src, 180);
            if (Math.Abs(a - 270) < 1e-6)
                return RotateRightAngle(src, 270);

            return RotateArbitrary(src, a, expandCanvas);
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
            using var srcBmp = src.ToBitmap();
            if (srcBmp == null)
                throw new InvalidOperationException("旋转: 无法转换为位图");

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

            using var dstBmp = new Bitmap(dstW, dstH, srcBmp.PixelFormat);
            if (srcBmp.PixelFormat == PixelFormat.Format8bppIndexed)
            {
                ColorPalette pal = dstBmp.Palette;
                for (int i = 0; i < 256; i++)
                    pal.Entries[i] = Color.FromArgb(i, i, i);
                dstBmp.Palette = pal;
            }

            using (var g = Graphics.FromImage(dstBmp))
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

            return FromBitmap(dstBmp);
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
    }
}
