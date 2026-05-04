/**
 * CalibOperatorExport.cpp - Native C API Implementation
 */

#include "CalibOperatorExport.h"
#include "FlowEngineNative.h"

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

CALIB_API int CALIB_LoadImageFile(const char* filename, Image* img) {
    return ::LoadImageFile(filename, img);
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

CALIB_API int CALIB_FindChessboardCorners(Image* img, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck) {
    return ::FindChessboardCorners(img, boardCols, boardRows, outPts, outCount, maxPts, refineSubPix, fastCheck);
}

CALIB_API void CALIB_DrawChessboardCorners(Image* img, Point2D* pts, int count, int boardCols, int boardRows) {
    ::DrawChessboardCorners(img, pts, count, boardCols, boardRows);
}

CALIB_API int CALIB_CalibrateCameraChessboard(const char* pathsDelimited, int boardCols, int boardRows,
    double squareSizeMm, CALIB_CameraIntrinsics* outIntrinsics,
    char* fullCalibrationJsonOut, int fullCalibrationJsonOutSize) {
    if (!outIntrinsics) return -1;
    outIntrinsics->fx = outIntrinsics->fy = outIntrinsics->cx = outIntrinsics->cy = 0.0;
    outIntrinsics->k1 = outIntrinsics->k2 = outIntrinsics->p1 = outIntrinsics->p2 = outIntrinsics->k3 = 0.0;
    outIntrinsics->rms = 0.0;
    outIntrinsics->success = 0;
    double fx, fy, cx, cy, k1, k2, p1, p2, k3, rms;
    int rc = ::CalibrateCameraChessboardMultiview(pathsDelimited, boardCols, boardRows, squareSizeMm,
        &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, &rms,
        fullCalibrationJsonOut, fullCalibrationJsonOutSize);
    if (rc != 0) return rc;
    outIntrinsics->fx = fx;
    outIntrinsics->fy = fy;
    outIntrinsics->cx = cx;
    outIntrinsics->cy = cy;
    outIntrinsics->k1 = k1;
    outIntrinsics->k2 = k2;
    outIntrinsics->p1 = p1;
    outIntrinsics->p2 = p2;
    outIntrinsics->k3 = k3;
    outIntrinsics->rms = rms;
    outIntrinsics->success = 1;
    return 0;
}

CALIB_API int CALIB_PixelsToChessboardPlaneFromCalibrationJson(
    const char* calibrationJsonUtf8,
    int viewIndex,
    const Point2D* pixels,
    Point2D* worldXYOut,
    int count) {
    return ::PixelsToChessboardPlaneXYFromCalibrationJson(calibrationJsonUtf8, viewIndex, pixels, worldXYOut, count);
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
                                            double grayMergeRatio,
                                            int hollowGrayLow, int hollowGrayHigh) {
    int detected = 0;
    ::DetectHollowTrajectory(img, trajPixels, &detected, stepImages, stepBarIds, stepImageCount,
                              blurKsize, morphKernelSize, targetHollows, bandWidth,
                              useContourMode != 0, outerExpandPixels, grayMergeRatio,
                              hollowGrayLow, hollowGrayHigh);
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

CALIB_API int CALIB_TrajStep_2_PreprocessAndFindContours(TrajStepContext ctx, int blurKsize, int morphKernelSize, bool enableWatershed, double minArea) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_PreprocessAndFindContours(&impl->grayMat, &impl->binaryBright, 
                                    &impl->morphed, &impl->mask, &impl->coloredMask, 
                                    &impl->outerContours, blurKsize, morphKernelSize,
                                    enableWatershed, minArea);
    return 0;
}

CALIB_API int CALIB_TrajStep_2c_GrayRangeBinary(TrajStepContext ctx, int grayLow, int grayHigh) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->grayMat.empty()) return -1;

    // 按灰度范围二值化: grayLow <= gray <= grayHigh → 255, 其余 → 0
    cv::Mat result;
    cv::inRange(impl->grayMat, grayLow, grayHigh, result);
    impl->binaryBright = result;
    return 0;
}

CALIB_API int CALIB_TrajStep_2_5_CreateMask(TrajStepContext ctx, int contourIdx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->outerContours.empty()) {
        return -1;
    }
    impl->mask.release();
    Step_CreateMaskFromLargestContour(impl->outerContours, impl->width, impl->height, &impl->mask, contourIdx);
    return 0;
}

CALIB_API int CALIB_TrajStep_3_CreateWorkpieceMask(TrajStepContext ctx, int maxContourIdx) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    impl->mask.release();
    Step_CreateWorkpieceMask(&impl->outerContours, maxContourIdx, impl->width, impl->height, &impl->mask);
    return 0;
}

CALIB_API int CALIB_TrajStep_3b_DetectHollow(TrajStepContext ctx, int hollowGrayLow, int hollowGrayHigh, int targetHollows, int morphKernelSize) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->grayMat.empty() || impl->mask.empty()) return -1;

    if (morphKernelSize % 2 == 0) morphKernelSize++;
    if (morphKernelSize < 3) morphKernelSize = 3;

    int h = impl->height, w = impl->width;

    // ---- 按灰度范围在 mask 内二值化 ----
    cv::Mat innerRegion = impl->grayMat.clone();
    innerRegion.setTo(255, impl->mask == 0);

    cv::Mat binaryDark;
    binaryDark = (innerRegion >= hollowGrayLow) & (innerRegion <= hollowGrayHigh);
    binaryDark.setTo(0, impl->mask == 0);
    innerRegion.release();

    // ---- 形态学处理 ----
    cv::Mat kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(morphKernelSize, morphKernelSize));
    cv::Mat morphed;
    cv::morphologyEx(binaryDark, morphed, cv::MORPH_OPEN, kernel);
    cv::morphologyEx(morphed, morphed, cv::MORPH_OPEN, kernel);
    cv::morphologyEx(morphed, morphed, cv::MORPH_CLOSE, kernel);
    cv::morphologyEx(morphed, morphed, cv::MORPH_CLOSE, kernel);

    // ---- 找空洞轮廓并按面积排序 ----
    std::vector<std::vector<cv::Point>> hierarchyContours;
    cv::findContours(morphed, hierarchyContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);

    std::vector<std::pair<double, int>> sortedHollows;
    for (size_t i = 0; i < hierarchyContours.size(); i++) {
        double area = cv::contourArea(hierarchyContours[i]);
        if (area > 100) {
            sortedHollows.push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(sortedHollows.begin(), sortedHollows.end(),
              [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
                  return a.first > b.first;
              });

    int takeHollows = std::min((int)sortedHollows.size(), std::min(targetHollows, CALIB_MAX_BARS));

    // ---- 构建空洞二值图 hollowBinary ----
    cv::Mat hollowBinary = cv::Mat::zeros(h, w, CV_8UC1);
    for (int k = 0; k < takeHollows; k++) {
        int idx = sortedHollows[k].second;
        cv::drawContours(hollowBinary, hierarchyContours, idx, cv::Scalar(255), cv::FILLED);
    }
    hollowBinary.setTo(0, impl->mask == 0);
    binaryDark.release();

    // ---- 再次形态学清理空洞二值图 ----
    cv::morphologyEx(hollowBinary, hollowBinary, cv::MORPH_CLOSE, kernel);
    cv::morphologyEx(hollowBinary, hollowBinary, cv::MORPH_OPEN, kernel);
    cv::morphologyEx(hollowBinary, hollowBinary, cv::MORPH_OPEN, kernel);
    cv::morphologyEx(hollowBinary, hollowBinary, cv::MORPH_CLOSE, kernel);

    cv::Mat dilateKernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(morphKernelSize + 2, morphKernelSize + 2));
    cv::erode(hollowBinary, hollowBinary, dilateKernel);
    cv::dilate(hollowBinary, hollowBinary, dilateKernel);
    dilateKernel.release();

    hollowBinary.setTo(0, impl->mask == 0);

    // ---- 最终提取空洞轮廓并排序 ----
    std::vector<std::vector<cv::Point>> hollowContours;
    cv::findContours(hollowBinary, hollowContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);

    double areaThreshold = (double)(w * h) * 0.002;
    if (areaThreshold < 500.0) areaThreshold = 500.0;

    impl->sortedBars.clear();
    for (size_t i = 0; i < hollowContours.size(); i++) {
        double area = cv::contourArea(hollowContours[i]);
        if (area > areaThreshold) {
            impl->sortedBars.push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(impl->sortedBars.begin(), impl->sortedBars.end(),
              [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
                  return a.first > b.first;
              });

    int barCount = std::min((int)impl->sortedBars.size(), CALIB_MAX_BARS);

    // ---- 按排序结果提取对应的轮廓 ----
    impl->darkContours.clear();
    for (int i = 0; i < barCount; i++) {
        int idx = impl->sortedBars[i].second;
        impl->darkContours.push_back(hollowContours[idx]);
    }

    // ---- 存入 context ----
    impl->darkBinary = hollowBinary;
    impl->darkBarCount = barCount;
    // bandWidth=0, includeOuterBand=true (后续 sample 时如需窄带可调整)
    impl->bandWidth = 0;
    impl->includeOuterBand = true;

    return barCount;
}

CALIB_API int CALIB_TrajStep_4_DetectDarkBars(TrajStepContext ctx, int darkThreshold, int darkMinThreshold, int outerThreshold) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    Step_DetectDarkBars(&impl->grayMat, &impl->mask, darkThreshold, darkMinThreshold, outerThreshold,
                         &impl->darkBinary, &impl->outerMask);
    return 0;
}


CALIB_API int CALIB_TrajStep_SetDarkBinary(TrajStepContext ctx, const void* data, int width, int height) {
    if (!ctx || !data) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->darkBinary.empty() || impl->darkBinary.cols != width || impl->darkBinary.rows != height) {
        impl->darkBinary.create(height, width, CV_8UC1);
    }
    memcpy(impl->darkBinary.data, data, width * height);
    return 0;
}

CALIB_API int CALIB_TrajStep_SetGrayMat(TrajStepContext ctx, const void* data, int width, int height) {
    if (!ctx || !data) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->grayMat.empty() || impl->grayMat.cols != width || impl->grayMat.rows != height) {
        impl->grayMat.create(height, width, CV_8UC1);
    }
    memcpy(impl->grayMat.data, data, width * height);
    return 0;
}

CALIB_API int CALIB_TrajStep_SetMask(TrajStepContext ctx, const void* data, int width, int height) {
    if (!ctx || !data) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;
    if (impl->mask.empty() || impl->mask.cols != width || impl->mask.rows != height) {
        impl->mask.create(height, width, CV_8UC1);
    }
    memcpy(impl->mask.data, data, width * height);
    return 0;
}

CALIB_API int CALIB_TrajStep_GetContourVis(TrajStepContext ctx, Image* outImage) {
    if (!ctx || !outImage) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 在 darkBinary 上叠加暗条轮廓边界线
    cv::Mat vis = impl->darkBinary.clone();

    // 只画通过面积过滤的轮廓（sortedBars 里记录的）
    for (size_t i = 0; i < impl->sortedBars.size(); i++) {
        int idx = impl->sortedBars[i].second;
        if (idx >= 0 && idx < (int)impl->darkContours.size()) {
            cv::drawContours(vis, impl->darkContours, idx, cv::Scalar(128), 2);
        }
    }

    if (outImage->data) free(outImage->data);
    outImage->width = vis.cols;
    outImage->height = vis.rows;
    outImage->channels = 1;
    int dataSize = vis.cols * vis.rows;
    outImage->data = (unsigned char*)malloc(dataSize);
    memcpy(outImage->data, vis.data, dataSize);
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

CALIB_API int CALIB_TrajStep_7_SampleContours(TrajStepContext ctx, int targetBars, double spacing) {
    if (!ctx) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    // 快照恢复：重复运行时从上次输入重新开始
    impl->RestoreSnapshot();

    if (spacing <= 0.0) spacing = 3.0;

    Step_SampleContoursEquidistant(&impl->darkContours, &impl->sortedBars, targetBars,
                                    impl->width, impl->height, &impl->allPoints, &impl->allBarIds,
                                    spacing);

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
        case 8: srcMat = &impl->mask; break;  // 二值mask（仅最大轮廓）
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

// ================================================================
// Independent Contour Sampling
// ================================================================

CALIB_API int CALIB_SampleContours(const unsigned char* binaryData, int width, int height,
                                    int targetBars, double spacing, int minArea,
                                    Point2D* outPoints, int* outBarIds, int maxPoints) {
    return ::SampleContoursFromBinary(binaryData, width, height, targetBars, spacing, minArea,
                                       outPoints, outBarIds, maxPoints);
}

CALIB_API int CALIB_TrajStep_ExportSortedContours(TrajStepContext ctx,
                                                   int* flatX, int* flatY,
                                                   int* contourLengths,
                                                   int maxContours, int maxTotalPoints) {
    if (!ctx || !flatX || !flatY || !contourLengths) return -1;
    TrajStepContextImpl* impl = (TrajStepContextImpl*)ctx;

    int exportCount = 0;
    int totalPts = 0;
    for (int b = 0; b < (int)impl->sortedBars.size() && b < maxContours; b++) {
        int idx = impl->sortedBars[b].second;
        if (idx < 0 || idx >= (int)impl->darkContours.size()) continue;
        const std::vector<cv::Point>& contour = impl->darkContours[idx];
        int ptCount = (int)contour.size();
        if (totalPts + ptCount > maxTotalPoints) break;

        contourLengths[exportCount] = ptCount;
        for (int j = 0; j < ptCount; j++) {
            flatX[totalPts + j] = contour[j].x;
            flatY[totalPts + j] = contour[j].y;
        }
        totalPts += ptCount;
        exportCount++;
    }
    printf("ExportSortedContours: exported %d contours, %d total points\n", exportCount, totalPts);
    return exportCount;
}

CALIB_API int CALIB_SampleContoursFromPoints(const int* flatX, const int* flatY,
                                              const int* contourLengths, int numContours,
                                              int targetBars, int width, int height,
                                              double spacing,
                                              Point2D* outPoints, int* outBarIds, int maxPoints) {
    return ::SampleContoursFromPoints(flatX, flatY, contourLengths, numContours,
                                       targetBars, width, height, spacing,
                                       outPoints, outBarIds, maxPoints);
}

// ================================================================
// Image Utility
// ================================================================

CALIB_API int CALIB_ApplyMask(Image* src, Image* mask, Image* outImg) {
    if (!src || !mask || !outImg) return -1;
    if (src->width != mask->width || src->height != mask->height) return -2;

    int totalPixels = src->width * src->height;
    int channels = src->channels;
    outImg->width = src->width;
    outImg->height = src->height;
    outImg->channels = channels;
    int dataSize = totalPixels * channels;
    if (outImg->data) free(outImg->data);
    outImg->data = (unsigned char*)malloc(dataSize);
    if (!outImg->data) return -1;

    for (int i = 0; i < totalPixels; i++) {
        if (mask->data[i] != 0) {
            memcpy(outImg->data + i * channels, src->data + i * channels, channels);
        } else {
            memset(outImg->data + i * channels, 0, channels);
        }
    }
    return 0;
}

// ================================================================
// Native Flow Engine exports
// ================================================================

CALIB_API FlowEngineContext CALIB_FlowEngine_Create() {
    return FlowEngine_Create();
}

CALIB_API void CALIB_FlowEngine_Free(FlowEngineContext ctx) {
    FlowEngine_Free(ctx);
}

CALIB_API int CALIB_FlowEngine_LoadFromFile(FlowEngineContext ctx, const char* flowFilePath) {
    return FlowEngine_LoadFromFile(ctx, flowFilePath);
}

CALIB_API int CALIB_FlowEngine_LoadFromJson(FlowEngineContext ctx, const char* flowJsonText) {
    return FlowEngine_LoadFromJson(ctx, flowJsonText);
}

CALIB_API FlowRunResult CALIB_FlowEngine_Run(FlowEngineContext ctx) {
    NativeFlowRunResult r = FlowEngine_Run(ctx);
    FlowRunResult out{};
    out.success = r.success;
    out.executedNodes = r.executedNodes;
    out.totalNodes = r.totalNodes;
    return out;
}

CALIB_API const char* CALIB_FlowEngine_GetLastError(FlowEngineContext ctx) {
    return FlowEngine_GetLastError(ctx);
}

CALIB_API const char* CALIB_FlowEngine_GetLastReportJson(FlowEngineContext ctx) {
    return FlowEngine_GetLastReportJson(ctx);
}
