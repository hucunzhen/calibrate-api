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
// Trajectory Step-by-Step Context Implementation
// ================================================================

struct TrajStepContextImpl {
    cv::Mat grayMat;
    cv::Mat binaryBright;
    cv::Mat morphed;
    cv::Mat mask;
    cv::Mat darkBinary;
    cv::Mat coloredMask;  // 彩色mask，用于显示
    
    std::vector<std::vector<cv::Point>> outerContours;
    std::vector<std::vector<cv::Point>> darkContours;
    std::vector<std::pair<double, int>> sortedBars;
    
    std::vector<cv::Point> allPoints;
    std::vector<int> allBarIds;
    
    int width;
    int height;
    int darkBarCount;
};

CALIB_API TrajStepContext CALIB_TrajStep_Create() {
    return new TrajStepContextImpl();
}

CALIB_API void CALIB_TrajStep_Free(TrajStepContext ctx) {
    if (ctx) {
        TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
        impl->grayMat.release();
        impl->binaryBright.release();
        impl->morphed.release();
        impl->mask.release();
        impl->darkBinary.release();
        impl->coloredMask.release();
        impl->outerContours.clear();
        impl->darkContours.clear();
        impl->sortedBars.clear();
        impl->allPoints.clear();
        impl->allBarIds.clear();
        delete impl;
    }
}

CALIB_API int CALIB_TrajStep_1_ConvertToGrayscale(TrajStepContext ctx, Image* src) {
    if (!ctx || !src) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->width = src->width;
    impl->height = src->height;
    Step_ConvertToGrayscale(src, &impl->grayMat);
    return 0;
}

CALIB_API int CALIB_TrajStep_2_PreprocessAndFindContours(TrajStepContext ctx, int blurKsize, int morphKernelSize) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_PreprocessAndFindContours(&impl->grayMat, &impl->binaryBright, 
                                    &impl->morphed, &impl->mask, &impl->coloredMask, 
                                    &impl->outerContours, blurKsize, morphKernelSize);
    return 0;
}

CALIB_API int CALIB_TrajStep_3_CreateWorkpieceMask(TrajStepContext ctx, int maxContourIdx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->mask.release();
    Step_CreateWorkpieceMask(&impl->outerContours, maxContourIdx, impl->width, impl->height, &impl->mask);
    return 0;
}

CALIB_API int CALIB_TrajStep_4_DetectDarkBars(TrajStepContext ctx, int threshold) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_DetectDarkBars(&impl->grayMat, &impl->mask, threshold, &impl->darkBinary);
    return 0;
}

CALIB_API int CALIB_TrajStep_5_MorphologyCleanup(TrajStepContext ctx, int kernelSize, int blurKsize, double blurSigma) {

    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_MorphologyCleanup(&impl->darkBinary, kernelSize, blurKsize, blurSigma);

    return 0;
}

CALIB_API int CALIB_TrajStep_6_FindAndSortDarkContours(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->darkBarCount = Step_FindAndSortDarkContours(&impl->darkBinary, impl->width, impl->height,
                                                       &impl->sortedBars, &impl->darkContours);
    return impl->darkBarCount;
}

CALIB_API int CALIB_TrajStep_7_SampleContours(TrajStepContext ctx, int targetBars) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_SampleContoursEquidistant(&impl->darkContours, &impl->sortedBars, targetBars,
                                    impl->width, impl->height, &impl->allPoints, &impl->allBarIds);
    return 0;
}

CALIB_API int CALIB_TrajStep_7_5_FitShape(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_FitShape(&impl->allPoints, &impl->allBarIds, impl->width, impl->height);
    return 0;
}

CALIB_API int CALIB_TrajStep_8_VerifyByMask(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_VerifyByMask(&impl->allPoints, &impl->allBarIds, &impl->darkBinary, impl->width, impl->height);
    return 0;
}

CALIB_API int CALIB_TrajStep_9_DeduplicateAndSort(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_DeduplicateAndSort(&impl->allPoints, &impl->allBarIds);
    return 0;
}

CALIB_API int CALIB_TrajStep_10_ConvertToOutput(TrajStepContext ctx, Point2D* trajPixels, int* count, int* stepBarIds) {
    if (!ctx || !trajPixels) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_ConvertToOutput(&impl->allPoints, &impl->allBarIds, trajPixels, count, stepBarIds);
    return *count;
}

CALIB_API int CALIB_TrajStep_11_DrawColoredTrajectory(TrajStepContext ctx, unsigned char* outputData, int width, int height) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    
    // Convert allPoints to Point2D array
    std::vector<Point2D> trajPixelsVec(impl->allPoints.size());
    for (size_t i = 0; i < impl->allPoints.size(); i++) {
        trajPixelsVec[i].x = impl->allPoints[i].x;
        trajPixelsVec[i].y = impl->allPoints[i].y;
    }
    
    int count = (int)trajPixelsVec.size();
    int* barIdsPtr = impl->allBarIds.empty() ? NULL : impl->allBarIds.data();
    
    Step_DrawColoredTrajectory(trajPixelsVec.data(), count, barIdsPtr, width, height, outputData);
    return 0;
}

CALIB_API int CALIB_TrajStep_GetStepImage(TrajStepContext ctx, int step, Image* outImage) {
    if (!ctx || !outImage) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    
    cv::Mat* srcMat = NULL;
    int dstChannels = 1;
    
    switch (step) {
        case 1: srcMat = &impl->grayMat; break;
        case 2: srcMat = &impl->binaryBright; break;
        case 3: 
            // 步骤3返回彩色mask
            if (!impl->coloredMask.empty()) {
                srcMat = &impl->coloredMask;
                dstChannels = 3;
            } else {
                srcMat = &impl->mask;  // fallback
            }
            break;
        case 4: srcMat = &impl->darkBinary; break;
        default: return -1;
    }
    
    if (srcMat && !srcMat->empty()) {
        if (outImage->data) free(outImage->data);
        outImage->width = srcMat->cols;
        outImage->height = srcMat->rows;
        outImage->channels = dstChannels;
        int dataSize = srcMat->cols * srcMat->rows * srcMat->channels();
        outImage->data = (unsigned char*)malloc(dataSize);
        memcpy(outImage->data, srcMat->data, dataSize);
        return 0;
    }
    return -1;
}

CALIB_API int CALIB_TrajStep_GetPointCount(TrajStepContext ctx) {
    if (!ctx) return 0;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    return (int)impl->allPoints.size();
}

CALIB_API int CALIB_TrajStep_GetPoints(TrajStepContext ctx, Point2D* points, int* barIds, int maxCount) {
    if (!ctx || !points) return 0;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    int count = std::min((int)impl->allPoints.size(), maxCount);
    for (int i = 0; i < count; i++) {
        points[i].x = impl->allPoints[i].x;
        points[i].y = impl->allPoints[i].y;
        if (barIds) barIds[i] = impl->allBarIds[i];
    }
    return count;
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
