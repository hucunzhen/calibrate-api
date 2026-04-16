/**
 * Direct test of affine transformation with known coordinates
 */

#include <windows.h>
#include <stdio.h>
#include <math.h>
#include <vector>
#include <cmath>
#include <iomanip>
#include <fstream>

using namespace std;

struct Point2D { float x, y; };
struct AffineTransform { float ax, bx, cx, ay, by, cy; };

float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) return -1;

    // Use mean-centered coordinates for better numerical stability
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
        // Center the coordinates
        double xi = imagePts[i].x - mean_xi;
        double yi = imagePts[i].y - mean_yi;
        double Xi = worldPts[i].x - mean_Xi;
        double Yi = worldPts[i].y - mean_Yi;

        // X transformation: centered
        ATA[0][0] += xi*xi; ATA[0][1] += xi*yi; ATA[0][2] += xi;
        ATA[1][0] += xi*yi; ATA[1][1] += yi*yi; ATA[1][2] += yi;
        ATA[2][0] += xi; ATA[2][1] += yi; ATA[2][2] += n;
        ATB[0] += Xi*xi; ATB[1] += Xi*yi; ATB[2] += Xi;

        // Y transformation: centered
        ATA[3][3] += xi*xi; ATA[3][4] += xi*yi; ATA[3][5] += xi;
        ATA[4][3] += xi*yi; ATA[4][4] += yi*yi; ATA[4][5] += yi;
        ATA[5][3] += xi; ATA[5][4] += yi; ATA[5][5] += n;
        ATB[3] += Yi*xi; ATB[4] += Yi*yi; ATB[5] += Yi;
    }

    double X[6] = {0};
    for (int k = 0; k < 6; k++) {
        int maxRow = k;
        for (int i = k+1; i < 6; i++)
            if (fabs(ATA[i][k]) > fabs(ATA[maxRow][k])) maxRow = i;
        if (maxRow != k) {
            for (int j = 0; j < 6; j++) swap(ATA[k][j], ATA[maxRow][j]);
            swap(ATB[k], ATB[maxRow]);
        }
        if (fabs(ATA[k][k]) < 1e-10) continue;
        for (int i = k+1; i < 6; i++) {
            double factor = ATA[i][k] / ATA[k][k];
            for (int j = k; j < 6; j++) ATA[i][j] -= factor * ATA[k][j];
            ATB[i] -= factor * ATB[k];
        }
    }

    for (int i = 5; i >= 0; i--) {
        if (fabs(ATA[i][i]) < 1e-10) { X[i] = 0; continue; }
        double sum = ATB[i];
        for (int j = i+1; j < 6; j++) sum -= ATA[i][j] * X[j];
        X[i] = sum / ATA[i][i];
    }

    // Convert back from centered to original coordinates
    // Xi' = X[0]*xi' + X[1]*yi' + X[2] => Xi = ax*xi + bx*yi + cx
    // where cx = mean_Xi - ax*mean_xi - bx*mean_yi
    transform.ax = (float)X[0];
    transform.bx = (float)X[1];
    transform.cx = (float)(mean_Xi - X[0]*mean_xi - X[1]*mean_yi);
    transform.ay = (float)X[3];
    transform.by = (float)X[4];
    transform.cy = (float)(mean_Yi - X[3]*mean_xi - X[4]*mean_yi);

    double residual = 0;
    for (int i = 0; i < n; i++) {
        double Xp = transform.ax*imagePts[i].x + transform.bx*imagePts[i].y + transform.cx;
        double Yp = transform.ay*imagePts[i].x + transform.by*imagePts[i].y + transform.cy;
        double dx = Xp - worldPts[i].x;
        double dy = Yp - worldPts[i].y;
        residual += sqrt(dx*dx + dy*dy);
    }
    return (float)(residual / n);
}

Point2D imageToWorld(Point2D p, const AffineTransform& t) {
    return {t.ax*p.x + t.bx*p.y + t.cx, t.ay*p.x + t.by*p.y + t.cy};
}

Point2D worldToImage(Point2D p, const AffineTransform& t) {
    Point2D r = {0, 0};
    float det = t.ax*t.by - t.ay*t.bx;
    if (fabs(det) < 1e-6f) return r;
    float invDet = 1.0f / det;
    float dx = p.x - t.cx, dy = p.y - t.cy;
    r.x = invDet * (t.by*dx - t.bx*dy);
    r.y = invDet * (-t.ay*dx + t.ax*dy);
    return r;
}

int main() {
    printf("========================================\n");
    printf("     Calibration with Perfect Data\n");
    printf("========================================\n\n");

    // Perfect image coordinates (from generated image)
    vector<Point2D> imagePts = {
        {100, 100}, {400, 100}, {700, 100},   // Row 1
        {100, 300}, {400, 300}, {700, 300},   // Row 2
        {100, 500}, {400, 500}, {700, 500}     // Row 3
    };

    // Perfect physical coordinates - matching image generation
    // Image: 800x600, circles at (100,100),(400,100),(700,100), (100,300),(400,300),(700,300), (100,500),(400,500),(700,500)
    // Mapping formula: worldX = (imageX - 100) / 3, worldY = 250 - 0.5 * imageY
    // Verified: (100,100)→(0,200), (400,100)→(100,200), (700,100)→(200,200)
    //           (100,300)→(0,100), (400,300)→(100,100), (700,300)→(200,100)
    //           (100,500)→(0,0),   (400,500)→(100,0),   (700,500)→(200,0)
    vector<Point2D> worldPts = {
        {0, 200}, {100, 200}, {200, 200},   // Image Y=100 -> worldY=200
        {0, 100}, {100, 100}, {200, 100},   // Image Y=300 -> worldY=100
        {0, 0},   {100, 0},   {200, 0}     // Image Y=500 -> worldY=0
    };

    printf("Image Points (detected):\n");
    for (size_t i = 0; i < imagePts.size(); i++)
        printf("  %d: (%.0f, %.0f)\n", (int)i+1, imagePts[i].x, imagePts[i].y);

    printf("\nWorld Points (physical mm):\n");
    for (size_t i = 0; i < worldPts.size(); i++)
        printf("  %d: (%.0f, %.0f)\n", (int)i+1, worldPts[i].x, worldPts[i].y);

    // Solve calibration
    AffineTransform T;
    float residual = solveAffineWithMatrix(imagePts, worldPts, T);

    printf("\n========================================\n");
    printf("     Affine Transform Result\n");
    printf("========================================\n");
    printf("X = %.8f * x + %.8f * y + %.8f\n", T.ax, T.bx, T.cx);
    printf("Y = %.8f * x + %.8f * y + %.8f\n", T.ay, T.by, T.cy);

    // Verify with known formula
    // From image generation: worldX = (x - 100) / 3, worldY = 250 - 0.5 * y
    printf("\n--- Expected (from image generation) ---\n");
    printf("X = 0.33333333 * x - 33.33333333\n");
    printf("Y = -0.50000000 * y + 250.00000000\n");

    // Calculate error for each point
    printf("\n--- Reprojection Error (Perfect Data) ---\n");
    double maxE = 0, sumE = 0;
    for (size_t i = 0; i < imagePts.size(); i++) {
        Point2D calc = imageToWorld(imagePts[i], T);
        double dx = calc.x - worldPts[i].x;
        double dy = calc.y - worldPts[i].y;
        double dist = sqrt(dx*dx + dy*dy);
        maxE = max(maxE, dist);
        sumE += dist;
        printf("Point %d: Expected(%.1f,%.1f) Calc(%.4f,%.4f) Error=%.8f mm\n",
               (int)i+1, worldPts[i].x, worldPts[i].y, calc.x, calc.y, dist);
    }
    printf("\nAverage Error: %.10f mm\n", sumE / 9.0);
    printf("Max Error: %.10f mm\n", maxE);

    // Test coordinate transform
    printf("\n========================================\n");
    printf("     Coordinate Transform Test\n");
    printf("========================================\n");

    Point2D testPt = {400, 300};
    Point2D worldPt = imageToWorld(testPt, T);
    Point2D backPt = worldToImage(worldPt, T);

    printf("\nTest pixel (400, 300):\n");
    printf("  Calculated physical: (%.4f, %.4f) mm\n", worldPt.x, worldPt.y);
    printf("  Expected physical: (100.0, 100.0) mm\n");
    printf("  Back to pixel: (%.6f, %.6f)\n", backPt.x, backPt.y);
    printf("  Round-trip error: (%.10f, %.10f)\n", testPt.x - backPt.x, testPt.y - backPt.y);

    // Save result
    ofstream fout("calibration_result.txt");
    fout << "# Calibration Result (Perfect Data Test)\n";
    fout << "# Image: calibration_9points.bmp\n";
    fout << fixed << setprecision(8);
    fout << "ax=" << T.ax << "\nbx=" << T.bx << "\ncx=" << T.cx << "\n";
    fout << "ay=" << T.ay << "\nby=" << T.by << "\ncy=" << T.cy << "\n";
    fout << "\navg_error=" << (sumE/9.0) << "\nmax_error=" << maxE << "\n";
    fout.close();

    printf("\n========================================\n");
    if (maxE < 1e-6) {
        printf("     TEST PASSED! (Perfect accuracy)\n");
    } else {
        printf("     TEST COMPLETED (Error: %.6f mm)\n", maxE);
    }
    printf("========================================\n");
    printf("Result saved to: calibration_result.txt\n");

    MessageBox(NULL,
        maxE < 1e-6 ?
            "Perfect Calibration Test PASSED!\n\nAffine transformation math is correct.\nAverage error: < 0.000001 mm" :
            "Calibration Test Completed\n\nAverage error: some numerical error exists.",
        "Result", MB_OK);

    return 0;
}
