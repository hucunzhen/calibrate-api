/**
 * CalibOperator - 工业视觉标定算子库
 * 纯算法层，不依赖 Windows GUI (无 HWND/HDC/WM_*)
 * 依赖：OpenCV 4.x, C 标准库
 */

#ifndef CALIB_OPERATOR_H
#define CALIB_OPERATOR_H

#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>
#include <algorithm>
#include <vector>

#include <opencv2/opencv.hpp>
#include <opencv2/imgproc.hpp>

// ========== 基础类型定义 ==========

typedef struct {
    double x;
    double y;
} Point2D;

typedef struct {
    double a, b, c;
    double d, e, f;
} AffineTransform;

typedef struct {
    int width;
    int height;
    int channels;  // 1=grayscale, 3=color
    unsigned char* data;
} Image;

#pragma pack(push, 1)
typedef struct {
    unsigned short type;
    unsigned int size;
    unsigned short reserved1;
    unsigned short reserved2;
    unsigned int offset;
} BMPHeader;

typedef struct {
    unsigned int size;
    int width;
    int height;
    unsigned short planes;
    unsigned short bitCount;
    unsigned int compression;
    unsigned int imageSize;
    int xPelsPerMeter;
    int yPelsPerMeter;
    unsigned int colorsUsed;
    unsigned int colorsImportant;
} BMPInfoHeader;
#pragma pack(pop)

// ========== 检测方法枚举 ==========

enum DetectMethod {
    METHOD_SELF = 0,      // Self-implemented algorithm (gradient-based)
    METHOD_OPENCV = 1     // OpenCV-based algorithm (contour + shape fitting)
};

// ========== 日志接口（GUI 层设置回调） ==========

// 日志回调函数类型：severity(0=INFO,1=WARN,2=ERROR,3=DEBUG), format_string, ...
typedef void (*LogCallback)(int level, const char* fmt, ...);

// 设置日志回调（GUI 层调用）
void CalibSetLogCallback(LogCallback cb);

// 设置全局变换矩阵（GUI 层在标定后调用，供 DetectTrajectory 使用）
void CalibSetTransform(AffineTransform trans);

// 默认日志输出到 stdout
void CalibDefaultLog(int level, const char* fmt, ...);

// ========== 图像 I/O ==========

// 创建 8-bit 灰度标定图像（3x3 网格圆+十字准心）
int CreateCalibrationImage(Image* img, int width, int height);

// 保存 BMP 文件（支持 8-bit 灰度和 24-bit 彩色）
int SaveBMP(const char* filename, Image* img);

// 加载 BMP 文件（支持 8-bit/24-bit, 无压缩/RLE8）
// 注意：失败时不弹 MessageBox，仅通过日志输出错误
int LoadBMP(const char* filename, Image* img);

// OpenCV imread：bmp/png/jpeg/tiff 等，输出为 3 通道 BGR（灰度/RGBA 会转换）
int LoadImageFile(const char* filename, Image* img);

// ========== 圆检测 ==========

// Contour + Crosshair 圆检测（适用于 9 点标定图）
// 支持 OTSU + adaptive fallback 策略
void DetectCircles(Image* img, Point2D* pts, int* count);

// 在图像上绘制检测到的圆标记
void DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray);

// ========== 棋盘格 / 相机内参（OpenCV）==========
// boardCols/boardRows：棋盘内侧角点列数、行数（与 cv::findChessboardCorners 的 patternSize 一致）
// FindChessboardCorners：返回 0 表示检测到完整角点集；1 表示未找到；负值表示参数错误
int FindChessboardCorners(Image* img, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck);

// 连续存储的灰度行优先缓冲区（width×height），用于已由 cv::Mat 等得到的灰度图
int FindChessboardCornersGrayBuffer(const unsigned char* grayRowMajor, int width, int height, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck);

void DrawChessboardCorners(Image* img, Point2D* pts, int count, int boardCols, int boardRows);

// pathsDelimited：多张标定图路径，分号分隔；至少需 3 张成功检出棋盘格的图像
// squareSize：方格边长（与 world 单位一致，常用 mm）
// 使用 cv::calibrateCamera 优化求解内参与畸变，结果为标定计算值，非从相机读取
// fullCalibrationJsonOut：可选；写入 UTF-8 JSON（含 intrinsics 与每视图外参 rvec/tvec，OpenCV board→camera）。
// 缓冲区不足时写入空串（首字节 0），内参仍有效。
int CalibrateCameraChessboardMultiview(const char* pathsDelimited, int boardCols, int boardRows, double squareSize,
    double* outFx, double* outFy, double* outCx, double* outCy,
    double* outK1, double* outK2, double* outP1, double* outP2, double* outK3,
    double* outRms,
    char* fullCalibrationJsonOut, int fullCalibrationJsonOutSize);

// 像素 → 棋盘平面 XY（Z=0），长度单位与标定时 squareSize 一致（如 mm）；畸变先 undistort，再与 extrinsics 求射线与棋盘平面交点
int PixelsToChessboardPlaneXY(double fx, double fy, double cx, double cy,
    double k1, double k2, double p1, double p2, double k3,
    const double rvec[3], const double tvec[3],
    const Point2D* pixels, Point2D* worldXYOut, int count);

// 从 CalibrateCameraChessboardMultiview 输出的 CalibrationJson 解析内参与 extrinsicsPerView[viewIndex]
int PixelsToChessboardPlaneXYFromCalibrationJson(const char* calibrationJsonUtf8, int viewIndex,
    const Point2D* pixels, Point2D* worldXYOut, int count);

// ========== 标定 ==========

// 9 点仿射标定（cv::solve SVD 求解超定方程组）
int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans);

// 像素坐标 → 世界坐标
Point2D ImageToWorld(Point2D pixel, AffineTransform trans);

// 计算重投影误差
void CalculateError(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform trans, double* avgErr, double* maxErr);

// ========== 轨迹检测 ==========

// 自实现梯度轨迹检测（纯 C，不依赖 OpenCV）
// 注意：此函数内部使用全局 g_transform，调用前需先标定
// 暂保留内部 extern 声明方式
void DetectTrajectory(Image* img, Point2D* trajPixels, int* count);

// OpenCV 16 暗条轨迹检测（等间距轮廓采样 + mask 验证）
// stepImages: 输出 6 步中间结果图（调用者负责释放 data）
// stepBarIds: 输出每个轨迹点所属的暗条编号
// stepImageCount: 固定为 6
// useContourMode: true=Canvas 轮廓等弧长采样，false=膨胀+网格采样（旧方案）
// fitMode: 0=stadium跑道型拟合(默认), 1=曲率去噪(删除局部曲率异常大的点)
// approxEpsilon: fitMode=1时未使用(保留参数供未来扩展)
void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount,
                            bool useContourMode = false, int fitMode = 0, double approxEpsilon = 0.0);

// ========== DetectTrajectoryOpenCV 分步骤接口 ==========

// 1. 图像转灰度
void Step_ConvertToGrayscale(Image* src, cv::Mat* dstGray);

// 1.5（可选）. 分水岭预分割
void Step_WatershedPresegment(const cv::Mat& grayMat, cv::Mat* step2Output, bool enableWatershed);

// 1b（可选）. CLAHE 对比度受限自适应直方图均衡化
void Step_ApplyCLAHE(cv::Mat* grayMat, double clipLimit, int tileGridSize);

// 2. 高斯模糊 + OTSU二值化 + 形态学处理 + 提取外轮廓
// minArea: 最小轮廓面积阈值，小于此值的轮廓被过滤掉 (0=不过滤)
void Step_PreprocessAndFindContours(cv::Mat* grayMat, cv::Mat* binaryBright, 
                                     cv::Mat* morphed, cv::Mat* mask, cv::Mat* coloredMask,
                                     std::vector<std::vector<cv::Point>>* outerContours,
                                     int blurKsize, int morphKernelSize, bool enableWatershed = false, double minArea = 0);

// 2b（可选）. Canny 边缘检测，输出边缘图
// grayMat: 输入灰度图
// edgeMap: 输出边缘图（CV_8UC1，边缘=255，非边缘=0）
// lowThreshold: Canny 低阈值（0=自适应，使用 median * 0.66）
// highThreshold: Canny 高阈值（0=自适应，使用 lowThreshold * 2）
// blurKsize: 边缘检测前高斯模糊核大小（0=不模糊，>0=先模糊降噪）
void Step_DetectCannyEdges(const cv::Mat& grayMat, cv::Mat* edgeMap,
                            double lowThreshold, double highThreshold, int blurKsize,
                            int bilateralD = 0, double bilateralSigmaColor = 75, double bilateralSigmaSpace = 75,
                            int nlmeansH = 0, int nlmeansTemplateSize = 7, int nlmeansSearchSize = 21);

// 2.5 生成 Mask：从外轮廓中找到面积最大的轮廓，填充内部区域
// outerContours: Step2 输出的外轮廓列表
// mask: 输出 mask（最大轮廓内部=255，其余=0）
void Step_CreateMaskFromLargestContour(const std::vector<std::vector<cv::Point>>& outerContours,
                                        int width, int height, cv::Mat* mask, int contourIdx = -1);

// 3. 根据外轮廓生成工件mask
void Step_CreateWorkpieceMask(std::vector<std::vector<cv::Point>>* outerContours,
                               int maxIdx, int width, int height, cv::Mat* mask);

// 4. 暗条二值化 + 外围跑道区域提取
// darkThreshold: 暗条上限阈值（灰度 >= darkThreshold 不算暗条）
// darkMinThreshold: 暗条下限阈值（灰度 < darkMinThreshold 为纯黑背景噪声，排除），0=不过滤
// outerThreshold: 外围跑道阈值（darkThreshold <= gray < outerThreshold 为外围区域，0=自适应Otsu）
// darkBinary: 输出暗条二值化结果
// outerMask: 输出外围跑道区域 mask
void Step_DetectDarkBars(cv::Mat* grayMat, cv::Mat* mask, int darkThreshold,
                          int darkMinThreshold, int outerThreshold,
                          cv::Mat* darkBinary, cv::Mat* outerMask);

// 5. 形态学清理（闭-开-开-闭 + 膨胀腐蚀）
void Step_MorphologyCleanup(cv::Mat* darkBinary, int kernelSize, int blurKsize, double blurSigma);

// 5b. 扩展到边缘边界（可选，Canny 辅助）
void Step_ExpandToEdgeBoundary(cv::Mat* darkBinary, const cv::Mat& edgeMap,
                                int expandPixels, const cv::Mat& mask,
                                cv::Mat* expandedMask);

// 6. 提取并按面积排序暗条轮廓，返回有效暗条数量
int Step_FindAndSortDarkContours(cv::Mat* darkBinary, int width, int height,
                                  std::vector<std::pair<double, int>>* sortedBars,
                                  std::vector<std::vector<cv::Point>>* darkContours);


// 7. 等间距轮廓采样（纯 OpenCV 方案）
// spacing: 采样间距（像素），沿每条暗条轮廓等弧长采样（建议 2.0~5.0）
void Step_SampleContoursEquidistant(std::vector<std::vector<cv::Point>>* darkContours,
                                     std::vector<std::pair<double, int>>* sortedBars,
                                     int targetBars, int width, int height,
                                     std::vector<cv::Point>* allPoints,
                                     std::vector<int>* allBarIds,
                                     double spacing = 3.0);

// 独立轮廓采样：从二值图直接 findContours + 排序 + 等弧长采样，不依赖 stepDetector
// binaryData: 二值图数据（width*height, 0/255）
// 返回采样点数（<= maxPoints），通过 outPoints 输出
int SampleContoursFromBinary(const unsigned char* binaryData, int width, int height,
                              int targetBars, double spacing, int minArea,
                              Point2D* outPoints, int* outBarIds, int maxPoints);

// 从已有的轮廓点数据做等弧长采样（独立函数，不依赖 stepDetector 上下文）
// flatPoints: 扁平化的轮廓点数组，所有轮廓的点依次排列
// contourLengths: 每条轮廓的点数，数组长度 = numContours
// numContours: 轮廓数量
// targetBars: 取前N条轮廓做采样
// spacing: 等弧长采样间距
// outPoints, outBarIds: 输出采样点和对应的 barId
// maxPoints: 输出缓冲区最大点数
// 返回实际采样点数
int SampleContoursFromPoints(const int* flatX, const int* flatY,
                             const int* contourLengths, int numContours,
                             int targetBars, int width, int height,
                             double spacing,
                             Point2D* outPoints, int* outBarIds, int maxPoints);

// 按barId分组，对每条暗条的采样点做分段形状拟合
// fitMode: 0=stadium跑道型拟合(默认), 1=曲率去噪(删除局部曲率异常大的点)
// approxEpsilon: fitMode=1时未使用(保留参数供未来扩展)
void Step_FitShape(std::vector<cv::Point>* allPoints, std::vector<int>* allBarIds,
                   int width, int height, int fitMode = 0, double approxEpsilon = 0.0);

// 8. Mask验证采样点
void Step_VerifyByMask(std::vector<cv::Point>* allPoints, std::vector<int>* allBarIds,
                       cv::Mat* darkBinary, int width, int height);

// 9. 去重 + 按Y/X排序
void Step_DeduplicateAndSort(std::vector<cv::Point>* points, std::vector<int>* barIds);

// 10. 转换为轨迹点输出
void Step_ConvertToOutput(std::vector<cv::Point>* uniquePoints, 
                          std::vector<int>* uniqueBarIds,
                          Point2D* trajPixels, int* count, int* stepBarIds);

// 11. 绘制彩色轨迹
void Step_DrawColoredTrajectory(Point2D* trajPixels, int count, int* stepBarIds,
                                 int width, int height, unsigned char* outputData);

// ========== 空洞检测轨迹（新方案） ==========

// 空洞轨迹检测：二值化→最大轮廓mask→反求空洞→灰度过渡合并→采样
// 算法流程：
// 1. 高斯模糊 + OTSU 二值化，取最大轮廓作为工件 mask
// 2. 在 mask 内部用 RETR_CCOMP 反求空洞（hierarchy 的子轮廓）
// 3. 按面积排序取前 targetHollows 个最大空洞
// 4. 对每个空洞边缘区域，将灰度值介于空洞内部均值和外部均值之间的像素也归入空洞
//    （灰度过渡区域包容合并，让空洞边界更贴合真实边缘）
// 5. 形态学清理 → 等间距轮廓采样 → 拟合 → 验证 → 去重 → 输出
//
// stepImages: 输出中间结果图（调用者负责释放 data）
// stepBarIds: 输出每个轨迹点所属的空洞编号
// stepImageCount: 固定为 5 (灰度/二值化/mask/空洞二值化/窄带可视化)
// blurKsize: 高斯模糊核大小
// morphKernelSize: 形态学核大小
// targetHollows: 目标空洞数量（默认16）
// bandWidth: 窄带采样宽度(像素)，0=原始轮廓采样，>0=空洞外侧窄带采样
// useContourMode: true=Canvas 轮廓等弧长采样，false=膨胀+网格采样
// outerExpandPixels: Canvas 模式外围膨胀半径(像素)
// grayMergeRatio: 灰度过渡合并强度（0.0~1.0），0=不合并，默认0.3
// hollowGrayLow: 空洞灰度下限（默认5），mask 内灰度低于此值的像素被排除
// hollowGrayHigh: 空洞灰度上限（默认50），mask 内灰度高于此值的像素被排除
void DetectHollowTrajectory(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount,
                            int blurKsize = 7, int morphKernelSize = 5,
                            int targetHollows = 16, int bandWidth = 8,
                            bool useContourMode = true, int outerExpandPixels = 25,
                            double grayMergeRatio = 0.3,
                            int hollowGrayLow = 5, int hollowGrayHigh = 50);

// 委托接口（兼容旧代码）
void DetectTrajectoryFitShape(Image* img, Point2D* trajPixels, int* count);

// ========== 轨迹绘制 ==========

// 在 3 通道图像上绘制彩色轨迹
void DrawTrajectoryColored(Image* img, Point2D* trajPixels, int count, int* barIds);

// 在单通道图像上绘制灰度轨迹
void DrawTrajectoryGrayscale(Image* img, Point2D* trajPixels, int count, int grayValue);

// ========== 图像工具 ==========

// OpenCV Mat 转 Image（1通道灰度或3通道BGR → 3通道BGR Image）
void MatToImageBGR(const cv::Mat& src, Image* dst);

// ========== 常量 ==========

#define CALIB_IMAGE_WIDTH   2448
#define CALIB_IMAGE_HEIGHT  2048
#define CALIB_MAX_TRAJ_POINTS 50000
#define CALIB_MAX_BARS 16

// 16 种高饱和度颜色（BGR格式）
extern const unsigned char BAR_COLORS[16][3];

#endif // CALIB_OPERATOR_H
