// Split from CalibOperatorPInvoke.cs — CalibAPI high-level wrappers.

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

namespace CalibOperatorPInvoke
{
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
            // Native CreateCalibrationImage 返回 1 表示成功（与部分接口 0=成功 不同）
            if (result <= 0)
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

        /// <summary>
        /// 复制图像缓冲区（行紧凑排列 width×channels）
        /// </summary>
        public static CalibImage DuplicateImage(CalibImage src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            NativeImage sn = src.GetNativeStruct();
            if (sn.data == IntPtr.Zero || sn.width <= 0 || sn.height <= 0)
                throw new InvalidOperationException("Invalid image");
            var dst = new CalibImage(sn.width, sn.height, sn.channels);
            NativeImage dn = dst.GetNativeStruct();
            int rowBytes = sn.width * sn.channels;
            int bytes = rowBytes * sn.height;
            byte[] tmp = new byte[bytes];
            Marshal.Copy(sn.data, tmp, 0, bytes);
            Marshal.Copy(tmp, 0, dn.data, bytes);
            return dst;
        }

        // ================================================================
        // Circle Detection
        // ================================================================

        public const int HoughDetectMaxCircles = 512;
        public const int HoughJsonCap = 262144;

        /// <summary>霍夫圆（绿圈/青圆心）；circlesJson 为 [[cx,cy,r],...]，可与霍夫跑道形 CirclesJson 对接。</summary>
        public static CalibImage HoughCirclesOverlay(CalibImage src, out Point2D[] circleCenters, out string circlesJson,
            int blurKsize, double hcDp, double hcMinDist, double hcParam1, double hcParam2, int hcMinR, int hcMaxR)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            var dst = new CalibImage(src.Width, src.Height, 3);
            int count = 0;
            circlesJson = "[]";
            IntPtr ptsPin = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * HoughDetectMaxCircles);
            IntPtr circlesJsonPtr = Marshal.AllocHGlobal(HoughJsonCap);
            try
            {
                int rc = NativeAPI.CALIB_HoughCirclesDetect(
                    src.NativePtr, dst.NativePtr, ptsPin, ref count, HoughDetectMaxCircles,
                    circlesJsonPtr, HoughJsonCap,
                    blurKsize, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR);
                if (rc != 0)
                    throw new InvalidOperationException($"CALIB_HoughCirclesDetect failed ({rc})");
                circleCenters = new Point2D[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr ep = IntPtr.Add(ptsPin, i * Marshal.SizeOf<NativePoint2D>());
                    NativePoint2D np = Marshal.PtrToStructure<NativePoint2D>(ep);
                    circleCenters[i] = Point2D.FromNative(np);
                }
                circlesJson = Marshal.PtrToStringUTF8(circlesJsonPtr) ?? "[]";
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPin);
                Marshal.FreeHGlobal(circlesJsonPtr);
            }
            return dst;
        }

        /// <summary>概率霍夫线段（红线）。输入边缘图转灰度；coverageMatchHalfWidthPx&gt;0 时先边缘二值再以该半径圆形膨胀，膨胀图作为 HoughLinesP 输入，否则直接用灰度边缘。检出后按线段长度降序、沿线覆盖率（同霍夫输入图）降序排序，再截取最多线段数。</summary>
        public static CalibImage HoughLinesOverlay(CalibImage src, out string linesJson, out int lineCount,
            double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap, int maxLinesOut,
            int coverageMatchHalfWidthPx = 0)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            var dst = new CalibImage(src.Width, src.Height, 3);
            int nseg = 0;
            IntPtr jsonPtr = Marshal.AllocHGlobal(HoughJsonCap);
            try
            {
                int rc = NativeAPI.CALIB_HoughLinesDetect(
                    src.NativePtr, dst.NativePtr, jsonPtr, HoughJsonCap, ref nseg,
                    hlRho, hlThetaDeg, hlThreshold, hlMinLen, hlMaxGap, maxLinesOut, coverageMatchHalfWidthPx);
                if (rc != 0)
                    throw new InvalidOperationException($"CALIB_HoughLinesDetect failed ({rc})");
                linesJson = Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
                lineCount = nseg;
            }
            finally
            {
                Marshal.FreeHGlobal(jsonPtr);
            }
            return dst;
        }

        /// <summary>跑道形：parallel 为 ρ 分桶线段 JSON；stadium 为两条合并直道 + 两端半圆弧 JSON。可选传入霍夫线段/霍夫圆的 JSON 以跳过内部检测。</summary>
        public static CalibImage HoughRunwayOverlay(CalibImage src, out string runwayLinesJson, out int runwayLineCount,
            int blurKsize, double cannyTh1, double cannyTh2,
            double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap, int maxLinesOut,
            double runwayAngleTolDeg, int runwayRhoBinPx, int runwayStripCount, int maxRunwayLinesOut,
            int runwayShapeMode = 0,
            double hcDp = 1.2, double hcMinDist = 40.0, double hcParam1 = 100.0, double hcParam2 = 30.0,
            int hcMinR = 5, int hcMaxR = 200,
            string? linesJsonUtf8 = null, string? circlesJsonUtf8 = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            var dst = new CalibImage(src.Width, src.Height, 3);
            int rwc = 0;
            IntPtr runwayJsonPtr = Marshal.AllocHGlobal(HoughJsonCap);
            try
            {
                int rc = NativeAPI.CALIB_HoughRunwayDetect(
                    src.NativePtr, dst.NativePtr, runwayJsonPtr, HoughJsonCap, ref rwc,
                    blurKsize, cannyTh1, cannyTh2, hlRho, hlThetaDeg, hlThreshold, hlMinLen, hlMaxGap, maxLinesOut,
                    runwayAngleTolDeg, runwayRhoBinPx, runwayStripCount, maxRunwayLinesOut,
                    runwayShapeMode, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR,
                    linesJsonUtf8, circlesJsonUtf8);
                if (rc != 0)
                    throw new InvalidOperationException($"CALIB_HoughRunwayDetect failed ({rc})");
                runwayLinesJson = Marshal.PtrToStringUTF8(runwayJsonPtr) ?? "[]";
                runwayLineCount = rwc;
            }
            finally
            {
                Marshal.FreeHGlobal(runwayJsonPtr);
            }
            return dst;
        }

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

        /// <summary>cornerPreprocessMode: 0=auto, 1=none, 2=clahe</summary>
        public static int ParseChessboardCornerPreprocessMode(string? mode)
        {
            string m = (mode ?? "auto").Trim().ToLowerInvariant();
            return m switch
            {
                "none" or "off" or "false" or "0" => 1,
                "clahe" or "2" => 2,
                _ => 0,
            };
        }

        /// <summary>
        /// 棋盘格内侧角点检测（完整棋盘时返回 cols×rows 个点）
        /// </summary>
        public static Point2D[] FindChessboardCorners(CalibImage img, int boardCols, int boardRows, bool refineSubPix = true, bool fastCheck = true,
            string cornerPreprocess = "auto", double claheClipLimit = 2.5, int claheTileSize = 8)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            int maxPts = boardCols * boardRows;
            if (boardCols < 2 || boardRows < 2 || maxPts <= 0)
                return Array.Empty<Point2D>();

            int count = 0;
            int preprocessMode = ParseChessboardCornerPreprocessMode(cornerPreprocess);
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * maxPts);
            try
            {
                NativeAPI.CALIB_FindChessboardCorners(img.NativePtr, boardCols, boardRows, ptsPtr, ref count, maxPts,
                    refineSubPix ? 1 : 0, fastCheck ? 1 : 0, preprocessMode, claheClipLimit, claheTileSize);
                if (count <= 0)
                    return Array.Empty<Point2D>();

                Point2D[] result = new Point2D[count];
                for (int i = 0; i < count; i++)
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
        /// 在图像上绘制棋盘格角点（输出 BGR）
        /// </summary>
        public static void DrawChessboardCorners(CalibImage img, Point2D[] pts, int boardCols, int boardRows)
        {
            if (img == null || pts == null || pts.Length == 0) return;

            int count = pts.Length;
            IntPtr ptsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint2D>() * count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr elementPtr = IntPtr.Add(ptsPtr, i * Marshal.SizeOf<NativePoint2D>());
                    Marshal.StructureToPtr(pts[i].ToNative(), elementPtr, false);
                }
                NativeAPI.CALIB_DrawChessboardCorners(img.NativePtr, ptsPtr, count, boardCols, boardRows);
            }
            finally
            {
                Marshal.FreeHGlobal(ptsPtr);
            }
        }

        /// <summary>
        /// 规范为透视/去畸变可用的完整棋盘标定 JSON（须含 intrinsics + extrinsicsPerView）。
        /// 支持：标定算子输出的整包、save 包装格式内的 calibrationJson 字段、磁盘路径。
        /// </summary>
        public static string NormalizeChessboardCalibrationJson(string jsonOrFilePath)
        {
            if (string.IsNullOrWhiteSpace(jsonOrFilePath))
                throw new ArgumentException("标定文件不能为空", nameof(jsonOrFilePath));

            string json = jsonOrFilePath.Trim();
            if (File.Exists(json))
                json = File.ReadAllText(json).Trim();

            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json[1..].Trim();

            if (json.Contains("\"extrinsicsPerView\"", StringComparison.Ordinal))
                return json;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("calibrationJson", out var calEl) && calEl.ValueKind == JsonValueKind.String)
                {
                    string inner = calEl.GetString() ?? "";
                    if (inner.Contains("\"extrinsicsPerView\"", StringComparison.Ordinal))
                        return inner;
                }
                if (root.TryGetProperty("CalibrationJson", out var calEl2) && calEl2.ValueKind == JsonValueKind.String)
                {
                    string inner = calEl2.GetString() ?? "";
                    if (inner.Contains("\"extrinsicsPerView\"", StringComparison.Ordinal))
                        return inner;
                }
            }
            catch (JsonException)
            {
                // fall through
            }

            if (json.Contains("\"fx\"", StringComparison.Ordinal) || json.Contains("\"Fx\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "当前 JSON 只有相机内参，缺少 extrinsicsPerView，不能用于透视展开。\n" +
                    "请使用「棋盘格内参标定」的 **CalibrationJson** 输出（整包），不要接 **IntrinsicsJson**；\n" +
                    "或指定含 extrinsicsPerView 的完整文件，例如 test_images/chessboard_calibration_from_dir.json。");
            }

            throw new InvalidOperationException(
                "无法识别的标定文件：透视展开需要含 intrinsics 与 extrinsicsPerView 的完整标定结果。");
        }

        /// <summary>内参去畸变用 JSON：完整包或仅内参短 JSON 均可。</summary>
        public static string NormalizeIntrinsicsCalibrationJson(string jsonOrFilePath)
        {
            string json = NormalizeChessboardCalibrationJsonSafe(jsonOrFilePath);
            if (json.Contains("\"intrinsics\"", StringComparison.Ordinal))
                return json;
            if (json.Contains("\"fx\"", StringComparison.Ordinal) || json.Contains("\"Fx\"", StringComparison.Ordinal))
                return "{\"intrinsics\":" + json + "}";
            throw new InvalidOperationException("无法从内参 JSON 解析 fx/fy/cx/cy。");
        }

        private static string NormalizeChessboardCalibrationJsonSafe(string jsonOrFilePath)
        {
            if (string.IsNullOrWhiteSpace(jsonOrFilePath))
                return "";
            string json = jsonOrFilePath.Trim();
            if (File.Exists(json))
                json = File.ReadAllText(json).Trim();
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json[1..].Trim();
            return json;
        }

        /// <summary>确保 native Image.channels 为 1 或 3（部分路径 C# Channels 已更新但 native 未同步）。</summary>
        public static void EnsureNativeImageChannels(CalibImage img)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            var n = img.GetNativeStruct();
            if (n.data == IntPtr.Zero || n.width <= 0 || n.height <= 0)
                throw new InvalidOperationException($"图像无效: {n.width}x{n.height} channels={n.channels}");

            if (n.channels == 1 || n.channels == 3)
            {
                img.Channels = n.channels;
                return;
            }

            int fix = img.Channels;
            if (fix != 1 && fix != 3)
                fix = 1;
            n.channels = fix;
            Marshal.StructureToPtr(n, img.NativePtr, false);
            img.Channels = fix;
        }

        /// <summary>
        /// 多视图棋盘格标定：OpenCV calibrateCamera 优化求解内参、畸变，以及每张成功视图的外参 rvec/tvec（board→camera）。
        /// 返回的 calibrationJson 含 intrinsics、extrinsicsPerView、convention 字段。
        /// </summary>
        public static (CameraIntrinsics intrinsics, string calibrationJson) CalibrateCameraChessboard(
            string pathsDelimited, int boardCols, int boardRows, double squareSizeMm,
            string cornerPreprocess = "auto", double claheClipLimit = 2.5, int claheTileSize = 8)
        {
            NativeCameraIntrinsics n = new NativeCameraIntrinsics();
            byte[] buf = new byte[524288];
            GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int preprocessMode = ParseChessboardCornerPreprocessMode(cornerPreprocess);
                int rc = NativeAPI.CALIB_CalibrateCameraChessboard(pathsDelimited ?? string.Empty, boardCols, boardRows, squareSizeMm, ref n,
                    handle.AddrOfPinnedObject(), buf.Length, preprocessMode, claheClipLimit, claheTileSize);
                if (rc != 0 || n.success == 0)
                    throw new InvalidOperationException($"棋盘格标定求解失败 (code {rc})：需至少 3 张成功检出棋盘格的图像以计算内参与外参。");
                string calJson = Utf8NullTerminated(buf);
                return (CameraIntrinsics.FromNative(n), calJson);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// 像素轨迹 → 棋盘平面 XY（与标定 squareSize 同单位）。使用 CalibrationJson 内参 + extrinsicsPerView[viewIndex]。
        /// </summary>
        public static Point2D[] PixelsToChessboardWorld(string calibrationJson, int viewIndex, Point2D[] pixels)
        {
            if (pixels == null || pixels.Length == 0)
                throw new ArgumentException("pixels 不能为空", nameof(pixels));
            if (string.IsNullOrWhiteSpace(calibrationJson))
                throw new ArgumentException("calibrationJson 不能为空", nameof(calibrationJson));
            if (viewIndex < -1)
                throw new ArgumentOutOfRangeException(nameof(viewIndex), "viewIndex 须 >= -1（-1/axis=光轴对称）");

            var nativeIn = new NativePoint2D[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                nativeIn[i] = pixels[i].ToNative();
            var nativeOut = new NativePoint2D[pixels.Length];
            int rc = NativeAPI.CALIB_PixelsToChessboardPlaneFromCalibrationJson(calibrationJson, viewIndex, nativeIn, nativeOut, pixels.Length);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"棋盘像素→世界坐标失败 (code {rc})。请检查 CalibrationJson 是否完整、viewIndex 是否在 extrinsicsPerView 范围内，以及射线是否与棋盘平面近似平行。");

            var result = new Point2D[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                result[i] = Point2D.FromNative(nativeOut[i]);
            return result;
        }

        /// <summary>
        /// 使用针孔内参与畸变系数对图像做 lens undistort（OpenCV cv::undistort）。
        /// alpha &lt; 0：保持原 K 与尺寸；0..1：getOptimalNewCameraMatrix 裁剪黑边。
        /// </summary>
        public static CalibImage UndistortImage(CalibImage src, CameraIntrinsics intrinsics, double alpha = -1.0)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            int ch = src.Channels > 0 ? src.Channels : 3;
            var dst = new CalibImage(src.Width, src.Height, ch);
            int rc = NativeAPI.CALIB_UndistortImageWithIntrinsics(
                src.NativePtr, dst.NativePtr,
                intrinsics.Fx, intrinsics.Fy, intrinsics.Cx, intrinsics.Cy,
                intrinsics.K1, intrinsics.K2, intrinsics.P1, intrinsics.P2, intrinsics.K3,
                alpha);
            if (rc != 0)
                throw new InvalidOperationException($"内参畸变矫正失败 (code {rc})");
            return dst;
        }

        /// <summary>从 CalibrationJson 读取 intrinsics 后 undistort。</summary>
        public static CalibImage UndistortImage(CalibImage src, string calibrationJson, double alpha = -1.0)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (string.IsNullOrWhiteSpace(calibrationJson))
                throw new ArgumentException("calibrationJson 不能为空", nameof(calibrationJson));
            int ch = src.Channels > 0 ? src.Channels : 3;
            var dst = new CalibImage(src.Width, src.Height, ch);
            string calNorm = NormalizeIntrinsicsCalibrationJson(calibrationJson);
            EnsureNativeImageChannels(src);
            int rc = NativeAPI.CALIB_UndistortImageFromCalibrationJson(src.NativePtr, dst.NativePtr, calNorm, alpha);
            if (rc != 0)
                throw new InvalidOperationException($"内参畸变矫正失败 (code {rc})，请检查 CalibrationJson 是否含 intrinsics。");
            SyncCalibImageFromNative(dst);
            return dst;
        }

        /// <summary>解析 viewIndex：0..n-1 或 axis/-1=光轴对称。</summary>
        public static int ParseChessboardViewIndex(string? raw, int defaultIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultIndex;
            var t = raw.Trim();
            if (string.Equals(t, "axis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "optical_axis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "optical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "光轴", StringComparison.OrdinalIgnoreCase)
                || t == "-1")
                return -1;
            return int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int vi)
                ? vi
                : defaultIndex;
        }

        /// <summary>透视展开到棋盘平面（鸟瞰）。viewIndex=-1 或 axis：光轴对称外参（多视图平均 rvec，棋盘中心对齐主点）。</summary>
        public static CalibImage WarpToChessboardPlane(CalibImage src, string calibrationJson, int viewIndex,
            int boardCols, int boardRows, double squareSizeMm, double pxPerMm = 32.0, int perspectiveOutputMode = 0,
            bool assumeUndistortedInput = false, int outputSizeMode = 0)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (viewIndex < -1)
                throw new ArgumentOutOfRangeException(nameof(viewIndex), "viewIndex 须 >= -1（-1/axis=光轴对称）");
            string calNorm = NormalizeChessboardCalibrationJson(calibrationJson);
            if (boardCols < 2 || boardRows < 2)
                throw new ArgumentOutOfRangeException(nameof(boardCols), "棋盘内侧角点列/行数须 >= 2");
            if (squareSizeMm <= 0 || pxPerMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(squareSizeMm));

            EnsureNativeImageChannels(src);
            var sn = src.GetNativeStruct();
            int ch = sn.channels is 1 or 3 ? sn.channels : 1;
            int outW = (int)Math.Round((boardCols - 1) * squareSizeMm * pxPerMm);
            int outH = (int)Math.Round((boardRows - 1) * squareSizeMm * pxPerMm);
            if (outW < 8 || outH < 8)
                throw new InvalidOperationException("透视矫正输出尺寸过小，请检查 squareSizeMm 与 pxPerMm。");

            var dst = new CalibImage(outW, outH, ch);
            int rc = NativeAPI.CALIB_WarpImageToChessboardPlaneFromCalibrationJson(
                src.NativePtr, dst.NativePtr, calNorm, viewIndex, boardCols, boardRows, squareSizeMm, pxPerMm,
                perspectiveOutputMode, assumeUndistortedInput ? 1 : 0, outputSizeMode);
            if (rc != 0)
            {
                dst.Dispose();
                throw new InvalidOperationException(
                    $"透视矫正失败 (code {rc}): {DescribePerspectiveWarpError(rc)}");
            }

            SyncCalibImageFromNative(dst);
            return dst;
        }

        private static void SyncCalibImageFromNative(CalibImage img)
        {
            var n = img.GetNativeStruct();
            img.Width = n.width;
            img.Height = n.height;
            img.Channels = n.channels;
        }

        private static string DescribePerspectiveWarpError(int code) => code switch
        {
            -1 => "参数无效",
            -2 => "棋盘 cols/rows/squareSizeMm/pxPerMm 无效",
            -3 => "native 无法读取图像像素（channels 须为 1 或 3；请重新编译 CalibOperator.dll）",
            -4 => "projectPoints 失败",
            -5 => "输出尺寸过小",
            -6 => "warpPerspective 失败",
            -7 => "输出缓冲分配失败",
            -8 => "整图(共面)透视画布过大，请检查 viewIndex/外参或改用 board/local",
            -9 => "perspectiveOutputMode 无效（0=board 1=local 2=plane）",
            -10 => "光轴对称：extrinsicsPerView 为空",
            -11 => "光轴对称：CalibrationJson 缺少 boardSpec（cols/rows/squareSizeMm）",
            _ => $"JSON 解析或 viewIndex 越界 (native code {code})，请确认接的是 CalibrationJson 整包且 viewIndex 有效"
        };

        private static string Utf8NullTerminated(byte[] buf)
        {
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return Encoding.UTF8.GetString(buf, 0, len);
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
        /// <returns>采样点与对应轮廓 ID</returns>
        public static (Point2D[] Points, int[] BarIds) SampleContoursFromPointsWithBarIds(int[] flatX, int[] flatY,
            int[] contourLengths, int width, int height,
            int targetBars, double spacing)
        {
            if (flatX == null || flatY == null || contourLengths == null)
                throw new ArgumentNullException("轮廓数据不能为空");
            int numContours = contourLengths.Length;
            if (numContours == 0) return (Array.Empty<Point2D>(), Array.Empty<int>());

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
                if (count <= 0) return (Array.Empty<Point2D>(), Array.Empty<int>());

                Point2D[] points = new Point2D[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr elemPtr = IntPtr.Add(ptsPtr, i * ptSize);
                    NativePoint2D nativePt = Marshal.PtrToStructure<NativePoint2D>(elemPtr);
                    points[i] = Point2D.FromNative(nativePt);
                }
                int[] barIds = new int[count];
                Marshal.Copy(barIdsPtr, barIds, 0, count);
                return (points, barIds);
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

        /// <summary>
        /// 从轮廓点数据做等弧长采样（仅返回点，兼容旧调用）
        /// </summary>
        public static Point2D[] SampleContoursFromPoints(int[] flatX, int[] flatY,
            int[] contourLengths, int width, int height,
            int targetBars, double spacing)
        {
            var (points, _) = SampleContoursFromPointsWithBarIds(flatX, flatY, contourLengths, width, height, targetBars, spacing);
            return points;
        }
    }
}
