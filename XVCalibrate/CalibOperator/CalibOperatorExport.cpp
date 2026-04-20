/**
 * CalibOperatorExport.cpp - Native C API Implementation
 */

#include "CalibOperatorExport.h"

#ifdef CALIBOPERATOR_EXPORTS
#define CALIB_API __declspec(dllexport)
#else
#define CALIB_API __declspec(dllimport)
#endif

// ================================================================
// Image Management
// ================================================================

CALIB_API Image* CALIB_CreateImage() {
    Image* img = new Image();
    img->width = 0;
    img->height = 0;
    img->channels = 0;
    img->data = nullptr;
    return img;
}

CALIB_API void CALIB_FreeImage(Image* img) {
    if (img) {
        if (img->data) {
            delete[] img->data;
        }
        delete img;
    }
}

CALIB_API int CALIB_CreateCalibrationImage(Image* img, int width, int height) {
    return ::CreateCalibrationImage(img, width, height);
}

CALIB_API int CALIB_CreateBlankImage(Image* img, int width, int height, int channels) {
    if (!img) return -1;
    int rowSize = width * channels;
    if (rowSize % 4 != 0) rowSize = ((rowSize / 4) + 1) * 4;  // Align to 4 bytes

    if (img->data) delete[] img->data;
    img->width = width;
    img->height = height;
    img->channels = channels;
    img->data = (unsigned char*)malloc(rowSize * height);
    if (!img->data) return -1;

    memset(img->data, 0, rowSize * height);
    return 0;
}

CALIB_API int CALIB_SaveBMP(const char* filename, Image* img) {
    return ::SaveBMP(filename, img);
}

CALIB_API int CALIB_LoadBMP(const char* filename, Image* img) {
    return ::LoadBMP(filename, img);
}

// ================================================================
// Circle Detection
// ================================================================

CALIB_API int CALIB_DetectCircles(Image* img, Point2D* pts, int* count, int maxCount) {
    int detected = 0;
    ::DetectCircles(img, pts, &detected);
    if (count) *count = detected;
    return detected;
}

CALIB_API void CALIB_DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray) {
    ::DrawDetectedCircles(img, pts, count, gray);
}

// ================================================================
// Calibration
// ================================================================

CALIB_API int CALIB_CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    return ::CalibrateNinePoint(imagePts, worldPts, n, trans);
}

CALIB_API Point2D CALIB_ImageToWorld(Point2D pixel, AffineTransform trans) {
    return ::ImageToWorld(pixel, trans);
}

CALIB_API void CALIB_SetTransform(AffineTransform trans) {
    ::CalibSetTransform(trans);
}

CALIB_API void CALIB_CalculateError(Point2D* imagePts, Point2D* worldPts, int n,
                                     AffineTransform trans, double* avgErr, double* maxErr) {
    ::CalculateError(imagePts, worldPts, n, trans, avgErr, maxErr);
}

// ================================================================
// Trajectory Detection
// ================================================================

CALIB_API int CALIB_DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                                            Image* stepImages, int* stepBarIds, int stepImageCount) {
    int detected = 0;
    ::DetectTrajectoryOpenCV(img, trajPixels, &detected, stepImages, stepBarIds, stepImageCount);
    if (count) *count = detected;
    return detected;
}

CALIB_API int CALIB_DetectTrajectory(Image* img, Point2D* trajPixels, int* count) {
    int detected = 0;
    ::DetectTrajectory(img, trajPixels, &detected);
    if (count) *count = detected;
    return detected;
}

CALIB_API void CALIB_DrawTrajectoryColored(Image* img, Point2D* trajPixels, int count, int* barIds) {
    ::DrawTrajectoryColored(img, trajPixels, count, barIds);
}

CALIB_API void CALIB_DrawTrajectoryGrayscale(Image* img, Point2D* trajPixels, int count, int grayValue) {
    ::DrawTrajectoryGrayscale(img, trajPixels, count, grayValue);
}

// ================================================================
// Bar Colors
// ================================================================

CALIB_API void CALIB_GetBarColors(unsigned char colors[48]) {
    for (int i = 0; i < 16 && i < CALIB_MAX_BARS; i++) {
        colors[i * 3 + 0] = BAR_COLORS[i][0];
        colors[i * 3 + 1] = BAR_COLORS[i][1];
        colors[i * 3 + 2] = BAR_COLORS[i][2];
    }
}
