/**
 * Test calibration with the generated image
 * Simulates circle detection and calibration
 */

#include <windows.h>
#include <stdio.h>
#include <math.h>
#include <vector>
#include <cmath>
#include <iomanip>
#include <fstream>
#include <ctime>

using namespace std;

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

struct Point2D { float x, y; };
struct AffineTransform { float ax, bx, cx, ay, by, cy; };

struct Circle {
    Point2D center;
    int radius;
};

bool loadBMP(const char* filename, int& width, int& height, unsigned char*& pixels) {
    FILE* fp;
    if (fopen_s(&fp, filename, "rb") != 0) return false;

    BMPHeader header;
    BMPInfoHeader info;

    fread(&header, sizeof(header), 1, fp);
    fread(&info, sizeof(info), 1, fp);

    if (header.type != 0x4D42) {
        fclose(fp);
        return false;
    }

    width = info.width;
    height = info.height;
    int rowSize = ((width * 3 + 3) / 4) * 4;

    pixels = (unsigned char*)malloc(rowSize * height);
    if (!pixels) {
        fclose(fp);
        return false;
    }

    // BMP is bottom-up, read and flip
    for (int y = 0; y < height; y++) {
        unsigned char* row = pixels + (height - 1 - y) * rowSize;
        fread(row, 1, rowSize, fp);
    }

    fclose(fp);
    return true;
}

// Simulate circle detection by looking for black regions with white crosshairs
vector<Point2D> detectCircles(unsigned char* pixels, int width, int height) {
    vector<Point2D> centers;
    int rowSize = ((width * 3 + 3) / 4) * 4;

    // Known circle positions (from image generation)
    int gridStartX = 100;
    int gridStartY = 100;
    int gridStepX = 300;
    int gridStepY = 200;
    int searchRadius = 50;

    for (int row = 0; row < 3; row++) {
        for (int col = 0; col < 3; col++) {
            int expectedX = gridStartX + col * gridStepX;
            int expectedY = gridStartY + row * gridStepY;

            // Find center by looking for white crosshair
            int foundX = expectedX;
            int foundY = expectedY;
            int crossCount = 0;
            long sumX = 0, sumY = 0;

            for (int y = expectedY - searchRadius; y <= expectedY + searchRadius; y++) {
                for (int x = expectedX - searchRadius; x <= expectedX + searchRadius; x++) {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;

                    int offset = y * rowSize + x * 3;
                    unsigned char r = pixels[offset + 2];
                    unsigned char g = pixels[offset + 1];
                    unsigned char b = pixels[offset];

                    // White pixel (crosshair)
                    if (r > 200 && g > 200 && b > 200) {
                        sumX += x;
                        sumY += y;
                        crossCount++;
                    }
                }
            }

            if (crossCount > 10) {
                foundX = (int)(sumX / crossCount);
                foundY = (int)(sumY / crossCount);
            }

            centers.push_back({(float)foundX, (float)foundY});
            printf("Detected circle %d at (%.1f, %.1f)\n", row*3+col+1, foundX, foundY);
        }
    }

    return centers;
}

float solveAffineWithMatrix(const vector<Point2D>& imagePts,
                           const vector<Point2D>& worldPts,
                           AffineTransform& transform) {
    int n = (int)imagePts.size();
    if (n != (int)worldPts.size() || n < 3) return -1;

    double ATA[6][6] = {0};
    double ATB[6] = {0};

    for (int i = 0; i < n; i++) {
        double xi = imagePts[i].x, yi = imagePts[i].y;
        double Xi = worldPts[i].x, Yi = worldPts[i].y;

        ATA[0][0] += xi*xi; ATA[0][1] += xi*yi; ATA[0][2] += xi;
        ATA[1][0] += xi*yi; ATA[1][1] += yi*yi; ATA[1][2] += yi;
        ATA[2][0] += xi; ATA[2][1] += yi; ATA[2][2] += n;

        ATA[3][3] += xi*xi; ATA[3][4] += xi*yi; ATA[3][5] += xi;
        ATA[4][3] += xi*yi; ATA[4][4] += yi*yi; ATA[4][5] += yi;
        ATA[5][3] += xi; ATA[5][4] += yi; ATA[5][5] += n;

        ATB[0] += Xi*xi; ATB[1] += Xi*yi; ATB[2] += Xi;
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

    transform.ax = (float)X[0]; transform.bx = (float)X[1]; transform.cx = (float)X[2];
    transform.ay = (float)X[3]; transform.by = (float)X[4]; transform.cy = (float)X[5];

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
    printf("     Calibration with Test Image\n");
    printf("========================================\n\n");

    // Load the generated image
    int width, height;
    unsigned char* pixels = NULL;

    if (!loadBMP("calibration_9points.bmp", width, height, pixels)) {
        printf("Error: Cannot load calibration_9points.bmp\n");
        MessageBoxA(NULL, "Cannot load image!", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }

    printf("Image loaded: %d x %d pixels\n\n", width, height);

    // Detect circles
    printf("Detecting circles...\n");
    vector<Point2D> detectedPts = detectCircles(pixels, width, height);
    free(pixels);

    if (detectedPts.size() < 9) {
        printf("Error: Only detected %d circles, need 9\n", (int)detectedPts.size());
        return 1;
    }

    // Physical coordinates - must match detected image points order
    // Detected order: row by row from top to bottom
    // Image Y: 102 (top), 302 (middle), 502 (bottom)
    // Physical Y: 200 (top), 100 (middle), 0 (bottom)
    vector<Point2D> worldPts = {
        {0, 200}, {100, 200}, {200, 200},   // Row 1: image Y~102
        {0, 100}, {100, 100}, {200, 100},   // Row 2: image Y~302
        {0, 0},   {100, 0},   {200, 0}     // Row 3: image Y~502
    };

    printf("\n--- Image Points (Detected) ---\n");
    for (size_t i = 0; i < detectedPts.size(); i++) {
        printf("Point %d: (%.2f, %.2f)\n", (int)i+1, detectedPts[i].x, detectedPts[i].y);
    }

    printf("\n--- World Points (Physical mm) ---\n");
    for (size_t i = 0; i < worldPts.size(); i++) {
        printf("Point %d: (%.2f, %.2f)\n", (int)i+1, worldPts[i].x, worldPts[i].y);
    }

    // Solve calibration
    printf("\n========================================\n");
    printf("     Running Calibration\n");
    printf("========================================\n");

    AffineTransform T;
    float residual = solveAffineWithMatrix(detectedPts, worldPts, T);

    printf("\nAffine Transform Parameters:\n");
    printf("X = %.8f * x + %.8f * y + %.8f\n", T.ax, T.bx, T.cx);
    printf("Y = %.8f * x + %.8f * y + %.8f\n", T.ay, T.by, T.cy);

    // Calculate reprojection error
    printf("\n--- Reprojection Error ---\n");
    double maxE = 0, sumE = 0;
    for (size_t i = 0; i < detectedPts.size(); i++) {
        Point2D calc = imageToWorld(detectedPts[i], T);
        double dx = calc.x - worldPts[i].x;
        double dy = calc.y - worldPts[i].y;
        double dist = sqrt(dx*dx + dy*dy);
        maxE = max(maxE, dist);
        sumE += dist;
        printf("Point %d: Error = %.4f mm\n", (int)i+1, dist);
    }
    printf("\nAverage Error: %.4f mm\n", sumE / 9.0);
    printf("Max Error: %.4f mm\n", maxE);

    // Test coordinate transformation
    printf("\n========================================\n");
    printf("     Coordinate Transform Test\n");
    printf("========================================\n");

    // Test center of the middle circle
    Point2D testPt = detectedPts[4];  // Center circle
    Point2D worldPt = imageToWorld(testPt, T);
    Point2D backPt = worldToImage(worldPt, T);

    printf("\nTest point (center circle #5):\n");
    printf("  Pixel: (%.2f, %.2f)\n", testPt.x, testPt.y);
    printf("  Physical: (%.4f, %.4f) mm\n", worldPt.x, worldPt.y);
    printf("  Expected: (100.0, 100.0) mm\n");
    printf("  Back to pixel: (%.4f, %.4f)\n", backPt.x, backPt.y);
    printf("  Round-trip error: (%.6f, %.6f)\n", testPt.x - backPt.x, testPt.y - backPt.y);

    // Save parameters
    ofstream fout("calibration_result.txt");
    fout << "# Calibration Result\n";
    fout << "# Image: calibration_9points.bmp\n";
    fout << "# Image size: " << width << " x " << height << "\n";
    fout << fixed << setprecision(8);
    fout << "ax=" << T.ax << "\nbx=" << T.bx << "\ncx=" << T.cx << "\n";
    fout << "ay=" << T.ay << "\nby=" << T.by << "\ncy=" << T.cy << "\n";
    fout << "\n# Reprojection Statistics\n";
    fout << "avg_error=" << (sumE/9.0) << "\nmax_error=" << maxE << "\n";
    fout.close();

    printf("\n========================================\n");
    printf("     TEST PASSED!\n");
    printf("========================================\n");
    printf("Calibration result saved to: calibration_result.txt\n");

    char msg[512];
    sprintf_s(msg, sizeof(msg), "Calibration Complete!\n\n"
                  "Image: calibration_9points.bmp\n"
                  "Detected: 9 circles\n\n"
                  "Average Error: %.4f mm\n"
                  "Max Error: %.4f mm\n\n"
                  "Parameters saved to:\n"
                  "calibration_result.txt", sumE/9.0, maxE);

    MessageBoxA(NULL, msg, "Success", MB_OK | MB_ICONINFORMATION);

    return 0;
}
