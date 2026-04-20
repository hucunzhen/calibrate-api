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

// ========== 圆检测 ==========

// Contour + Crosshair 圆检测（适用于 9 点标定图）
// 支持 OTSU + adaptive fallback 策略
void DetectCircles(Image* img, Point2D* pts, int* count);

// 在图像上绘制检测到的圆标记
void DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray);

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
void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount);

// ========== DetectTrajectoryOpenCV 分步骤接口 ==========

// 1. 图像转灰度
void Step_ConvertToGrayscale(Image* src, cv::Mat* dstGray);

// 2. 高斯模糊 + OTSU二值化 + 形态学处理 + 提取外轮廓
void Step_PreprocessAndFindContours(cv::Mat* grayMat, cv::Mat* binaryBright, 
                                     cv::Mat* morphed, cv::Mat* mask, cv::Mat* coloredMask,
                                     std::vector<std::vector<cv::Point>>* outerContours);

// 3. 根据外轮廓生成工件mask
void Step_CreateWorkpieceMask(std::vector<std::vector<cv::Point>>* outerContours,
                               int maxIdx, int width, int height, cv::Mat* mask);

// 4. 暗条二值化
void Step_DetectDarkBars(cv::Mat* grayMat, cv::Mat* mask, int threshold,
                          cv::Mat* darkBinary);

// 5. 形态学清理
void Step_MorphologyCleanup(cv::Mat* darkBinary, int kernelSize);

// 6. 提取暗条轮廓并按面积排序，返回找到的暗条数量
int Step_FindAndSortDarkContours(cv::Mat* darkBinary, int width, int height,
                                   std::vector<std::pair<double, int>>* sortedBars,
                                   std::vector<std::vector<cv::Point>>* darkContours);

// 7. 等间距轮廓采样
void Step_SampleContoursEquidistant(std::vector<std::vector<cv::Point>>* darkContours,
                                     std::vector<std::pair<double, int>>* sortedBars,
                                     int targetBars, int width, int height,
                                     std::vector<cv::Point>* allPoints,
                                     std::vector<int>* allBarIds);

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
