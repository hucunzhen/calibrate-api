/**
 * 九点标定完整示例
 * 
 * 功能: 建立图像坐标到物理坐标的仿射变换映射
 * 输入: 9个已知物理坐标的圆点图像
 * 输出: 仿射变换参数及误差
 */

#include <iostream>
#include <vector>
#include <cmath>
#include <iomanip>
#include <fstream>
#include <algorithm>
#include <ctime>

// 包含算法库头文件
#include "BlobAnalysis.h"
#include "FitShape.h"
#include "Preprocess.h"
#include "XVBase.h"
#include "XV2DBase.h"

using namespace std;
using namespace XVL;

//==============================================================================
// 第一部分: 数学工具 - 仿射变换求解
//==============================================================================

struct AffineTransform {
    float ax, bx, cx;  // X = ax*x + bx*y + cx
    float ay, by, cy;  // Y = ay*x + by*y + cy
};

/**
 * 使用矩阵法求解仿射变换(最小二乘法)
 * 方程组:
 *   X = ax*x + bx*y + cx
 *   Y = ay*x + by*y + cy
 */
float solveAffineWithMatrix(const vector<XVPoint2D>& imagePts,
                           const vector<XVPoint2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) {
        cerr << "Error: 至少需要3个点，当前点数: " << n << endl;
        return -1;
    }

    // 使用均值中心化坐标以提高数值稳定性
    double mean_xi = 0, mean_yi = 0, mean_Xi = 0, mean_Yi = 0;
    for (int i = 0; i < n; i++) {
        mean_xi += imagePts[i].x;
        mean_yi += imagePts[i].y;
        mean_Xi += worldPts[i].x;
        mean_Yi += worldPts[i].y;
    }
    mean_xi /= n; mean_yi /= n; mean_Xi /= n; mean_Yi /= n;

    // 构建法方程: ATA * params = ATB
    // [x y 1 0 0 0] [ax]   [X]
    // [0 0 0 x y 1] [bx] = [Y]
    //                [cx]
    //                [ay]
    //                [by]
    //                [cy]

    // 初始化矩阵元素 (使用double提高精度)
    double ATA[6][6] = {0};
    double ATB[6] = {0};

    for (int i = 0; i < n; i++) {
        // 中心化坐标
        double xi = imagePts[i].x - mean_xi;
        double yi = imagePts[i].y - mean_yi;
        double Xi = worldPts[i].x - mean_Xi;
        double Yi = worldPts[i].y - mean_Yi;

        // 填充 ATA 矩阵 (X方程)
        ATA[0][0] += xi * xi;    ATA[0][1] += xi * yi;    ATA[0][2] += xi;
        ATA[1][0] += xi * yi;    ATA[1][1] += yi * yi;    ATA[1][2] += yi;
        ATA[2][0] += xi;         ATA[2][1] += yi;          ATA[2][2] += n;

        // 填充 ATA 矩阵 (Y方程)
        ATA[3][3] += xi * xi;    ATA[3][4] += xi * yi;    ATA[3][5] += xi;
        ATA[4][3] += xi * yi;    ATA[4][4] += yi * yi;    ATA[4][5] += yi;
        ATA[5][3] += xi;         ATA[5][4] += yi;          ATA[5][5] += n;

        // 填充 ATB 向量
        ATB[0] += Xi * xi;        ATB[1] += Xi * yi;        ATB[2] += Xi;
        ATB[3] += Yi * xi;        ATB[4] += Yi * yi;        ATB[5] += Yi;
    }

    // 高斯消元求解
    double X[6] = {0};

    // 前代消去
    for (int k = 0; k < 6; k++) {
        // 找主元
        int maxRow = k;
        for (int i = k + 1; i < 6; i++) {
            if (fabs(ATA[i][k]) > fabs(ATA[maxRow][k])) {
                maxRow = i;
            }
        }
        // 交换行
        if (maxRow != k) {
            for (int j = 0; j < 6; j++) swap(ATA[k][j], ATA[maxRow][j]);
            swap(ATB[k], ATB[maxRow]);
        }

        if (fabs(ATA[k][k]) < 1e-10) continue;

        // 消去
        for (int i = k + 1; i < 6; i++) {
            double factor = ATA[i][k] / ATA[k][k];
            for (int j = k; j < 6; j++) {
                ATA[i][j] -= factor * ATA[k][j];
            }
            ATB[i] -= factor * ATB[k];
        }
    }

    // 回代求解
    for (int i = 5; i >= 0; i--) {
        if (fabs(ATA[i][i]) < 1e-10) {
            X[i] = 0;
            continue;
        }
        double         sum = ATB[i];
        for (int j = i + 1; j < 6; j++) {
            sum -= ATA[i][j] * X[j];
        }
        X[i] = sum / ATA[i][i];
    }

    // 转换回原始坐标: cx = mean_Xi - ax*mean_xi - bx*mean_yi
    transform.ax = (float)X[0];
    transform.bx = (float)X[1];
    transform.cx = (float)(mean_Xi - X[0]*mean_xi - X[1]*mean_yi);
    transform.ay = (float)X[3];
    transform.by = (float)X[4];
    transform.cy = (float)(mean_Yi - X[3]*mean_xi - X[4]*mean_yi);

    // 计算残差
    double residual = 0;
    for (int i = 0; i < n; i++) {
        double Xp = transform.ax * imagePts[i].x + transform.bx * imagePts[i].y + transform.cx;
        double Yp = transform.ay * imagePts[i].x + transform.by * imagePts[i].y + transform.cy;
        double dx = Xp - worldPts[i].x;
        double dy = Yp - worldPts[i].y;
        residual += sqrt(dx * dx + dy * dy);
    }

    return (float)(residual / n);
}

/**
 * 图像坐标转物理坐标
 */
XVPoint2D imageToWorld(XVPoint2D pixelPt, const AffineTransform& transform) {
    XVPoint2D worldPt;
    worldPt.x = transform.ax * pixelPt.x + transform.bx * pixelPt.y + transform.cx;
    worldPt.y = transform.ay * pixelPt.x + transform.by * pixelPt.y + transform.cy;
    return worldPt;
}

/**
 * 物理坐标转图像坐标(逆变换)
 */
XVPoint2D worldToImage(XVPoint2D worldPt, const AffineTransform& transform) {
    XVPoint2D pixelPt = {0, 0};  // 默认初始化
    // 求解逆矩阵
    float det = transform.ax * transform.by - transform.ay * transform.bx;
    if (fabs(det) < 1e-6f) {
        cerr << "Error: 变换矩阵奇异，无法求逆" << endl;
        return pixelPt;
    }
    float invDet = 1.0f / det;
    float dx = worldPt.x - transform.cx;
    float dy = worldPt.y - transform.cy;
    pixelPt.x = invDet * (transform.by * dx - transform.bx * dy);
    pixelPt.y = invDet * (-transform.ay * dx + transform.ax * dy);
    return pixelPt;
}

/**
 * 打印变换参数
 */
void printTransform(const AffineTransform& transform) {
    cout << "======================================" << endl;
    cout << "        仿射变换参数" << endl;
    cout << "======================================" << endl;
    cout << fixed << setprecision(6);
    cout << "X = " << transform.ax << " * x + " << transform.bx << " * y + " << transform.cx << endl;
    cout << "Y = " << transform.ay << " * x + " << transform.by << " * y + " << transform.cy << endl;
    cout << "======================================" << endl;
}


//==============================================================================
// 第二部分: 圆心检测
//==============================================================================

/**
 * 灰度转换辅助函数
 */
bool convertToGray(const XVImage* inputImage, XVImage& grayImage) {
    if (inputImage->depth == 1) {
        grayImage = *inputImage;
        return true;
    }

    XVRgbToGrayIn rgb2grayIn;
    rgb2grayIn.inRgbImage = const_cast<XVImage*>(inputImage);
    XVRgbToGrayOut rgb2grayOut;
    XVRgbToGray(rgb2grayIn, rgb2grayOut);

    if (rgb2grayOut.outResult != 1 || rgb2grayOut.outGrayImage == nullptr) {
        cerr << "灰度转换失败" << endl;
        return false;
    }

    grayImage = *rgb2grayOut.outGrayImage;
    return true;
}

/**
 * 使用质心法快速提取圆心(适用于黑色实心圆)
 */
vector<XVPoint2D> extractCircleCentersByBlob(const XVImage* inputImage) {
    vector<XVPoint2D> centers;

    // Step 1: 灰度化
    XVImage grayImage;
    if (!convertToGray(inputImage, grayImage)) {
        return centers;
    }

    // Step 2: 阈值化提取黑色区域
    XVThresholdImageToRegionMonoIn threshIn;
    threshIn.inImage = &grayImage;
    threshIn.inRegion.optional = XVOptionalType::NUL;
    threshIn.inMin = 0.0f;
    threshIn.inMax = 80.0f;
    XVThresholdImageToRegionMonoOut threshOut;
    int ret = XVThresholdImageToRegionMono(threshIn, threshOut);
    if (ret != 0) {
        cerr << "阈值化失败: " << ret << endl;
        return centers;
    }

    // Step 3: 分割连通域
    XVSplitRegionToBlobsIn splitIn;
    splitIn.inRegion = threshOut.outRegion;
    splitIn.inNeighborhood = 8;
    XVSplitRegionToBlobsOut splitOut;
    ret = XVSplitRegionToBlobs(splitIn, splitOut);
    if (ret != 0) {
        cerr << "连通域分割失败: " << ret << endl;
        return centers;
    }

    cout << "检测到 " << splitOut.outRegions.size() << " 个连通域" << endl;

    // Step 4: 对每个区域提取中心
    for (auto& region : splitOut.outRegions) {
        XVRegionCenterIn centerIn;
        centerIn.inRegion = region;
        XVRegionCenterOut centerOut;
        if (XVRegionCenter(centerIn, centerOut) == 0) {
            centers.push_back(centerOut.outCenter);
            cout << "  圆心: (" << centerOut.outCenter.x << ", " << centerOut.outCenter.y << ")" << endl;
        }
    }

    // Step 5: 按位置排序(从左上到右下) - 3x3网格
    if (centers.size() >= 9) {
        // 计算Y方向的分组阈值(中位数)
        vector<float> yValues;
        for (auto& p : centers) yValues.push_back(p.y);
        sort(yValues.begin(), yValues.end());
        float yThreshold1 = yValues[yValues.size() / 3];  // 约1/3位置
        float yThreshold2 = yValues[2 * yValues.size() / 3]; // 约2/3位置

        // 分成3行
        vector<XVPoint2D> row1, row2, row3;
        for (auto& p : centers) {
            if (p.y < yThreshold1) row1.push_back(p);
            else if (p.y < yThreshold2) row2.push_back(p);
            else row3.push_back(p);
        }

        // 每行按X排序
        auto cmpX = [](const XVPoint2D& a, const XVPoint2D& b) { return a.x < b.x; };
        sort(row1.begin(), row1.end(), cmpX);
        sort(row2.begin(), row2.end(), cmpX);
        sort(row3.begin(), row3.end(), cmpX);

        // 合并(取前9个)
        centers.clear();
        for (size_t i = 0; i < row1.size() && centers.size() < 3; i++) centers.push_back(row1[i]);
        for (size_t i = 0; i < row2.size() && centers.size() < 6; i++) centers.push_back(row2[i]);
        for (size_t i = 0; i < row3.size() && centers.size() < 9; i++) centers.push_back(row3[i]);
    }

    return centers;
}

/**
 * 使用圆拟合高精度提取圆心(推荐方法)
 * @param inputImage 输入图像
 * @param expectedRadius 期望的圆半径(像素)
 * @return 圆心列表
 */
vector<XVPoint2D> extractCircleCentersByFit(const XVImage* inputImage, float expectedRadius) {
    vector<XVPoint2D> centers;

    // Step 1: 灰度化
    XVImage grayImage;
    if (!convertToGray(inputImage, grayImage)) {
        return centers;
    }

    // Step 2: 使用Blob分析获取大致圆心位置
    vector<XVPoint2D> roughCenters = extractCircleCentersByBlob(inputImage);

    if (roughCenters.empty()) {
        cerr << "Blob分析未检测到圆" << endl;
        return centers;
    }

    cout << "使用圆拟合精化 " << roughCenters.size() << " 个圆心..." << endl;

    // Step 3: 对每个大致圆心位置进行精确圆拟合
    for (auto& roughCenter : roughCenters) {
        XVDetectSingleCircleIn detectIn;
        detectIn.inImage = &grayImage;
        detectIn.inRoi = NULL;
        detectIn.inRadius = expectedRadius > 0 ? expectedRadius : 20.0f;
        detectIn.inThreshold1 = 80;   // 梯度阈值
        detectIn.inThreshold2 = 10;   // 得分阈值
        detectIn.inDiagFlag = 0;

        XVDetectSingleCircleOut detectOut;
        int ret = XVDetectSingleCircle(detectIn, detectOut);

        if (ret == 0 && detectOut.outScore > 0) {
            centers.push_back(detectOut.outCircle.center);
            cout << "  精化圆心: (" << detectOut.outCircle.center.x << ", "
                 << detectOut.outCircle.center.y << ") 半径: "
                 << detectOut.outCircle.radius << " 得分: " << detectOut.outScore << endl;
        } else {
            // 圆拟合失败，使用质心作为备选
            centers.push_back(roughCenter);
            cout << "  圆拟合失败，使用质心: (" << roughCenter.x << ", " << roughCenter.y << ")" << endl;
        }
    }

    // Step 4: 按位置排序(3x3网格)
    if (centers.size() >= 9) {
        vector<float> yValues;
        for (auto& p : centers) yValues.push_back(p.y);
        sort(yValues.begin(), yValues.end());
        float yThreshold1 = yValues[yValues.size() / 3];
        float yThreshold2 = yValues[2 * yValues.size() / 3];

        vector<XVPoint2D> row1, row2, row3;
        for (auto& p : centers) {
            if (p.y < yThreshold1) row1.push_back(p);
            else if (p.y < yThreshold2) row2.push_back(p);
            else row3.push_back(p);
        }

        auto cmpX = [](const XVPoint2D& a, const XVPoint2D& b) { return a.x < b.x; };
        sort(row1.begin(), row1.end(), cmpX);
        sort(row2.begin(), row2.end(), cmpX);
        sort(row3.begin(), row3.end(), cmpX);

        centers.clear();
        for (size_t i = 0; i < row1.size() && centers.size() < 3; i++) centers.push_back(row1[i]);
        for (size_t i = 0; i < row2.size() && centers.size() < 6; i++) centers.push_back(row2[i]);
        for (size_t i = 0; i < row3.size() && centers.size() < 9; i++) centers.push_back(row3[i]);
    }

    return centers;
}


//==============================================================================
// 第三部分: 标定主程序
//==============================================================================

/**
 * 九点标定
 */
AffineTransform calibrateNinePoint(const vector<XVPoint2D>& imagePts,
                                  const vector<XVPoint2D>& worldPts) {
    AffineTransform transform = {0};

    if (imagePts.size() != 9 || worldPts.size() != 9) {
        cerr << "Error: 需要9个标定点，当前点数: " << imagePts.size() << endl;
        return transform;
    }

    cout << "\n========== 九点标定开始 ==========" << endl;

    cout << "\n输入像素坐标:" << endl;
    cout << setw(3) << "序号" << setw(15) << "X(像素)" << setw(15) << "Y(像素)" << endl;
    for (size_t i = 0; i < imagePts.size(); i++) {
        cout << setw(3) << (i + 1)
             << setw(15) << fixed << setprecision(2) << imagePts[i].x
             << setw(15) << imagePts[i].y << endl;
    }

    cout << "\n输入物理坐标:" << endl;
    cout << setw(3) << "序号" << setw(15) << "X(mm)" << setw(15) << "Y(mm)" << endl;
    for (size_t i = 0; i < worldPts.size(); i++) {
        cout << setw(3) << (i + 1)
             << setw(15) << fixed << setprecision(3) << worldPts[i].x
             << setw(15) << worldPts[i].y << endl;
    }

    // 求解变换参数
    float residual = solveAffineWithMatrix(imagePts, worldPts, transform);
    printTransform(transform);

    // 计算每个点的重投影误差
    cout << "\n重投影误差分析:" << endl;
    cout << setw(3) << "序" 
         << setw(12) << "理论X" << setw(12) << "理论Y"
         << setw(12) << "计算X" << setw(12) << "计算Y"
         << setw(12) << "误差X" << setw(12) << "误差Y"
         << setw(12) << "距离误差" << endl;

    double maxError = 0, sumError = 0;
    for (size_t i = 0; i < imagePts.size(); i++) {
        XVPoint2D calc = imageToWorld(imagePts[i], transform);
        double dx = calc.x - worldPts[i].x;
        double dy = calc.y - worldPts[i].y;
        double dist = sqrt(dx * dx + dy * dy);
        maxError = max(maxError, dist);
        sumError += dist;

        cout << setw(3) << (i + 1)
             << setw(12) << fixed << setprecision(3) << worldPts[i].x
             << setw(12) << worldPts[i].y
             << setw(12) << calc.x
             << setw(12) << calc.y
             << setw(12) << dx
             << setw(12) << dy
             << setw(12) << dist << endl;
    }

    cout << fixed << setprecision(4);
    cout << "\n统计信息:" << endl;
    cout << "  平均重投影误差: " << (sumError / imagePts.size()) << " mm" << endl;
    cout << "  最大重投影误差: " << maxError << " mm" << endl;
    cout << "\n========== 九点标定完成 ==========" << endl;

    return transform;
}

/**
 * 保存标定参数到文件
 */
void saveCalibration(const AffineTransform& transform, const char* filename) {
    ofstream fout(filename);
    if (!fout.is_open()) {
        cerr << "无法打开文件: " << filename << endl;
        return;
    }

    fout << "# 九点标定参数" << endl;
    fout << "# 仿射变换模型: X = ax*x + bx*y + cx" << endl;
    fout << "#                  Y = ay*x + by*y + cy" << endl;
    fout << fixed << setprecision(8);
    fout << "ax=" << transform.ax << endl;
    fout << "bx=" << transform.bx << endl;
    fout << "cx=" << transform.cx << endl;
    fout << "ay=" << transform.ay << endl;
    fout << "by=" << transform.by << endl;
    fout << "cy=" << transform.cy << endl;

    fout.close();
    cout << "标定参数已保存到: " << filename << endl;
}

/**
 * 从文件加载标定参数
 */
bool loadCalibration(const char* filename, AffineTransform& transform) {
    ifstream fin(filename);
    if (!fin.is_open()) {
        cerr << "无法打开文件: " << filename << endl;
        return false;
    }

    string line;
    while (getline(fin, line)) {
        if (line.empty() || line[0] == '#') continue;

        size_t pos = line.find('=');
        if (pos == string::npos) continue;

        string key = line.substr(0, pos);
        float value = (float)atof(line.substr(pos + 1).c_str());

        if (key == "ax") transform.ax = value;
        else if (key == "bx") transform.bx = value;
        else if (key == "cx") transform.cx = value;
        else if (key == "ay") transform.ay = value;
        else if (key == "by") transform.by = value;
        else if (key == "cy") transform.cy = value;
    }

    fin.close();
    return true;
}


//==============================================================================
// 第四部分: 主程序
//==============================================================================

int main() {
    // 初始化随机数种子
    srand((unsigned int)time(nullptr));

    cout << "========================================" << endl;
    cout << "      九点标定示例程序" << endl;
    cout << "========================================" << endl;

    //==========================================================================
    // 示例1: 模拟数据测试
    //==========================================================================
    cout << "\n\n========== 示例1: 模拟数据测试 ==========" << endl;

    // 假设物理坐标(3x3网格, 间距10mm, 原点在左下角)
    vector<XVPoint2D> worldPts = {
        {0, 20}, {10, 20}, {20, 20},   // 第1行
        {0, 10}, {10, 10}, {20, 10},   // 第2行
        {0, 0},  {10, 0},  {20, 0}     // 第3行
    };

    // 模拟相机像素坐标: X = 500 + 10*worldX, Y = 300 + 10*(20-worldY)
    // (图像坐标系Y向下，物理坐标系Y向上)
    vector<XVPoint2D> imagePts;
    for (size_t i = 0; i < worldPts.size(); i++) {
        XVPoint2D p;
        p.x = 500 + worldPts[i].x * 10.0f;
        p.y = 300 + (20.0f - worldPts[i].y) * 10.0f;
        // 添加噪声模拟误差
        p.x += (rand() % 10 - 5) * 0.3f;
        p.y += (rand() % 10 - 5) * 0.3f;
        imagePts.push_back(p);
    }

    // 执行标定
    AffineTransform transform = calibrateNinePoint(imagePts, worldPts);

    // 保存参数
    saveCalibration(transform, "calibration_params.txt");

    //==========================================================================
    // 示例2: 坐标转换测试
    //==========================================================================
    cout << "\n\n========== 示例2: 坐标转换测试 ==========" << endl;

    // 加载参数
    AffineTransform loadedTransform;
    if (loadCalibration("calibration_params.txt", loadedTransform)) {
        printTransform(loadedTransform);

        // 测试点
        XVPoint2D testPixel = {620.5f, 350.3f};
        cout << "\n像素坐标 -> 物理坐标:" << endl;
        cout << "  输入像素: (" << testPixel.x << ", " << testPixel.y << ")" << endl;

        XVPoint2D worldCoord = imageToWorld(testPixel, loadedTransform);
        cout << "  输出物理: (" << worldCoord.x << ", " << worldCoord.y << ") mm" << endl;

        // 逆变换验证
        cout << "\n逆变换验证:" << endl;
        XVPoint2D backPixel = worldToImage(worldCoord, loadedTransform);
        cout << "  物理: (" << worldCoord.x << ", " << worldCoord.y << ")" << endl;
        cout << "  恢复像素: (" << backPixel.x << ", " << backPixel.y << ")" << endl;
        cout << "  误差: (" << (testPixel.x - backPixel.x) << ", " << (testPixel.y - backPixel.y) << ")" << endl;
    }

    //==========================================================================
    // 示例3: 实际使用流程说明
    //==========================================================================
    cout << "\n\n========== 示例3: 实际使用流程 ==========" << endl;
    cout << R"(
    // 1. 采集标定板图像
    // XVImage* calibImage = captureImage();
    
    // 2. 提取圆心像素坐标
    // vector<XVPoint2D> imagePts = extractCircleCenters(calibImage);
    
    // 3. 输入已知的物理坐标(根据标定板规格)
    // vector<XVPoint2D> worldPts = {
    //     {0, 20}, {10, 20}, {20, 20},
    //     {0, 10}, {10, 10}, {20, 10},
    //     {0, 0},  {10, 0},  {20, 0}
    // };
    
    // 4. 执行标定
    // AffineTransform transform = calibrateNinePoint(imagePts, worldPts);
    
    // 5. 保存标定参数
    // saveCalibration(transform, "calibration_params.txt");
    
    // 6. 加载标定参数
    // AffineTransform transform;
    // loadCalibration("calibration_params.txt", transform);
    
    // 7. 坐标转换
    // XVPoint2D pixel = detectTarget(image);
    // XVPoint2D world = imageToWorld(pixel, transform);
    // printf("物理位置: (%.3f, %.3f) mm\n", world.x, world.y);
    )" << endl;

    cout << "\n========================================" << endl;
    cout << "      程序结束" << endl;
    cout << "========================================" << endl;

    return 0;
}
