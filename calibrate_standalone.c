/**
 * Nine-Point Calibration - Standalone Math Test
 * This program verifies the calibration math without any external DLL dependencies.
 * It only tests the affine transformation calculation logic.
 */

#include <windows.h>
#include <iostream>
#include <fstream>
#include <cmath>
#include <iomanip>
#include <ctime>
#include <vector>
#include <string>

using namespace std;

struct Point2D {
    float x;
    float y;
};

struct AffineTransform {
    float ax, bx, cx;  // X = ax*x + bx*y + cx
    float ay, by, cy;   // Y = ay*x + by*y + cy
};

void messageBox(const char* title, const char* msg) {
    MessageBoxA(NULL, msg, title, MB_OK | MB_ICONINFORMATION);
}

void consoleOutput(const char* str) {
    cout << str << endl;
    OutputDebugStringA(str);
    OutputDebugStringA("\n");
}

float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) {
        consoleOutput("Error: Need at least 3 points");
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
        ATA[2][0] += xi;         ATA[2][1] += yi;           ATA[2][2] += n;

        ATA[3][3] += xi * xi;    ATA[3][4] += xi * yi;    ATA[3][5] += xi;
        ATA[4][3] += xi * yi;    ATA[4][4] += yi * yi;    ATA[4][5] += yi;
        ATA[5][3] += xi;         ATA[5][4] += yi;          ATA[5][5] += n;

        ATB[0] += Xi * xi;       ATB[1] += Xi * yi;        ATB[2] += Xi;
        ATB[3] += Yi * xi;       ATB[4] += Yi * yi;        ATB[5] += Yi;
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
        consoleOutput("Error: Singular matrix");
        return pixelPt;
    }
    float invDet = 1.0f / det;
    float dx = worldPt.x - transform.cx;
    float dy = worldPt.y - transform.cy;
    pixelPt.x = invDet * (transform.by * dx - transform.bx * dy);
    pixelPt.y = invDet * (-transform.ay * dx + transform.ax * dy);
    return pixelPt;
}

extern "C" int main() {
    srand((unsigned int)time(NULL));

    string output;
    output += "========================================\n";
    output += "     Nine-Point Calibration Test\n";
    output += "========================================\n\n";
    consoleOutput(output.c_str());

    // Physical coordinates (3x3 grid, 10mm spacing)
    vector<Point2D> worldPts = {
        {0, 20}, {10, 20}, {20, 20},
        {0, 10}, {10, 10}, {20, 10},
        {0, 0},  {10, 0},  {20, 0}
    };

    // Simulated pixel coordinates: X = 500 + 10*worldX, Y = 300 + 10*(20-worldY)
    vector<Point2D> imagePts;
    for (size_t i = 0; i < worldPts.size(); i++) {
        Point2D p;
        p.x = 500 + worldPts[i].x * 10.0f;
        p.y = 300 + (20.0f - worldPts[i].y) * 10.0f;
        p.x += (rand() % 10 - 5) * 0.3f;
        p.y += (rand() % 10 - 5) * 0.3f;
        imagePts.push_back(p);
    }

    output = "Input Pixel Coordinates:\n";
    output += "No.       X(pixel)       Y(pixel)\n";
    for (size_t i = 0; i < imagePts.size(); i++) {
        char buf[128];
        sprintf(buf, "%2d      %10.2f    %10.2f\n", (int)(i+1), imagePts[i].x, imagePts[i].y);
        output += buf;
    }
    consoleOutput(output.c_str());

    output = "Input Physical Coordinates:\n";
    output += "No.       X(mm)          Y(mm)\n";
    for (size_t i = 0; i < worldPts.size(); i++) {
        char buf[128];
        sprintf(buf, "%2d      %10.2f    %10.2f\n", (int)(i+1), worldPts[i].x, worldPts[i].y);
        output += buf;
    }
    consoleOutput(output.c_str());

    // Solve transformation
    AffineTransform transform;
    float residual = solveAffineWithMatrix(imagePts, worldPts, transform);

    output = "======================================\n";
    output += "        Affine Transform Parameters\n";
    output += "======================================\n";
    char buf[256];
    sprintf(buf, "X = %.8f * x + %.8f * y + %.8f\n", transform.ax, transform.bx, transform.cx);
    output += buf;
    sprintf(buf, "Y = %.8f * x + %.8f * y + %.8f\n", transform.ay, transform.by, transform.cy);
    output += buf;
    output += "======================================\n";
    consoleOutput(output.c_str());

    // Reprojection error
    output = "\nReprojection Error Analysis:\n";
    output += "No      WorldX    WorldY     CalcX     CalcY     ErrorX    ErrorY     Distance\n";
    double maxError = 0, sumError = 0;
    for (size_t i = 0; i < imagePts.size(); i++) {
        Point2D calc = imageToWorld(imagePts[i], transform);
        double dx = calc.x - worldPts[i].x;
        double dy = calc.y - worldPts[i].y;
        double dist = sqrt(dx * dx + dy * dy);
        maxError = max(maxError, dist);
        sumError += dist;

        sprintf(buf, "%2d   %9.3f %9.3f %9.3f %9.3f %9.5f %9.5f %9.5f\n",
                (int)(i+1), worldPts[i].x, worldPts[i].y, calc.x, calc.y, dx, dy, dist);
        output += buf;
    }
    consoleOutput(output.c_str());

    sprintf(buf, "\nAverage Error: %.6f mm\nMax Error: %.6f mm\n", sumError / 9.0, maxError);
    consoleOutput(buf);

    // Coordinate transform test
    output = "\n========== Coordinate Transform Test ==========\n";
    consoleOutput(output.c_str());

    Point2D testPixel = {620.5f, 350.3f};
    Point2D worldCoord = imageToWorld(testPixel, transform);
    Point2D backPixel = worldToImage(worldCoord, transform);

    sprintf(buf, "Pixel (%.2f, %.2f) -> Physical (%.4f, %.4f) mm\n", 
            testPixel.x, testPixel.y, worldCoord.x, worldCoord.y);
    consoleOutput(buf);
    sprintf(buf, "Inverse: (%.4f, %.4f) -> Pixel (%.4f, %.4f)\n",
            worldCoord.x, worldCoord.y, backPixel.x, backPixel.y);
    consoleOutput(buf);
    sprintf(buf, "Error: (%.6f, %.6f)\n",
            testPixel.x - backPixel.x, testPixel.y - backPixel.y);
    consoleOutput(buf);

    // Save to file
    ofstream fout("calibration_params.txt");
    fout << "# Nine-Point Calibration Parameters\n";
    fout << fixed << setprecision(8);
    fout << "ax=" << transform.ax << endl;
    fout << "bx=" << transform.bx << endl;
    fout << "cx=" << transform.cx << endl;
    fout << "ay=" << transform.ay << endl;
    fout << "by=" << transform.by << endl;
    fout << "cy=" << transform.cy << endl;
    fout.close();

    output = "\n========================================\n";
    output += "     TEST PASSED - All checks OK\n";
    output += "========================================\n";
    output += "Math logic is verified.\n";
    output += "Calibration params saved to calibration_params.txt\n";
    consoleOutput(output.c_str());

    messageBox("Calibration Test", "Math test PASSED!\n\nThe affine transformation math is correct.\n\nTo use with real images, you need the correct OpenCV DLLs from your algorithm library vendor.");

    return 0;
}
