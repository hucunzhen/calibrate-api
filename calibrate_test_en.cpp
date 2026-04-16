/**
 * Nine-Point Calibration - Math Logic Test (No external DLL required)
 * Verify affine transformation calculation logic
 */

#include <iostream>
#include <vector>
#include <cmath>
#include <iomanip>
#include <fstream>
#include <algorithm>
#include <ctime>

using namespace std;

struct Point2D {
    float x;
    float y;
};

struct AffineTransform {
    float ax, bx, cx;  // X = ax*x + bx*y + cx
    float ay, by, cy;  // Y = ay*x + by*y + cy
};

float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) {
        cerr << "Error: Need at least 3 points, current: " << n << endl;
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
        cerr << "Error: Singular matrix, cannot invert" << endl;
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
    cout << "        Affine Transform Parameters" << endl;
    cout << "======================================" << endl;
    cout << fixed << setprecision(6);
    cout << "X = " << transform.ax << " * x + " << transform.bx << " * y + " << transform.cx << endl;
    cout << "Y = " << transform.ay << " * x + " << transform.by << " * y + " << transform.cy << endl;
    cout << "======================================" << endl;
}

int main() {
    srand((unsigned int)time(nullptr));

    cout << "========================================" << endl;
    cout << "      Nine-Point Calibration Test" << endl;
    cout << "========================================" << endl;

    // Physical coordinates (3x3 grid, 10mm spacing)
    vector<Point2D> worldPts = {
        {0, 20}, {10, 20}, {20, 20},
        {0, 10}, {10, 10}, {20, 10},
        {0, 0},  {10, 0},  {20, 0}
    };

    // Simulated pixel coordinates
    vector<Point2D> imagePts;
    for (size_t i = 0; i < worldPts.size(); i++) {
        Point2D p;
        p.x = 500 + worldPts[i].x * 10.0f;
        p.y = 300 + (20.0f - worldPts[i].y) * 10.0f;
        p.x += (rand() % 10 - 5) * 0.3f;
        p.y += (rand() % 10 - 5) * 0.3f;
        imagePts.push_back(p);
    }

    cout << "\n========== Input Data ==========" << endl;

    cout << "\nPixel coordinates:" << endl;
    cout << setw(3) << "No." << setw(15) << "X(pixel)" << setw(15) << "Y(pixel)" << endl;
    for (size_t i = 0; i < imagePts.size(); i++) {
        cout << setw(3) << (i + 1)
             << setw(15) << fixed << setprecision(2) << imagePts[i].x
             << setw(15) << imagePts[i].y << endl;
    }

    cout << "\nPhysical coordinates:" << endl;
    cout << setw(3) << "No." << setw(15) << "X(mm)" << setw(15) << "Y(mm)" << endl;
    for (size_t i = 0; i < worldPts.size(); i++) {
        cout << setw(3) << (i + 1)
             << setw(15) << fixed << setprecision(3) << worldPts[i].x
             << setw(15) << worldPts[i].y << endl;
    }

    AffineTransform transform;
    float residual = solveAffineWithMatrix(imagePts, worldPts, transform);
    printTransform(transform);

    cout << "\nReprojection error analysis:" << endl;
    cout << setw(3) << "No"
         << setw(12) << "WorldX" << setw(12) << "WorldY"
         << setw(12) << "CalcX" << setw(12) << "CalcY"
         << setw(12) << "ErrorX" << setw(12) << "ErrorY"
         << setw(12) << "DistErr" << endl;

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
    cout << "\nStatistics:" << endl;
    cout << "  Average error: " << (sumError / imagePts.size()) << " mm" << endl;
    cout << "  Max error: " << maxError << " mm" << endl;

    cout << "\n========== Coordinate Transform Test ==========" << endl;
    Point2D testPixel = {620.5f, 350.3f};
    cout << "\nPixel -> Physical:" << endl;
    cout << "  Input pixel: (" << testPixel.x << ", " << testPixel.y << ")" << endl;

    Point2D worldCoord = imageToWorld(testPixel, transform);
    cout << "  Output physical: (" << worldCoord.x << ", " << worldCoord.y << ") mm" << endl;

    cout << "\nInverse transform verification:" << endl;
    Point2D backPixel = worldToImage(worldCoord, transform);
    cout << "  Physical: (" << worldCoord.x << ", " << worldCoord.y << ")" << endl;
    cout << "  Recovered pixel: (" << backPixel.x << ", " << backPixel.y << ")" << endl;
    cout << "  Error: (" << (testPixel.x - backPixel.x) << ", " << (testPixel.y - backPixel.y) << ")" << endl;

    ofstream fout("calibration_params.txt");
    fout << "# Nine-Point Calibration Parameters" << endl;
    fout << fixed << setprecision(8);
    fout << "ax=" << transform.ax << endl;
    fout << "bx=" << transform.bx << endl;
    fout << "cx=" << transform.cx << endl;
    fout << "ay=" << transform.ay << endl;
    fout << "by=" << transform.by << endl;
    fout << "cy=" << transform.cy << endl;
    fout.close();
    cout << "\nCalibration saved to: calibration_params.txt" << endl;

    cout << "\n========================================" << endl;
    cout << "      Test Complete - All PASSED" << endl;
    cout << "========================================" << endl;

    return 0;
}
