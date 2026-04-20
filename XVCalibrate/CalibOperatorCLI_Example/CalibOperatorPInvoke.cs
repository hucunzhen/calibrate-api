/**
 * CalibOperatorPInvoke.cs - C# P/Invoke Wrapper for CalibOperator
 * 
 * 本文件使用 P/Invoke (Platform Invoke) 方式调用 native C++ DLL，
 * 避免了 C++/CLI 与 OpenCV intrinsics 的兼容性问题。
 * 
 * 使用方式:
 * 1. 先编译 CalibOperatorNative 项目生成 CalibOperatorNative.dll
 * 2. 确保 opencv_world4130.dll 在同目录或 PATH 中
 * 3. 在 C# 项目中添加本文件或引用本命名空间
 */

using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CalibOperatorPInvoke
{
    // ================================================================
    // Native Structures (must match C struct layout)
    // ================================================================

    /// <summary>
    /// 2D 点坐标
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint2D
    {
        public double x;
        public double y;
    }

    /// <summary>
    /// 仿射变换矩阵
    /// X = a*x + b*y + c
    /// Y = d*x + e*y + f
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeAffineTransform
    {
        public double a, b, c;
        public double d, e, f;
    }

    /// <summary>
    /// 图像结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeImage
    {
        public int width;
        public int height;
        public int channels;
        public IntPtr data;  // unsigned char*
    }

    // ================================================================
    // Managed Structures
    // ================================================================

    /// <summary>
    /// 托管 2D 点
    /// </summary>
    public struct Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        internal NativePoint2D ToNative()
        {
            return new NativePoint2D { x = X, y = Y };
        }

        internal static Point2D FromNative(NativePoint2D native)
        {
            return new Point2D(native.x, native.y);
        }
    }

    /// <summary>
    /// 仿射变换
    /// </summary>
    public struct AffineTransform
    {
        public double A, B, C;  // X = a*x + b*y + c
        public double D, E, F;  // Y = d*x + e*y + f

        internal NativeAffineTransform ToNative()
        {
            return new NativeAffineTransform { a = A, b = B, c = C, d = D, e = E, f = F };
        }

        internal static AffineTransform FromNative(NativeAffineTransform native)
        {
            return new AffineTransform
            {
                A = native.a, B = native.b, C = native.c,
                D = native.d, E = native.e, F = native.f
            };
        }

        public override string ToString()
        {
            return $"X = {A:F6}*x + {B:F6}*y + {C:F6}\nY = {D:F6}*x + {E:F6}*y + {F:F6}";
        }
    }

    /// <summary>
    /// 标定结果
    /// </summary>
    public class CalibrationResult
    {
        public AffineTransform Transform { get; set; }
        public double AverageError { get; set; }
        public double MaxError { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 轨迹检测结果
    /// </summary>
    public class TrajectoryResult
    {
        public Point2D[] Points { get; set; }
        public int[] BarIds { get; set; }
        public int Count { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    // ================================================================
    // Native API (P/Invoke)
    // ================================================================

    /// <summary>
    /// Native API 声明
    /// </summary>
    internal static class NativeAPI
    {
        private const string DllName = "CalibOperatorNative.dll";

        // Image management
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CALIB_CreateImage();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_FreeImage(IntPtr img);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_CreateCalibrationImage(IntPtr img, int width, int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_CreateBlankImage(IntPtr img, int width, int height, int channels);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CALIB_SaveBMP(string filename, IntPtr img);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CALIB_LoadBMP(string filename, IntPtr img);

        // Circle detection
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_DetectCircles(IntPtr img, IntPtr pts, ref int count, int maxCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_DrawDetectedCircles(IntPtr img, IntPtr pts, int count, int gray);

        // Calibration
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_CalibrateNinePoint(IntPtr imagePts, IntPtr worldPts, int n, ref NativeAffineTransform trans);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern NativePoint2D CALIB_ImageToWorld(NativePoint2D pixel, NativeAffineTransform trans);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_SetTransform(NativeAffineTransform trans);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_CalculateError(IntPtr imagePts, IntPtr worldPts, int n,
            NativeAffineTransform trans, ref double avgErr, ref double maxErr);

        // Trajectory detection
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_DetectTrajectoryOpenCV(IntPtr img, IntPtr trajPixels, ref int count,
            IntPtr stepImages, IntPtr stepBarIds, int stepImageCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_DetectTrajectory(IntPtr img, IntPtr trajPixels, ref int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_DrawTrajectoryColored(IntPtr img, IntPtr trajPixels, int count, IntPtr barIds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_DrawTrajectoryGrayscale(IntPtr img, IntPtr trajPixels, int count, int grayValue);

        // Bar colors
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_GetBarColors(byte[] colors);
    }

    // ================================================================
    // CalibImage Wrapper
    // ================================================================

    /// <summary>
    /// 托管图像包装类
    /// </summary>
    public class CalibImage : IDisposable
    {
        private IntPtr _nativePtr;
        private bool _disposed = false;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Channels { get; private set; }

        internal IntPtr NativePtr => _nativePtr;

        public CalibImage()
        {
            _nativePtr = NativeAPI.CALIB_CreateImage();
            if (_nativePtr == IntPtr.Zero)
                throw new OutOfMemoryException("Failed to create native image");
        }

        public CalibImage(int width, int height, int channels)
        {
            _nativePtr = NativeAPI.CALIB_CreateImage();
            if (_nativePtr == IntPtr.Zero)
                throw new OutOfMemoryException("Failed to create native image");

            int result = NativeAPI.CALIB_CreateBlankImage(_nativePtr, width, height, channels);
            if (result != 0)
                throw new InvalidOperationException($"Failed to create blank image: {result}");

            Width = width;
            Height = height;
            Channels = channels;
        }

        ~CalibImage()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_nativePtr != IntPtr.Zero)
                {
                    NativeAPI.CALIB_FreeImage(_nativePtr);
                    _nativePtr = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal NativeImage GetNativeStruct()
        {
            return Marshal.PtrToStructure<NativeImage>(_nativePtr);
        }

        /// <summary>
        /// 转换为 System.Drawing.Bitmap
        /// </summary>
        public Bitmap ToBitmap()
        {
            var native = GetNativeStruct();
            if (native.data == IntPtr.Zero || native.width == 0 || native.height == 0)
                return null;

            Bitmap bmp;
            if (native.channels == 1)
            {
                // Grayscale
                bmp = new Bitmap(native.width, native.height, PixelFormat.Format8bppIndexed);
                
                // Set grayscale palette
                ColorPalette palette = bmp.Palette;
                for (int i = 0; i < 256; i++)
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                bmp.Palette = palette;

                // Copy data
                Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                int stride = Math.Abs(bd.Stride);
                for (int y = 0; y < native.height; y++)
                {
                    IntPtr srcRow = IntPtr.Add(native.data, y * native.width);
                    IntPtr dstRow = IntPtr.Add(bd.Scan0, y * stride);
                    CopyMemory(dstRow, srcRow, (uint)native.width);
                }
                bmp.UnlockBits(bd);
            }
            else if (native.channels == 3)
            {
                // BGR -> RGB
                bmp = new Bitmap(native.width, native.height, PixelFormat.Format24bppRgb);
                Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                byte[] srcData = new byte[native.width * native.height * 3];
                Marshal.Copy(native.data, srcData, 0, srcData.Length);
                
                unsafe
                {
                    byte* dst = (byte*)bd.Scan0;
                    for (int i = 0; i < native.width * native.height; i++)
                    {
                        dst[i * 3 + 0] = srcData[i * 3 + 2];  // R = B
                        dst[i * 3 + 1] = srcData[i * 3 + 1];  // G = G
                        dst[i * 3 + 2] = srcData[i * 3 + 0];  // B = R
                    }
                }
                bmp.UnlockBits(bd);
            }
            else
            {
                return null;
            }

            return bmp;
        }

        /// <summary>
        /// 从文件加载图像
        /// </summary>
        public static CalibImage Load(string filename)
        {
            CalibImage img = new CalibImage();
            int result = NativeAPI.CALIB_LoadBMP(filename, img.NativePtr);
            if (result != 1)
            {
                img.Dispose();
                throw new Exception($"Failed to load image: {filename}");
            }
            img.RefreshProperties();
            return img;
        }

        /// <summary>
        /// 保存图像到文件
        /// </summary>
        public bool Save(string filename)
        {
            return NativeAPI.CALIB_SaveBMP(filename, _nativePtr) == 0;
        }

        internal void RefreshProperties()
        {
            var native = GetNativeStruct();
            Width = native.width;
            Height = native.height;
            Channels = native.channels;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint length);
    }

    // ================================================================
    // CalibOperator API
    // ================================================================

    /// <summary>
    /// CalibOperator C# API
    /// </summary>
    public static class CalibAPI
    {
        public const int ImageWidth = 2448;
        public const int ImageHeight = 2048;
        public const int MaxTrajectoryPoints = 50000;
        public const int MaxBars = 16;

        // ================================================================
        // Image Creation & I/O
        // ================================================================

        /// <summary>
        /// 创建标定图案图像
        /// </summary>
        public static CalibImage CreateCalibrationImage(int width, int height)
        {
            CalibImage img = new CalibImage();
            int result = NativeAPI.CALIB_CreateCalibrationImage(img.NativePtr, width, height);
            if (result != 0)
            {
                img.Dispose();
                throw new Exception($"Failed to create calibration image: error {result}");
            }
            img.RefreshProperties();
            return img;
        }

        /// <summary>
        /// 加载 BMP 图像
        /// </summary>
        public static CalibImage LoadImage(string filename)
        {
            return CalibImage.Load(filename);
        }

        /// <summary>
        /// 保存图像
        /// </summary>
        public static bool SaveImage(string filename, CalibImage img)
        {
            return img.Save(filename);
        }

        // ================================================================
        // Circle Detection
        // ================================================================

        /// <summary>
        /// 检测圆点
        /// </summary>
        public static Point2D[] DetectCircles(CalibImage img)
        {
            const int MaxPoints = 9;
            int count = 0;

            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * MaxPoints);
            try
            {
                int detected = NativeAPI.CALIB_DetectCircles(img.NativePtr, ptsPtr, ref count, MaxPoints);
                
                Point2D[] result = new Point2D[detected];
                for (int i = 0; i < detected; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    NativePoint2D nativePt = Marshal.PtrToStructure<NativePoint2D>(elementPtr);
                    result[i] = Point2D.FromNative(nativePt);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
            }
        }

        /// <summary>
        /// 绘制检测到的圆
        /// </summary>
        public static void DrawDetectedCircles(CalibImage img, Point2D[] pts, int gray = 255)
        {
            if (pts == null || pts.Length == 0) return;

            int count = pts.Length;
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(pts[i].ToNative(), elementPtr, false);
                }
                NativeAPI.CALIB_DrawDetectedCircles(img.NativePtr, ptsPtr, count, gray);
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
            }
        }

        // ================================================================
        // Calibration
        // ================================================================

        /// <summary>
        /// 九点标定
        /// </summary>
        public static CalibrationResult CalibrateNinePoint(Point2D[] imagePts, Point2D[] worldPts)
        {
            CalibrationResult result = new CalibrationResult();

            if (imagePts == null || worldPts == null || imagePts.Length != worldPts.Length)
            {
                result.ErrorMessage = "Invalid point arrays";
                return result;
            }

            int n = imagePts.Length;
            IntPtr imgPtsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * n);
            IntPtr worldPtsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * n);

            try
            {
                // Copy image points to native memory
                for (int i = 0; i < n; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(imgPtsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(imagePts[i].ToNative(), elementPtr, false);
                }

                // Copy world points to native memory
                for (int i = 0; i < n; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(worldPtsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(worldPts[i].ToNative(), elementPtr, false);
                }

                NativeAffineTransform nativeTrans = new NativeAffineTransform();
                int success = NativeAPI.CALIB_CalibrateNinePoint(imgPtsPtr, worldPtsPtr, n, ref nativeTrans);

                if (success == 1)
                {
                    result.Transform = AffineTransform.FromNative(nativeTrans);
                    result.Success = true;

                    double avgErr = 0, maxErr = 0;
                    NativeAPI.CALIB_CalculateError(imgPtsPtr, worldPtsPtr, n, nativeTrans, ref avgErr, ref maxErr);
                    result.AverageError = avgErr;
                    result.MaxError = maxErr;
                }
                else
                {
                    result.ErrorMessage = $"Calibration failed with code {success}";
                }
            }
            finally
            {
                Marshal.FreeHGlobal(imgPtsPtr);
                Marshal.FreeHGlobal(worldPtsPtr);
            }

            return result;
        }

        /// <summary>
        /// 像素坐标转世界坐标
        /// </summary>
        public static Point2D ImageToWorld(Point2D pixel, AffineTransform transform)
        {
            return Point2D.FromNative(NativeAPI.CALIB_ImageToWorld(pixel.ToNative(), transform.ToNative()));
        }

        /// <summary>
        /// 设置全局变换矩阵
        /// </summary>
        public static void SetTransform(AffineTransform transform)
        {
            NativeAPI.CALIB_SetTransform(transform.ToNative());
        }

        // ================================================================
        // Trajectory Detection
        // ================================================================

        /// <summary>
        /// 检测轨迹
        /// </summary>
        public static TrajectoryResult DetectTrajectoryOpenCV(CalibImage img)
        {
            TrajectoryResult result = new TrajectoryResult();

            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * MaxTrajectoryPoints);

            try
            {
                int count = 0;
                int detected = NativeAPI.CALIB_DetectTrajectoryOpenCV(img.NativePtr, ptsPtr, ref count,
                    IntPtr.Zero, IntPtr.Zero, 0);

                if (detected > 0)
                {
                    result.Points = new Point2D[detected];
                    for (int i = 0; i < detected; i++)
                    {
                        IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                        NativePoint2D nativePt = Marshal.PtrToStructure<NativePoint2D>(elementPtr);
                        result.Points[i] = Point2D.FromNative(nativePt);
                    }
                    result.Count = detected;
                    result.Success = true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
            }

            return result;
        }

        /// <summary>
        /// 绘制彩色轨迹
        /// </summary>
        public static void DrawTrajectoryColored(CalibImage img, Point2D[] trajPixels, int[] barIds)
        {
            if (trajPixels == null || trajPixels.Length == 0) return;

            int count = trajPixels.Length;
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * count);
            IntPtr barIdsPtr = IntPtr.Zero;

            try
            {
                // Copy points to native memory
                for (int i = 0; i < count; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(trajPixels[i].ToNative(), elementPtr, false);
                }

                if (barIds != null)
                {
                    int[] nativeBarIds = new int[count];
                    for (int i = 0; i < count && i < barIds.Length; i++)
                        nativeBarIds[i] = barIds[i];
                    barIdsPtr = Marshal.AllocHGlobal(sizeof(int) * count);
                    Marshal.Copy(nativeBarIds, 0, barIdsPtr, count);
                }

                NativeAPI.CALIB_DrawTrajectoryColored(img.NativePtr, ptsPtr, count, barIdsPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
                if (barIdsPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(barIdsPtr);
            }
        }

        /// <summary>
        /// 绘制灰度轨迹（单通道图像）
        /// </summary>
        public static void DrawTrajectoryGrayscale(CalibImage img, Point2D[] trajPixels, int grayValue)
        {
            if (trajPixels == null || trajPixels.Length == 0) return;

            int count = trajPixels.Length;
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * count);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(trajPixels[i].ToNative(), elementPtr, false);
                }

                NativeAPI.CALIB_DrawTrajectoryGrayscale(img.NativePtr, ptsPtr, count, grayValue);
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
            }
        }

        // ================================================================
        // Bar Colors
        // ================================================================

        /// <summary>
        /// 获取轨迹条颜色 (BGR format)
        /// </summary>
        public static Color[] GetBarColors()
        {
            byte[] nativeColors = new byte[48];
            NativeAPI.CALIB_GetBarColors(nativeColors);

            Color[] colors = new Color[16];
            for (int i = 0; i < 16; i++)
            {
                colors[i] = Color.FromArgb(
                    nativeColors[i * 3 + 2],  // R
                    nativeColors[i * 3 + 1],  // G
                    nativeColors[i * 3 + 0]   // B
                );
            }
            return colors;
        }
    }
}
