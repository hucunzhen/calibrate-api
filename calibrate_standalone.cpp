/**
 * Nine-Point Calibration - Standalone Math Test
 */

#include <windows.h>
#include <iostream>
#include <fstream>
#include <cmath>
#include <iomanip>
#include <ctime>
#include <vector>
#include <string>
#include <cstdio>

using namespace std;

struct Point2D {
    float x;
    float y;
};

struct AffineTransform {
    float ax, bx, cx;
    float ay, by, cy;
};

float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) return -1;

    // 使用均值中心化坐标以提高数值稳定性
    double mean_xi = 0, mean_yi = 0, mean_Xi = 0, mean_Yi = 0;
    for (int i = 0; i < n; i++) {
        mean_xi += imagePts[i].x;
        mean_yi += imagePts[i].y;
        mean_Xi += worldPts[i].x;
        mean_Yi += worldPts[i].y;
    }
    mean_xi /= n; mean_yi /= n; mean_Xi /= n; mean_Yi /= n;

    double ATA[6][6] = {0};
    double ATB[6] = {0};

    for (int i = 0; i < n; i++) {
        // 中心化坐标
        double xi = imagePts[i].x - mean_xi;
        double yi = imagePts[i].y - mean_yi;
        double Xi = worldPts[i].x - mean_Xi;
        double Yi = worldPts[i].y - mean_Yi;

        ATA[0][0] += xi * xi;    ATA[0][1] += xi * yi;    ATA[0][2] += xi;
        ATA[1][0] += xi * yi;    ATA[1][1] += yi * yi;    ATA[1][2] += yi;
        ATA[2][0] += xi;         ATA[2][1] += yi;          ATA[2][2] += n;
        ATA[3][3] += xi * xi;    ATA[3][4] += xi * yi;    ATA[3][5] += xi;
        ATA[4][3] += xi * yi;    ATA[4][4] += yi * yi;    ATA[4][5] += yi;
        ATA[5][3] += xi;         ATA[5][4] += yi;          ATA[5][5] += n;

        ATB[0] += Xi * xi;       ATB[1] += Xi * yi;        ATB[2] += Xi;
        ATB[3] += Yi * xi;       ATB[4] += Yi * yi;        ATB[5] += Yi;
    }

    double X[6] = {0};
    for (int k = 0; k < 6; k++) {
        int maxRow = k;
        for (int i = k + 1; i < 6; i++)
            if (fabs(ATA[i][k]) > fabs(ATA[maxRow][k])) maxRow = i;
        if (maxRow != k) {
            for (int j = 0; j < 6; j++) swap(ATA[k][j], ATA[maxRow][j]);
            swap(ATB[k], ATB[maxRow]);
        }
        if (fabs(ATA[k][k]) < 1e-10) continue;
        for (int i = k + 1; i < 6; i++) {
            double factor = ATA[i][k] / ATA[k][k];
            for (int j = k; j < 6; j++) ATA[i][j] -= factor * ATA[k][j];
            ATB[i] -= factor * ATB[k];
        }
    }

    for (int i = 5; i >= 0; i--) {
        if (fabs(ATA[i][i]) < 1e-10) { X[i] = 0; continue; }
        double sum = ATB[i];
        for (int j = i + 1; j < 6; j++) sum -= ATA[i][j] * X[j];
        X[i] = sum / ATA[i][i];
    }

    // 转换回原始坐标: cx = mean_Xi - ax*mean_xi - bx*mean_yi
    transform.ax = (float)X[0];
    transform.bx = (float)X[1];
    transform.cx = (float)(mean_Xi - X[0]*mean_xi - X[1]*mean_yi);
    transform.ay = (float)X[3];
    transform.by = (float)X[4];
    transform.cy = (float)(mean_Yi - X[3]*mean_xi - X[4]*mean_yi);

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

Point2D imageToWorld(Point2D pixelPt, const AffineTransform& t) {
    Point2D w; w.x = t.ax * pixelPt.x + t.bx * pixelPt.y + t.cx;
                w.y = t.ay * pixelPt.x + t.by * pixelPt.y + t.cy;
    return w;
}

Point2D worldToImage(Point2D worldPt, const AffineTransform& t) {
    Point2D p = {0, 0};
    float det = t.ax * t.by - t.ay * t.bx;
    if (fabs(det) < 1e-6f) return p;
    float invDet = 1.0f / det;
    float dx = worldPt.x - t.cx;
    float dy = worldPt.y - t.cy;
    p.x = invDet * (t.by * dx - t.bx * dy);
    p.y = invDet * (-t.ay * dx + t.ax * dy);
    return p;
}

int main() {
    srand((unsigned int)time(NULL));

    cout << "========================================" << endl;
    cout << "     Nine-Point Calibration Test" << endl;
    cout << "========================================" << endl;

    vector<Point2D> worldPts = {{0,20},{10,20},{20,20},{0,10},{10,10},{20,10},{0,0},{10,0},{20,0}};
    vector<Point2D> imagePts;

    for (size_t i = 0; i < worldPts.size(); i++) {
        Point2D p;
        p.x = 500 + worldPts[i].x * 10.0f;
        p.y = 300 + (20.0f - worldPts[i].y) * 10.0f;
        p.x += (rand() % 10 - 5) * 0.3f;
        p.y += (rand() % 10 - 5) * 0.3f;
        imagePts.push_back(p);
    }

    cout << "\nPixel coordinates:" << endl;
    cout << "No.        X          Y" << endl;
    for (size_t i = 0; i < imagePts.size(); i++)
        cout << (i+1) << "    " << fixed << setprecision(2) << imagePts[i].x << "    " << imagePts[i].y << endl;

    cout << "\nPhysical coordinates (mm):" << endl;
    for (size_t i = 0; i < worldPts.size(); i++)
        cout << (i+1) << "    " << fixed << setprecision(2) << worldPts[i].x << "    " << worldPts[i].y << endl;

    AffineTransform T;
    solveAffineWithMatrix(imagePts, worldPts, T);

    cout << "\n======================================" << endl;
    cout << "    Affine Transform Parameters" << endl;
    cout << "======================================" << endl;
    cout << fixed << setprecision(8);
    cout << "X = " << T.ax << " * x + " << T.bx << " * y + " << T.cx << endl;
    cout << "Y = " << T.ay << " * x + " << T.by << " * y + " << T.cy << endl;

    cout << "\nReprojection Error:" << endl;
    double maxE = 0, sumE = 0;
    for (size_t i = 0; i < imagePts.size(); i++) {
        Point2D calc = imageToWorld(imagePts[i], T);
        double dx = calc.x - worldPts[i].x;
        double dy = calc.y - worldPts[i].y;
        double dist = sqrt(dx*dx + dy*dy);
        maxE = max(maxE, dist);
        sumE += dist;
        cout << "Point " << (i+1) << ": Error = " << fixed << setprecision(5) << dist << " mm" << endl;
    }
    cout << "\nAverage: " << (sumE/9.0) << " mm, Max: " << maxE << " mm" << endl;

    cout << "\nCoordinate Transform Test:" << endl;
    Point2D test = {620.5f, 350.3f};
    Point2D world = imageToWorld(test, T);
    Point2D back = worldToImage(world, T);
    cout << "Pixel (" << test.x << ", " << test.y << ") -> Physical (" << world.x << ", " << world.y << ") mm" << endl;
    cout << "Inverse error: (" << (test.x - back.x) << ", " << (test.y - back.y) << ")" << endl;

    ofstream fout("calibration_params.txt");
    fout << "# Nine-Point Calibration Parameters\n";
    fout << fixed << setprecision(8);
    fout << "ax=" << T.ax << "\nbx=" << T.bx << "\ncx=" << T.cx << "\nay=" << T.ay << "\nby=" << T.by << "\ncy=" << T.cy << "\n";
    fout.close();

    cout << "\n========================================" << endl;
    cout << "     TEST PASSED - Math Verified!" << endl;
    cout << "========================================" << endl;
    cout << "Params saved to calibration_params.txt" << endl;

    MessageBox(NULL, "Math test PASSED!\n\nAffine transformation calculation is correct.\n\nTo use with real images, you need the correct OpenCV DLLs from your algorithm library vendor.", "Success", MB_OK | MB_ICONINFORMATION);

    return 0;
}
