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

CALIB_API void CALIB_FreeImageData(unsigned char* data) {
    if (data) free(data);
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
                                            Image* stepImages, int* stepBarIds, int stepImageCount,
                                            int useContourMode, int fitMode, double approxEpsilon) {
    int detected = 0;
    ::DetectTrajectoryOpenCV(img, trajPixels, &detected, stepImages, stepBarIds, stepImageCount,
                             useContourMode != 0, fitMode, approxEpsilon);
    if (count) *count = detected;
    return detected;
}

CALIB_API int CALIB_DetectTrajectory(Image* img, Point2D* trajPixels, int* count) {
    int detected = 0;
    ::DetectTrajectory(img, trajPixels, &detected);
    if (count) *count = detected;
    return detected;
}

CALIB_API int CALIB_DetectHollowTrajectory(Image* img, Point2D* trajPixels, int* count,
                                            Image* stepImages, int* stepBarIds, int stepImageCount,
                                            int blurKsize, int morphKernelSize,
                                            int targetHollows, int bandWidth,
                                            int useContourMode, int outerExpandPixels,
                                            double grayMergeRatio) {
    int detected = 0;
    ::DetectHollowTrajectory(img, trajPixels, &detected, stepImages, stepBarIds, stepImageCount,
                              blurKsize, morphKernelSize, targetHollows, bandWidth,
                              useContourMode != 0, outerExpandPixels, grayMergeRatio);
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
    cv::Mat watershedOutput;  // 分水岭预分割输出（步骤1.5）
    cv::Mat edgeMap;  // Canny 边缘图（步骤2b，可选）
    cv::Mat expandedMask;  // 边界约束膨胀增量区域（步骤5.5）
    
    std::vector<std::vector<cv::Point>> outerContours;
    std::vector<std::vector<cv::Point>> outerContourBars;  // Canvas 模式：每个暗条的外围跑道轮廓
    std::vector<std::vector<cv::Point>> darkContours;
    std::vector<std::pair<double, int>> sortedBars;
    
    std::vector<cv::Point> allPoints;
    std::vector<int> allBarIds;
    
    int width;
    int height;
    int darkBarCount;
    int bandWidth;  // 窄带模式标记（0=原始轮廓采样，>0=窄带宽度）
    bool includeOuterBand;  // 是否包含暗条外围跑道型区域
    cv::Mat outerMask;  // 外围跑道区域灰度阈值 mask
    cv::Mat bandMask;  // 窄带区域可视化图（暗条=255，窄带=128，外围=64，背景=0）

    // ===== 快照机制：保证分步函数幂等（重复运行结果一致） =====
    std::vector<cv::Point> snapshotPoints;
    std::vector<int> snapshotBarIds;
    bool hasSnapshot = false;

    void SaveSnapshot() {
        snapshotPoints = allPoints;
        snapshotBarIds = allBarIds;
        hasSnapshot = true;
    }

    void RestoreSnapshot() {
        if (hasSnapshot) {
            allPoints = snapshotPoints;
            allBarIds = snapshotBarIds;
        }
    }

    void InvalidateSnapshot() {
        snapshotPoints.clear();
        snapshotBarIds.clear();
        hasSnapshot = false;
    }
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
        impl->outerMask.release();
        impl->bandMask.release();
        impl->edgeMap.release();
        impl->coloredMask.release();
        impl->expandedMask.release();
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

CALIB_API int CALIB_TrajStep_1_5_WatershedPresegment(TrajStepContext ctx, bool enable) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_WatershedPresegment(impl->grayMat, &impl->watershedOutput, enable);
    if (enable) {
        impl->grayMat = impl->watershedOutput.clone();
    }
    return 0;
}

CALIB_API int CALIB_TrajStep_1b_CLAHE(TrajStepContext ctx, double clipLimit, int tileGridSize) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_ApplyCLAHE(&impl->grayMat, clipLimit, tileGridSize);
    return 0;
}

CALIB_API int CALIB_TrajStep_2b_CannyEdges(TrajStepContext ctx, double lowThreshold, double highThreshold,
                                            int blurKsize, int bilateralD, double bilateralSigmaColor, double bilateralSigmaSpace,
                                            int nlmeansH, int nlmeansTemplateSize, int nlmeansSearchSize) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_DetectCannyEdges(impl->grayMat, &impl->edgeMap, lowThreshold, highThreshold, blurKsize,
                          bilateralD, bilateralSigmaColor, bilateralSigmaSpace,
                          nlmeansH, nlmeansTemplateSize, nlmeansSearchSize);
    return 0;
}

CALIB_API int CALIB_TrajStep_2_PreprocessAndFindContours(TrajStepContext ctx, int blurKsize, int morphKernelSize, bool enableWatershed) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_PreprocessAndFindContours(&impl->grayMat, &impl->binaryBright, 
                                    &impl->morphed, &impl->mask, &impl->coloredMask, 
                                    &impl->outerContours, blurKsize, morphKernelSize,
                                    enableWatershed);
    return 0;
}

CALIB_API int CALIB_TrajStep_3_CreateWorkpieceMask(TrajStepContext ctx, int maxContourIdx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->mask.release();
    Step_CreateWorkpieceMask(&impl->outerContours, maxContourIdx, impl->width, impl->height, &impl->mask);
    return 0;
}

CALIB_API int CALIB_TrajStep_4_DetectDarkBars(TrajStepContext ctx, int darkThreshold, int darkMinThreshold, int outerThreshold) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_DetectDarkBars(&impl->grayMat, &impl->mask, darkThreshold, darkMinThreshold, outerThreshold,
                         &impl->darkBinary, &impl->outerMask);
    return 0;
}

CALIB_API int CALIB_TrajStep_5_MorphologyCleanup(TrajStepContext ctx, int kernelSize, int blurKsize, double blurSigma) {

    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_MorphologyCleanup(&impl->darkBinary, kernelSize, blurKsize, blurSigma);

    return 0;
}

CALIB_API int CALIB_TrajStep_5_5_ExpandToEdgeBoundary(TrajStepContext ctx, int expandPixels) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    if (impl->edgeMap.empty()) {
        //LOG_WARN("CALIB_TrajStep_5_5: edgeMap is empty, run Step 2b (Canny) first");
        return -1;
    }

    Step_ExpandToEdgeBoundary(&impl->darkBinary, impl->edgeMap, expandPixels, impl->mask, &impl->expandedMask);
    return 0;
}

CALIB_API int CALIB_TrajStep_6_FindAndSortDarkContours(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->darkBarCount = Step_FindAndSortDarkContours(&impl->darkBinary, impl->width, impl->height,
                                                       &impl->sortedBars, &impl->darkContours);
    return impl->darkBarCount;
}

CALIB_API int CALIB_TrajStep_7_SampleContours(TrajStepContext ctx, int targetBars, int bandWidth, bool includeOuterBand, bool useContourMode, int outerExpandPixels) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 快照恢复：重复运行时从上次输入重新开始
    impl->RestoreSnapshot();

    impl->bandWidth = bandWidth;
    impl->includeOuterBand = includeOuterBand;
    const cv::Mat* workMask = (bandWidth > 0) ? &impl->mask : NULL;
    const cv::Mat* outerMaskPtr = includeOuterBand ? &impl->outerMask : NULL;

    // Canvas 模式：先提取外围跑道轮廓
    const std::vector<std::vector<cv::Point>>* outerContourPtr = NULL;
    if (useContourMode && includeOuterBand && bandWidth > 0) {
        impl->outerContourBars.clear();
        Step_DetectOuterContour(impl->darkContours, impl->sortedBars, targetBars,
                                 impl->width, impl->height, outerExpandPixels, impl->mask,
                                 &impl->outerContourBars);
        outerContourPtr = &impl->outerContourBars;
    }

    Step_SampleContoursEquidistant(&impl->darkContours, &impl->sortedBars, targetBars,
                                    impl->width, impl->height, &impl->allPoints, &impl->allBarIds,
                                    bandWidth, workMask, includeOuterBand, outerMaskPtr,
                                    outerContourPtr, useContourMode);

    // 计算窄带可视化图：暗条=255，窄带=128，外围跑道=64，背景=0
    impl->bandMask.release();
    if (bandWidth > 0 && !impl->darkBinary.empty()) {
        impl->bandMask = cv::Mat::zeros(impl->height, impl->width, CV_8UC1);
        impl->bandMask.setTo(255, impl->darkBinary);

        // 内窄带
        int kw = 2 * bandWidth + 1;
        cv::Mat bandKernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(kw, kw));
        cv::Mat dilated;
        cv::dilate(impl->darkBinary, dilated, bandKernel);
        bandKernel.release();
        cv::Mat band = dilated - impl->darkBinary;
        dilated.release();
        band.setTo(0, impl->mask == 0);
        impl->bandMask.setTo(128, band);

        // 外围跑道型区域（基于灰度阈值 mask，用64灰度显示）
        if (includeOuterBand && !impl->outerMask.empty()) {
            // 减去暗条和窄带区域，只显示纯外围
            cv::Mat outerVis = impl->outerMask.clone();
            outerVis.setTo(0, impl->darkBinary != 0);  // 减去暗条
            outerVis.setTo(0, band != 0);  // 减去窄带
            impl->bandMask.setTo(64, outerVis);
            outerVis.release();
        }

        band.release();
    }

    // 保存快照：下次重复运行时从这里恢复
    impl->SaveSnapshot();

    return 0;
}

CALIB_API int CALIB_TrajStep_7_5_FitShape(TrajStepContext ctx, int fitMode, double approxEpsilon) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 快照恢复：重复运行时从上次输入重新开始
    impl->RestoreSnapshot();

    Step_FitShape(&impl->allPoints, &impl->allBarIds, impl->width, impl->height,
                  fitMode, approxEpsilon);

    // 保存快照
    impl->SaveSnapshot();

    return 0;
}

CALIB_API int CALIB_TrajStep_8_VerifyByMask(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 快照恢复：重复运行时从上次输入重新开始
    impl->RestoreSnapshot();

    // 窄带模式: 验证点在工件 mask 内; 原始模式: 验证点在暗条区域内
    if (impl->bandWidth > 0) {
        int keptCount = 0;
        for (size_t i = 0; i < impl->allPoints.size(); i++) {
            int px = impl->allPoints[i].x;
            int py = impl->allPoints[i].y;
            if (px >= 0 && px < impl->width && py >= 0 && py < impl->height) {
                if (impl->mask.at<unsigned char>(py, px) != 0) {
                    impl->allPoints[keptCount] = impl->allPoints[i];
                    impl->allBarIds[keptCount] = impl->allBarIds[i];
                    keptCount++;
                }
            }
        }
        impl->allPoints.resize(keptCount);
        impl->allBarIds.resize(keptCount);
    } else {
        Step_VerifyByMask(&impl->allPoints, &impl->allBarIds, &impl->darkBinary, impl->width, impl->height);
    }

    // 保存快照
    impl->SaveSnapshot();

    return 0;
}

CALIB_API int CALIB_TrajStep_9_DeduplicateAndSort(TrajStepContext ctx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 快照恢复：重复运行时从上次输入重新开始
    impl->RestoreSnapshot();

    Step_DeduplicateAndSort(&impl->allPoints, &impl->allBarIds);

    // 保存快照
    impl->SaveSnapshot();

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
        case 5:
            // 步骤5返回窄带可视化图（暗条=255，窄带=128，背景=0）
            if (!impl->bandMask.empty()) {
                srcMat = &impl->bandMask;
            } else {
                srcMat = &impl->darkBinary;  // fallback
            }
            break;
        case 6: srcMat = &impl->edgeMap; break;  // 步骤2b: Canny边缘图
        case 7: srcMat = &impl->expandedMask; break;  // 步骤5.5: 边界膨胀增量区域
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
