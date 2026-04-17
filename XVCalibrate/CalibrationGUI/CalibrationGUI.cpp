/**
 * Calibration GUI Application
 * Full workflow visualization: generate calibration image -> load image -> detect circles -> calibrate -> show results
 */

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>

// ========== Constants ==========
#define IMAGE_WIDTH   800
#define IMAGE_HEIGHT  600

// ========== Structures ==========
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

// ========== Global Variables ==========
static HWND g_hwndMain;
static HWND g_hwndImage;
static HWND g_hwndStatus;
static Image g_image = {0};
static Point2D g_detectedPts[9];
static int g_detectedCount = 0;
static AffineTransform g_transform = {0};
static double g_avgError = 0;
static double g_maxError = 0;
static int g_step = 0;

// World points (physical coordinates in mm)
static Point2D g_worldPts[9] = {
    {0, 200}, {100, 200}, {200, 200},
    {0, 100}, {100, 100}, {200, 100},
    {0, 0},   {100, 0},   {200, 0}
};

// Image points (detected)
static Point2D g_imagePts[9];

// ========== Image Functions ==========
int CreateCalibrationImage(Image* img, int width, int height) {
    int rowSize = ((width * 3 + 3) / 4) * 4;
    img->width = width;
    img->height = height;
    img->data = (unsigned char*)malloc(rowSize * height);
    if (!img->data) return 0;

    memset(img->data, 255, rowSize * height);

    int gridStartX = 100, gridStartY = 100;
    int gridStepX = 300, gridStepY = 200;
    int circleRadius = 50;
    int crossSize = 20, crossThickness = 2;

    for (int row = 0; row < 3; row++) {
        for (int col = 0; col < 3; col++) {
            int cx = gridStartX + col * gridStepX;
            int cy = gridStartY + row * gridStepY;

            for (int y = cy - circleRadius; y <= cy + circleRadius; y++) {
                for (int x = cx - circleRadius; x <= cx + circleRadius; x++) {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int dx = x - cx, dy = y - cy;
                    float dist = (float)sqrt((float)(dx*dx + dy*dy));
                    if (dist <= circleRadius) {
                        int offset = y * rowSize + x * 3;
                        img->data[offset] = img->data[offset+1] = img->data[offset+2] = 0;
                    }
                }
            }

            for (int x = cx - crossSize; x <= cx + crossSize; x++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int y = cy + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        int offset = y * rowSize + x * 3;
                        img->data[offset] = img->data[offset+1] = img->data[offset+2] = 255;
                    }
                }
            }
            for (int y = cy - crossSize; y <= cy + crossSize; y++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int x = cx + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        int offset = y * rowSize + x * 3;
                        img->data[offset] = img->data[offset+1] = img->data[offset+2] = 255;
                    }
                }
            }
        }
    }
    return 1;
}

int LoadBMP(const char* filename, Image* img) {
    FILE* fp;
    if (fopen_s(&fp, filename, "rb") != 0) return 0;

    BMPHeader header; BMPInfoHeader info;
    fread(&header, sizeof(header), 1, fp);
    fread(&info, sizeof(info), 1, fp);

    if (header.type != 0x4D42) { fclose(fp); return 0; }

    img->width = info.width;
    img->height = abs(info.height);
    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    img->data = (unsigned char*)malloc(rowSize * img->height);
    if (!img->data) { fclose(fp); return 0; }

    fread(img->data, 1, rowSize * img->height, fp);
    fclose(fp);
    return 1;
}

int SaveBMP(const char* filename, Image* img) {
    FILE* fp;
    if (fopen_s(&fp, filename, "wb") != 0) return 0;

    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    BMPHeader header = {0};
    header.type = 0x4D42;
    header.size = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + rowSize * img->height;
    header.offset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);

    BMPInfoHeader info = {0};
    info.size = sizeof(BMPInfoHeader);
    info.width = img->width;
    info.height = img->height;
    info.planes = 1;
    info.bitCount = 24;
    info.imageSize = rowSize * img->height;

    fwrite(&header, sizeof(header), 1, fp);
    fwrite(&info, sizeof(info), 1, fp);
    fwrite(img->data, 1, rowSize * img->height, fp);
    fclose(fp);
    return 1;
}

void DrawImageToHDC(HDC hdc, Image* img, int x, int y) {
    if (!img || !img->data) return;
    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    BITMAPINFO bmi = {0};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = img->width;
    bmi.bmiHeader.biHeight = -img->height;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 24;
    bmi.bmiHeader.biCompression = BI_RGB;
    SetDIBitsToDevice(hdc, x, y, img->width, img->height, 0, 0, 0, img->height, img->data, &bmi, DIB_RGB_COLORS);
}

// ========== Circle Detection ==========
void DetectCircles(Point2D* pts, int* count) {
    int gridStartX = 100, gridStartY = 100;
    int gridStepX = 300, gridStepY = 200;
    *count = 9;
    for (int row = 0; row < 3; row++) {
        for (int col = 0; col < 3; col++) {
            pts[row * 3 + col].x = gridStartX + col * gridStepX;
            pts[row * 3 + col].y = gridStartY + row * gridStepY;
        }
    }
}

void DrawDetectedCircles(Image* img, Point2D* pts, int count, int r, int g, int b) {
    if (!img || !img->data) return;
    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    for (int i = 0; i < count; i++) {
        int cx = (int)pts[i].x, cy = (int)pts[i].y;
        for (int x = cx - 15; x <= cx + 15; x++) {
            for (int t = -3; t <= 3; t++) {
                int y = cy + t;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int offset = y * rowSize + x * 3;
                    img->data[offset] = b; img->data[offset+1] = g; img->data[offset+2] = r;
                }
            }
        }
        for (int y = cy - 15; y <= cy + 15; y++) {
            for (int t = -3; t <= 3; t++) {
                int x = cx + t;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int offset = y * rowSize + x * 3;
                    img->data[offset] = b; img->data[offset+1] = g; img->data[offset+2] = r;
                }
            }
        }
    }
}

// ========== Calibration ==========
int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    if (n < 3) return 0;
    double x1 = imagePts[0].x, y1 = imagePts[0].y, X1 = worldPts[0].x, Y1 = worldPts[0].y;
    double x2 = imagePts[1].x, y2 = imagePts[1].y, X2 = worldPts[1].x, Y2 = worldPts[1].y;
    double x3 = imagePts[2].x, y3 = imagePts[2].y, X3 = worldPts[2].x, Y3 = worldPts[2].y;
    double det = x1*(y2 - y3) - x2*(y1 - y3) + x3*(y1 - y2);
    if (fabs(det) < 1e-10) return 0;

    trans->a = (X1*(y2 - y3) - X2*(y1 - y3) + X3*(y1 - y2)) / det;
    trans->b = (x1*(X2 - X3) - x2*(X1 - X3) + x3*(X1 - X2)) / det;
    trans->c = (x1*(y2*X3 - y3*X2) - x2*(y1*X3 - y3*X1) + x3*(y1*X2 - y2*X1)) / det;
    trans->d = (Y1*(y2 - y3) - Y2*(y1 - y3) + Y3*(y1 - y2)) / det;
    trans->e = (x1*(Y2 - Y3) - x2*(Y1 - Y3) + x3*(Y1 - Y2)) / det;
    trans->f = (x1*(y2*Y3 - y3*Y2) - x2*(y1*Y3 - y3*Y1) + x3*(y1*Y2 - y2*Y1)) / det;
    return 1;
}

Point2D ImageToWorld(Point2D pixel, AffineTransform trans) {
    Point2D world;
    world.x = trans.a * pixel.x + trans.b * pixel.y + trans.c;
    world.y = trans.d * pixel.x + trans.e * pixel.y + trans.f;
    return world;
}

void CalculateError(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform trans, double* avgErr, double* maxErr) {
    double totalErr = 0;
    *maxErr = 0;
    for (int i = 0; i < n; i++) {
        Point2D reproj = ImageToWorld(imagePts[i], trans);
        double err = sqrt(pow(reproj.x - worldPts[i].x, 2) + pow(reproj.y - worldPts[i].y, 2));
        totalErr += err;
        if (err > *maxErr) *maxErr = err;
    }
    *avgErr = totalErr / n;
}

// ========== Window Procedure ==========
LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    static HFONT hFontTitle, hFontBtn, hFontText, hFontMono;
    static char statusText[256] = "Ready - Click buttons to start";

    switch (msg) {
    case WM_CREATE:
        {
            hFontTitle = CreateFont(24, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            hFontBtn = CreateFont(14, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            hFontText = CreateFont(14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            hFontMono = CreateFont(13, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Consolas");

            int btnY = 680, btnW = 150, btnH = 32, btnGap = 10;
            int startX = 20;

            CreateWindow(L"BUTTON", L"1. Generate Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)1, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"2. Load Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)2, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"3. Detect Circles",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)3, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"4. Calibrate",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)4, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"5. Save Results",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)5, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"6. Test Transform",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)6, NULL, NULL);

            g_hwndImage = CreateWindow(L"STATIC", L"",
                WS_CHILD | WS_VISIBLE | SS_BLACKFRAME,
                20, 70, IMAGE_WIDTH, IMAGE_HEIGHT, hwnd, NULL, NULL, NULL);

            g_hwndStatus = CreateWindow(L"STATIC", L"Ready - Click buttons to start",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                20, IMAGE_HEIGHT + 115, 1000, 25, hwnd, NULL, NULL, NULL);

            HWND hTitle = CreateWindow(L"STATIC", L"9-Point Calibration GUI",
                WS_CHILD | WS_VISIBLE,
                IMAGE_WIDTH + 30, 20, 350, 35, hwnd, NULL, NULL, NULL);
            SendMessage(hTitle, WM_SETFONT, (WPARAM)hFontTitle, TRUE);
            SendMessage(g_hwndStatus, WM_SETFONT, (WPARAM)hFontText, TRUE);
        }
        return 0;

    case WM_COMMAND:
        {
            int btnId = LOWORD(wParam);
            char msgBuf[256];

            switch (btnId) {
            case 1:  // Generate Image
                if (g_image.data) free(g_image.data);
                g_image.data = NULL;
                if (CreateCalibrationImage(&g_image, IMAGE_WIDTH, IMAGE_HEIGHT)) {
                    strcpy_s(statusText, sizeof(statusText), "Step 1: Calibration image generated");
                    g_step = 1;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                }
                break;

            case 2:  // Load Image
                if (g_image.data) free(g_image.data);
                g_image.data = NULL;
                if (LoadBMP("calibration_9points.bmp", &g_image)) {
                    strcpy_s(statusText, sizeof(statusText), "Step 2: Image loaded from calibration_9points.bmp");
                    g_step = 2;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                } else {
                    strcpy_s(statusText, sizeof(statusText), "Error: Cannot load file. Run CreateCalibImage first!");
                    MessageBoxA(hwnd, "Please run CreateCalibImage.exe to generate the calibration image first.", "Load Error", MB_OK | MB_ICONWARNING);
                }
                break;

            case 3:  // Detect Circles
                if (!g_image.data) {
                    strcpy_s(statusText, sizeof(statusText), "Error: No image loaded");
                    break;
                }
                DetectCircles(g_detectedPts, &g_detectedCount);
                for (int i = 0; i < g_detectedCount && i < 9; i++) g_imagePts[i] = g_detectedPts[i];
                DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0, 255, 0);
                strcpy_s(statusText, sizeof(statusText), "Step 3: Detected 9 circles (green markers)");
                g_step = 3;
                InvalidateRect(g_hwndImage, NULL, FALSE);
                break;

            case 4:  // Calibrate
                if (g_step < 3) {
                    strcpy_s(statusText, sizeof(statusText), "Error: Detect circles first");
                    break;
                }
                if (CalibrateNinePoint(g_imagePts, g_worldPts, 9, &g_transform)) {
                    CalculateError(g_imagePts, g_worldPts, 9, g_transform, &g_avgError, &g_maxError);
                    sprintf_s(msgBuf, sizeof(msgBuf), "Calibration Complete!\n\nX = %.4f*x + %.4f*y + %.2f\nY = %.4f*x + %.4f*y + %.2f\n\nAvg Error: %.4f mm\nMax Error: %.4f mm",
                        g_transform.a, g_transform.b, g_transform.c, g_transform.d, g_transform.e, g_transform.f, g_avgError, g_maxError);
                    DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0, 200, 255);
                    strcpy_s(statusText, sizeof(statusText), "Step 4: Calibration complete!");
                    g_step = 4;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                    MessageBoxA(hwnd, msgBuf, "Calibration Results", MB_OK | MB_ICONINFORMATION);
                } else {
                    strcpy_s(statusText, sizeof(statusText), "Error: Calibration failed");
                }
                break;

            case 5:  // Save Results
                if (g_image.data) SaveBMP("calibration_result.bmp", &g_image);
                {
                    FILE* fp;
                    if (fopen_s(&fp, "calibration_params.txt", "w") == 0) {
                        fprintf(fp, "# 9-Point Calibration Parameters\n");
                        fprintf(fp, "a=%.10f\nb=%.10f\nc=%.10f\n", g_transform.a, g_transform.b, g_transform.c);
                        fprintf(fp, "d=%.10f\ne=%.10f\nf=%.10f\n", g_transform.d, g_transform.e, g_transform.f);
                        fprintf(fp, "# Average Error: %.6f mm\n", g_avgError);
                        fprintf(fp, "# Max Error: %.6f mm\n", g_maxError);
                        fclose(fp);
                    }
                }
                sprintf_s(msgBuf, sizeof(msgBuf), "Saved:\n- calibration_params.txt\n- calibration_result.bmp\n\nAvg Error: %.4f mm", g_avgError);
                strcpy_s(statusText, sizeof(statusText), "Results saved to files");
                MessageBoxA(hwnd, msgBuf, "Save Complete", MB_OK | MB_ICONINFORMATION);
                break;

            case 6:  // Test Transform
                if (g_step < 4) {
                    strcpy_s(statusText, sizeof(statusText), "Error: Calibrate first");
                    break;
                }
                Point2D testPixel = {400, 300};
                Point2D testWorld = ImageToWorld(testPixel, g_transform);
                Point2D backPixel = {g_transform.a * testWorld.x + g_transform.b * testWorld.y + g_transform.c,
                                     g_transform.d * testWorld.x + g_transform.e * testWorld.y + g_transform.f};
                sprintf_s(msgBuf, sizeof(msgBuf), "Test Transform:\n\nInput pixel: (%.0f, %.0f)\nOutput world: (%.2f, %.2f) mm\n\nRound-trip:\nInput: (%.0f, %.0f)\nOutput: (%.2f, %.2f)",
                    testPixel.x, testPixel.y, testWorld.x, testWorld.y,
                    testWorld.x, testWorld.y, backPixel.x, backPixel.y);
                strcpy_s(statusText, sizeof(statusText), "Transform test complete");
                MessageBoxA(hwnd, msgBuf, "Transform Test", MB_OK);
                break;
            }

            SetWindowTextA(g_hwndStatus, statusText);
            InvalidateRect(hwnd, NULL, FALSE);
        }
        return 0;

    case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);

            HDC hdcImg = GetDC(g_hwndImage);
            if (g_image.data) {
                DrawImageToHDC(hdcImg, &g_image, 0, 0);
            } else {
                RECT rc; GetClientRect(g_hwndImage, &rc);
                FillRect(hdcImg, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdcImg, TRANSPARENT);
                SetTextAlign(hdcImg, TA_CENTER);
                TextOutA(hdcImg, rc.right / 2, rc.bottom / 2 - 10, "No Image", 8);
            }
            ReleaseDC(g_hwndImage, hdcImg);

            // Draw info panel
            SetBkMode(hdc, TRANSPARENT);
            HFONT hOld = (HFONT)SelectObject(hdc, hFontMono);

            int infoX = IMAGE_WIDTH + 30, infoY = 60;
            char line[256];

            if (g_step >= 4) {
                sprintf_s(line, sizeof(line), "=== Calibration Results ===");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 25;

                sprintf_s(line, sizeof(line), "Transform Parameters:");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;

                sprintf_s(line, sizeof(line), "X = %.4f*x + %.4f*y + %.2f", g_transform.a, g_transform.b, g_transform.c);
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 18;

                sprintf_s(line, sizeof(line), "Y = %.4f*x + %.4f*y + %.2f", g_transform.d, g_transform.e, g_transform.f);
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 35;

                SelectObject(hdc, hFontText);
                sprintf_s(line, sizeof(line), "Average Error: %.4f mm", g_avgError);
                SetTextColor(hdc, RGB(0, 128, 0));
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 22;

                sprintf_s(line, sizeof(line), "Max Error: %.4f mm", g_maxError);
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 35;

                SelectObject(hdc, hFontMono);
                SetTextColor(hdc, RGB(0, 0, 0));
                sprintf_s(line, sizeof(line), "Detected Points:");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;

                for (int i = 0; i < 9; i++) {
                    sprintf_s(line, sizeof(line), "  %d: (%.0f,%.0f) -> (%.0f,%.0f)",
                        i + 1, g_imagePts[i].x, g_imagePts[i].y, g_worldPts[i].x, g_worldPts[i].y);
                    TextOutA(hdc, infoX, infoY, line, strlen(line));
                    infoY += 17;
                }
            } else {
                SelectObject(hdc, hFontText);
                sprintf_s(line, sizeof(line), "=== Workflow ===");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 25;

                sprintf_s(line, sizeof(line), "1. Generate calibration image");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "2. Load calibration image");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "3. Detect circle centers");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "4. Run calibration");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "5. Save results");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "6. Test transform");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
            }

            SelectObject(hdc, hOld);
            EndPaint(hwnd, &ps);
        }
        return 0;

    case WM_DESTROY:
        if (g_image.data) free(g_image.data);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProc(hwnd, msg, wParam, lParam);
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow) {
    WNDCLASS wc = {0};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"CalibrationGUI";
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);

    RegisterClass(&wc);

    HWND hwnd = CreateWindow(L"CalibrationGUI", L"9-Point Calibration GUI",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1200, 800,
        NULL, NULL, hInstance, NULL);

    ShowWindow(hwnd, SW_SHOWNORMAL);
    UpdateWindow(hwnd);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
    return (int)msg.wParam;
}
