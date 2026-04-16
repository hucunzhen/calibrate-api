/**
 * 九点标定 - 数学逻辑测试版（无需算法库DLL）
 * 用于验证仿射变换计算逻辑
 */

#include <iostream>
#include <vector>
#include <cmath>
#include <iomanip>
#include <fstream>
#include <algorithm>
#include <ctime>

using namespace std;

// 简化版坐标点（不依赖XV库）
struct Point2D {
    float x;
    float y;
};

struct AffineTransform {
    float ax, bx, cx;  // X = ax*x + bx*y + cx
    float ay, by, cy;  // Y = ay*x + by*y + cy
};

// 使用矩阵法求解仿射变换(最小二乘法)
float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) {
        cerr << "Error: 至少需要3个点，当前点数: " << n << endl;
        return -1;
    }

    double ATA[6][6] = {0};
    double ATB[6] = {0};

    for (int i = 0; i < n; i++) {
        double xi = imagePts[i].x;
        double yi = imagePts[i].y;
        double Xi = worldPts[i].x;
        double Yi = worldPts[i].y;

        ATA[0][0] += xi * xi;    ATA[0][1] += xi * yi;    ATA[0][2] += xi;
        ATA[1][0] += xi * yi;    ATA[1][1] += yi * yi;    ATA[1][2] += yi;
        ATA[2][0] += xi;         ATA[2][1] += yi;          ATA[2][2] += n;

        ATA[3][3] += xi * xi;    ATA[3][4] += xi * yi;    ATA[3][5] += xi;
        ATA[4][3] += xi * yi;    ATA[4][4] += yi * yi;    ATA[4][5] += yi;
        ATA[5][3] += xi;         ATA[5][4] += yi;          ATA[5][5] += n;

        ATB[0] += Xi * xi;        ATB[1] += Xi * yi;        ATB[2] += Xi;
        ATB[3] += Yi * xi;        ATB[4] += Yi * yi;        ATB[5] += Yi;
    }

    double X[6] = {0};

    for (int k = 0; k < 6; k++) {
        int maxRow = k;
        for (int i = k + 1; i < 6; i++) {
            if (fabs(ATA[i][k]) > fabs(ATA[maxRow][k])) {
                maxRow = i;
            }
        }
        if (maxRow != k) {
            for (int j = 0; j < 6; j++) swap(ATA[k][j], ATA[maxRow][j]);
            swap(ATB[k], ATB[maxRow]);
        }

        if (fabs(ATA[k][k]) < 1e-10) continue;

        for (int i = k + 1; i < 6; i++) {
            double factor = ATA[i][k] / ATA[k][k];
            for (int j = k; j < 6; j++) {
                ATA[i][j] -= factor * ATA[k][j];
            }
            ATB[i] -= factor * ATB[k];
        }
    }

    for (int i = 5; i >= 0; i--) {
        if (fabs(ATA[i][i]) < 1e-10) {
            X[i] = 0;
            continue;
        }
        double sum = ATB[i];
        for (int j = i + 1; j < 6; j++) {
            sum -= ATA[i][j] * X[j];
        }
        X[i] = sum / ATA[i][i];
    }

    transform.ax = (float)X[0];  transform.bx = (float)X[1];  transform.cx = (float)X[2];
    transform.ay = (float)X[3];  transform.by = (float)X[4];  transform.cy = (float)X[5];

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

Point2D imageToWorld(Point2D pixelPt, const AffineTransform& transform) {
    Point2D worldPt;
    worldPt.x = transform.ax * pixelPt.x + transform.bx * pixelPt.y + transform.cx;
    worldPt.y = transform.ay * pixelPt.x + transform.by * pixelPt.y + transform.cy;
    return worldPt;
}

Point2D worldToImage(Point2D worldPt, const AffineTransform& transform) {
    Point2D pixelPt = {0, 0};
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

void printTransform(const AffineTransform& transform) {
    cout << "======================================" << endl;
    cout << "        仿射变换参数" << endl;
    cout << "======================================" << endl;
    cout << fixed << setprecision(6);
    cout << "X = " << transform.ax << " * x + " << transform.bx << " * y + " << transform.cx << endl;
    cout << "Y = " << transform.ay << " * x + " << transform.by << " * y + " << transform.cy << endl;
    cout << "======================================" << endl;
}

int main() {
    srand((unsigned int)time(nullptr));

    cout << "========================================" << endl;
    cout << "      九点标定数学逻辑测试" << endl;
    cout << "========================================" << endl;

    // 假设物理坐标(3x3网格, 间距10mm, 原点在左下角)
    vector<Point2D> worldPts = {
        {0, 20}, {10, 20}, {20, 20},   // 第1行
        {0, 10}, {10, 10}, {20, 10},   // 第2行
        {0, 0},  {10, 0},  {20, 0}     // 第3行
    };

    // 模拟相机像素坐标: X = 500 + 10*worldX, Y = 300 + 10*(20-worldY)
    vector<Point2D> imagePts;
    for (size_t i = 0; i < worldPts.size(); i++) {
        Point2D p;
        p.x = 500 + worldPts[i].x * 10.0f;
        p.y = 300 + (20.0f - worldPts[i].y) * 10.0f;
        // 添加噪声模拟误差
        p.x += (rand() % 10 - 5) * 0.3f;
        p.y += (rand() % 10 - 5) * 0.3f;
        imagePts.push_back(p);
    }

    cout << "\n========== 模拟数据测试 ==========" << endl;

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
    AffineTransform transform;
    float residual = solveAffineWithMatrix(imagePts, worldPts, transform);
    printTransform(transform);

    // 计算重投影误差
    cout << "\n重投影误差分析:" << endl;
    cout << setw(3) << "序"
         << setw(12) << "理论X" << setw(12) << "理论Y"
         << setw(12) << "计算X" << setw(12) << "计算Y"
         << setw(12) << "误差X" << setw(12) << "误差Y"
         << setw(12) << "距离误差" << endl;

    double maxError = 0, sumError = 0;
    for (size_t i = 0; i < imagePts.size(); i++) {
        Point2D calc = imageToWorld(imagePts[i], transform);
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

    // 坐标转换测试
    cout << "\n========== 坐标转换测试 ==========" << endl;
    Point2D testPixel = {620.5f, 350.3f};
    cout << "\n像素坐标 -> 物理坐标:" << endl;
    cout << "  输入像素: (" << testPixel.x << ", " << testPixel.y << ")" << endl;

    Point2D worldCoord = imageToWorld(testPixel, transform);
    cout << "  输出物理: (" << worldCoord.x << ", " << worldCoord.y << ") mm" << endl;

    // 逆变换验证
    cout << "\n逆变换验证:" << endl;
    Point2D backPixel = worldToImage(worldCoord, transform);
    cout << "  物理: (" << worldCoord.x << ", " << worldCoord.y << ")" << endl;
    cout << "  恢复像素: (" << backPixel.x << ", " << backPixel.y << ")" << endl;
    cout << "  误差: (" << (testPixel.x - backPixel.x) << ", " << (testPixel.y - backPixel.y) << ")" << endl;

    // 保存参数
    ofstream fout("calibration_params.txt");
    fout << "# 九点标定参数" << endl;
    fout << fixed << setprecision(8);
    fout << "ax=" << transform.ax << endl;
    fout << "bx=" << transform.bx << endl;
    fout << "cx=" << transform.cx << endl;
    fout << "ay=" << transform.ay << endl;
    fout << "by=" << transform.by << endl;
    fout << "cy=" << transform.cy << endl;
    fout.close();
    cout << "\n标定参数已保存到: calibration_params.txt" << endl;

    cout << "\n========================================" << endl;
    cout << "      测试完成" << endl;
    cout << "========================================" << endl;

    return 0;
}
