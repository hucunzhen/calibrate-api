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
using System.Linq;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

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

        /// <summary>
        /// 中间步骤图（DetectHollowTrajectory 等函数填充）
        /// 每个元素为 CalibImage，调用者负责 Dispose
        /// </summary>
        public List<CalibImage> StepImages { get; set; } = new List<CalibImage>();

        /// <summary>
        /// 中间步骤图标签名称
        /// </summary>
        public List<string> StepLabels { get; set; } = new List<string>();
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
        public static extern void CALIB_FreeImageData(IntPtr data);

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
            IntPtr stepImages, IntPtr stepBarIds, int stepImageCount, int useContourMode, int fitMode, double approxEpsilon);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_DetectTrajectory(IntPtr img, IntPtr trajPixels, ref int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_DetectHollowTrajectory(IntPtr img, IntPtr trajPixels, ref int count,
            IntPtr stepImages, IntPtr stepBarIds, int stepImageCount,
            int blurKsize, int morphKernelSize,
            int targetHollows, int bandWidth,
            int useContourMode, int outerExpandPixels,
            double grayMergeRatio,
            int hollowGrayLow, int hollowGrayHigh);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_DrawTrajectoryColored(IntPtr img, IntPtr trajPixels, int count, IntPtr barIds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_DrawTrajectoryGrayscale(IntPtr img, IntPtr trajPixels, int count, int grayValue);

        // Bar colors
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_GetBarColors(byte[] colors);

        // Trajectory step-by-step API
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_ApplyMask(IntPtr src, IntPtr mask, IntPtr outImg);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_SampleContours(IntPtr binaryData, int width, int height,
            int targetBars, double spacing, int minArea,
            IntPtr outPoints, IntPtr outBarIds, int maxPoints);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_ExportSortedContours(IntPtr ctx,
            IntPtr flatX, IntPtr flatY, IntPtr contourLengths, int maxContours, int maxTotalPoints);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_SampleContoursFromPoints(IntPtr flatX, IntPtr flatY,
            IntPtr contourLengths, int numContours,
            int targetBars, int width, int height, double spacing,
            IntPtr outPoints, IntPtr outBarIds, int maxPoints);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CALIB_TrajStep_Create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CALIB_TrajStep_Free(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_1_ConvertToGrayscale(IntPtr ctx, IntPtr src);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern int CALIB_TrajStep_1_5_WatershedPresegment(IntPtr ctx, [MarshalAs(UnmanagedType.I1)] bool enable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_1b_CLAHE(IntPtr ctx, double clipLimit, int tileGridSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_2b_CannyEdges(IntPtr ctx, double lowThreshold, double highThreshold,
                                                              int blurKsize, int bilateralD, double bilateralSigmaColor, double bilateralSigmaSpace,
                                                              int nlmeansH, int nlmeansTemplateSize, int nlmeansSearchSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_2_PreprocessAndFindContours(IntPtr ctx, int blurKsize, int morphKernelSize, [MarshalAs(UnmanagedType.I1)] bool enableWatershed, double minArea);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_2c_GrayRangeBinary(IntPtr ctx, int grayLow, int grayHigh);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_2_5_CreateMask(IntPtr ctx, int contourIdx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_3_CreateWorkpieceMask(IntPtr ctx, int maxContourIdx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_3b_DetectHollow(IntPtr ctx, int hollowGrayLow, int hollowGrayHigh, int targetHollows, int morphKernelSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_4_DetectDarkBars(IntPtr ctx, int darkThreshold, int darkMinThreshold, int outerThreshold);


        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_SetDarkBinary(IntPtr ctx, IntPtr data, int width, int height);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_SetGrayMat(IntPtr ctx, IntPtr data, int width, int height);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_SetMask(IntPtr ctx, IntPtr data, int width, int height);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_GetContourVis(IntPtr ctx, IntPtr outImage);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_5_MorphologyCleanup(IntPtr ctx, int kernelSize, int blurKsize, double blurSigma);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_5_5_ExpandToEdgeBoundary(IntPtr ctx, int expandPixels);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_6_FindAndSortDarkContours(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_7_SampleContours(IntPtr ctx, int targetBars, double spacing);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_7_5_FitShape(IntPtr ctx, int fitMode, double approxEpsilon);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_8_VerifyByMask(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_9_DeduplicateAndSort(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_10_ConvertToOutput(IntPtr ctx, IntPtr trajPixels, IntPtr count, IntPtr stepBarIds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_11_DrawColoredTrajectory(IntPtr ctx, IntPtr outputData, int width, int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_GetStepImage(IntPtr ctx, int step, IntPtr outImage);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_GetPointCount(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CALIB_TrajStep_GetPoints(IntPtr ctx, IntPtr points, IntPtr barIds, int maxCount);
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

        public int Width { get; set; }
        public int Height { get; set; }
        public int Channels { get; set; }

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
        /// <param name="useContourMode">true=Canvas 轮廓等弧长采样，false=膨胀+网格采样（旧方案）</param>
        /// <param name="fitMode">0=stadium跑道型拟合(默认), 1=曲率去噪(删除局部曲率异常大的点)</param>
        /// <param name="approxEpsilon">保留参数，fitMode=1时未使用</param>
        public static TrajectoryResult DetectTrajectoryOpenCV(CalibImage img, bool useContourMode = false, int fitMode = 0, double approxEpsilon = 0.0)
        {
            TrajectoryResult result = new TrajectoryResult();

            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * MaxTrajectoryPoints);

            try
            {
                int count = 0;
                int detected = NativeAPI.CALIB_DetectTrajectoryOpenCV(img.NativePtr, ptsPtr, ref count,
                    IntPtr.Zero, IntPtr.Zero, 0, useContourMode ? 1 : 0, fitMode, approxEpsilon);

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
        /// 空洞轨迹检测（新方案）
        /// 通过二值化获取最大轮廓作为 mask，在 mask 内部反求空洞，
        /// 将空洞边缘灰度介于内部/外部均值之间的像素也归入空洞，
        /// 然后等间距轮廓采样生成轨迹。
        /// 返回结果中包含 StepImages（中间步骤图）和 StepLabels（标签名）。
        /// </summary>
        /// <param name="img">输入图像</param>
        /// <param name="blurKsize">高斯模糊核大小（奇数，默认7）</param>
        /// <param name="morphKernelSize">形态学核大小（奇数，默认5）</param>
        /// <param name="targetHollows">目标空洞数量（默认16）</param>
        /// <param name="bandWidth">窄带采样宽度(像素)，0=原始轮廓采样，>0=窄带采样（默认8）</param>
        /// <param name="useContourMode">true=Canvas轮廓等弧长采样（默认true）</param>
        /// <param name="outerExpandPixels">Canvas模式外围膨胀半径(像素，默认25)</param>
        /// <param name="hollowGrayLow">空洞灰度下限（默认5）</param>
        /// <param name="hollowGrayHigh">空洞灰度上限（默认50）</param>
        public static TrajectoryResult DetectHollowTrajectory(CalibImage img,
            int blurKsize = 7, int morphKernelSize = 5,
            int targetHollows = 16, int bandWidth = 8,
            bool useContourMode = true, int outerExpandPixels = 25,
            int hollowGrayLow = 5, int hollowGrayHigh = 50)
        {
            TrajectoryResult result = new TrajectoryResult();

            // 步骤图标签
            result.StepLabels = new List<string> { "Grayscale", $"Binary({hollowGrayLow}-{hollowGrayHigh})", "Mask", "Hollow", "Hollow(Color)" };

            int stepImageCount = 5;
            int nativeImgSize = Marshal.SizeOf<NativeImage>();

            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * MaxTrajectoryPoints);
            IntPtr stepImgsPtr = Marshal.AllocHGlobal(nativeImgSize * stepImageCount);

            try
            {
                // 初始化 5 个 NativeImage 为零
                for (int i = 0; i < stepImageCount; i++)
                {
                    NativeImage zero = new NativeImage();
                    Marshal.StructureToPtr(zero, IntPtr.Add(stepImgsPtr, i * nativeImgSize), false);
                }

                int count = 0;
                int detected = NativeAPI.CALIB_DetectHollowTrajectory(img.NativePtr, ptsPtr, ref count,
                    stepImgsPtr, IntPtr.Zero, stepImageCount,
                    blurKsize, morphKernelSize, targetHollows, bandWidth,
                    useContourMode ? 1 : 0, outerExpandPixels, 0.0,
                    hollowGrayLow, hollowGrayHigh);

                // 解析步骤图
                for (int i = 0; i < stepImageCount; i++)
                {
                    IntPtr imgPtr = IntPtr.Add(stepImgsPtr, i * nativeImgSize);
                    NativeImage nativeImg = Marshal.PtrToStructure<NativeImage>(imgPtr);
                    if (nativeImg.data != IntPtr.Zero && nativeImg.width > 0 && nativeImg.height > 0)
                    {
                        CalibImage stepImg = new CalibImage();
                        // 将 native 数据拷贝到 CalibImage
                        int dataLen = nativeImg.width * nativeImg.height * nativeImg.channels;
                        byte[] srcBuf = new byte[dataLen];
                        Marshal.Copy(nativeImg.data, srcBuf, 0, dataLen);
                        NativeAPI.CALIB_CreateBlankImage(stepImg.NativePtr, nativeImg.width, nativeImg.height, nativeImg.channels);
                        var outNative = stepImg.GetNativeStruct();
                        Marshal.Copy(srcBuf, 0, outNative.data, dataLen);
                        stepImg.RefreshProperties();
                        result.StepImages.Add(stepImg);

                        // 释放 C++ 分配的 data
                        if (nativeImg.data != IntPtr.Zero)
                            NativeAPI.CALIB_FreeImageData(nativeImg.data);
                    }
                }

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
                Marshal.FreeHGlobal(stepImgsPtr);
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

        /// <summary>
        /// 独立轮廓采样：从二值图直接 findContours + 排序 + 等弧长采样
        /// </summary>
        /// <param name="binaryImage">二值图 CalibImage</param>
        /// <param name="targetBars">目标轮廓数</param>
        /// <param name="spacing">采样间距（像素）</param>
        /// <param name="minArea">最小轮廓面积</param>
        /// <returns>采样点数组</returns>
        public static Point2D[] SampleContours(CalibImage binaryImage, int targetBars = 16, double spacing = 3.0, int minArea = 500)
        {
            if (binaryImage == null) throw new ArgumentNullException(nameof(binaryImage));
            int w = binaryImage.Width, h = binaryImage.Height;
            if (w <= 0 || h <= 0) return Array.Empty<Point2D>();

            NativeImage native = binaryImage.GetNativeStruct();
            if (native.data == IntPtr.Zero) return Array.Empty<Point2D>();

            int maxPoints = w * h;
            int ptSize = Marshal.SizeOf<NativePoint2D>();
            IntPtr ptsPtr = Marshal.AllocHGlobal(ptSize * maxPoints);
            IntPtr barIdsPtr = Marshal.AllocHGlobal(sizeof(int) * maxPoints);
            try
            {
                int count = NativeAPI.CALIB_SampleContours(native.data, w, h, targetBars, spacing, minArea,
                                                             ptsPtr, barIdsPtr, maxPoints);
                if (count <= 0) return Array.Empty<Point2D>();

                Point2D[] result = new Point2D[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr elemPtr = IntPtr.Add(ptsPtr, i * ptSize);
                    NativePoint2D nativePt = Marshal.PtrToStructure<NativePoint2D>(elemPtr);
                    result[i] = Point2D.FromNative(nativePt);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
                Marshal.FreeHGlobal(barIdsPtr);
            }
        }

        /// <summary>
        /// 从轮廓点数据做等弧长采样（独立函数，不依赖 stepDetector 上下文）
        /// </summary>
        /// <param name="flatX">扁平化的 X 坐标数组</param>
        /// <param name="flatY">扁平化的 Y 坐标数组</param>
        /// <param name="contourLengths">每条轮廓的点数</param>
        /// <param name="width">图像宽度（边界检查用）</param>
        /// <param name="height">图像高度（边界检查用）</param>
        /// <param name="targetBars">取前N条轮廓</param>
        /// <param name="spacing">等弧长采样间距</param>
        /// <returns>采样点数组</returns>
        public static Point2D[] SampleContoursFromPoints(int[] flatX, int[] flatY,
            int[] contourLengths, int width, int height,
            int targetBars, double spacing)
        {
            if (flatX == null || flatY == null || contourLengths == null)
                throw new ArgumentNullException("轮廓数据不能为空");
            int numContours = contourLengths.Length;
            if (numContours == 0) return Array.Empty<Point2D>();

            int maxPoints = width * height;
            int ptSize = Marshal.SizeOf<NativePoint2D>();
            IntPtr ptsPtr = Marshal.AllocHGlobal(ptSize * maxPoints);
            IntPtr barIdsPtr = Marshal.AllocHGlobal(sizeof(int) * maxPoints);

            // 将 int[] 复制到非托管内存
            int totalInputPts = flatX.Length;
            IntPtr xPtr = Marshal.AllocHGlobal(sizeof(int) * totalInputPts);
            IntPtr yPtr = Marshal.AllocHGlobal(sizeof(int) * totalInputPts);
            IntPtr lenPtr = Marshal.AllocHGlobal(sizeof(int) * numContours);
            try
            {
                Marshal.Copy(flatX, 0, xPtr, totalInputPts);
                Marshal.Copy(flatY, 0, yPtr, totalInputPts);
                Marshal.Copy(contourLengths, 0, lenPtr, numContours);

                int count = NativeAPI.CALIB_SampleContoursFromPoints(xPtr, yPtr, lenPtr,
                    numContours, targetBars, width, height, spacing,
                    ptsPtr, barIdsPtr, maxPoints);
                if (count <= 0) return Array.Empty<Point2D>();

                Point2D[] result = new Point2D[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr elemPtr = IntPtr.Add(ptsPtr, i * ptSize);
                    NativePoint2D nativePt = Marshal.PtrToStructure<NativePoint2D>(elemPtr);
                    result[i] = Point2D.FromNative(nativePt);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
                Marshal.FreeHGlobal(barIdsPtr);
                Marshal.FreeHGlobal(xPtr);
                Marshal.FreeHGlobal(yPtr);
                Marshal.FreeHGlobal(lenPtr);
            }
        }
    }

    // ================================================================
    // General Image Operations
    // ================================================================

    public static class ImageOps
    {
        /// <summary>
        /// Apply mask to source image: output = src where mask != 0, else 0
        /// </summary>
        public static CalibImage ApplyMask(CalibImage src, CalibImage mask)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            var result = new CalibImage();
            int ret = NativeAPI.CALIB_ApplyMask(src.NativePtr, mask.NativePtr, result.NativePtr);
            if (ret == -2)
                throw new ArgumentException("Source and mask images must have the same dimensions");
            else if (ret != 0)
                throw new InvalidOperationException("ApplyMask failed");
            result.RefreshProperties();
            return result;
        }
    }

    // ================================================================
    // Trajectory Step-by-Step API
    // ================================================================

    /// <summary>
    /// 分步骤轨迹检测器
    /// </summary>
    public class TrajectoryStepDetector : IDisposable
    {
        private const int MaxPoints = 50000;
        
        private IntPtr _context;
        private bool _disposed;

        public int DarkBarCount { get; private set; }
        public int PointCount { get; private set; }

        public TrajectoryStepDetector()
        {
            _context = NativeAPI.CALIB_TrajStep_Create();
            if (_context == IntPtr.Zero)
                throw new OutOfMemoryException("Failed to create trajectory step context");
        }

        ~TrajectoryStepDetector()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_context != IntPtr.Zero)
                {
                    NativeAPI.CALIB_TrajStep_Free(_context);
                    _context = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Step 1: 图像转灰度
        /// </summary>
        public void ConvertToGrayscale(CalibImage src)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_1_ConvertToGrayscale(_context, src.NativePtr);
        }

        /// <summary>
        /// Step 1.5（可选）: 分水岭预分割，分离主要工件区域
        /// </summary>
        public void WatershedPresegment(bool enable = false)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_1_5_WatershedPresegment(_context, enable);
        }

        /// <summary>
        /// Step 1b（可选）: CLAHE 对比度受限自适应直方图均衡化
        /// </summary>
        /// <param name="clipLimit">对比度限制阈值（2.0~4.0），0 或负数表示不启用</param>
        /// <param name="tileGridSize">分块大小（像素），通常 8 或 16</param>
        public void ApplyCLAHE(double clipLimit = 0, int tileGridSize = 8)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_1b_CLAHE(_context, clipLimit, tileGridSize);
        }

        /// <summary>
        /// Step 2b: Canny 边缘检测
        /// </summary>
        /// <param name="lowThreshold">Canny 低阈值（0=自适应，median * 0.66）</param>
        /// <param name="highThreshold">Canny 高阈值（0=自适应，lowThreshold * 2）</param>
        /// <param name="blurKsize">高斯模糊核大小（0=不模糊，奇数）</param>
        public void CannyEdges(double lowThreshold = 0, double highThreshold = 0, int blurKsize = 0,
                               int bilateralD = 0, double bilateralSigmaColor = 75, double bilateralSigmaSpace = 75,
                               int nlmeansH = 0, int nlmeansTemplateSize = 7, int nlmeansSearchSize = 21)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_2b_CannyEdges(_context, lowThreshold, highThreshold, blurKsize,
                                                   bilateralD, bilateralSigmaColor, bilateralSigmaSpace,
                                                   nlmeansH, nlmeansTemplateSize, nlmeansSearchSize);
        }

        /// <summary>
        /// Step 2: 预处理 + 找外轮廓
        /// </summary>
        /// <param name="blurKsize">高斯模糊核大小（奇数）</param>
        /// <param name="morphKernelSize">形态学核大小（奇数）</param>
        /// <param name="enableWatershed">启用分水岭预分割</param>
        /// <param name="minArea">最小轮廓面积阈值，小于此值的轮廓被过滤（0=不过滤）</param>
        public void PreprocessAndFindContours(int blurKsize = 7, int morphKernelSize = 5, bool enableWatershed = false, double minArea = 0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_2_PreprocessAndFindContours(_context, blurKsize, morphKernelSize, enableWatershed, minArea);
        }

        /// <summary>
        /// Step 2c: 按灰度范围二值化
        /// grayLow ≤ gray ≤ grayHigh → 255（目标），其余 → 0
        /// 结果存入 binaryBright，可通过 GetStepImage(2) 获取
        /// </summary>
        public void GrayRangeBinary(int grayLow = 5, int grayHigh = 50)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_2c_GrayRangeBinary(_context, grayLow, grayHigh);
        }

        /// <summary>
        /// Step 2.5: 生成 Mask — 自动取外轮廓中面积最大的轮廓，填充内部区域
        /// 需要先执行 Step 2（PreprocessAndFindContours）
        /// </summary>
        /// <returns>0 成功，-1 失败（轮廓为空）</returns>
        /// <summary>
        /// Step 2.5: 从外轮廓生成Mask
        /// </summary>
        /// <param name="contourIdx">轮廓索引 (-1=自动选最大)</param>
        public int CreateMaskFromLargestContour(int contourIdx = -1)
        {
            CheckDisposed();
            return NativeAPI.CALIB_TrajStep_2_5_CreateMask(_context, contourIdx);
        }

        /// <summary>
        /// Step 3: 创建工件mask
        /// </summary>
        public void CreateWorkpieceMask(int maxContourIdx = 0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_3_CreateWorkpieceMask(_context, maxContourIdx);
        }

        /// <summary>
        /// Step 3b: 空洞检测 — 在 mask 内按灰度范围反求空洞
        /// 填充 darkBinary / sortedBars / darkContours，后续可直接 sample
        /// </summary>
        /// <param name="hollowGrayLow">空洞灰度下限（默认5）</param>
        /// <param name="hollowGrayHigh">空洞灰度上限（默认50）</param>
        /// <param name="targetHollows">目标空洞数量（默认16）</param>
        /// <param name="morphKernelSize">形态学核大小（奇数，默认5）</param>
        /// <returns>检测到的空洞数量</returns>
        public int DetectHollow(int hollowGrayLow = 5, int hollowGrayHigh = 50, int targetHollows = 16, int morphKernelSize = 5)
        {
            CheckDisposed();
            return NativeAPI.CALIB_TrajStep_3b_DetectHollow(_context, hollowGrayLow, hollowGrayHigh, targetHollows, morphKernelSize);
        }

        /// <summary>
        /// Step 4: 暗条二值化 + 外围跑道区域提取
        /// </summary>
        /// <param name="darkThreshold">暗条阈值（低于此值为暗条，默认50）</param>
        /// <param name="darkMinThreshold">暗条下限阈值（低于此值排除为纯黑背景噪声，0=不启用）</param>
        /// <param name="outerThreshold">外围跑道上界阈值（darkThreshold~outerThreshold为外围区域，0=自适应Otsu）</param>
        public void DetectDarkBars(int darkThreshold = 50, int darkMinThreshold = 0, int outerThreshold = 0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_4_DetectDarkBars(_context, darkThreshold, darkMinThreshold, outerThreshold);
        }

        /// <summary>
        /// Step 5: 形态学清理
        /// </summary>
        public void MorphologyCleanup(int kernelSize = 5, int blurKsize = 9, double blurSigma = 2.0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_5_MorphologyCleanup(_context, kernelSize, blurKsize, blurSigma);
        }


        /// <summary>
        /// Set darkBinary data from external source (e.g. FlowPage upstream operator output)
        /// </summary>
        public void SetDarkBinary(CalibImage img)
        {
            CheckDisposed();
            if (img == null)
                throw new ArgumentNullException(nameof(img));
            var native = img.GetNativeStruct();
            if (native.data == IntPtr.Zero)
                throw new ArgumentException("CalibImage has no native data", nameof(img));
            NativeAPI.CALIB_TrajStep_SetDarkBinary(_context, native.data, native.width, native.height);
        }

        /// <summary>
        /// 设置 grayMat 数据（从外部图像传入，替代内部灰度化结果）
        /// </summary>
        public void SetGrayMat(CalibImage img)
        {
            CheckDisposed();
            if (img == null)
                throw new ArgumentNullException(nameof(img));
            var native = img.GetNativeStruct();
            if (native.data == IntPtr.Zero)
                throw new ArgumentException("CalibImage has no native data", nameof(img));
            NativeAPI.CALIB_TrajStep_SetGrayMat(_context, native.data, native.width, native.height);
        }

        /// <summary>
        /// 设置 mask 数据（从外部图像传入）
        /// </summary>
        public void SetMask(CalibImage img)
        {
            CheckDisposed();
            if (img == null)
                throw new ArgumentNullException(nameof(img));
            var native = img.GetNativeStruct();
            if (native.data == IntPtr.Zero)
                throw new ArgumentException("CalibImage has no native data", nameof(img));
            NativeAPI.CALIB_TrajStep_SetMask(_context, native.data, native.width, native.height);
        }

        /// <summary>
        /// 获取轮廓可视化图（darkBinary + 轮廓边界线）
        /// </summary>
        public CalibImage GetContourVis()
        {
            CheckDisposed();
            CalibImage output = new CalibImage();
            int result = NativeAPI.CALIB_TrajStep_GetContourVis(_context, output.NativePtr);
            if (result != 0)
            {
                output.Dispose();
                return null;
            }
            var native = output.GetNativeStruct();
            output.Width = native.width;
            output.Height = native.height;
            output.Channels = native.channels;
            return output;
        }

        /// <summary>
        /// Step 5.5: 边界约束膨胀——将暗条沿 Canny 边缘膨胀到下一条跑道型边界
        /// 需要先执行 Step 2b (Canny) 生成 edgeMap。
        /// 膨胀结果会原地更新 darkBinary，膨胀增量为跑道型边界区域。
        /// </summary>
        /// <param name="expandPixels">膨胀半径（像素），决定暗条向外扩展多远</param>
        public void ExpandToEdgeBoundary(int expandPixels = 15)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_5_5_ExpandToEdgeBoundary(_context, expandPixels);
        }

        /// <summary>
        /// Step 6: 找暗条轮廓并排序
        /// </summary>
        public int FindAndSortDarkContours()
        {
            CheckDisposed();
            DarkBarCount = NativeAPI.CALIB_TrajStep_6_FindAndSortDarkContours(_context);
            return DarkBarCount;
        }

        /// <summary>
        /// 导出排序后的轮廓点数据（扁平化），用于传给独立采样算子
        /// </summary>
        /// <returns>元组 (flatX, flatY, contourLengths, contourCount)</returns>
        public (int[] flatX, int[] flatY, int[] contourLengths, int contourCount) ExportSortedContours()
        {
            CheckDisposed();
            int maxContours = DarkBarCount > 0 ? DarkBarCount : 16;
            int maxTotalPoints = 2448 * 2048;

            int[] flatX = new int[maxTotalPoints];
            int[] flatY = new int[maxTotalPoints];
            int[] contourLengths = new int[maxContours];

            IntPtr xPtr = Marshal.AllocHGlobal(sizeof(int) * maxTotalPoints);
            IntPtr yPtr = Marshal.AllocHGlobal(sizeof(int) * maxTotalPoints);
            IntPtr lenPtr = Marshal.AllocHGlobal(sizeof(int) * maxContours);
            try
            {
                int count = NativeAPI.CALIB_TrajStep_ExportSortedContours(_context,
                    xPtr, yPtr, lenPtr, maxContours, maxTotalPoints);
                if (count <= 0)
                    return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

                // 计算实际总点数
                int totalPts = 0;
                for (int i = 0; i < count; i++) totalPts += contourLengths[i];
                // 先从 lenPtr 读取 contourLengths
                Marshal.Copy(lenPtr, contourLengths, 0, count);
                totalPts = 0;
                for (int i = 0; i < count; i++) totalPts += contourLengths[i];

                Marshal.Copy(xPtr, flatX, 0, totalPts);
                Marshal.Copy(yPtr, flatY, 0, totalPts);
                return (flatX.Take(totalPts).ToArray(), flatY.Take(totalPts).ToArray(),
                        contourLengths.Take(count).ToArray(), count);
            }
            finally
            {
                Marshal.FreeHGlobal(xPtr);
                Marshal.FreeHGlobal(yPtr);
                Marshal.FreeHGlobal(lenPtr);
            }
        }

        /// <summary>
        /// Step 7: 等间距轮廓采样（纯 OpenCV 等弧长采样）
        /// </summary>
        /// <param name="targetBars">目标暗条数量（默认16）</param>
        /// <param name="spacing">采样间距（像素），沿每条暗条轮廓等弧长采样（默认3.0）</param>
        public void SampleContours(int targetBars = 16, double spacing = 3.0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_7_SampleContours(_context, targetBars, spacing);
        }

        /// <summary>
        /// Step 7.5: 形状拟合
        /// </summary>
        /// <param name="fitMode">0=stadium跑道型拟合(默认), 1=曲率去噪(删除局部曲率异常大的点)</param>
        /// <param name="approxEpsilon">保留参数，fitMode=1时未使用</param>
        public void FitShape(int fitMode = 0, double approxEpsilon = 0.0)
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_7_5_FitShape(_context, fitMode, approxEpsilon);
        }

        /// <summary>
        /// Step 8: Mask验证
        /// </summary>
        public void VerifyByMask()
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_8_VerifyByMask(_context);
        }

        /// <summary>
        /// Step 9: 去重 + 排序
        /// </summary>
        public void DeduplicateAndSort()
        {
            CheckDisposed();
            NativeAPI.CALIB_TrajStep_9_DeduplicateAndSort(_context);
        }

        /// <summary>
        /// Step 10: 转换为输出格式
        /// </summary>
        public TrajectoryResult ConvertToOutput()
        {
            CheckDisposed();
            TrajectoryResult result = new TrajectoryResult();
            
            IntPtr countPtr = Marshal.AllocHGlobal(sizeof(int));
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * MaxPoints);
            
            try
            {
                int detected = NativeAPI.CALIB_TrajStep_10_ConvertToOutput(_context, ptsPtr, countPtr, IntPtr.Zero);
                
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
                PointCount = detected;
            }
            finally
            {
                Marshal.FreeHGlobal(countPtr);
                Marshal.FreeHGlobal(ptsPtr);
            }
            
            return result;
        }

        /// <summary>
        /// Step 11: 绘制彩色轨迹
        /// </summary>
        public CalibImage DrawColoredTrajectory(int width, int height)
        {
            CheckDisposed();
            CalibImage output = new CalibImage(width, height, 3);
            NativeAPI.CALIB_TrajStep_11_DrawColoredTrajectory(_context, output.GetNativeStruct().data, width, height);
            return output;
        }

        /// <summary>
        /// 获取当前点数
        /// </summary>
        public int GetPointCount()
        {
            CheckDisposed();
            return NativeAPI.CALIB_TrajStep_GetPointCount(_context);
        }

        /// <summary>
        /// 获取中间结果图像
        /// </summary>
        /// <param name="step">步骤号: 1=灰度图, 2=二值化, 3=mask(彩色), 4=暗条二值化, 5=窄带可视化, 6=Canny边缘图, 7=边界膨胀增量区域</param>
        /// <returns>中间结果图像，失败返回null</returns>
        public CalibImage GetStepImage(int step)
        {
            CheckDisposed();
            CalibImage output = new CalibImage();
            int result = NativeAPI.CALIB_TrajStep_GetStepImage(_context, step, output.NativePtr);
            if (result != 0)
            {
                output.Dispose();
                return null;
            }
            var native = output.GetNativeStruct();
            output.Width = native.width;
            output.Height = native.height;
            output.Channels = native.channels;
            return output;
        }

        /// <summary>
        /// 获取当前点
        /// </summary>
        public Point2D[] GetPoints()
        {
            CheckDisposed();
            int count = NativeAPI.CALIB_TrajStep_GetPointCount(_context);
            if (count == 0) return new Point2D[0];
            
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * count);
            try
            {
                int actual = NativeAPI.CALIB_TrajStep_GetPoints(_context, ptsPtr, IntPtr.Zero, count);
                Point2D[] result = new Point2D[actual];
                for (int i = 0; i < actual; i++)
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
        /// 一键检测（执行所有步骤）
        /// </summary>
        /// <param name="doFit">是否执行形状拟合</param>
        /// <param name="doVerify">是否执行Mask验证</param>
        /// <param name="doDedup">是否执行去重排序</param>
        /// <param name="spacing">采样间距（像素），沿每条暗条轮廓等弧长采样（默认3.0）</param>
        /// <param name="outerThreshold">外围跑道上界阈值（0=自适应Otsu，保留兼容）</param>
        public TrajectoryResult Detect(CalibImage img, bool doFit = true, bool doVerify = true, bool doDedup = true, double spacing = 3.0, int outerThreshold = 0, double claheClipLimit = 0, int claheTileGridSize = 8)
        {
            ConvertToGrayscale(img);
            if (claheClipLimit > 0) ApplyCLAHE(claheClipLimit, claheTileGridSize);
            PreprocessAndFindContours();
            CreateWorkpieceMask();
            DetectDarkBars(50, 0, outerThreshold);
            MorphologyCleanup(5, 9, 2.0);
            FindAndSortDarkContours();
            SampleContours(16, spacing);
            if (doFit) FitShape();
            if (doVerify) VerifyByMask();
            if (doDedup) DeduplicateAndSort();
            return ConvertToOutput();
        }

        private void CheckDisposed()
        {
            if (_disposed || _context == IntPtr.Zero)
                throw new ObjectDisposedException("TrajectoryStepDetector");
        }
    }
}
