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
                                           Image* stepImages, int* stepBarIds, int stepImageCount);

/**
 * Detect trajectory using legacy algorithm
 */
CALIB_API int CALIB_DetectTrajectory(Image* img, Point2D* trajPixels, int* count);

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
 * Step 2: Preprocess and find outer contours
 * blurKsize: Gaussian blur kernel size (default: 7)
 * morphKernelSize: Morphology kernel size (default: 5)
 * enableWatershed: Enable watershed pre-segmentation before Step 2 (default: false)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_2_PreprocessAndFindContours(TrajStepContext ctx, int blurKsize, int morphKernelSize, bool enableWatershed);

/**
 * Step 3: Create workpiece mask from contours
 * maxContourIdx: Index of the largest contour
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_3_CreateWorkpieceMask(TrajStepContext ctx, int maxContourIdx);

/**
 * Step 4: Detect dark bars
 * threshold: Dark bar threshold (default: 45)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_4_DetectDarkBars(TrajStepContext ctx, int threshold);

/**
 * Step 5: Morphology cleanup
 * kernelSize: Morphology kernel size (default: 3)
 * blurKsize: Gaussian blur kernel size (default: 5)
 * blurSigma: Gaussian blur sigma (default: 1.0)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_5_MorphologyCleanup(TrajStepContext ctx, int kernelSize, int blurKsize, double blurSigma);

/**
 * Step 6: Find and sort dark contours by area
 * Returns: Number of dark bars found
 */
CALIB_API int CALIB_TrajStep_6_FindAndSortDarkContours(TrajStepContext ctx);

/**
 * Step 7: Sample contours equidistantly
 * targetBars: Number of bars to sample (default: 16)
 * bandWidth: Narrow band width in pixels (0 = original contour sampling, >0 = narrow band sampling)
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_7_SampleContours(TrajStepContext ctx, int targetBars, int bandWidth);

/**
 * Step 7.5: Fit shape for sampled points
 * Returns: 0 on success
 */
CALIB_API int CALIB_TrajStep_7_5_FitShape(TrajStepContext ctx);

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

#ifdef __cplusplus
}
#endif
