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
