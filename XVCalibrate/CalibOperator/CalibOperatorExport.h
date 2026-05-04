/**
 * CalibOperatorExport.h - Native C API for CalibOperator
 * 
 * This header provides plain C exports for the CalibOperator library,
 * enabling easy P/Invoke calls from C# or other .NET languages.
 * 
 * Build: Compile this as a native DLL (not /clr)
 */

#pragma once

#include "CalibOperator.h"

#ifdef CALIBOPERATOR_EXPORTS
#define CALIB_API __declspec(dllexport)
#else
#define CALIB_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ================================================================
// Constants
// ================================================================

#define CALIB_IMAGE_WIDTH    2448
#define CALIB_IMAGE_HEIGHT   2048
#define CALIB_MAX_TRAJ_POINTS 50000
#define CALIB_MAX_BARS 16

// ================================================================
// Image Management
// ================================================================

/**
 * Create a new image structure
 * Returns: Pointer to Image, or NULL on failure
 */
CALIB_API Image* CALIB_CreateImage();

/**
 * Free an image structure (including its data buffer)
 */
CALIB_API void CALIB_FreeImage(Image* img);

/**
 * Free raw image data buffer allocated by C++ (malloc)
 * Used to free step image data returned by DetectHollowTrajectory etc.
 */
CALIB_API void CALIB_FreeImageData(unsigned char* data);

/**
 * Create calibration pattern image
 * Returns: 0 on success, non-zero on failure
 */
CALIB_API int CALIB_CreateCalibrationImage(Image* img, int width, int height);

/**
 * Create a blank image with specified dimensions and channels
 * Returns: 0 on success, non-zero on failure
 */
CALIB_API int CALIB_CreateBlankImage(Image* img, int width, int height, int channels);

/**
 * Save image to BMP file
 * Returns: 0 on success, non-zero on failure
 */
CALIB_API int CALIB_SaveBMP(const char* filename, Image* img);

/**
 * Load BMP file into image
 * Returns: 0 on success, non-zero on failure
 */
CALIB_API int CALIB_LoadBMP(const char* filename, Image* img);

/**
 * Load image via OpenCV (bmp/png/jpeg/tiff...). Image stored as 3-channel BGR.
 * Returns: 1 on success, 0 on failure
 */
CALIB_API int CALIB_LoadImageFile(const char* filename, Image* img);

// ================================================================
// Circle Detection
// ================================================================

/**
 * Detect circles in calibration image
 * pts: Array to store detected points (should be at least 9 elements)
 * count: Pointer to store the number of detected points
 * maxCount: Maximum number of points to store
 * Returns: Number of detected circles
 */
CALIB_API int CALIB_DetectCircles(Image* img, Point2D* pts, int* count, int maxCount);

/**
 * Draw detected circles on image
 */
CALIB_API void CALIB_DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray);

/**
 * Chessboard inner corners (OpenCV). Returns 0 if full pattern found, 1 if not found.
 */
CALIB_API int CALIB_FindChessboardCorners(Image* img, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck);

CALIB_API void CALIB_DrawChessboardCorners(Image* img, Point2D* pts, int count, int boardCols, int boardRows);

/** Populated by CALIB_CalibrateCameraChessboard: intrinsics from optimization (cv::calibrateCamera), not read from device. */
typedef struct {
    double fx, fy, cx, cy;
    double k1, k2, p1, p2, k3;
    double rms;
    int success;
} CALIB_CameraIntrinsics;

/**
 * Estimate camera intrinsics + distortion + per-view extrinsics (rvec,tvec) by cv::calibrateCamera.
 * fullCalibrationJsonOut: optional UTF-8 JSON (intrinsics + extrinsicsPerView); NULL to skip.
 * If JSON buffer too small, output is empty string; intrinsics still filled.
 */
CALIB_API int CALIB_CalibrateCameraChessboard(const char* pathsDelimited, int boardCols, int boardRows,
    double squareSizeMm, CALIB_CameraIntrinsics* outIntrinsics,
    char* fullCalibrationJsonOut, int fullCalibrationJsonOutSize);

/**
 * 像素轨迹 → 棋盘坐标系 XY（平面 Z=0）。使用 CalibrationJson 中 intrinsics 与 extrinsicsPerView[viewIndex]。
 * 返回值：0 成功；负数表示参数/解析/几何失败（例如视图序号越界、射线与棋盘平面近似平行）。
 */
CALIB_API int CALIB_PixelsToChessboardPlaneFromCalibrationJson(
    const char* calibrationJsonUtf8,
    int viewIndex,
    const Point2D* pixels,
    Point2D* worldXYOut,
    int count);

// ================================================================
// Calibration
// ================================================================

/**
 * 9-point affine calibration
 * Returns: 0 on success, non-zero on failure
 */
CALIB_API int CALIB_CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans);

/**
 * Convert pixel to world coordinates
 */
CALIB_API Point2D CALIB_ImageToWorld(Point2D pixel, AffineTransform trans);

/**
 * Set the global transform matrix for trajectory detection
 */
CALIB_API void CALIB_SetTransform(AffineTransform trans);

/**
 * Calculate reprojection error
 */
CALIB_API void CALIB_CalculateError(Point2D* imagePts, Point2D* worldPts, int n, 
                                     AffineTransform trans, double* avgErr, double* maxErr);

// ================================================================
// Trajectory Detection
// ================================================================

/**
 * Detect trajectory using OpenCV algorithm
 * Returns: Number of trajectory points detected
 */
CALIB_API int CALIB_DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                                           Image* stepImages, int* stepBarIds, int stepImageCount,
                                           int useContourMode, int fitMode, double approxEpsilon);

/**
 * Detect trajectory using legacy algorithm
 */
CALIB_API int CALIB_DetectTrajectory(Image* img, Point2D* trajPixels, int* count);

/**
 * Detect trajectory using hollow detection algorithm
 * Different from dark bar detection: finds hollows (holes) inside the workpiece contour,
 * then merges edge pixels with intermediate gray values into the hollows.
 * stepImageCount: fixed 5 (grayscale/binary/mask/hollow binary/band visualization)
 * Returns: Number of trajectory points detected
 */
CALIB_API int CALIB_DetectHollowTrajectory(Image* img, Point2D* trajPixels, int* count,
                                            Image* stepImages, int* stepBarIds, int stepImageCount,
                                            int blurKsize, int morphKernelSize,
                                            int targetHollows, int bandWidth,
                                            int useContourMode, int outerExpandPixels,
                                            double grayMergeRatio,
                                            int hollowGrayLow, int hollowGrayHigh);

/**
 * Draw colored trajectory on image
 */
CALIB_API void CALIB_DrawTrajectoryColored(Image* img, Point2D* trajPixels, int count, int* barIds);

/**
 * Draw grayscale trajectory on single-channel image
 */
CALIB_API void CALIB_DrawTrajectoryGrayscale(Image* img, Point2D* trajPixels, int count, int grayValue);

// ================================================================
// Trajectory Step-by-Step Detection (C API)
// ================================================================

/**
 * Trajectory step context handle
 */
typedef void* TrajStepContext;

/**
 * Create trajectory step context
 * Returns: Context handle, or NULL on failure
 */
CALIB_API TrajStepContext CALIB_TrajStep_Create();

/**
 * Free trajectory step context
 */
CALIB_API void CALIB_TrajStep_Free(TrajStepContext ctx);

/**
 * Step 1: Convert image to grayscale
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_1_ConvertToGrayscale(TrajStepContext ctx, Image* src);

/**
 * Step 1.5 (optional): Watershed pre-segmentation
 * Separates main workpiece region from complex background before Step 2.
 * When enable is false, grayMat remains unchanged.
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_1_5_WatershedPresegment(TrajStepContext ctx, bool enable);

/**
 * Step 1b（可选）: CLAHE 对比度受限自适应直方图均衡化
 * clipLimit <= 0 时不启用
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_1b_CLAHE(TrajStepContext ctx, double clipLimit, int tileGridSize);

/**
 * Step 2b (optional): Canny edge detection
 * Produces an edge map that can be used as additional input for subsequent steps.
 * lowThreshold: Canny low threshold (0 = adaptive, median * 0.66)
 * highThreshold: Canny high threshold (0 = adaptive, lowThreshold * 2)
 * blurKsize: Gaussian blur kernel size before edge detection (0 = no blur)
 * bilateralD: Bilateral filter diameter (0 = disabled)
 * bilateralSigmaColor/space: Bilateral filter sigma params
 * nlmeansH: Non-local means denoising strength (0 = disabled)
 * nlmeansTemplateSize: Template window size for NLMeans (must be odd)
 * nlmeansSearchSize: Search window size for NLMeans (must be odd)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_2b_CannyEdges(TrajStepContext ctx, double lowThreshold, double highThreshold,
                                            int blurKsize, int bilateralD, double bilateralSigmaColor, double bilateralSigmaSpace,
                                            int nlmeansH, int nlmeansTemplateSize, int nlmeansSearchSize);

/**
 * Step 2: Preprocess and find outer contours
 * blurKsize: Gaussian blur kernel size (default: 7)
 * morphKernelSize: Morphology kernel size (default: 5)
 * enableWatershed: Enable watershed pre-segmentation before Step 2 (default: false)
 * minArea: Minimum contour area threshold, contours below are filtered (0=no filter)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_2_PreprocessAndFindContours(TrajStepContext ctx, int blurKsize, int morphKernelSize, bool enableWatershed, double minArea);

/**
 * Step 2.5: Create mask from specified contour
 * contourIdx: Index of contour to fill as mask (-1 = auto-select largest).
 *   Only the selected contour interior is filled (255), background and other contours remain 0.
 * Fills context: mask (CV_8UC1)
 * Returns: 0 on success, -1 on error or empty contours
 */
CALIB_API int CALIB_TrajStep_2_5_CreateMask(TrajStepContext ctx, int contourIdx);

/**
 * Step 3: Create workpiece mask from contours
 * maxContourIdx: Index of the largest contour
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_3_CreateWorkpieceMask(TrajStepContext ctx, int maxContourIdx);

/**
 * Step 3b: Detect hollow contours inside workpiece mask
 * Uses gray range [hollowGrayLow, hollowGrayHigh] to binarize inside mask,
 * then morphology cleanup + contour finding + area-based sorting.
 * Fills context: darkBinary, sortedBars, darkContours, darkBarCount
 * Subsequent steps (6-sort, 7-sample, etc.) can operate on these directly.
 * Returns: Number of hollows found, -1 on error
 */
CALIB_API int CALIB_TrajStep_3b_DetectHollow(TrajStepContext ctx, int hollowGrayLow, int hollowGrayHigh, int targetHollows, int morphKernelSize);

/**
 * Step 4: Detect dark bars and outer stadium region
 * darkThreshold: Dark bar upper threshold (default: 50, gray >= this is NOT dark bar)
 * darkMinThreshold: Dark bar lower threshold (default: 5, gray < this is pure-black noise, 0=no filter)
 * outerThreshold: Outer region upper threshold (0 = adaptive Otsu)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_4_DetectDarkBars(TrajStepContext ctx, int darkThreshold, int darkMinThreshold, int outerThreshold);

/**
 * Step 5: Morphology cleanup
 * kernelSize: Morphology kernel size (default: 3)
 * blurKsize: Gaussian blur kernel size (default: 5)
 * blurSigma: Gaussian blur sigma (default: 1.0)
 * Returns: 0 on success
 */

/** Set darkBinary data from external source (e.g. FlowPage upstream output) */
CALIB_API int CALIB_TrajStep_SetDarkBinary(TrajStepContext ctx, const void* data, int width, int height);
/** Set grayMat data from external source (e.g. FlowPage upstream output) */
CALIB_API int CALIB_TrajStep_SetGrayMat(TrajStepContext ctx, const void* data, int width, int height);
/** Set mask data from external source */
CALIB_API int CALIB_TrajStep_SetMask(TrajStepContext ctx, const void* data, int width, int height);
/** Get contour visualization image (dark bar contours drawn on dark background) */
CALIB_API int CALIB_TrajStep_GetContourVis(TrajStepContext ctx, Image* outImage);
CALIB_API int CALIB_TrajStep_5_MorphologyCleanup(TrajStepContext ctx, int kernelSize, int blurKsize, double blurSigma);

/**
 * Step 5.5: Expand dark bars to edge boundary
 * Expands morphed darkBinary outward using iterative dilation, stopping at Canny edges.
 * Requires Step 2b (Canny) to have been executed first.
 * expandPixels: Expansion radius in pixels (default: 15)
 * Returns: 0 on success, -1 if edgeMap is empty (Canny not run)
 */
CALIB_API int CALIB_TrajStep_5_5_ExpandToEdgeBoundary(TrajStepContext ctx, int expandPixels);

CALIB_API int CALIB_TrajStep_2c_GrayRangeBinary(TrajStepContext ctx, int grayLow, int grayHigh);

/**
 * Step 6: Find and sort dark contours by area
 * Returns: Number of dark bars found
 */
CALIB_API int CALIB_TrajStep_6_FindAndSortDarkContours(TrajStepContext ctx);

/**
 * Step 7: Sample contours equidistantly (pure OpenCV arc-length sampling)
 * targetBars: Number of bars to sample (default: 16)
 * spacing: Sampling spacing in pixels along each contour (default: 3.0, range 1.0~10.0)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_7_SampleContours(TrajStepContext ctx, int targetBars, double spacing);

/**
 * Step 7.5: Fit shape for sampled points
 * Returns: 0 on success
 * fitMode: 0=stadium跑道型拟合(默认), 1=曲率去噪(删除局部曲率异常大的点)
 * approxEpsilon: 保留参数，fitMode=1时未使用
 */
CALIB_API int CALIB_TrajStep_7_5_FitShape(TrajStepContext ctx, int fitMode, double approxEpsilon);

/**
 * Step 8: Verify points by mask
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_8_VerifyByMask(TrajStepContext ctx);

/**
 * Step 9: Deduplicate and sort
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_9_DeduplicateAndSort(TrajStepContext ctx);

/**
 * Step 10: Convert to output
 * trajPixels: Output trajectory points
 * count: Output count (max CALIB_MAX_TRAJ_POINTS)
 * stepBarIds: Optional output bar IDs (can be NULL)
 * Returns: Number of output points
 */
CALIB_API int CALIB_TrajStep_10_ConvertToOutput(TrajStepContext ctx, Point2D* trajPixels, int* count, int* stepBarIds);

/**
 * Step 11: Draw colored trajectory
 * outputData: Output buffer (width * height * 3 bytes, caller allocated)
 * width, height: Image dimensions
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_11_DrawColoredTrajectory(TrajStepContext ctx, unsigned char* outputData, int width, int height);

/**
 * Get intermediate result image (step 1-6)
 * step: Step number (1-6)
 * Returns: 0 on success, fills width/height/channels/data
 */
CALIB_API int CALIB_TrajStep_GetStepImage(TrajStepContext ctx, int step, Image* outImage);

/**
 * Get current point count at intermediate step
 * Returns: Current number of points
 */
CALIB_API int CALIB_TrajStep_GetPointCount(TrajStepContext ctx);

/**
 * Get all sampled points at current step
 * points: Output array (caller allocated)
 * barIds: Output bar IDs (caller allocated, can be NULL)
 * maxCount: Maximum points to copy
 * Returns: Number of points copied
 */
CALIB_API int CALIB_TrajStep_GetPoints(TrajStepContext ctx, Point2D* points, int* barIds, int maxCount);

// ================================================================
// Bar Colors
// ================================================================

/**
 * Get bar colors (BGR format)
 * colors: Array to store 16 colors (48 bytes total)
 */
CALIB_API void CALIB_GetBarColors(unsigned char colors[48]);

// ================================================================
// Independent Contour Sampling (no step context required)
// ================================================================

/**
 * Sample contours directly from binary image data.
 * Internally does findContours + area sort + equidistant arc-length sampling.
 * binaryData: Single-channel binary data (width*height bytes, 0 or 255)
 * Returns: Number of sampled points (<= maxPoints)
 */
CALIB_API int CALIB_SampleContours(const unsigned char* binaryData, int width, int height,
                                    int targetBars, double spacing, int minArea,
                                    Point2D* outPoints, int* outBarIds, int maxPoints);

/**
 * Export sorted contour data from step context (after Step 6 FindAndSortDarkContours).
 * flatX, flatY: Output arrays for flattened contour point coordinates.
 * contourLengths: Output array, contourLengths[i] = number of points in contour i.
 * maxContours: Maximum number of contours to export.
 * maxPointsPerContour: Maximum total points buffer size.
 * Returns: Number of contours exported (>= 0), or -1 on error.
 */
CALIB_API int CALIB_TrajStep_ExportSortedContours(TrajStepContext ctx,
                                                   int* flatX, int* flatY,
                                                   int* contourLengths,
                                                   int maxContours, int maxTotalPoints);

/**
 * Sample contours from pre-exported point data (no findContours needed).
 * flatX, flatY: Flattened contour points (all contours concatenated).
 * contourLengths: Number of points per contour.
 * numContours: Total number of contours.
 * targetBars: Take first N contours.
 * width, height: Image dimensions for bounds checking.
 * spacing: Arc-length sampling spacing in pixels.
 * outPoints, outBarIds: Output sampled points and bar IDs.
 * maxPoints: Maximum output buffer size.
 * Returns: Number of sampled points.
 */
CALIB_API int CALIB_SampleContoursFromPoints(const int* flatX, const int* flatY,
                                              const int* contourLengths, int numContours,
                                              int targetBars, int width, int height,
                                              double spacing,
                                              Point2D* outPoints, int* outBarIds, int maxPoints);

// ================================================================
// Image Utility
// ================================================================

/**
 * Apply mask to source image: output = src where mask != 0, else 0
 * src: Source image (grayscale or color)
 * mask: Mask image (single channel, same dimensions as src)
 * outImg: Output image (same dimensions and channels as src)
 * Returns: 0 on success, -1 on null input, -2 on dimension mismatch
 */
CALIB_API int CALIB_ApplyMask(Image* src, Image* mask, Image* outImg);

// ================================================================
// Native Flow Engine (flow.json execution in C++)
// ================================================================

typedef void* FlowEngineContext;

typedef struct {
    int success;
    int executedNodes;
    int totalNodes;
} FlowRunResult;

CALIB_API FlowEngineContext CALIB_FlowEngine_Create();
CALIB_API void CALIB_FlowEngine_Free(FlowEngineContext ctx);
CALIB_API int CALIB_FlowEngine_LoadFromFile(FlowEngineContext ctx, const char* flowFilePath);
CALIB_API int CALIB_FlowEngine_LoadFromJson(FlowEngineContext ctx, const char* flowJsonText);
CALIB_API FlowRunResult CALIB_FlowEngine_Run(FlowEngineContext ctx);
CALIB_API const char* CALIB_FlowEngine_GetLastError(FlowEngineContext ctx);
CALIB_API const char* CALIB_FlowEngine_GetLastReportJson(FlowEngineContext ctx);

#ifdef __cplusplus
}
#endif
