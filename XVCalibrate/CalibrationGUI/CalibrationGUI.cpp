/**
 * Calibration GUI Application
 * GUI 层 — 依赖 CalibOperator 算子库
 * 纯界面代码：窗口创建、按钮事件、图像渲染、网格展示
 */

#define UNICODE
#define _UNICODE

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>
#include <stdarg.h>
#include <algorithm>
#include <time.h>

// 引入 CalibOperator 算子库
#include "../CalibOperator/CalibOperator.h"

using namespace cv;

// ========== 前置声明 ==========
static FILE* g_logFile = NULL;

// ========== CalibOperator 日志回调（写到文件） ==========
static void OperatorLogCallback(int level, const char* fmt, ...) {
    const char* prefix = "";
    switch (level) {
        case 0: prefix = "[INFO] "; break;
        case 1: prefix = "[WARN] "; break;
        case 2: prefix = "[ERROR]"; break;
        case 3: prefix = "[DEBUG]"; break;
        default: prefix = "[LOG] "; break;
    }
    time_t now = time(NULL);
    struct tm t;
    localtime_s(&t, &now);
    char timeStr[32];
    strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &t);

    char buf[2048];
    va_list args;
    va_start(args, fmt);
    int n = sprintf_s(buf, sizeof(buf), "[%s] %s ", timeStr, prefix);
    vsnprintf_s(buf + n, sizeof(buf) - n, _TRUNCATE, fmt, args);
    strcat_s(buf, sizeof(buf), "\n");
    va_end(args);

    printf("%s", buf);
    fflush(stdout);
    if (g_logFile) {
        fprintf(g_logFile, "%s", buf);
        fflush(g_logFile);
    }
}

// ========== Console Logger ==========
static FILE* g_consoleFile = NULL;

void InitConsole() {
    if (AllocConsole()) {
        freopen_s(&g_consoleFile, "CONOUT$", "w", stdout);
        freopen_s(&g_consoleFile, "CONIN$", "r", stdin);
        SetConsoleTitle(L"Calibration GUI - Debug Log");
        SetConsoleOutputCP(CP_UTF8);
        printf("=== Console Logger Initialized ===\n");
        printf("Time: %s\n", __TIME__);
        printf("==================================\n");
    }
    g_logFile = fopen("D:\\work\\calibrate-api\\debug_autotest.log", "w");
    if (g_logFile) fprintf(g_logFile, "=== Auto-test log started ===\n");
}

void CloseConsole() {
    if (g_consoleFile) {
        fclose(g_consoleFile);
        g_consoleFile = NULL;
    }
}

// Log macro with timestamp
#define LOG(fmt, ...) do { \
    time_t now = time(NULL); \
    struct tm t; localtime_s(&t, &now); \
    char timeStr[32]; \
    strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &t); \
    printf("[%s] " fmt "\n", timeStr, ##__VA_ARGS__); \
    fflush(stdout); \
    if (g_logFile) { fprintf(g_logFile, "[%s] " fmt "\n", timeStr, ##__VA_ARGS__); fflush(g_logFile); } \
} while(0)

#define LOG_INFO(fmt, ...)   LOG("[INFO] " fmt, ##__VA_ARGS__)
#define LOG_WARN(fmt, ...)    LOG("[WARN] " fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...)   LOG("[ERROR] " fmt, ##__VA_ARGS__)
#define LOG_DEBUG(fmt, ...)   LOG("[DEBUG] " fmt, ##__VA_ARGS__)

// ========== Global Variables ==========
static HINSTANCE g_hInstance;
static HWND g_hwndMain;
static HWND g_hwndImage;
static HWND g_hwndStatus;
static HWND g_hwndMethodSelect;
static HWND g_hwndTraj = NULL;
static HWND g_hwndSobel = NULL;
static Image g_image = {0};
static Image g_trajImage = {0};
static Image g_sobelImage = {0};

// 网格展示的6步中间结果图
#define STEP_COUNT 6
static Image g_stepImages[STEP_COUNT] = {0};
static const char* g_stepLabels[STEP_COUNT] = {
    "1. Original Grayscale",
    "2. Gaussian+OTSU Binary",
    "3. Workpiece Mask",
    "4. Dark Bar Binary",
    "5. Morph Cleaned Bars",
    "6. Trajectory (colored)"
};
static const int GRID_COLS = 2;
static const int GRID_ROWS = 3;
static const int LABEL_HEIGHT = 22;
static const int GRID_PADDING = 6;
static Point2D g_detectedPts[9];
static int g_detectedCount = 0;
static AffineTransform g_transform = {0};
static double g_avgError = 0;
static double g_maxError = 0;
static int g_step = 0;
static DetectMethod g_detectMethod = METHOD_OPENCV;

// World points (physical coordinates in mm)
static Point2D g_worldPts[9] = {
    {100, 100}, {400, 100}, {700, 100},
    {100, 300}, {400, 300}, {700, 300},
    {100, 500}, {400, 500}, {700, 500}
};

static Point2D g_imagePts[9];

// Trajectory points
static Point2D g_trajPixels[50000];
static Point2D g_trajWorld[50000];
static int g_trajCount = 0;
static int g_trajBarId[50000];

// ========== Image Rendering (GDI) ==========

void DrawImageToHDC(HDC hdc, Image* img, int x, int y) {
    if (!img || !img->data) return;
    int bitCount = img->channels * 8;

    BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    if (!pbmi) return;
    memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));

    pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pbmi->bmiHeader.biWidth = img->width;
    pbmi->bmiHeader.biHeight = -img->height;
    pbmi->bmiHeader.biPlanes = 1;
    pbmi->bmiHeader.biBitCount = (unsigned short)bitCount;
    pbmi->bmiHeader.biCompression = BI_RGB;

    if (bitCount == 8) {
        for (int i = 0; i < 256; i++) {
            pbmi->bmiColors[i].rgbBlue = i;
            pbmi->bmiColors[i].rgbGreen = i;
            pbmi->bmiColors[i].rgbRed = i;
            pbmi->bmiColors[i].rgbReserved = 0;
        }
    }

    SetDIBitsToDevice(hdc, x, y, img->width, img->height, 0, 0, 0, img->height, img->data, pbmi, DIB_RGB_COLORS);
    free(pbmi);
}

// ========== 网格展示工具函数 ==========

// 释放所有步骤图像
static void FreeStepImages() {
    for (int i = 0; i < STEP_COUNT; i++) {
        if (g_stepImages[i].data) {
            free(g_stepImages[i].data);
            g_stepImages[i].data = NULL;
        }
    }
}

// ========== Trajectory Window ==========

void CreateTrajectoryWindow(HINSTANCE hInstance) {
    LOG_INFO("CreateTrajectoryWindow: g_hwndTraj=%p, hInstance=%p", g_hwndTraj, hInstance);
    if (g_hwndTraj) {
        ShowWindow(g_hwndTraj, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndTraj);
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        return;
    }

    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        static int g_scrollX = 0, g_scrollY = 0;

        if (msg == WM_SIZE) {
            RECT rc; GetClientRect(hwnd, &rc);
            int clientW = rc.right;
            int clientH = rc.bottom;
            int contentW = g_trajImage.width > 0 ? g_trajImage.width : 640;
            int contentH = g_trajImage.height > 0 ? g_trajImage.height : 480;

            SCROLLINFO si = {0};
            si.cbSize = sizeof(si);

            si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
            si.nMin = 0;
            si.nMax = contentW - 1;
            si.nPage = clientW;
            SetScrollInfo(hwnd, SB_HORZ, &si, TRUE);

            si.nMax = contentH - 1;
            si.nPage = clientH;
            SetScrollInfo(hwnd, SB_VERT, &si, TRUE);

            int maxScrollX = contentW - clientW;
            int maxScrollY = contentH - clientH;
            if (maxScrollX < 0) maxScrollX = 0;
            if (maxScrollY < 0) maxScrollY = 0;
            if (g_scrollX < 0) g_scrollX = 0;
            if (g_scrollX > maxScrollX) g_scrollX = maxScrollX;
            if (g_scrollY < 0) g_scrollY = 0;
            if (g_scrollY > maxScrollY) g_scrollY = maxScrollY;

            InvalidateRect(hwnd, NULL, FALSE);
            return 0;
        }
        else if (msg == WM_HSCROLL || msg == WM_VSCROLL) {
            int bar = (msg == WM_HSCROLL) ? SB_HORZ : SB_VERT;
            SCROLLINFO si = {0};
            si.cbSize = sizeof(si);
            si.fMask = SIF_ALL;
            GetScrollInfo(hwnd, bar, &si);

            int delta = 0;
            switch (LOWORD(wParam)) {
                case SB_LINELEFT:   delta = -30;  break;
                case SB_LINERIGHT:  delta = 30;   break;
                case SB_PAGELEFT:   delta = -(int)si.nPage;  break;
                case SB_PAGERIGHT:  delta = (int)si.nPage;   break;
                case SB_THUMBTRACK: delta = si.nTrackPos - si.nPos; break;
            }

            RECT rc; GetClientRect(hwnd, &rc);
            if (bar == SB_HORZ) {
                g_scrollX += delta;
                int contentW = g_trajImage.width > 0 ? g_trajImage.width : 640;
                int maxScroll = contentW - rc.right;
                if (maxScroll < 0) maxScroll = 0;
                if (g_scrollX < 0) g_scrollX = 0;
                if (g_scrollX > maxScroll) g_scrollX = maxScroll;
                ScrollWindow(hwnd, -delta, 0, NULL, NULL);
            } else {
                g_scrollY += delta;
                int contentH = g_trajImage.height > 0 ? g_trajImage.height : 480;
                int maxScroll = contentH - rc.bottom;
                if (maxScroll < 0) maxScroll = 0;
                if (g_scrollY < 0) g_scrollY = 0;
                if (g_scrollY > maxScroll) g_scrollY = maxScroll;
                ScrollWindow(hwnd, 0, -delta, NULL, NULL);
            }

            si.fMask = SIF_POS;
            si.nPos = (bar == SB_HORZ) ? g_scrollX : g_scrollY;
            SetScrollInfo(hwnd, bar, &si, TRUE);
            UpdateWindow(hwnd);
            return 0;
        }
        else if (msg == WM_MOUSEWHEEL) {
            int delta = -(short)HIWORD(wParam) / WHEEL_DELTA * 60;
            SCROLLINFO si = {0};
            si.cbSize = sizeof(si);
            si.fMask = SIF_ALL;
            GetScrollInfo(hwnd, SB_VERT, &si);

            g_scrollY += delta;
            RECT rc; GetClientRect(hwnd, &rc);
            int contentH = g_trajImage.height > 0 ? g_trajImage.height : 480;
            int maxScroll = contentH - rc.bottom;
            if (maxScroll < 0) maxScroll = 0;
            if (g_scrollY < 0) g_scrollY = 0;
            if (g_scrollY > maxScroll) g_scrollY = maxScroll;

            si.fMask = SIF_POS;
            si.nPos = g_scrollY;
            SetScrollInfo(hwnd, SB_VERT, &si, TRUE);
            ScrollWindow(hwnd, 0, -delta, NULL, NULL);
            UpdateWindow(hwnd);
            return 0;
        }
        else if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);

            if (g_trajImage.data) {
                SetViewportOrgEx(hdc, -g_scrollX, -g_scrollY, NULL);

                BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
                memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
                pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                pbmi->bmiHeader.biWidth = g_trajImage.width;
                pbmi->bmiHeader.biHeight = -g_trajImage.height;
                pbmi->bmiHeader.biPlanes = 1;
                pbmi->bmiHeader.biBitCount = 8;
                pbmi->bmiHeader.biCompression = BI_RGB;
                for (int i = 0; i < 256; i++) {
                    pbmi->bmiColors[i].rgbBlue = (unsigned char)i;
                    pbmi->bmiColors[i].rgbGreen = (unsigned char)i;
                    pbmi->bmiColors[i].rgbRed = (unsigned char)i;
                    pbmi->bmiColors[i].rgbReserved = 0;
                }

                int rowSize = ((g_trajImage.width + 3) / 4) * 4;
                int alignedSize = rowSize * g_trajImage.height;
                unsigned char* alignedData = (unsigned char*)malloc(alignedSize);
                memset(alignedData, 0, alignedSize);
                for (int y = 0; y < g_trajImage.height; y++) {
                    memcpy(alignedData + y * rowSize,
                           g_trajImage.data + y * g_trajImage.width,
                           g_trajImage.width);
                }

                SetDIBitsToDevice(hdc, 0, 0, g_trajImage.width, g_trajImage.height,
                    0, 0, 0, g_trajImage.height, alignedData, pbmi, DIB_RGB_COLORS);

                free(alignedData);
                free(pbmi);

                int srcImgW = g_stepImages[0].width > 0 ? g_stepImages[0].width : 800;
                int srcImgH = g_stepImages[0].height > 0 ? g_stepImages[0].height : 600;
                int cellW = srcImgW / GRID_COLS;
                int cellH = (int)(cellW * (double)srcImgH / srcImgW + 0.5);
                int cellTotalH = LABEL_HEIGHT + cellH;

                SetBkMode(hdc, TRANSPARENT);
                HFONT hFont = CreateFontA(14, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                    DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                    CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, "Consolas");
                HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);
                SetTextColor(hdc, RGB(255, 255, 0));

                for (int s = 0; s < STEP_COUNT; s++) {
                    int col = s % GRID_COLS;
                    int row = s / GRID_COLS;
                    int labelX = GRID_PADDING + col * (cellW + GRID_PADDING);
                    int labelY = GRID_PADDING + row * (cellTotalH + GRID_PADDING);
                    TextOutA(hdc, labelX + 4, labelY + 3, g_stepLabels[s], (int)strlen(g_stepLabels[s]));
                }

                SelectObject(hdc, hOldFont);
                DeleteObject(hFont);

                SetViewportOrgEx(hdc, 0, 0, NULL);
            } else {
                RECT rc; GetClientRect(hwnd, &rc);
                FillRect(hdc, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdc, TRANSPARENT);
                TextOutA(hdc, 10, 10, "No trajectory image", 19);
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        else if (msg == WM_DESTROY) {
            g_hwndTraj = NULL;
            if (g_trajImage.data) {
                free(g_trajImage.data);
                g_trajImage.data = NULL;
            }
            FreeStepImages();
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    };
    wc.hInstance = hInstance;
    wc.lpszClassName = L"TrajectoryWindowClass";
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    RegisterClass(&wc);

    int contentW = g_trajImage.width > 0 ? g_trajImage.width : 640;
    int contentH = g_trajImage.height > 0 ? g_trajImage.height : 480;
    int initW = contentW + 20;
    int initH = contentH + 50;
    if (initW > 1280) initW = 1280;
    if (initH > 900) initH = 900;

    g_hwndTraj = CreateWindowEx(0, L"TrajectoryWindowClass", L"Detection Steps (2x3 Grid)",
        WS_OVERLAPPEDWINDOW | WS_VSCROLL | WS_HSCROLL | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, initW, initH,
        NULL, NULL, hInstance, NULL);
}

// ========== Sobel Edge Display Window ==========
void CreateSobelWindow(HINSTANCE hInstance) {
    if (g_hwndSobel) {
        ShowWindow(g_hwndSobel, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndSobel);
        InvalidateRect(g_hwndSobel, NULL, FALSE);
        return;
    }

    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            if (g_sobelImage.data) {
                int bitCount = g_sobelImage.channels * 8;
                int rowSize = ((g_sobelImage.width * g_sobelImage.channels + 3) / 4) * 4;
                BITMAPINFO bmi = {0};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = g_sobelImage.width;
                bmi.bmiHeader.biHeight = -g_sobelImage.height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = (unsigned short)bitCount;
                bmi.bmiHeader.biCompression = BI_RGB;
                SetDIBitsToDevice(hdc, 0, 0, g_sobelImage.width, g_sobelImage.height, 0, 0, 0, g_sobelImage.height, g_sobelImage.data, &bmi, DIB_RGB_COLORS);
            } else {
                RECT rc; GetClientRect(hwnd, &rc);
                FillRect(hdc, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdc, TRANSPARENT);
                TextOutA(hdc, 10, 10, "No Sobel image", 14);
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        else if (msg == WM_DESTROY) {
            g_hwndSobel = NULL;
            if (g_sobelImage.data) {
                free(g_sobelImage.data);
                g_sobelImage.data = NULL;
            }
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    };
    wc.hInstance = hInstance;
    wc.lpszClassName = L"SobelWindowClass";
    RegisterClass(&wc);

    g_hwndSobel = CreateWindowEx(0, L"SobelWindowClass", L"Sobel Edge Detection",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT,
        g_sobelImage.width > 0 ? g_sobelImage.width + 100 : 640,
        g_sobelImage.height > 0 ? g_sobelImage.height + 150 : 480,
        NULL, NULL, hInstance, NULL);
}

// ========== 网格展示 ==========

void ShowTrajectoryImage(Image* src, Point2D* trajPixels, int trajCount) {
    int imgW = src->width;
    int imgH = src->height;
    LOG_INFO("ShowTrajectoryImage: src=%dx%d, trajCount=%d", imgW, imgH, trajCount);

    int cellW = imgW / GRID_COLS;
    int cellH = (int)(cellW * (double)imgH / imgW + 0.5);
    int cellTotalH = LABEL_HEIGHT + cellH;
    int gridW = GRID_COLS * cellW + (GRID_COLS + 1) * GRID_PADDING;
    int gridH = GRID_ROWS * cellTotalH + (GRID_ROWS + 1) * GRID_PADDING;

    int dstChannels = 1;
    int dstRowSize = gridW;
    int totalSize = dstRowSize * gridH;

    if (g_trajImage.data) free(g_trajImage.data);
    g_trajImage.width = gridW;
    g_trajImage.height = gridH;
    g_trajImage.channels = dstChannels;
    g_trajImage.data = (unsigned char*)malloc(totalSize);
    memset(g_trajImage.data, 0, totalSize);

    for (int s = 0; s < STEP_COUNT; s++) {
        Image* stepImg = &g_stepImages[s];
        if (!stepImg->data) continue;

        int col = s % GRID_COLS;
        int row = s / GRID_COLS;
        int cellX = GRID_PADDING + col * (cellW + GRID_PADDING);
        int cellY = GRID_PADDING + row * (cellTotalH + GRID_PADDING) + LABEL_HEIGHT;

        int srcW = stepImg->width;
        int srcH = stepImg->height;

        for (int dy = 0; dy < cellH; dy++) {
            int sy = dy * srcH / cellH;
            for (int dx = 0; dx < cellW; dx++) {
                int sx = dx * srcW / cellW;
                int dstX = cellX + dx;
                int dstY = cellY + dy;
                if (dstX >= gridW || dstY >= gridH) continue;

                unsigned char gray = 0;
                if (stepImg->channels == 1) {
                    gray = stepImg->data[sy * srcW + sx];
                } else {
                    int srcOff = (sy * srcW + sx) * 3;
                    unsigned char b = stepImg->data[srcOff];
                    unsigned char g = stepImg->data[srcOff + 1];
                    unsigned char r = stepImg->data[srcOff + 2];
                    gray = (unsigned char)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f);
                }
                g_trajImage.data[dstY * dstRowSize + dstX] = gray;
            }
        }

        int lineY = cellY + cellH;
        if (lineY < gridH) {
            for (int x = cellX; x < cellX + cellW && x < gridW; x++) {
                g_trajImage.data[lineY * dstRowSize + x] = 80;
            }
        }
    }

    if (g_hwndTraj) {
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        RECT rc;
        GetClientRect(g_hwndTraj, &rc);
        SendMessage(g_hwndTraj, WM_SIZE, 0, MAKELPARAM(rc.right, rc.bottom));
    }
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

            int btnW = 150, btnH = 32, btnGap = 10;
            // 第一行：按钮 1-4
            int btnY1 = 700, startX = 20;
            CreateWindow(L"BUTTON", L"1. Generate Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)1, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"2. Load Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)2, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"3. Detect Circles",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)3, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"4. Calibrate",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)4, NULL, NULL);

            // 第二行：按钮 5-8
            int btnY2 = btnY1 + btnH + 8;
            startX = 20;
            CreateWindow(L"BUTTON", L"5. Save Results",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)5, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"6. Test Transform",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)6, NULL, NULL);
            startX += btnW + btnGap;

            CreateWindow(L"BUTTON", L"7. TestImage",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)7, NULL, NULL);
            startX += btnW + btnGap;

            // Button 8: 一键轨迹测试（使用 test_auto.bmp）
            CreateWindow(L"BUTTON", L"8. AutoTrajTest",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)8, NULL, NULL);

            g_hwndImage = CreateWindow(L"STATIC", L"",
                WS_CHILD | WS_VISIBLE | SS_BLACKFRAME,
                20, 70, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT, hwnd, NULL, NULL, NULL);

            g_hwndStatus = CreateWindow(L"STATIC", L"Ready - Click buttons to start",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                20, CALIB_IMAGE_HEIGHT + 75, 1000, 25, hwnd, NULL, NULL, NULL);

            HWND hTitle = CreateWindow(L"STATIC", L"9-Point Calibration GUI",
                WS_CHILD | WS_VISIBLE,
                CALIB_IMAGE_WIDTH + 30, 20, 350, 35, hwnd, NULL, NULL, NULL);
            SendMessage(hTitle, WM_SETFONT, (WPARAM)hFontTitle, TRUE);
            SendMessage(g_hwndStatus, WM_SETFONT, (WPARAM)hFontText, TRUE);

            HWND hMethodLabel = CreateWindow(L"STATIC", L"Detection Method:",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                CALIB_IMAGE_WIDTH + 30, 680, 120, 25, hwnd, NULL, NULL, NULL);
            SendMessage(hMethodLabel, WM_SETFONT, (WPARAM)hFontText, TRUE);

            g_hwndMethodSelect = CreateWindow(L"COMBOBOX", L"",
                WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST | CBS_HASSTRINGS | WS_VSCROLL,
                CALIB_IMAGE_WIDTH + 150, 678, 200, 100, hwnd, (HMENU)100, NULL, NULL);
            SendMessage(g_hwndMethodSelect, WM_SETFONT, (WPARAM)hFontText, TRUE);
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"OpenCV-Contour");
            SendMessage(g_hwndMethodSelect, CB_SETCURSEL, 0, 0);
        }
        return 0;

    // 自动测试
    case WM_USER + 1:
        {
            LOG_INFO("=== Auto-test triggered ===");
            char filename[MAX_PATH];
            const char* autoTestPath = "D:\\work\\calibrate-api\\test_auto.bmp";
            strcpy_s(filename, sizeof(filename), autoTestPath);

            Image loadedImg = {0};
            if (LoadBMP(filename, &loadedImg)) {
                LOG_INFO("Auto-test: loaded %s (%dx%d)", filename, loadedImg.width, loadedImg.height);
                if (g_image.data) free(g_image.data);
                g_image = loadedImg;

                g_step = 2;

                // 调用 DetectTrajectoryOpenCV（新签名：传入 stepImages/stepBarIds）
                FreeStepImages();
                DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount,
                                      g_stepImages, g_trajBarId, STEP_COUNT);
                LOG_INFO("Auto-test: detected %d trajectory points", g_trajCount);

                ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                CreateTrajectoryWindow(g_hInstance);

                sprintf_s(statusText, sizeof(statusText),
                    "Auto-test: %s | %d points", filename, g_trajCount);
                InvalidateRect(g_hwndImage, NULL, FALSE);
            } else {
                LOG_ERROR("Auto-test: failed to load %s", filename);
                sprintf_s(statusText, sizeof(statusText), "Auto-test: failed to load %s", filename);
            }
        }
        return 0;

    case WM_COMMAND:
        {
            int btnId = LOWORD(wParam);
            char msgBuf[1024 * 10];

            switch (btnId) {
            case 1:  // Generate Image
                LOG_INFO("Button 1: Generate Image clicked");
                if (g_image.data) free(g_image.data);
                g_image.data = NULL;
                if (CreateCalibrationImage(&g_image, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT)) {
                    SaveBMP("calibration_9points.bmp", &g_image);
                    strcpy_s(statusText, sizeof(statusText), "Step 1: Calibration image generated and saved");
                    g_step = 1;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                }
                break;

            case 2: { // Load Image
                LOG_INFO("Button 2: Load Image clicked");
                OPENFILENAMEA ofn;
                char filename[MAX_PATH] = "";
                memset(&ofn, 0, sizeof(ofn));
                ofn.lStructSize = sizeof(ofn);
                ofn.hwndOwner = hwnd;
                ofn.lpstrFilter = "Image Files\0*.bmp;*.jpg;*.jpeg;*.png\0BMP Files\0*.bmp\0All Files\0*.*\0\0";
                ofn.lpstrFile = filename;
                ofn.nMaxFile = MAX_PATH;
                ofn.Flags = OFN_FILEMUSTEXIST;
                ofn.lpstrTitle = "Select Calibration Image";

                if (GetOpenFileNameA(&ofn)) {
                    Image loadedImg = {0};
                    if (LoadBMP(filename, &loadedImg)) {
                        if (g_image.data) free(g_image.data);
                        g_image = loadedImg;

                        sprintf_s(statusText, sizeof(statusText), "Loaded: %s (%dx%d)", filename, g_image.width, g_image.height);
                        g_step = 2;
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                    } else {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s", filename);
                        MessageBoxA(hwnd, msgBuf, "Load Error", MB_OK | MB_ICONWARNING);
                        strcpy_s(statusText, sizeof(statusText), "Error: Cannot load image");
                    }
                }
                break;
            }

            case 3: { // Detect Circles
                LOG_INFO("Button 3: Detect Circles clicked");
                if (!g_image.data) {
                    strcpy_s(statusText, sizeof(statusText), "Error: No image loaded");
                    MessageBoxA(hwnd, "Please complete Step 1 or Step 2 to load an image first.", "No Image", MB_OK | MB_ICONWARNING);
                } else {
                    int sel = SendMessage(g_hwndMethodSelect, CB_GETCURSEL, 0, 0);
                    g_detectMethod = (DetectMethod)sel;

                    DetectCircles(&g_image, g_detectedPts, &g_detectedCount);

                    for (int i = 0; i < g_detectedCount && i < 9; i++) g_imagePts[i] = g_detectedPts[i];
                    DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0);

                    if (g_detectedCount < 3) {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Circle detection failed!\nDetected only %d circles.", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Error: Insufficient circles detected");
                        MessageBoxA(hwnd, msgBuf, "Detection Error", MB_OK | MB_ICONERROR);
                    } else {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Detected %d circles successfully!", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Step 3: Detected circles (green markers)");
                        MessageBoxA(hwnd, msgBuf, "Detection Complete", MB_OK | MB_ICONINFORMATION);
                        g_step = 3;
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                    }
                }
                break;
            }

            case 4: { // Calibrate
                LOG_INFO("Button 4: Calibrate clicked");
                if (g_step < 3) {
                    MessageBoxA(hwnd, "Please complete Step 3 (Detect Circles) before calibration.", "Step Error", MB_OK | MB_ICONWARNING);
                } else {
                    if (CalibrateNinePoint(g_imagePts, g_worldPts, 9, &g_transform)) {
                        CalibSetTransform(g_transform);  // 同步到算子层
                        CalculateError(g_imagePts, g_worldPts, 9, g_transform, &g_avgError, &g_maxError);
                        sprintf_s(msgBuf, sizeof(msgBuf), "Calibration Complete!\n\nX = %.4f*x + %.4f*y + %.2f\nY = %.4f*x + %.4f*y + %.2f\n\nAvg Error: %.4f mm\nMax Error: %.4f mm",
                            g_transform.a, g_transform.b, g_transform.c, g_transform.d, g_transform.e, g_transform.f, g_avgError, g_maxError);
                        DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 128);
                        strcpy_s(statusText, sizeof(statusText), "Step 4: Calibration complete!");
                        g_step = 4;
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                        MessageBoxA(hwnd, msgBuf, "Calibration Results", MB_OK | MB_ICONINFORMATION);
                    } else {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Calibration Failed! Detected: %d points", g_detectedCount);
                        MessageBoxA(hwnd, msgBuf, "Calibration Failed", MB_OK | MB_ICONERROR);
                    }
                }
                break;
            }

            case 5:  // Save Results
                LOG_INFO("Button 5: Save Results clicked");
                if (g_image.data) {
                    SaveBMP("calibration_result.bmp", &g_image);
                }
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

            case 6: {  // Test Transform
                LOG_INFO("Button 6: Test Transform clicked");
                if (g_step < 4) break;
                Point2D testPixel = { 400, 300 };
                Point2D testWorld = ImageToWorld(testPixel, g_transform);
                sprintf_s(msgBuf, sizeof(msgBuf), "Test Transform:\n\nInput pixel: (%.0f, %.0f)\nOutput world: (%.2f, %.2f) mm",
                    testPixel.x, testPixel.y, testWorld.x, testWorld.y);
                strcpy_s(statusText, sizeof(statusText), "Transform test complete");
                MessageBoxA(hwnd, msgBuf, "Transform Test", MB_OK);
                break;
            }
                case 7: { // TestImage
                    LOG_INFO("Button 7: TestImage clicked");
                    OPENFILENAMEA ofn;
                    char filename[MAX_PATH] = "";
                    memset(&ofn, 0, sizeof(ofn));
                    ofn.lStructSize = sizeof(ofn);
                    ofn.hwndOwner = hwnd;
                    ofn.lpstrFilter = "Image Files\0*.bmp;*.jpg;*.jpeg;*.png\0BMP Files\0*.bmp\0All Files\0*.*\0\0";
                    ofn.lpstrFile = filename;
                    ofn.nMaxFile = MAX_PATH;
                    ofn.Flags = OFN_FILEMUSTEXIST;
                    ofn.lpstrTitle = "Select Calibration Image";

                    if (GetOpenFileNameA(&ofn)) {
                        Image loadedImg = { 0 };
                        if (LoadBMP(filename, &loadedImg)) {
                            if (g_image.data) free(g_image.data);
                            g_image = loadedImg;

                            sprintf_s(statusText, sizeof(statusText), "Loaded: %s (%dx%d)", filename, g_image.width, g_image.height);
                            LOG_INFO("Image loaded: %s (%dx%d)", filename, g_image.width, g_image.height);

                            if (g_step >= 4) {
                                LOG_INFO("Calibration available, starting trajectory detection...");
                                FreeStepImages();
                                DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount,
                                                      g_stepImages, g_trajBarId, STEP_COUNT);
                                LOG_INFO("Trajectory detection finished: %d points", g_trajCount);
                                sprintf_s(msgBuf, sizeof(msgBuf),
                                    "Detected %d trajectory points\n\nStart: (%.2f, %.2f)\nEnd: (%.2f, %.2f)",
                                    g_trajCount,
                                    g_trajCount > 0 ? g_trajWorld[0].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[0].y : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].y : 0);
                                MessageBoxA(hwnd, msgBuf, "Trajectory Detected", MB_OK);

                                ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                                CreateTrajectoryWindow((HINSTANCE)GetWindowLongPtr(hwnd, GWLP_HINSTANCE));
                                strcat_s(statusText, sizeof(statusText), " | Trajectory in new window");
                            }

                            InvalidateRect(g_hwndImage, NULL, FALSE);
                        } else {
                            sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s", filename);
                            MessageBoxA(hwnd, msgBuf, "Load Error", MB_OK | MB_ICONWARNING);
                            strcpy_s(statusText, sizeof(statusText), "Error: Cannot load image");
                        }
                    }
                    break;
                }

            case 8: { // AutoTrajTest — 加载 test_auto.bmp 跑轨迹检测，展示6步结果
                LOG_INFO("Button 8: AutoTrajTest clicked");
                const char* trajTestPath = "D:\\work\\calibrate-api\\test_auto.bmp";
                Image loadedImg = { 0 };
                if (LoadBMP(trajTestPath, &loadedImg)) {
                    if (g_image.data) free(g_image.data);
                    g_image = loadedImg;
                    LOG_INFO("AutoTrajTest: loaded %s (%dx%d)", trajTestPath, g_image.width, g_image.height);

                    // 运行 OpenCV 16暗条轨迹检测，获取 6 步中间结果
                    FreeStepImages();
                    DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount,
                                          g_stepImages, g_trajBarId, STEP_COUNT);
                    LOG_INFO("AutoTrajTest: detected %d trajectory points", g_trajCount);

                    // 在网格窗口中展示 6 步结果
                    ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                    CreateTrajectoryWindow((HINSTANCE)GetWindowLongPtr(hwnd, GWLP_HINSTANCE));

                    sprintf_s(statusText, sizeof(statusText),
                        "AutoTrajTest: %s | %d traj points", trajTestPath, g_trajCount);
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                } else {
                    LOG_ERROR("AutoTrajTest: failed to load %s", trajTestPath);
                    sprintf_s(statusText, sizeof(statusText),
                        "AutoTrajTest: failed to load test_auto.bmp");
                    MessageBoxA(hwnd, "Cannot load test_auto.bmp\nPlease check file exists at D:\\work\\calibrate-api\\",
                        "Load Error", MB_OK | MB_ICONWARNING);
                }
                break;
            }

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

            SetBkMode(hdc, TRANSPARENT);
            HFONT hOld = (HFONT)SelectObject(hdc, hFontMono);

            int infoX = CALIB_IMAGE_WIDTH + 40, infoY = 80;
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
                infoY += 20;
                sprintf_s(line, sizeof(line), "7. Load TestImage");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;
                sprintf_s(line, sizeof(line), "8. AutoTrajTest (test_auto.bmp)");
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
    g_hInstance = hInstance;

    InitConsole();
    LOG_INFO("Application starting...");

    // 设置算子层日志回调（写入文件 + 控制台）
    CalibSetLogCallback(OperatorLogCallback);

    WNDCLASS wc = {0};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"CalibrationGUI";
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);

    RegisterClass(&wc);

    HWND hwnd = CreateWindow(L"CalibrationGUI", L"9-Point Calibration GUI",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 2800, 2300,
        NULL, NULL, hInstance, NULL);
    LOG_INFO("Main window created: %p", hwnd);

    ShowWindow(hwnd, SW_SHOWNORMAL);
    UpdateWindow(hwnd);

    // 启动后自动执行测试
    PostMessage(hwnd, WM_USER + 1, 0, 0);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    CloseConsole();
    return (int)msg.wParam;
}
