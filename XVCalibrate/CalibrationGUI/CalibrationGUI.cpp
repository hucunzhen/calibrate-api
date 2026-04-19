/**
 * Calibration GUI Application
 * Full workflow visualization: generate calibration image -> load image -> detect circles -> calibrate -> show results
 */

#define UNICODE
#define _UNICODE

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>
#include <algorithm>
#include <time.h>

// OpenCV headers
#include <opencv2/opencv.hpp>
#include <opencv2/imgproc.hpp>
using namespace cv;

// Include headers — proprietary libraries removed, using OpenCV only

// ========== Console Logger ==========
static FILE* g_consoleFile = NULL;
static FILE* g_logFile = NULL;

void InitConsole() {
    // Allocate console window
    if (AllocConsole()) {
        // Redirect stdout to console
        freopen_s(&g_consoleFile, "CONOUT$", "w", stdout);
        freopen_s(&g_consoleFile, "CONIN$", "r", stdin);
        
        // Set console title
        SetConsoleTitle(L"Calibration GUI - Debug Log");
        
        // Set console output to UTF-8
        SetConsoleOutputCP(CP_UTF8);
        
        printf("=== Console Logger Initialized ===\n");
        printf("Time: %s\n", __TIME__);
        printf("==================================\n");
    }
    // 同时输出到日志文件，方便排查自动测试问题
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

// Log function variants
#define LOG_INFO(fmt, ...)   LOG("[INFO] " fmt, ##__VA_ARGS__)
#define LOG_WARN(fmt, ...)    LOG("[WARN] " fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...)   LOG("[ERROR] " fmt, ##__VA_ARGS__)
#define LOG_DEBUG(fmt, ...)   LOG("[DEBUG] " fmt, ##__VA_ARGS__)

// Detection method enum
enum DetectMethod {
    METHOD_SELF = 0,      // Self-implemented algorithm (gradient-based)
    METHOD_OPENCV = 1     // OpenCV-based algorithm (contour + shape fitting)
};

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
    int channels;  // 1=�Ҷ�, 3=��ɫ
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
static HINSTANCE g_hInstance;
static HWND g_hwndMain;
static HWND g_hwndImage;
static HWND g_hwndStatus;
static HWND g_hwndMethodSelect;
static HWND g_hwndTraj = NULL;  // Trajectory display window
static HWND g_hwndSobel = NULL;  // Sobel edge display window
static Image g_image = {0};
static Image g_trajImage = {0};  // Trajectory display image
static Image g_sobelImage = {0};  // Sobel edge display image

// 网格展示的6步中间结果图（每步都是3通道BGR，避免调色板问题）
#define STEP_COUNT 6
static Image g_stepImages[STEP_COUNT] = {0};
static const char* g_stepLabels[STEP_COUNT] = {
    "1. Original Grayscale",     // 原始灰度图（缩放后）
    "2. Gaussian+OTSU Binary",   // 高斯模糊 + OTSU 二值化
    "3. Workpiece Mask",          // 形态学处理后的工件 mask
    "4. Dark Bar Binary",         // 暗条二值化（阈值45反转）
    "5. Morph Cleaned Bars",      // 形态学清理后的暗条
    "6. Trajectory (colored)"     // 最终彩色轨迹
};
// 网格布局参数
static const int GRID_COLS = 2;    // 2列，每个格子更宽，避免图片被压缩失真
static const int GRID_ROWS = 3;    // 3行
static const int LABEL_HEIGHT = 22;  // 每个格子顶部标签的高度
static const int GRID_PADDING = 6;   // 格子之间的间距
static Point2D g_detectedPts[9];
static int g_detectedCount = 0;
static AffineTransform g_transform = {0};
static double g_avgError = 0;
static double g_maxError = 0;
static int g_step = 0;
static DetectMethod g_detectMethod = METHOD_SELF;  // Default: Self-Algorithm

// World points (physical coordinates in mm)
// MUST match the circle positions in CreateCalibrationImage:
// gridStartX=100, gridStartY=100, gridStepX=300, gridStepY=200
static Point2D g_worldPts[9] = {
    {100, 100}, {400, 100}, {700, 100},
    {100, 300}, {400, 300}, {700, 300},
    {100, 500}, {400, 500}, {700, 500}
};

// Image points (detected)
static Point2D g_imagePts[9];

// Trajectory points
static Point2D g_trajPixels[10000];
static Point2D g_trajWorld[10000];
static int g_trajCount = 0;
static int g_trajBarId[10000];  // bar index for each trajectory point

// ========== Image Functions ==========
// Create 8-bit grayscale calibration image
int CreateCalibrationImage(Image* img, int width, int height) {
    int rowSize = width;  // 8-bit: row size equals width
    img->width = width;
    img->height = height;
    img->channels = 1;  // �Ҷ�ͼ
    img->data = (unsigned char*)malloc(rowSize * height);
    if (!img->data) return 0;

    // Fill with white (255)
    memset(img->data, 255, rowSize * height);

    int gridStartX = 100, gridStartY = 100;
    int gridStepX = 300, gridStepY = 200;
    int circleRadius = 50;
    int crossSize = 20, crossThickness = 2;

    for (int row = 0; row < 3; row++) {
        for (int col = 0; col < 3; col++) {
            int cx = gridStartX + col * gridStepX;
            int cy = gridStartY + row * gridStepY;

            // Draw black filled circle
            for (int y = cy - circleRadius; y <= cy + circleRadius; y++) {
                for (int x = cx - circleRadius; x <= cx + circleRadius; x++) {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int dx = x - cx, dy = y - cy;
                    float dist = (float)sqrt((float)(dx*dx + dy*dy));
                    if (dist <= circleRadius) {
                        img->data[y * rowSize + x] = 0;  // Black
                    }
                }
            }

            // Draw crosshair (white on black circle)
            for (int x = cx - crossSize; x <= cx + crossSize; x++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int y = cy + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        img->data[y * rowSize + x] = 255;  // White
                    }
                }
            }
            for (int y = cy - crossSize; y <= cy + crossSize; y++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int x = cx + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        img->data[y * rowSize + x] = 255;  // White
                    }
                }
            }
        }
    }
    return 1;
}

int SaveBMP(const char* filename, Image* img) {
    FILE* fp;
    if (fopen_s(&fp, filename, "wb") != 0) return 0;

    // Save based on channels: 1=8-bit grayscale, 3=24-bit color
    int bitCount = img->channels * 8;
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;  // With padding
    BMPHeader header = { 0 };
    header.type = 0x4D42;
    header.size = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + rowSize * img->height;
    header.offset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);

    BMPInfoHeader info = { 0 };
    info.size = sizeof(BMPInfoHeader);
    info.width = img->width;
    info.height = img->height;
    info.planes = 1;
    info.bitCount = (unsigned short)bitCount;
    info.imageSize = rowSize * img->height;

    fwrite(&header, sizeof(header), 1, fp);
    fwrite(&info, sizeof(info), 1, fp);
    
    // Write pixel data
    fwrite(img->data, 1, rowSize * img->height, fp);
    fclose(fp);
    return 1;
}
int LoadBMP(const char* filename, Image* img) {
    LOG_INFO("Loading BMP file: %s", filename);
    FILE* fp;
    if (fopen_s(&fp, filename, "rb") != 0) {
        LOG_ERROR("Cannot open file: %s", filename);
        char buf[1024];
        sprintf_s(buf, sizeof(buf), "Cannot open file:\n%s", filename);
        MessageBoxA(NULL, buf, "File Open Error", MB_OK);
        return 0;
    }

    BMPHeader header;
    memset(img, 0, sizeof(*img));
    
    // Read BMP header
    if (fread(&header, sizeof(header), 1, fp) != 1) {
        MessageBoxA(NULL, "Cannot read BMP header", "Read Error", MB_OK);
        fclose(fp);
        return 0;
    }

    // Check BMP signature "BM" (0x4D42)
    if (header.type != 0x4D42) {
        // Try to find "BM" signature in file
        unsigned char buf[128];
        fseek(fp, 0, SEEK_SET);
        int bytesRead = fread(buf, 1, 128, fp);
        
        int foundOffset = -1;
        for (int i = 0; i < bytesRead - 1; i++) {
            if (buf[i] == 0x42 && buf[i+1] == 0x4D) {
                foundOffset = i;
                break;
            }
        }
        
        char buf2[512];
        if (foundOffset >= 0) {
            sprintf_s(buf2, sizeof(buf2), 
                "BMP signature found at offset %d (expected at 0)\nFile may have extra header.\n\n"
                "First bytes: 0x%02X 0x%02X 0x%02X 0x%02X...\n"
                "Original signature: 0x%04X",
                foundOffset, buf[0], buf[1], buf[2], buf[3], header.type);
        } else {
            sprintf_s(buf2, sizeof(buf2), 
                "Not a BMP file\n"
                "First 16 bytes: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n"
                "Expected 'BM' (0x4D42) at start",
                buf[0], buf[1], buf[2], buf[3], buf[4], buf[5], buf[6], buf[7],
                buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15]);
        }
        MessageBoxA(NULL, buf2, "Format Error", MB_OK);
        fclose(fp);
        return 0;
    }

    // Read DIB header
    unsigned char dibHeader[64];
    int dibHeaderSize;
    
    // Read DIB header size first
    fseek(fp, 14, SEEK_SET);
    if (fread(&dibHeaderSize, 4, 1, fp) != 1) {
        MessageBoxA(NULL, "Cannot read DIB header size", "Read Error", MB_OK);
        fclose(fp);
        return 0;
    }
    
    // Read full DIB header
    fseek(fp, 14, SEEK_SET);
    if (fread(dibHeader, dibHeaderSize, 1, fp) != 1) {
        MessageBoxA(NULL, "Cannot read DIB header", "Read Error", MB_OK);
        fclose(fp);
        return 0;
    }
    
    // Extract DIB header fields (little-endian)
    int width = *(int*)&dibHeader[4];
    int height = *(int*)&dibHeader[8];
    int bitCount = *(short*)&dibHeader[14];
    int compression = *(int*)&dibHeader[16];
    
    // Validate dimensions
    if (width <= 0 || width > 10000 || height == 0 || height > 10000) {
        char buf[256];
        sprintf_s(buf, sizeof(buf), "Invalid dimensions: %dx%d", width, height);
        MessageBoxA(NULL, buf, "Dimension Error", MB_OK);
        fclose(fp);
        return 0;
    }
    
    // Support 8-bit (grayscale) and 24-bit BMP
    if (bitCount != 8 && bitCount != 24) {
        char buf[256];
        sprintf_s(buf, sizeof(buf), "Only 8-bit or 24-bit BMP supported\nFile is %d-bit", bitCount);
        MessageBoxA(NULL, buf, "Format Error", MB_OK);
        fclose(fp);
        return 0;
    }
    
    // Support uncompressed BMP (compression=0) and RLE8 (compression=1)
    if (compression != 0 && compression != 1) {
        char buf[256];
        sprintf_s(buf, sizeof(buf), "Unsupported compression format: %d\nOnly uncompressed and RLE8 supported", compression);
        MessageBoxA(NULL, buf, "Format Error", MB_OK);
        fclose(fp);
        return 0;
    }
    
    img->width = width;
    img->height = abs(height);
    img->channels = bitCount / 8;  // 8λ�Ҷ�=1ͨ��, 24λ��ɫ=3ͨ��
    LOG_DEBUG("BMP dimensions: %dx%d, bitCount: %d, channels: %d", width, abs(height), bitCount, img->channels);
    
    int srcRowSize = ((img->width * bitCount / 8 + 3) / 4) * 4;  // Source row size with padding
    int dstRowSize = img->width * img->channels;                  // ԭʼ��ʽ���Ҷ�=width, ��ɫ=width*3
    int imageSize = dstRowSize * img->height;
    LOG_DEBUG("Image size: %d bytes, rowSize: %d", imageSize, dstRowSize);
    
    img->data = (unsigned char*)malloc(imageSize);
    if (!img->data) {
        MessageBoxA(NULL, "Memory allocation failed", "Memory Error", MB_OK);
        fclose(fp);
        return 0;
    }
    memset(img->data, 0, imageSize);  // Initialize to 0 (transparent/black)
    
    // Seek to pixel data
    fseek(fp, header.offset, SEEK_SET);
    
    // Determine if height is negative (top-down BMP)
    int topDown = (height < 0);
    int dstRowStride = topDown ? dstRowSize : -dstRowSize;
    unsigned char* firstRow = topDown ? img->data : img->data + (img->height - 1) * dstRowSize;
    
    if (bitCount == 24 && compression == 0) {
        // Uncompressed 24-bit: direct read
        unsigned char* srcRow = (unsigned char*)malloc(srcRowSize);
        if (!srcRow) {
            MessageBoxA(NULL, "Memory allocation failed", "Memory Error", MB_OK);
            free(img->data);
            img->data = NULL;
            fclose(fp);
            return 0;
        }
        
        for (int y = 0; y < img->height; y++) {
            unsigned char* dstRow = firstRow + y * dstRowStride;
            int srcY = topDown ? y : (img->height - 1 - y);
            fseek(fp, header.offset + srcY * srcRowSize, SEEK_SET);
            if (fread(srcRow, 1, srcRowSize, fp) != (size_t)srcRowSize) {
                char buf[256];
                sprintf_s(buf, sizeof(buf), "Failed to read row %d", y);
                MessageBoxA(NULL, buf, "Read Error", MB_OK);
                free(srcRow);
                free(img->data);
                img->data = NULL;
                fclose(fp);
                return 0;
            }
            memcpy(dstRow, srcRow, img->width * 3);
        }
        free(srcRow);
    } else if (bitCount == 8 && compression == 1) {
        // RLE8 compressed 8-bit decoding
        unsigned char* rowBuffer = (unsigned char*)malloc(img->width);
        if (!rowBuffer) {
            MessageBoxA(NULL, "Memory allocation failed", "Memory Error", MB_OK);
            free(img->data);
            img->data = NULL;
            fclose(fp);
            return 0;
        }
        
        int y = 0, x = 0;
        while (y < img->height) {
            unsigned char byte1, byte2;
            if (fread(&byte1, 1, 1, fp) != 1) break;
            if (fread(&byte2, 1, 1, fp) != 1) break;
            
            if (byte1 == 0 && byte2 == 0) {
                // End of line
                y++;
                x = 0;
            } else if (byte1 == 0 && byte2 == 1) {
                // End of bitmap
                break;
            } else if (byte1 == 0 && byte2 == 2) {
                // Delta (skip)
                unsigned char dx, dy;
                if (fread(&dx, 1, 1, fp) != 1) break;
                if (fread(&dy, 1, 1, fp) != 1) break;
                x += dx;
                y += dy;
            } else if (byte1 > 0) {
                // Encoded mode: byte1 pixels of color byte2
                int count = byte1;
                if (x + count > img->width) count = img->width - x;
                memset(rowBuffer + x, byte2, count);
                x += count;
                // RLE8 stores pairs, need to skip padding if odd
                if ((byte1 & 1) == 1) {
                    fseek(fp, 1, SEEK_CUR);  // Skip padding byte
                }
            } else {
                // Absolute mode: byte2 raw pixels follow
                int count = byte2;
                if (x + count > img->width) count = img->width - x;
                if (fread(rowBuffer + x, 1, count, fp) != (size_t)count) break;
                x += count;
                // Skip padding byte if count is odd
                if ((byte2 & 1) == 1) {
                    fseek(fp, 1, SEEK_CUR);
                }
            }
            
            // Safety check
            if (x > img->width) x = img->width;
        }
        
        // Write grayscale rows directly
        for (int row = 0; row < img->height; row++) {
            unsigned char* dstRow = firstRow + row * dstRowStride;
            memcpy(dstRow, rowBuffer, img->width);
        }
        free(rowBuffer);
    } else if (bitCount == 8 && compression == 0) {
        // Uncompressed 8-bit: read palette and convert to grayscale
        // 8-bit BMP has a color table (256 entries x 4 bytes BGRA)
        unsigned char palette[256];
        {
            // Read color table entries, extract green channel as grayscale value
            // Color table starts after BMPHeader(14) + BMPInfoHeader(40) = 54 bytes
            long colorTableOffset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);
            fseek(fp, colorTableOffset, SEEK_SET);
            unsigned int colorsUsed = *(unsigned int*)&dibHeader[32];
            int numColors = (colorsUsed > 0 && colorsUsed <= 256) ? (int)colorsUsed : 256;
            for (int i = 0; i < numColors; i++) {
                unsigned char bgra[4];
                if (fread(bgra, 1, 4, fp) != 4) {
                    palette[i] = i;  // fallback
                } else {
                    // Use luminance formula: 0.299*R + 0.587*G + 0.114*B
                    palette[i] = (unsigned char)(0.299f * bgra[2] + 0.587f * bgra[1] + 0.114f * bgra[0] + 0.5f);
                }
            }
            // Fill remaining entries if colorsUsed < 256
            for (int i = numColors; i < 256; i++) {
                palette[i] = i;
            }
        }

        unsigned char* srcRow = (unsigned char*)malloc(srcRowSize);
        if (!srcRow) {
            MessageBoxA(NULL, "Memory allocation failed", "Memory Error", MB_OK);
            free(img->data);
            img->data = NULL;
            fclose(fp);
            return 0;
        }
        
        for (int y = 0; y < img->height; y++) {
            unsigned char* dstRow = firstRow + y * dstRowStride;
            int srcY = topDown ? y : (img->height - 1 - y);
            fseek(fp, header.offset + srcY * srcRowSize, SEEK_SET);
            if (fread(srcRow, 1, srcRowSize, fp) != (size_t)srcRowSize) {
                char buf[256];
                sprintf_s(buf, sizeof(buf), "Failed to read row %d", y);
                MessageBoxA(NULL, buf, "Read Error", MB_OK);
                free(srcRow);
                free(img->data);
                img->data = NULL;
                fclose(fp);
                return 0;
            }
            // Apply palette mapping: index -> grayscale value
            for (int x = 0; x < img->width; x++) {
                dstRow[x] = palette[srcRow[x]];
            }
        }
        free(srcRow);
    }
    
    fclose(fp);
    
    // Save debug image to verify loading
    SaveBMP("debug_loaded.bmp", img);
    LOG_INFO("BMP file loaded successfully: %dx%d", img->width, img->height);
    
    return 1;
}


void DrawImageToHDC(HDC hdc, Image* img, int x, int y) {
    if (!img || !img->data) return;
    int bitCount = img->channels * 8;  // 1ͨ��=8λ�Ҷ�, 3ͨ��=24λ��ɫ
    
    // BITMAPINFOHEADER(40) + ��ɫ��(256*4=1024)���ܹ�1064�ֽ�
    BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    if (!pbmi) return;
    memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    
    pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pbmi->bmiHeader.biWidth = img->width;
    pbmi->bmiHeader.biHeight = -img->height;
    pbmi->bmiHeader.biPlanes = 1;
    pbmi->bmiHeader.biBitCount = (unsigned short)bitCount;
    pbmi->bmiHeader.biCompression = BI_RGB;
    
    // 8λ�Ҷ�ͼ��Ҫ��ɫ��
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

// ========== Image Scaling ==========
void ScaleImage(Image* src, Image* dst, int dstWidth, int dstHeight) {
    dst->width = dstWidth;
    dst->height = dstHeight;
    dst->channels = src->channels;
    int dstRowSize = ((dstWidth * dst->channels + 3) / 4) * 4;
    dst->data = (unsigned char*)malloc(dstRowSize * dstHeight);
    if (!dst->data) return;
    
    int srcRowSize = ((src->width * src->channels + 3) / 4) * 4;
    double scaleX = (double)src->width / dstWidth;
    double scaleY = (double)src->height / dstHeight;
    
    for (int y = 0; y < dstHeight; y++) {
        for (int x = 0; x < dstWidth; x++) {
            // Bilinear interpolation
            double srcX = x * scaleX;
            double srcY = y * scaleY;
            int x0 = (int)srcX;
            int y0 = (int)srcY;
            int x1 = std::min(x0 + 1, src->width - 1);
            int y1 = std::min(y0 + 1, src->height - 1);
            double fx = srcX - x0;
            double fy = srcY - y0;
            
            // Clamp source coordinates
            x0 = std::min(x0, src->width - 1);
            y0 = std::min(y0, src->height - 1);
            
            unsigned char* p00 = src->data + y0 * srcRowSize + x0 * src->channels;
            unsigned char* p10 = src->data + y0 * srcRowSize + x1 * src->channels;
            unsigned char* p01 = src->data + y1 * srcRowSize + x0 * src->channels;
            unsigned char* p11 = src->data + y1 * srcRowSize + x1 * src->channels;
            
            unsigned char* pdst = dst->data + y * dstRowSize + x * dst->channels;
            for (int c = 0; c < src->channels; c++) {
                double val = (1-fx)*(1-fy)*p00[c] + fx*(1-fy)*p10[c] + (1-fx)*fy*p01[c] + fx*fy*p11[c];
                pdst[c] = cv::saturate_cast<unsigned char>(val);
            }
        }
    }
}

// ========== Circle Detection (Contour + Crosshair Refinement) ==========
// Step 1: OTSU find calib region, then find dark circle contours inside it
// Step 2: Filter by circularity & radius consistency, select 9 best
// Step 3: Crosshair refinement within each circle for sub-pixel center
void DetectCircles(Image* img, Point2D* pts, int* count) {
    LOG_INFO("=== Starting Circle Detection (HoughCircles + Crosshair) ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);

    *count = 0;
    int w = img->width, h = img->height;

    // Convert Image to cv::Mat (grayscale)
    Mat grayMat;
    if (img->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;
        Mat tmp(h, w, CV_8UC1, img->data, pitch);
        grayMat = tmp.clone();
        tmp.release();
    } else {
        int srcRowSize = ((w * img->channels + 3) / 4) * 4;
        Mat colorMat(h, w, CV_8UC3, img->data, srcRowSize);
        cvtColor(colorMat, grayMat, COLOR_BGR2GRAY);
        colorMat.release();
    }

    int dstRowSize = ((img->width * img->channels + 3) / 4) * 4;

    // ---- Step 0: Find calibration region using OTSU ----
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(5, 5), 1.0);
    Mat brightBinary;
    threshold(blurred, brightBinary, 0, 255, THRESH_BINARY + THRESH_OTSU);

    // Morphological cleanup
    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(7, 7));
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);

    // Find the largest bright contour (calibration area)
    std::vector<std::vector<Point>> brightContours;
    findContours(brightBinary, brightContours, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE);

    if (brightContours.empty()) {
        MessageBoxA(NULL, "No calibration region found (OTSU)!", "Error", MB_OK);
        LOG_ERROR("No bright calibration region found by OTSU");
        return;
    }

    int maxIdx = 0;
    double maxArea = 0;
    for (size_t i = 0; i < brightContours.size(); i++) {
        double a = contourArea(brightContours[i]);
        if (a > maxArea) { maxArea = a; maxIdx = (int)i; }
    }

    Rect bbox = boundingRect(brightContours[maxIdx]);
    LOG_INFO("Calibration region: (%d,%d)-(%d,%d), area=%.0f",
             bbox.x, bbox.y, bbox.x + bbox.width, bbox.y + bbox.height, maxArea);

    if (bbox.width < 100 || bbox.height < 100) {
        MessageBoxA(NULL, "Calibration region too small!", "Error", MB_OK);
        return;
    }

    // Draw green border on original image
    for (int x = bbox.x; x <= bbox.x + bbox.width; x++) {
        for (int t = -2; t <= 2; t++) {
            int y1 = bbox.y + t, y2 = bbox.y + bbox.height + t;
            if (x >= 0 && x < w) {
                if (y1 >= 0 && y1 < h) {
                    int off = y1 * dstRowSize + x * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
                if (y2 >= 0 && y2 < h) {
                    int off = y2 * dstRowSize + x * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
            }
        }
    }
    for (int y = bbox.y; y <= bbox.y + bbox.height; y++) {
        for (int t = -2; t <= 2; t++) {
            int x1 = bbox.x + t, x2 = bbox.x + bbox.width + t;
            if (y >= 0 && y < h) {
                if (x1 >= 0 && x1 < w) {
                    int off = y * dstRowSize + x1 * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
                if (x2 >= 0 && x2 < w) {
                    int off = y * dstRowSize + x2 * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
            }
        }
    }

    // Create mask from calibration region
    Mat calibMask = Mat::zeros(h, w, CV_8UC1);
    drawContours(calibMask, brightContours, maxIdx, Scalar(255), FILLED);

    // ---- Step 1: Find dark circles using contour analysis within calib region ----
    Mat innerGray = grayMat.clone();
    innerGray.setTo(128, calibMask == 0);

    // Small morphological cleanup kernel
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));

    // Try OTSU first, then adaptive threshold as fallback
    Mat darkBinary;
    threshold(innerGray, darkBinary, 0, 255, THRESH_BINARY_INV + THRESH_OTSU);
    darkBinary.setTo(0, calibMask == 0);
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);

    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    printf("OTSU: Found %d dark regions\n", (int)darkContours.size());

    // Also try adaptive threshold — compare which gives more circular candidates
    Mat darkAdaptive;
    adaptiveThreshold(innerGray, darkAdaptive, 255,
                      ADAPTIVE_THRESH_GAUSSIAN_C, THRESH_BINARY_INV, 31, 10);
    darkAdaptive.setTo(0, calibMask == 0);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_OPEN, kernel2);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_CLOSE, kernel2);

    std::vector<std::vector<Point>> adaptiveContours;
    findContours(darkAdaptive, adaptiveContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    printf("Adaptive: Found %d dark regions\n", (int)adaptiveContours.size());

    // Count circular candidates (circularity > 0.5, area > 50) for each method
    auto countCircular = [](const std::vector<std::vector<Point>>& contours) -> int {
        int count = 0;
        for (size_t i = 0; i < contours.size(); i++) {
            double area = contourArea(contours[i]);
            if (area < 50) continue;
            double perimeter = arcLength(contours[i], true);
            double circularity = (perimeter > 0) ? (4.0 * CV_PI * area) / (perimeter * perimeter) : 0;
            if (circularity > 0.5) count++;
        }
        return count;
    };

    int otsuCircular = countCircular(darkContours);
    int adaptiveCircular = countCircular(adaptiveContours);
    printf("Circular candidates: OTSU=%d, Adaptive=%d\n", otsuCircular, adaptiveCircular);

    // Use the method with more circular candidates
    bool usedAdaptive = false;
    if (adaptiveCircular > otsuCircular) {
        darkContours = adaptiveContours;
        usedAdaptive = true;
    }
    LOG_INFO("Dark contours: %d (used=%s, circular: OTSU=%d, Adaptive=%d)",
             (int)darkContours.size(), usedAdaptive ? "adaptive" : "OTSU",
             otsuCircular, adaptiveCircular);
    LOG_INFO("Dark contours: %d", (int)darkContours.size());

    // Filter and fit circles
    struct CircleInfo {
        Point2f center;
        float radius;
        float circularity;
        double area;
    };
    std::vector<CircleInfo> circleCandidates;

    for (size_t i = 0; i < darkContours.size(); i++) {
        double area = contourArea(darkContours[i]);
        if (area < 50) continue;

        Point2f center;
        float radius;
        minEnclosingCircle(darkContours[i], center, radius);

        double perimeter = arcLength(darkContours[i], true);
        double circularity = (perimeter > 0) ? (4.0 * CV_PI * area) / (perimeter * perimeter) : 0;

        CircleInfo info;
        info.center = center;
        info.radius = radius;
        info.circularity = (float)circularity;
        info.area = area;
        circleCandidates.push_back(info);
    }

    // Select 9 circles: prefer high circularity with consistent radius
    std::vector<CircleInfo> circularOnes;
    for (size_t i = 0; i < circleCandidates.size(); i++) {
        if (circleCandidates[i].circularity > 0.5f)
            circularOnes.push_back(circleCandidates[i]);
    }

    std::vector<CircleInfo> selected;

    if (circularOnes.size() >= 9) {
        // Find median radius among circular candidates
        std::sort(circularOnes.begin(), circularOnes.end(),
            [](const CircleInfo& a, const CircleInfo& b) { return a.radius < b.radius; });
        float medianR = circularOnes[circularOnes.size() / 2].radius;

        // Take circles within 30% of median radius
        for (size_t i = 0; i < circularOnes.size(); i++) {
            if (fabs(circularOnes[i].radius - medianR) / medianR < 0.3f)
                selected.push_back(circularOnes[i]);
        }

        if (selected.size() > 9) {
            std::sort(selected.begin(), selected.end(),
                [](const CircleInfo& a, const CircleInfo& b) { return a.circularity > b.circularity; });
            selected.resize(9);
        }
        LOG_INFO("Selected %d from %d circular candidates (median R=%.1f)",
                 (int)selected.size(), (int)circularOnes.size(), medianR);
    }

    // Fallback: if not enough from circular filter, try the other threshold method
    if (selected.size() < 9 && !usedAdaptive) {
        LOG_WARN("OTSU gave only %d selected, retrying with adaptive threshold...", (int)selected.size());
        circleCandidates.clear();
        circularOnes.clear();
        selected.clear();

        for (size_t i = 0; i < adaptiveContours.size(); i++) {
            double area = contourArea(adaptiveContours[i]);
            if (area < 50) continue;
            Point2f center; float radius;
            minEnclosingCircle(adaptiveContours[i], center, radius);
            double perimeter = arcLength(adaptiveContours[i], true);
            double circularity = (perimeter > 0) ? (4.0 * CV_PI * area) / (perimeter * perimeter) : 0;
            CircleInfo info;
            info.center = center; info.radius = radius;
            info.circularity = (float)circularity; info.area = area;
            circleCandidates.push_back(info);
            if (circularity > 0.5f) circularOnes.push_back(info);
        }
        usedAdaptive = true;
        LOG_INFO("Adaptive retry: %d candidates, %d circular", 
                 (int)circleCandidates.size(), (int)circularOnes.size());

        // Re-run circular selection on adaptive data
        if (circularOnes.size() >= 9) {
            std::sort(circularOnes.begin(), circularOnes.end(),
                [](const CircleInfo& a, const CircleInfo& b) { return a.radius < b.radius; });
            float medianR = circularOnes[circularOnes.size() / 2].radius;
            for (size_t i = 0; i < circularOnes.size() && selected.size() < 9; i++) {
                if (fabs(circularOnes[i].radius - medianR) / medianR < 0.3f)
                    selected.push_back(circularOnes[i]);
            }
            if (selected.size() > 9) {
                std::sort(selected.begin(), selected.end(),
                    [](const CircleInfo& a, const CircleInfo& b) { return a.circularity > b.circularity; });
                selected.resize(9);
            }
            LOG_INFO("Adaptive selected: %d circles (median R=%.1f)", (int)selected.size(), medianR);
        }
    }

    // If still not enough, relax criteria
    if (selected.size() < 9) {
        LOG_WARN("Only %d circles, trying relaxed filter...", (int)selected.size());

        float targetR = 0;
        if (!circularOnes.empty()) {
            std::sort(circularOnes.begin(), circularOnes.end(),
                [](const CircleInfo& a, const CircleInfo& b) { return a.radius < b.radius; });
            targetR = circularOnes[circularOnes.size() / 2].radius;
        }

        std::sort(circleCandidates.begin(), circleCandidates.end(),
            [](const CircleInfo& a, const CircleInfo& b) { return a.area > b.area; });

        for (size_t i = 0; i < circleCandidates.size() && selected.size() < 9; i++) {
            // Skip duplicates
            bool dup = false;
            for (size_t j = 0; j < selected.size(); j++) {
                float d = norm(circleCandidates[i].center - selected[j].center);
                if (d < (targetR > 0 ? targetR * 0.5f : 50)) { dup = true; break; }
            }
            if (dup) continue;

            if (targetR > 0) {
                float rRatio = fabs(circleCandidates[i].radius - targetR) / targetR;
                if (rRatio < 0.3f || circleCandidates[i].circularity > 0.3f)
                    selected.push_back(circleCandidates[i]);
            } else {
                selected.push_back(circleCandidates[i]);
            }
        }
        LOG_INFO("After relaxed filter: %d circles", (int)selected.size());
    }

    if (selected.size() < 3) {
        MessageBoxA(NULL, "Not enough circles detected!", "Error", MB_OK);
        return;
    }

    // ---- Step 2: Crosshair refinement within each circle ----
    int foundCount = 0;
    for (int i = 0; i < (int)selected.size() && foundCount < 9; i++) {
        Point2f roughCenter = selected[i].center;
        float radius = selected[i].radius;
        int cx = (int)round(roughCenter.x);
        int cy = (int)round(roughCenter.y);
        int searchR = (int)(radius * 0.8);

        LOG_DEBUG("Circle %d: rough=(%.1f, %.1f), r=%.1f",
                  foundCount, roughCenter.x, roughCenter.y, radius);

        int x0 = std::max(0, cx - searchR);
        int y0 = std::max(0, cy - searchR);
        int x1 = std::min(w - 1, cx + searchR);
        int y1 = std::min(h - 1, cy + searchR);
        if (x1 <= x0 || y1 <= y0) continue;

        Mat roi = grayMat(Range(y0, y1 + 1), Range(x0, x1 + 1));
        int roiCx = cx - x0, roiCy = cy - y0;

        // Sample center vs ring to determine crosshair type
        int centerVal = 0;
        for (int dy = -2; dy <= 2; dy++) {
            for (int dx = -2; dx <= 2; dx++) {
                int nx = roiCx + dx, ny = roiCy + dy;
                if (nx >= 0 && nx < roi.cols && ny >= 0 && ny < roi.rows)
                    centerVal += roi.at<uchar>(ny, nx);
            }
        }
        centerVal /= 25;

        int ringVal = 0, samples = 0;
        for (int dy = -searchR; dy <= searchR; dy += 3) {
            for (int dx = -searchR; dx <= searchR; dx += 3) {
                float dist = sqrt((float)(dx*dx + dy*dy));
                if (dist > radius * 0.3f && dist < radius * 0.9f) {
                    int nx = roiCx + dx, ny = roiCy + dy;
                    if (nx >= 0 && nx < roi.cols && ny >= 0 && ny < roi.rows) {
                        ringVal += roi.at<uchar>(ny, nx);
                        samples++;
                    }
                }
            }
        }
        if (samples > 0) ringVal /= samples;

        bool brightCrosshair = (centerVal > ringVal);
        LOG_DEBUG("  centerVal=%d, ringVal=%d, brightCrosshair=%d", centerVal, ringVal, brightCrosshair);

        if (brightCrosshair) {
            // Weighted centroid of bright pixels (crosshair lines)
            float sumX = 0, sumW = 0;
            for (int dx = -searchR; dx <= searchR; dx++) {
                int nx = roiCx + dx;
                if (nx < 0 || nx >= roi.cols) continue;
                int val = 0;
                for (int dy2 = -1; dy2 <= 1; dy2++) {
                    int ny = roiCy + dy2;
                    if (ny >= 0 && ny < roi.rows) val += roi.at<uchar>(ny, nx);
                }
                val /= 3;
                if (val > ringVal) {
                    sumX += nx * val;
                    sumW += val;
                }
            }

            float sumY = 0;
            sumW = 0;
            for (int dy = -searchR; dy <= searchR; dy++) {
                int ny = roiCy + dy;
                if (ny < 0 || ny >= roi.rows) continue;
                int val = 0;
                for (int dx2 = -1; dx2 <= 1; dx2++) {
                    int nx = roiCx + dx2;
                    if (nx >= 0 && nx < roi.cols) val += roi.at<uchar>(ny, nx);
                }
                val /= 3;
                if (val > ringVal) {
                    sumY += ny * val;
                    sumW += val;
                }
            }

            if (sumW > 0) {
                pts[foundCount].x = sumX / sumW + x0;
                pts[foundCount].y = sumY / sumW + y0;
            } else {
                pts[foundCount].x = roughCenter.x;
                pts[foundCount].y = roughCenter.y;
            }
        } else {
            // Dark circle on bright background: centroid of dark pixels
            float thresh = (centerVal + ringVal) / 2.0f;
            float sumX = 0, sumY = 0, sumW = 0;
            for (int dy = -searchR; dy <= searchR; dy++) {
                for (int dx = -searchR; dx <= searchR; dx++) {
                    int nx = roiCx + dx, ny = roiCy + dy;
                    if (nx < 0 || nx >= roi.cols || ny < 0 || ny >= roi.rows) continue;
                    if (roi.at<uchar>(ny, nx) < thresh) {
                        sumX += nx;
                        sumY += ny;
                        sumW += 1;
                    }
                }
            }
            if (sumW > 0) {
                pts[foundCount].x = sumX / sumW + x0;
                pts[foundCount].y = sumY / sumW + y0;
            } else {
                pts[foundCount].x = roughCenter.x;
                pts[foundCount].y = roughCenter.y;
            }
        }
        LOG_DEBUG("  Final: (%.1f, %.1f)", pts[foundCount].x, pts[foundCount].y);
        foundCount++;
    }

    // ---- Step 3: Sort into 3x3 grid (by Y rows, then X columns) ----
    for (int i = 0; i < foundCount; i++) {
        for (int j = i + 1; j < foundCount; j++) {
            if (pts[j].y < pts[i].y) {
                Point2D tmp = pts[i]; pts[i] = pts[j]; pts[j] = tmp;
            }
        }
    }

    if (foundCount > 3) {
        // Find the 2 largest Y gaps to split into 3 rows
        float maxGap = 0, secondGap = 0;
        int maxGapIdx = 0, secondGapIdx = -1;
        for (int i = 0; i < foundCount - 1; i++) {
            float gap = fabs(pts[i+1].y - pts[i].y);
            if (gap > maxGap) {
                secondGap = maxGap; secondGapIdx = maxGapIdx;
                maxGap = gap; maxGapIdx = i;
            } else if (gap > secondGap) {
                secondGap = gap; secondGapIdx = i;
            }
        }

        int split1 = std::min(maxGapIdx, secondGapIdx);
        int split2 = std::max(maxGapIdx, secondGapIdx);

        auto sortRow = [&](int start, int end) {
            for (int i = start; i < end; i++) {
                for (int j = i + 1; j < end; j++) {
                    if (pts[j].x < pts[i].x) {
                        Point2D tmp = pts[i]; pts[i] = pts[j]; pts[j] = tmp;
                    }
                }
            }
        };

        sortRow(0, split1 + 1);
        sortRow(split1 + 1, split2 + 1);
        sortRow(split2 + 1, foundCount);
    } else {
        for (int i = 0; i < foundCount; i++) {
            for (int j = i + 1; j < foundCount; j++) {
                if (pts[j].x < pts[i].x) {
                    Point2D tmp = pts[i]; pts[i] = pts[j]; pts[j] = tmp;
                }
            }
        }
    }

    *count = std::min(9, foundCount);

    LOG_INFO("Circle detection complete: %d points", *count);
    for (int i = 0; i < *count; i++) {
        LOG_DEBUG("  Point %d: (%.1f, %.1f)", i, pts[i].x, pts[i].y);
    }

    char msg[512];
    sprintf_s(msg, sizeof(msg), "HoughCircles+Crosshair: %d/%d points\n", *count, foundCount);
    for (int i = 0; i < *count; i++) {
        char line[64];
        sprintf_s(line, sizeof(line), "%d: (%.1f, %.1f)\n", i + 1, pts[i].x, pts[i].y);
        strcat_s(msg, sizeof(msg), line);
    }
    MessageBoxA(NULL, msg, "Detection Results", MB_OK);
}

// ========== OpenCV-based Adaptive Blob Detection ==========
// Replaces BlobAnalysisPro with pure OpenCV: adaptive threshold + contour + circle fitting
void BlobDetectCircles(Image* img, Point2D* pts, int* count) {
    LOG_INFO("=== Starting OpenCV Adaptive Blob Detection ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);

    *count = 0;

    int w = img->width, h = img->height;

    // Convert Image to cv::Mat (grayscale)
    Mat grayMat;
    if (img->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;
        Mat tmp(h, w, CV_8UC1, img->data, pitch);
        grayMat = tmp.clone();
        tmp.release();
    } else {
        int srcRowSize = ((w * img->channels + 3) / 4) * 4;
        Mat colorMat(h, w, CV_8UC3, img->data, srcRowSize);
        cvtColor(colorMat, grayMat, COLOR_BGR2GRAY);
        colorMat.release();
    }

    printf("=== Adaptive Blob Detection (OpenCV) ===\n");

    // ========== Step 1: OTSU threshold to find bright calibration region ==========
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(5, 5), 1.0);

    Mat brightBinary;
    threshold(blurred, brightBinary, 0, 255, THRESH_BINARY + THRESH_OTSU);

    // Morphological cleanup
    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(7, 7));
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);

    // Find the largest bright contour (calibration area)
    std::vector<std::vector<Point>> brightContours;
    findContours(brightBinary, brightContours, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE);

    if (brightContours.empty()) {
        printf("No bright region found\n");
        LOG_ERROR("No bright calibration region found");
        return;
    }

    int maxIdx = 0;
    double maxArea = 0;
    for (size_t i = 0; i < brightContours.size(); i++) {
        double a = contourArea(brightContours[i]);
        if (a > maxArea) { maxArea = a; maxIdx = (int)i; }
    }
    printf("Found %d bright blobs, largest area: %.0f pixels\n",
           (int)brightContours.size(), maxArea);

    // Create mask from the calibration region
    Mat calibMask = Mat::zeros(h, w, CV_8UC1);
    drawContours(calibMask, brightContours, maxIdx, Scalar(255), FILLED);

    // Draw calib region border on original image
    Rect bbox = boundingRect(brightContours[maxIdx]);
    int dstRowSize = ((w * img->channels + 3) / 4) * 4;

    // Green border on original image
    for (int x = bbox.x; x <= bbox.x + bbox.width; x++) {
        for (int t = -2; t <= 2; t++) {
            int y1 = bbox.y + t, y2 = bbox.y + bbox.height + t;
            if (x >= 0 && x < w) {
                if (y1 >= 0 && y1 < h) {
                    int off = y1 * dstRowSize + x * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
                if (y2 >= 0 && y2 < h) {
                    int off = y2 * dstRowSize + x * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
            }
        }
    }
    for (int y = bbox.y; y <= bbox.y + bbox.height; y++) {
        for (int t = -2; t <= 2; t++) {
            int x1 = bbox.x + t, x2 = bbox.x + bbox.width + t;
            if (y >= 0 && y < h) {
                if (x1 >= 0 && x1 < w) {
                    int off = y * dstRowSize + x1 * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
                if (x2 >= 0 && x2 < w) {
                    int off = y * dstRowSize + x2 * img->channels;
                    if (img->channels == 1) img->data[off] = 255;
                    else { img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0; }
                }
            }
        }
    }

    printf("CalibRegion: bbox=(%d,%d)-(%d,%d)\n", bbox.x, bbox.y, bbox.x+bbox.width, bbox.y+bbox.height);

    // ========== Step 2: Find dark circles (holes) within calibration region ==========
    // Mask grayMat to only keep pixels inside calib region
    Mat innerGray = grayMat.clone();
    innerGray.setTo(128, calibMask == 0);

    // OTSU to find dark regions inside calib area
    Mat darkBinary;
    threshold(innerGray, darkBinary, 0, 255, THRESH_BINARY_INV + THRESH_OTSU);
    darkBinary.setTo(0, calibMask == 0);

    // Small morphological cleanup
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);

    // Find dark contours (holes/circles)
    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    printf("Found %d dark regions (candidate holes)\n", (int)darkContours.size());

    // ========== Step 3: Filter and fit circles ==========
    std::vector<Point2D> circleCenters;

    // Sort by area descending, take top candidates
    std::vector<std::pair<double, int>> sortedHoles;
    for (size_t i = 0; i < darkContours.size(); i++) {
        double a = contourArea(darkContours[i]);
        if (a > 20) sortedHoles.push_back(std::make_pair(a, (int)i));
    }
    std::sort(sortedHoles.begin(), sortedHoles.end(),
         [](const std::pair<double, int>& a, const std::pair<double, int>& b) { return a.first > b.first; });

    for (size_t idx = 0; idx < sortedHoles.size() && circleCenters.size() < 9; idx++) {
        const std::vector<Point>& contour = darkContours[sortedHoles[idx].second];
        if ((int)contour.size() < 8) continue;

        // Fit minimum enclosing circle for center
        Point2f center;
        float radius;
        minEnclosingCircle(contour, center, radius);

        // Filter by circularity (optional but helps reject noise)
        double area = contourArea(contour);
        double perimeter = arcLength(contour, true);
        double circularity = (perimeter > 0) ? (4.0 * CV_PI * area) / (perimeter * perimeter) : 0;

        // Accept if reasonably circular (circularity > 0.5) or just use all top candidates
        Point2D pt;
        pt.x = center.x;
        pt.y = center.y;
        circleCenters.push_back(pt);

        printf("  Hole %d: center=(%.2f, %.2f), radius=%.1f, area=%.0f, circularity=%.2f\n",
               (int)circleCenters.size(), center.x, center.y, radius, area, circularity);

        // Draw hole overlay on original image
        Rect holeRect = boundingRect(contour);
        for (int y = holeRect.y; y < holeRect.y + holeRect.height && y < h; y++) {
            for (int x = holeRect.x; x < holeRect.x + holeRect.width && x < w; x++) {
                if (darkBinary.at<uchar>(y, x) > 0 && x >= 0 && y >= 0) {
                    int off = y * dstRowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = (unsigned char)(img->data[off] * 0.5);
                    } else {
                        img->data[off] = (unsigned char)(img->data[off] * 0.5);
                        img->data[off + 1] = (unsigned char)(img->data[off + 1] * 0.5);
                        img->data[off + 2] = (unsigned char)(std::min(255, img->data[off + 2] + 128));
                    }
                }
            }
        }
    }

    // Sort by Y then X
    std::sort(circleCenters.begin(), circleCenters.end(), [](const Point2D& a, const Point2D& b) {
        if (fabs(a.y - b.y) > 20) return a.y < b.y;
        return a.x < b.x;
    });

    // Take up to 9 points
    *count = std::min(9, (int)circleCenters.size());
    for (int i = 0; i < *count && i < 9; i++) {
        pts[i] = circleCenters[i];
    }

    printf("=== Detection Complete: %d points ===\n", *count);

    // Show results
    char msg[512];
    sprintf_s(msg, sizeof(msg), "OpenCV Adaptive Detection found %d points", *count);
    for (int i = 0; i < *count && i < 9; i++) {
        char line[64];
        sprintf_s(line, sizeof(line), "\n%d: (%.1f, %.1f)", i + 1, pts[i].x, pts[i].y);
        strcat_s(msg, sizeof(msg), line);
    }
    MessageBoxA(NULL, msg, "Blob Detection Results", MB_OK);
}

void DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray) {
    if (!img || !img->data) return;
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;
    for (int i = 0; i < count; i++) {
        int cx = (int)pts[i].x, cy = (int)pts[i].y;
        // Draw crosshair at detected point
        for (int x = cx - 15; x <= cx + 15; x++) {
            for (int t = -3; t <= 3; t++) {
                int y = cy + t;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int off = y * rowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = (unsigned char)gray;
                    } else {
                        img->data[off] = (unsigned char)gray;      // B
                        img->data[off + 1] = (unsigned char)gray;  // G
                        img->data[off + 2] = (unsigned char)gray;  // R
                    }
                }
            }
        }
        for (int y = cy - 15; y <= cy + 15; y++) {
            for (int t = -3; t <= 3; t++) {
                int x = cx + t;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int off = y * rowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = (unsigned char)gray;
                    } else {
                        img->data[off] = (unsigned char)gray;      // B
                        img->data[off + 1] = (unsigned char)gray;  // G
                        img->data[off + 2] = (unsigned char)gray;  // R
                    }
                }
            }
        }
    }
}

// ========== Calibration (Least Squares via OpenCV) ==========
int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    LOG_INFO("=== Starting 9-Point Calibration (cv::solve) ===");
    LOG_DEBUG("Number of points: %d", n);

    if (n < 3) {
        LOG_ERROR("Insufficient points for calibration (need at least 3)");
        return 0;
    }

    LOG_DEBUG("Image Points -> World Points:");
    for (int i = 0; i < n; i++) {
        LOG_DEBUG("  %d: (%.2f, %.2f) -> (%.2f, %.2f)",
            i, imagePts[i].x, imagePts[i].y, worldPts[i].x, worldPts[i].y);
    }

    // Build overdetermined system: A * X = B
    // For each point i:
    //   [xi, yi, 1,  0,  0, 0] [a]   [Xi]
    //   [ 0,  0, 0, xi, yi, 1] [b] = [Yi]
    //                           [c]
    //                           [d]
    //                           [e]
    //                           [f]
    Mat A(2 * n, 6, CV_64F);
    Mat B(2 * n, 1, CV_64F);

    for (int i = 0; i < n; i++) {
        double xi = imagePts[i].x, yi = imagePts[i].y;
        double Xi = worldPts[i].x, Yi = worldPts[i].y;

        A.at<double>(2 * i,     0) = xi;
        A.at<double>(2 * i,     1) = yi;
        A.at<double>(2 * i,     2) = 1.0;
        A.at<double>(2 * i,     3) = 0;
        A.at<double>(2 * i,     4) = 0;
        A.at<double>(2 * i,     5) = 0;

        A.at<double>(2 * i + 1, 0) = 0;
        A.at<double>(2 * i + 1, 1) = 0;
        A.at<double>(2 * i + 1, 2) = 0;
        A.at<double>(2 * i + 1, 3) = xi;
        A.at<double>(2 * i + 1, 4) = yi;
        A.at<double>(2 * i + 1, 5) = 1.0;

        B.at<double>(2 * i,     0) = Xi;
        B.at<double>(2 * i + 1, 0) = Yi;
    }

    // Solve using SVD (DECOMP_SVD handles rank-deficient cases gracefully)
    Mat X;
    bool ok = cv::solve(A, B, X, DECOMP_SVD);

    if (!ok || X.empty()) {
        LOG_ERROR("cv::solve failed - calibration failed");
        return 0;
    }

    trans->a = X.at<double>(0);
    trans->b = X.at<double>(1);
    trans->c = X.at<double>(2);
    trans->d = X.at<double>(3);
    trans->e = X.at<double>(4);
    trans->f = X.at<double>(5);

    LOG_INFO("Calibration completed successfully!");
    LOG_INFO("Transform parameters:");
    LOG_INFO("  X = %.6f*x + %.6f*y + %.4f", trans->a, trans->b, trans->c);
    LOG_INFO("  Y = %.6f*x + %.6f*y + %.4f", trans->d, trans->e, trans->f);

    return 1;
}

Point2D ImageToWorld(Point2D pixel, AffineTransform trans) {
    Point2D world;
    world.x = trans.a * pixel.x + trans.b * pixel.y + trans.c;
    world.y = trans.d * pixel.x + trans.e * pixel.y + trans.f;
    return world;
}

// ========== 网格展示工具函数 ==========

// 将 OpenCV Mat（单通道灰度或3通道BGR）转换为 Image（3通道BGR）
// 用于网格展示，避免8位灰度在 SetDIBitsToDevice 中出现伪彩色
static void MatToImageBGR(const Mat& src, Image* dst) {
    Mat bgr;
    if (src.channels() == 1) {
        cvtColor(src, bgr, COLOR_GRAY2BGR);
    } else {
        bgr = src;
    }
    int w = bgr.cols, h = bgr.rows;
    int rowSize = w * 3;
    if (dst->data) free(dst->data);
    dst->width = w;
    dst->height = h;
    dst->channels = 3;
    dst->data = (unsigned char*)malloc(rowSize * h);
    memcpy(dst->data, bgr.data, rowSize * h);
}

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
// ========== 创建轨迹网格调试窗口 ==========
// 窗口 WM_PAINT 中渲染 g_trajImage（已合成网格大图）
// 然后用 GDI TextOutA 在每个格子顶部绘制步骤标签
void CreateTrajectoryWindow(HINSTANCE hInstance) {
    LOG_INFO("CreateTrajectoryWindow: g_hwndTraj=%p, hInstance=%p", g_hwndTraj, hInstance);
    if (g_hwndTraj) {
        // 窗口已存在，直接刷新
        ShowWindow(g_hwndTraj, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndTraj);
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        LOG_INFO("CreateTrajectoryWindow: existing window refreshed");
        return;
    }

    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);

            if (g_trajImage.data) {
                // ---- 1. 用 SetDIBitsToDevice 渲染网格大图（1通道灰度平面图）----
                // 构造 BITMAPINFO：包含 256 色灰度调色板，避免伪彩色
                BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
                memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
                pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                pbmi->bmiHeader.biWidth = g_trajImage.width;
                pbmi->bmiHeader.biHeight = -g_trajImage.height;  // 负值 = 自顶向下
                pbmi->bmiHeader.biPlanes = 1;
                pbmi->bmiHeader.biBitCount = 8;
                pbmi->bmiHeader.biCompression = BI_RGB;
                for (int i = 0; i < 256; i++) {
                    pbmi->bmiColors[i].rgbBlue = (unsigned char)i;
                    pbmi->bmiColors[i].rgbGreen = (unsigned char)i;
                    pbmi->bmiColors[i].rgbRed = (unsigned char)i;
                    pbmi->bmiColors[i].rgbReserved = 0;
                }

                // SetDIBitsToDevice 要求行步长 4 字节对齐，需临时转换
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

                // ---- 2. 在每个格子顶部用 GDI 渲染步骤标签 ----
                // 计算格子尺寸（与 ShowTrajectoryImage 中的布局一致）
                int srcImgW = g_stepImages[0].width > 0 ? g_stepImages[0].width : 800;
                int srcImgH = g_stepImages[0].height > 0 ? g_stepImages[0].height : 600;
                int cellW = srcImgW / GRID_COLS;
                int cellH = (int)(cellW * (double)srcImgH / srcImgW + 0.5);  // 保持原图长宽比
                int cellTotalH = LABEL_HEIGHT + cellH;

                SetBkMode(hdc, TRANSPARENT);
                HFONT hFont = CreateFontA(14, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                    DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                    CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, "Consolas");
                HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);
                SetTextColor(hdc, RGB(255, 255, 0));  // 黄色标签

                for (int s = 0; s < STEP_COUNT; s++) {
                    int col = s % GRID_COLS;
                    int row = s / GRID_COLS;
                    int labelX = GRID_PADDING + col * (cellW + GRID_PADDING);
                    int labelY = GRID_PADDING + row * (cellTotalH + GRID_PADDING);
                    TextOutA(hdc, labelX + 4, labelY + 3, g_stepLabels[s], (int)strlen(g_stepLabels[s]));
                }

                SelectObject(hdc, hOldFont);
                DeleteObject(hFont);
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
            // 同时释放步骤图像
            FreeStepImages();
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    };
    wc.hInstance = hInstance;
    wc.lpszClassName = L"TrajectoryWindowClass";
    RegisterClass(&wc);

    // 计算窗口初始大小
    int initW = g_trajImage.width > 0 ? g_trajImage.width + 20 : 640;
    int initH = g_trajImage.height > 0 ? g_trajImage.height + 50 : 480;

    g_hwndTraj = CreateWindowEx(0, L"TrajectoryWindowClass", L"Detection Steps (2x3 Grid)",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
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


// Draw trajectory on image
// 16 种高饱和度颜色（BGR格式），确保在黑底上醒目
static const unsigned char BAR_COLORS[16][3] = {
    {0,   0,   255},   // 0: 红
    {0,   255, 0  },   // 1: 绿
    {255, 0,   0  },   // 2: 蓝
    {0,   255, 255},   // 3: 黄
    {255, 0,   255},   // 4: 青
    {255, 255, 0  },   // 5: 洋红
    {0,   128, 255},   // 6: 橙
    {255, 128, 0  },   // 7: 浅蓝
    {0,   255, 128},   // 8: 春绿
    {128, 0,   255},   // 9: 紫
    {255, 0,   128},   // 10: 玫红
    {0,   128, 128},   // 11: 橄榄
    {128, 255, 0  },   // 12: 青柠
    {128, 0,   0  },   // 13: 深红
    {0,   0,   128},   // 14: 深绿
    {128, 128, 255},   // 15: 粉
};

void DrawTrajectoryColored(Image* img, Point2D* trajPixels, int count, int* barIds) {
    if (!img || !img->data || count < 2) return;
    if (img->channels < 3) return;  // Need 3-channel for color
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;

    for (int i = 0; i < count; i++) {
        int barIdx = barIds ? barIds[i] % 16 : 0;
        const unsigned char* color = BAR_COLORS[barIdx];
        int cx = (int)trajPixels[i].x;
        int cy = (int)trajPixels[i].y;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int off = y * rowSize + x * img->channels;
                    img->data[off]     = color[0];  // B
                    img->data[off + 1] = color[1];  // G
                    img->data[off + 2] = color[2];  // R
                }
            }
        }
    }
}

// ========== 显示网格调试窗口 ==========

// 将 ShowTrajectoryImage 改为：刷新网格窗口，窗口大小根据 g_stepImages 计算
// 网格布局：GRID_COLS 列 × GRID_ROWS 行，每格保持原图长宽比
// 注意：网格大图使用 1 通道灰度（平面图），不转 3 通道
void ShowTrajectoryImage(Image* src, Point2D* trajPixels, int trajCount) {
    // 计算网格总尺寸
    int imgW = src->width;
    int imgH = src->height;
    LOG_INFO("ShowTrajectoryImage: src=%dx%d, trajCount=%d", imgW, imgH, trajCount);
    // 每个格子的图片区域尺寸：宽度 = 原图宽 / 列数，高度按原图比例计算
    int cellW = imgW / GRID_COLS;
    int cellH = (int)(cellW * (double)imgH / imgW + 0.5);  // 保持原图长宽比
    LOG_INFO("ShowTrajectoryImage: cellW=%d, cellH=%d", cellW, cellH);
    // 每格总高度 = 标签 + 图片
    int cellTotalH = LABEL_HEIGHT + cellH;
    // 网格总尺寸
    int gridW = GRID_COLS * cellW + (GRID_COLS + 1) * GRID_PADDING;
    int gridH = GRID_ROWS * cellTotalH + (GRID_ROWS + 1) * GRID_PADDING;
    LOG_INFO("ShowTrajectoryImage: gridW=%d, gridH=%d", gridW, gridH);

    // 生成合成的大图（1 通道灰度平面图）
    int dstChannels = 1;
    int dstRowSize = gridW;
    int totalSize = dstRowSize * gridH;

    if (g_trajImage.data) free(g_trajImage.data);
    g_trajImage.width = gridW;
    g_trajImage.height = gridH;
    g_trajImage.channels = dstChannels;
    g_trajImage.data = (unsigned char*)malloc(totalSize);
    LOG_INFO("ShowTrajectoryImage: malloc %d bytes at %p", totalSize, g_trajImage.data);
    memset(g_trajImage.data, 0, totalSize);  // 黑色背景

    // 将每步的 Image 缩放后绘制到对应网格位置
    for (int s = 0; s < STEP_COUNT; s++) {
        Image* stepImg = &g_stepImages[s];
        LOG_INFO("  step[%d]: data=%p, %dx%d, ch=%d", s, stepImg->data, stepImg->width, stepImg->height, stepImg->channels);
        if (!stepImg->data) continue;

        // 计算该格在网格中的位置
        int col = s % GRID_COLS;
        int row = s / GRID_COLS;
        int cellX = GRID_PADDING + col * (cellW + GRID_PADDING);
        int cellY = GRID_PADDING + row * (cellTotalH + GRID_PADDING) + LABEL_HEIGHT;

        // 将 stepImg 缩放绘制到格子区域（最近邻缩放）
        int srcW = stepImg->width;
        int srcH = stepImg->height;

        for (int dy = 0; dy < cellH; dy++) {
            int sy = dy * srcH / cellH;  // 映射到源图 y
            for (int dx = 0; dx < cellW; dx++) {
                int sx = dx * srcW / cellW;  // 映射到源图 x
                int dstX = cellX + dx;
                int dstY = cellY + dy;
                if (dstX >= gridW || dstY >= gridH) continue;

                unsigned char gray = 0;
                if (stepImg->channels == 1) {
                    // 1 通道：直接取灰度值
                    gray = stepImg->data[sy * srcW + sx];
                } else {
                    // 3 通道（彩色轨迹）：转灰度 0.299R + 0.587G + 0.114B
                    int srcOff = (sy * srcW + sx) * 3;
                    unsigned char b = stepImg->data[srcOff];
                    unsigned char g = stepImg->data[srcOff + 1];
                    unsigned char r = stepImg->data[srcOff + 2];
                    gray = (unsigned char)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f);
                }
                g_trajImage.data[dstY * dstRowSize + dstX] = gray;
            }
        }

        // 在格子底部画一条分割线（灰色，1像素）
        int lineY = cellY + cellH;
        if (lineY < gridH) {
            for (int x = cellX; x < cellX + cellW && x < gridW; x++) {
                g_trajImage.data[lineY * dstRowSize + x] = 80;
            }
        }
    }

    // 在标签区域写文字（用简单的点阵方式，避免依赖 GDI 在图片中渲染）
    // 文字渲染由 WM_PAINT 中用 GDI TextOutA 完成

    LOG_INFO("ShowTrajectoryImage: grid image complete, calling CreateTrajectoryWindow");

    // 更新窗口
    if (g_hwndTraj) {
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        SetWindowPos(g_hwndTraj, HWND_TOP, 0, 0,
            gridW + 20, gridH + 50, SWP_NOMOVE);
    }
    LOG_INFO("ShowTrajectoryImage: done");
}

// ========== Trajectory Detection (FitShape removed, delegate to OpenCV) ==========

// Forward declaration — defined below in DetectTrajectoryOpenCV
void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count);

void DetectTrajectoryFitShape(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== FitShape Trajectory (deprecated, using OpenCV) ===");
    DetectTrajectoryOpenCV(img, trajPixels, count);
}

// Legacy FitShape code removed — the following block is intentionally empty.
// Original function used XVFitPathToStripe from FitShape library.
// All FitShape types (XVImage, XVPathFittingField, XVStripeScanParams, etc.)
// are no longer needed. If you need the old implementation, check git history.

#if 0 // Disabled: original FitShape implementation
void DetectTrajectoryFitShape_LEGACY(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== Starting FitShape Trajectory Detection (Stripe) ===");
    LOG_DEBUG("Input image: %dx%d", img->width, img->height);
    
    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }
    
    // Convert Image to XVImage format
    XVImage xvImage;
    memset(&xvImage, 0, sizeof(xvImage));
    xvImage.depth = 1;
    xvImage.width = img->width;
    xvImage.height = img->height;
    xvImage.pitch = img->width;  // grayscale: pitch = width (1 byte per pixel)
    xvImage.type = XVL::UInt8;  // �ؼ�������ͼ������Ϊ8λ��ͨ��
    
    // Allocate grayscale buffer
    int graySize = img->width * img->height;
    unsigned char* grayData = (unsigned char*)malloc(graySize);
    if (!grayData) {
        LOG_ERROR("Failed to allocate grayscale buffer!");
        *count = 0;
        return;
    }
    
    if (img->channels == 1) {
        // Already grayscale, direct copy
        memcpy(grayData, img->data, graySize);
        xvImage.data = grayData;
        LOG_DEBUG("Image is grayscale, direct copy: %dx%d", img->width, img->height);
    } else {
        // Convert BGR to grayscale
        int srcRowSize = ((img->width * img->channels + 3) / 4) * 4;
        for (int y = 0; y < img->height; y++) {
            for (int x = 0; x < img->width; x++) {
                int srcOff = y * srcRowSize + x * img->channels;
                grayData[y * img->width + x] = (img->data[srcOff] + img->data[srcOff+1] + img->data[srcOff+2]) / 3;
            }
        }
        xvImage.data = grayData;
        LOG_DEBUG("Converted BGR to grayscale: %dx%d", img->width, img->height);
    }
    
    LOG_DEBUG("Converted to grayscale: %dx%d, pitch=%d", xvImage.width, xvImage.height, xvImage.pitch);
    
    // Setup fitting field (use whole image with center vertical axis)
    XVPathFittingField fitField;
    memset(&fitField, 0, sizeof(fitField));
    fitField.width = 100.0f;
    XVPoint2D axisStart = { 0, (float)(img->height / 2) };  // ��ͼ���м俪ʼ
    XVPoint2D axisEnd = { (float)img->width, (float)(img->height / 2) };  // ˮƽɨ��
    fitField.axis.arrayPoint2D.push_back(axisStart);
    fitField.axis.arrayPoint2D.push_back(axisEnd);
    
    LOG_DEBUG("FittingField: axis from (%.1f,%.1f) to (%.1f,%.1f), width=%.1f",
        axisStart.x, axisStart.y, axisEnd.x, axisEnd.y, fitField.width);
    
    // Setup alignment (NUL = no transformation)
    XVCoordinateSystem2D alignment;
    memset(&alignment, 0, sizeof(alignment));
    alignment.optional = XVOptionalType::NUL;
    
    // Setup stripe scan parameters
    XVStripeScanParams stripeParams;
    memset(&stripeParams, 0, sizeof(stripeParams));
    stripeParams.smoothStDev = 0.6f;
    stripeParams.interMetnod = XVInterMethod::Parabola;
    stripeParams.minMagnitude = 20.0f;  // ���������ֵ
    stripeParams.stripeType = XVStripeType::DARK;  // ��ⰵ�������켣ͨ���ǰ��ߣ�
    stripeParams.minStripeWidth = 1.0f;  // ��С��������
    stripeParams.maxStripeWidth = 50.0f;  // �����������
    
    // Setup input for XVFitPathToStripe
    XVFitPathToStripeIn in;
    memset(&in, 0, sizeof(in));
    in.inImage = &xvImage;
    in.inFittingField = fitField;
    in.inAlignment = alignment;
    in.inScanStep = 3;  // scan every 3 pixels for more points
    in.inScanWidth = 10;  // scan rectangle width
    in.inStripeScanParams = stripeParams;
    in.inStripeSelection = XVSelection::Best;
    in.inMaxInCompleteness = 0.9f;
    in.inFlag = true;
    in.inMaxLeakPointsNumber = 20;
    
    // Execute
    XVFitPathToStripeOut out;
    memset(&out, 0, sizeof(out));
    int result = XVFitPathToStripe(in, out);
    
    if (result == 0 && out.outStripePoints.size() > 0) {
        *count = 0;
        for (size_t i = 0; i < out.outStripePoints.size() && *count < 10000; i++) {
            trajPixels[*count].x = out.outStripePoints[i].x;
            trajPixels[*count].y = out.outStripePoints[i].y;
            (*count)++;
        }
        
        // Transform to world coordinates
        for (int i = 0; i < *count; i++) {
            g_trajWorld[i] = ImageToWorld(trajPixels[i], g_transform);
        }
        
        LOG_INFO("FitShape Stripe trajectory detection complete: %d points", *count);
        if (*count > 0) {
            LOG_DEBUG("  First point: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
                trajPixels[0].x, trajPixels[0].y, g_trajWorld[0].x, g_trajWorld[0].y);
            LOG_DEBUG("  Last point: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
                trajPixels[*count-1].x, trajPixels[*count-1].y, 
                g_trajWorld[*count-1].x, g_trajWorld[*count-1].y);
        }
    } else {
        *count = 0;
        LOG_ERROR("FitShape Stripe detection failed with code: %d", result);
        char buf[256];
        sprintf_s(buf, sizeof(buf), "FitShape Stripe detection failed: %d", result);
        MessageBoxA(NULL, buf, "Error", MB_OK);
    }
    
    // Cleanup
    free(grayData);
}
#endif // End of disabled FitShape legacy code

// ========== Trajectory Detection ==========
// Detect trajectory edge points - improved continuous detection
void DetectTrajectory(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== Starting Self Trajectory Detection ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);
    
    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }
    
    int srcRowSize = ((img->width * img->channels + 3) / 4) * 4;
    *count = 0;
    
    // Helper lambda to get grayscale pixel value
    auto getGray = [&](int x, int y) -> int {
        if (x < 0) x = 0; if (x >= img->width) x = img->width - 1;
        if (y < 0) y = 0; if (y >= img->height) y = img->height - 1;
        int off = y * srcRowSize + x * img->channels;
        if (img->channels == 1) {
            return img->data[off];
        } else {
            return (img->data[off] + img->data[off+1] + img->data[off+2]) / 3;
        }
    };
    
    int w = img->width, h = img->height;
    float* gradMag = (float*)malloc(w * h * sizeof(float));
    float* gradDirX = (float*)malloc(w * h * sizeof(float));
    float* gradDirY = (float*)malloc(w * h * sizeof(float));
    
    if (!gradMag || !gradDirX || !gradDirY) {
        LOG_ERROR("Failed to allocate gradient buffers!");
        if (gradMag) free(gradMag);
        if (gradDirX) free(gradDirX);
        if (gradDirY) free(gradDirY);
        *count = 0;
        return;
    }
    
    // Compute gradient for each pixel
    float gradThreshold = 20.0f;
    for (int y = 1; y < h - 1; y++) {
        for (int x = 1; x < w - 1; x++) {
            float gx = -getGray(x-1,y-1) + getGray(x+1,y-1)
                      -2*getGray(x-1,y) + 2*getGray(x+1,y)
                      -getGray(x-1,y+1) + getGray(x+1,y+1);
            float gy = -getGray(x-1,y-1) - 2*getGray(x,y-1) - getGray(x+1,y-1)
                      + getGray(x-1,y+1) + 2*getGray(x,y+1) + getGray(x+1,y+1);
            
            gradMag[y * w + x] = sqrtf(gx*gx + gy*gy);
            if (gradMag[y * w + x] > 0.001f) {
                gradDirX[y * w + x] = gx / gradMag[y * w + x];
                gradDirY[y * w + x] = gy / gradMag[y * w + x];
            } else {
                gradDirX[y * w + x] = 0;
                gradDirY[y * w + x] = 0;
            }
        }
    }
    
    // ========== Display Sobel Edge Image ==========
    // Find max gradient for normalization
    float maxMag = 0;
    for (int i = 0; i < w * h; i++) {
        if (gradMag[i] > maxMag) maxMag = gradMag[i];
    }
    
    // Create Sobel display image (24-bit)
    int sobelRowSize = ((w * 3 + 3) / 4) * 4;
    if (g_sobelImage.data) free(g_sobelImage.data);
    g_sobelImage.width = w;
    g_sobelImage.height = h;
    g_sobelImage.channels = 3;  // 24-bit (3 channels)
    g_sobelImage.data = (unsigned char*)malloc(sobelRowSize * h);
    
    // Normalize and convert to grayscale
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            unsigned char val = 0;
            if (maxMag > 0) {
                val = (unsigned char)(255.0f * gradMag[y * w + x] / maxMag);
            }
            int dstOff = y * sobelRowSize + x * 3;
            g_sobelImage.data[dstOff] = val;      // B
            g_sobelImage.data[dstOff + 1] = val; // G
            g_sobelImage.data[dstOff + 2] = val; // R
        }
    }
    
    // Create and show Sobel window
    extern HINSTANCE g_hInstance;
    CreateSobelWindow(g_hInstance);
    LOG_INFO("Sobel edge image created: %dx%d (max gradient: %.2f)", w, h, maxMag);
    
    // Threshold gradient to binary edge map
    unsigned char* edgeMap = (unsigned char*)malloc(w * h);
    for (int i = 0; i < w * h; i++) {
        edgeMap[i] = (gradMag[i] > gradThreshold) ? 255 : 0;
    }
    
    // Find starting point (leftmost high-gradient point)
    int startY = -1, startX = -1;
    for (int y = 1; y < h - 1 && startY < 0; y++) {
        for (int x = 1; x < w - 1; x++) {
            if (edgeMap[y * w + x]) {
                startY = y;
                startX = x;
                break;
            }
        }
    }
    
    if (startY < 0) {
        LOG_WARN("No edge points found in image!");
        free(gradMag); free(gradDirX); free(gradDirY); free(edgeMap);
        *count = 0;
        return;
    }
    
    // Follow the edge path using gradient direction
    bool* visited = (bool*)calloc(w * h, sizeof(bool));
    int maxCount = 10000;
    
    int cx = startX, cy = startY;
    while (cx >= 0 && cy >= 0 && cx < w && cy < h && *count < maxCount) {
        // Mark current point
        visited[cy * w + cx] = true;
        trajPixels[*count].x = cx;
        trajPixels[*count].y = cy;
        (*count)++;
        
        // Move along gradient direction (perpendicular to edge)
        float dx = gradDirX[cy * w + cx];
        float dy = gradDirY[cy * w + cx];
        
        // Step size for continuous detection
        int stepX = (fabs(dx) > 0.1f) ? (dx > 0 ? 1 : -1) : 0;
        int stepY = (fabs(dy) > 0.1f) ? (dy > 0 ? 1 : -1) : 0;
        
        // If both components are small, use gradient magnitude to find next edge
        if (stepX == 0 && stepY == 0) {
            // Search in perpendicular direction
            int nextX = -1, nextY = -1;
            int bestMag = gradThreshold;
            // Search perpendicular to gradient (along edge direction)
            for (int off = -5; off <= 5; off++) {
                int nx = cx + off;
                if (nx >= 0 && nx < w && !visited[cy * w + nx] && edgeMap[cy * w + nx]) {
                    if (gradMag[cy * w + nx] > bestMag) {
                        bestMag = gradMag[cy * w + nx];
                        nextX = nx;
                        nextY = cy;
                    }
                }
            }
            
            if (nextX < 0) {
                // Try vertical direction
                for (int off = -5; off <= 5; off++) {
                    int ny = cy + off;
                    if (ny >= 0 && ny < h && !visited[ny * w + cx] && edgeMap[ny * w + cx]) {
                        if (gradMag[ny * w + cx] > bestMag) {
                            bestMag = gradMag[ny * w + cx];
                            nextX = cx;
                            nextY = ny;
                        }
                    }
                }
            }
            
            if (nextX >= 0) {
                cx = nextX;
                cy = nextY;
            } else {
                break;  // End of path
            }
        } else {
            // Move along the edge (follow the line)
            int newX = cx + stepX;
            int newY = cy + stepY;
            
            // Check if new point is valid and not visited
            if (newX >= 0 && newX < w && newY >= 0 && newY < h && 
                !visited[newY * w + newX] && edgeMap[newY * w + newX]) {
                cx = newX;
                cy = newY;
            } else {
                // Try just horizontal
                if (newX >= 0 && newX < w && !visited[cy * w + newX] && edgeMap[cy * w + newX]) {
                    cx = newX;
                }
                // Try just vertical
                else if (newY >= 0 && newY < h && !visited[newY * w + cx] && edgeMap[newY * w + cx]) {
                    cy = newY;
                }
                else {
                    break;  // End of path
                }
            }
        }
    }
    
    free(visited);
    free(gradMag);
    free(gradDirX);
    free(gradDirY);
    free(edgeMap);
    
    // Transform pixel coordinates to world coordinates using calibration
    for (int i = 0; i < *count; i++) {
        g_trajWorld[i] = ImageToWorld(trajPixels[i], g_transform);
    }
    
    LOG_INFO("Self trajectory detection complete: %d points", *count);
    if (*count > 0) {
        LOG_DEBUG("  First point: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
            trajPixels[0].x, trajPixels[0].y, g_trajWorld[0].x, g_trajWorld[0].y);
        LOG_DEBUG("  Last point: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
            trajPixels[*count-1].x, trajPixels[*count-1].y, 
            g_trajWorld[*count-1].x, g_trajWorld[*count-1].y);
    }
}

// ========== Helper: Generate Stadium/Capsule Shape ==========
// Stadium shape: rectangle with two semicircles at ends
// Parameters:
//   center: center point
//   length: length of middle rectangle section
//   width: width/diameter (same for both ends)
//   angle: rotation angle in degrees
//   pointCount: total points to generate
//   outPoints: output vector for generated points
static void GenerateStadiumShape(Point2f center, float length, float width, 
                                  float angle, int pointCount, std::vector<Point>& outPoints) {
    float radius = width / 2.0f;  // semicircle radius
    float angleRad = angle * (float)CV_PI / 180.0f;
    
    // Calculate points per section
    // Semicircle perimeter = PI * radius, rectangle sides = 2 * length
    float totalPerimeter = 2.0f * (CV_PI * radius + length);
    int circlePoints = (int)(pointCount * (CV_PI * radius) / totalPerimeter);
    int linePoints = (int)(pointCount * length / totalPerimeter);
    circlePoints = std::max(10, circlePoints / 2);  // Each end semicircle
    
    // Top semicircle (left end, from top to bottom)
    for (int i = 0; i <= circlePoints; i++) {
        float theta = CV_PI + (float)CV_PI * i / circlePoints;  // PI to 2*PI
        float x = -length/2.0f - radius + radius * cos(theta);
        float y = radius * sin(theta);
        // Rotate
        float xr = x * cos(angleRad) - y * sin(angleRad);
        float yr = x * sin(angleRad) + y * cos(angleRad);
        outPoints.push_back(Point((int)(center.x + xr), (int)(center.y + yr)));
    }
    
    // Top line (left to right)
    for (int i = 1; i <= linePoints; i++) {
        float t = (float)i / linePoints;
        float x = -length/2.0f + length * t;
        float y = 0.0f;
        float xr = x * cos(angleRad) - y * sin(angleRad);
        float yr = x * sin(angleRad) + y * cos(angleRad);
        outPoints.push_back(Point((int)(center.x + xr), (int)(center.y + yr)));
    }
    
    // Bottom semicircle (right end, from bottom to top)
    for (int i = 0; i <= circlePoints; i++) {
        float theta = (float)CV_PI * i / circlePoints;  // 0 to PI
        float x = length/2.0f + radius + radius * cos(theta);
        float y = radius * sin(theta);
        // Rotate
        float xr = x * cos(angleRad) - y * sin(angleRad);
        float yr = x * sin(angleRad) + y * cos(angleRad);
        outPoints.push_back(Point((int)(center.x + xr), (int)(center.y + yr)));
    }
    
    // Bottom line (right to left)
    for (int i = 1; i < linePoints; i++) {
        float t = 1.0f - (float)i / linePoints;
        float x = -length/2.0f + length * t;
        float y = 0.0f;
        float xr = x * cos(angleRad) - y * sin(angleRad);
        float yr = x * sin(angleRad) + y * cos(angleRad);
        outPoints.push_back(Point((int)(center.x + xr), (int)(center.y + yr)));
    }
}

// ========== OpenCV-based Trajectory Detection ==========
void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== Starting OpenCV 16-DarkBars Trajectory Detection ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);

    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }

    // 释放之前的步骤图
    FreeStepImages();

    int w = img->width, h = img->height;
    int srcRowSize = ((w * img->channels + 3) / 4) * 4;

    // ---- 转换输入图像为 OpenCV Mat 灰度图 ----
    // 注意：必须 clone() 得到紧凑步长的独立 Mat，不能零拷贝共享 img->data
    // 因为 img->data 的行步长可能有 4 字节对齐 padding，与 Mat 默认步长不一致
    Mat grayMat;
    if (img->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;  // img->data 的实际行步长（4字节对齐）
        Mat tmp(h, w, CV_8UC1, img->data, pitch);
        grayMat = tmp.clone();
        tmp.release();  // 防止析构时释放 img->data
    } else {
        int srcRowSize = ((w * img->channels + 3) / 4) * 4;
        Mat colorMat(h, w, CV_8UC3, img->data, srcRowSize);
        cvtColor(colorMat, grayMat, COLOR_BGR2GRAY);
        colorMat.release();  // 防止析构时释放 img->data（非 Mat 分配的内存）
    }

    // ============================================================
    // 网格步骤 1：保存原始灰度图（保持1通道，不转3通道）
    // ============================================================
    {
        Mat& src = grayMat;
        int sw = src.cols, sh = src.rows;
        if (g_stepImages[0].data) free(g_stepImages[0].data);
        g_stepImages[0].width = sw;
        g_stepImages[0].height = sh;
        g_stepImages[0].channels = 1;
        g_stepImages[0].data = (unsigned char*)malloc(sw * sh);
        memcpy(g_stepImages[0].data, src.data, sw * sh);
    }

    // ============================================================
    // 网格步骤 2：高斯模糊 + OTSU 二值化
    // 高斯模糊消除噪声，OTSU 自动选择阈值分割亮区（工件）和暗区（背景）
    // ============================================================
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(7, 7), 1.5);

    Mat binaryBright;
    threshold(blurred, binaryBright, 0, 255, THRESH_BINARY + THRESH_OTSU);
    // 网格步骤 2：高斯模糊 + OTSU 二值化（保持1通道）
    {
        Mat& src = binaryBright;
        int sw = src.cols, sh = src.rows;
        if (g_stepImages[1].data) free(g_stepImages[1].data);
        g_stepImages[1].width = sw;
        g_stepImages[1].height = sh;
        g_stepImages[1].channels = 1;
        g_stepImages[1].data = (unsigned char*)malloc(sw * sh);
        memcpy(g_stepImages[1].data, src.data, sw * sh);
    }

    // ---- 形态学处理：闭运算填补工件内部空洞，开运算去除背景噪点 ----
    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(5, 5));
    Mat morphed;
    morphologyEx(binaryBright, morphed, MORPH_CLOSE, kernel);
    morphologyEx(morphed, morphed, MORPH_OPEN, kernel);

    // ---- 提取外轮廓 ----
    std::vector<std::vector<Point>> outerContours;
    findContours(morphed, outerContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    if (outerContours.empty()) {
        LOG_ERROR("No workpiece contour found!");
        *count = 0;
        return;
    }

    // 取面积最大的外轮廓作为工件边界
    int maxOuterIdx = 0;
    double maxOuterArea = 0.0;
    for (size_t i = 0; i < outerContours.size(); i++) {
        double a = contourArea(outerContours[i]);
        if (a > maxOuterArea) {
            maxOuterArea = a;
            maxOuterIdx = (int)i;
        }
    }
    std::vector<Point>& outerContour = outerContours[maxOuterIdx];

    // ---- 生成工件 mask（工件内部=255，外部=0）----
    Mat mask = Mat::zeros(h, w, CV_8UC1);
    drawContours(mask, outerContours, maxOuterIdx, Scalar(255), FILLED);

    // ============================================================
    // 网格步骤 3：工件 mask（保持1通道）
    // ============================================================
    {
        Mat& src = mask;
        int sw = src.cols, sh = src.rows;
        if (g_stepImages[2].data) free(g_stepImages[2].data);
        g_stepImages[2].width = sw;
        g_stepImages[2].height = sh;
        g_stepImages[2].channels = 1;
        g_stepImages[2].data = (unsigned char*)malloc(sw * sh);
        memcpy(g_stepImages[2].data, src.data, sw * sh);
    }

    // ============================================================
    // 网格步骤 4：暗条二值化（保持1通道）
    // 思路：将工件外部像素设为 255（白），然后用阈值 45 做反转二值化
    // 灰度 < 45 的像素（暗条）变为白色(255)，其余变为黑色(0)
    // ============================================================
    Mat innerGray = grayMat.clone();
    innerGray.setTo(255, mask == 0);  // 工件外部涂白，防止被误检为暗条

    Mat darkBinary;
    threshold(innerGray, darkBinary, 45, 255, THRESH_BINARY_INV);
    darkBinary.setTo(0, mask == 0);  // 工件外部清零

    // 保存形态学清理前的暗条二值图
    Mat darkBinaryBeforeMorph = darkBinary.clone();
    {
        Mat& src = darkBinaryBeforeMorph;
        int sw = src.cols, sh = src.rows;
        if (g_stepImages[3].data) free(g_stepImages[3].data);
        g_stepImages[3].width = sw;
        g_stepImages[3].height = sh;
        g_stepImages[3].channels = 1;
        g_stepImages[3].data = (unsigned char*)malloc(sw * sh);
        memcpy(g_stepImages[3].data, src.data, sw * sh);
    }

    // ============================================================
    // 网格步骤 5：形态学清理后的暗条（保持1通道）
    // 使用 3x3 椭圆核做闭运算（填补暗条内小孔）和开运算（去除孤立噪点）
    // ============================================================
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    {
        Mat& src = darkBinary;
        int sw = src.cols, sh = src.rows;
        if (g_stepImages[4].data) free(g_stepImages[4].data);
        g_stepImages[4].width = sw;
        g_stepImages[4].height = sh;
        g_stepImages[4].channels = 1;
        g_stepImages[4].data = (unsigned char*)malloc(sw * sh);
        memcpy(g_stepImages[4].data, src.data, sw * sh);
    }

    // ---- 提取所有暗条轮廓 ----
    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    // ---- 自适应面积阈值 ----
    // 原始设计针对 2448×2048（面积约500万），固定阈值10000 ≈ 0.2%
    // 缩放到 800×600（面积约48万），阈值按比例缩小
    double areaThreshold = (double)(w * h) * 0.002;
    if (areaThreshold < 500.0) areaThreshold = 500.0;  // 下限保护
    LOG_INFO("Adaptive area threshold: %.0f (image %dx%d)", areaThreshold, w, h);

    // ---- 过滤 + 排序：保留面积 > 阈值的暗条，按面积降序，取前16个 ----
    std::vector<std::pair<double, int>> sortedBars; // (area, contour_index)
    for (size_t i = 0; i < darkContours.size(); i++) {
        double area = contourArea(darkContours[i]);
        if (area > areaThreshold) {
            sortedBars.push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(sortedBars.begin(), sortedBars.end(),
         [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
             return a.first > b.first;
         });

    int barCount = (int)sortedBars.size();
    int targetBars = std::min(barCount, 16);
    LOG_INFO("Dark bars found: %d (taking top %d)", barCount, targetBars);

    // ============================================================
    // Step 3: 对每个暗条进行形状拟合，生成轨迹点
    // 同时记录每个点所属的暗条编号（用于网格步骤6的彩色显示）
    // ============================================================
    std::vector<Point> allPoints;
    std::vector<int> allBarIds;

    for (int b = 0; b < targetBars; b++) {
        const std::vector<Point>& contour = darkContours[sortedBars[b].second];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;

        // 用 minAreaRect 获取最小外接旋转矩形
        RotatedRect box = minAreaRect(contour);
        float bw = box.size.width;
        float bh = box.size.height;
        if (bh > bw) swap(bw, bh);  // 确保 bw >= bh（长边在前）

        float aspectRatio = bw / bh;

        if (aspectRatio > 2.5f && bh > 5.0f) {
            // ---- Stadium 形状（体育场形/胶囊形）：长条两端带半圆 ----
            // length = 长边 - 短边（直线段长度），diam = 短边（端部半圆直径）
            float length = bw - bh;
            float diam = bh;

            // 点数按周长自适应：保证每段轮廓都有足够的采样密度
            int pointCount = std::max(50, (int)(2.0f * (CV_PI * diam / 2.0f + length) * 2.0f));
            std::vector<Point> stadiumPoints;
            GenerateStadiumShape(box.center, length, diam, box.angle, pointCount, stadiumPoints);

            for (size_t k = 0; k < stadiumPoints.size(); k++) {
                int px = std::max(0, std::min(w - 1, stadiumPoints[k].x));
                int py = std::max(0, std::min(h - 1, stadiumPoints[k].y));
                allPoints.push_back(Point(px, py));
                allBarIds.push_back(b);
            }
        } else {
            // ---- 椭圆拟合：适用于近似圆形或短胖形区域 ----
            if (ptCount >= 6) {
                RotatedRect ellipse = fitEllipse(contour);

                Size2f axes = ellipse.size;
                float rx = axes.width / 2.0f;
                float ry = axes.height / 2.0f;
                if (ry > rx) swap(rx, ry);  // rx = 长半轴

                // Ramanujan 近似计算椭圆周长
                float perimeter = (float)(CV_PI * (3.0f * (rx + ry) - sqrt((3.0f * rx + ry) * (rx + 3.0f * ry))));
                int ellipsePointCount = std::max(30, (int)(perimeter * 0.5f));

                Point2f center = ellipse.center;
                float angleRad = ellipse.angle * (float)CV_PI / 180.0f;

                for (int k = 0; k < ellipsePointCount; k++) {
                    float theta = 2.0f * (float)CV_PI * k / ellipsePointCount;
                    float x = rx * cos(theta);
                    float y = ry * sin(theta);
                    float rx_rot = x * cos(angleRad) - y * sin(angleRad);
                    float ry_rot = x * sin(angleRad) + y * cos(angleRad);

                    int px = (int)(center.x + rx_rot);
                    int py = (int)(center.y + ry_rot);

                    if (px >= 0 && px < w && py >= 0 && py < h) {
                        allPoints.push_back(Point(px, py));
                        allBarIds.push_back(b);
                    }
                }
            }
        }

        // 补充原始轮廓点（增加边缘采样密度）
        for (int j = 0; j < ptCount; j++) {
            allPoints.push_back(contour[j]);
            allBarIds.push_back(b);
        }
    }

    LOG_INFO("Shape fitting: %d dark bars processed, raw points=%d",
             targetBars, (int)allPoints.size());

    // ============================================================
    // Step 4: 去重 + 排序 + 输出（保留 bar id）
    // ============================================================

    // 去重：1像素半径内只保留第一个点，避免重复采样导致密集扎堆
    std::vector<Point> uniquePoints;
    std::vector<int> uniqueBarIds;
    std::vector<bool> kept(allPoints.size(), false);
    for (size_t i = 0; i < allPoints.size(); i++) {
        if (kept[i]) continue;
        uniquePoints.push_back(allPoints[i]);
        uniqueBarIds.push_back(allBarIds[i]);
        int x1 = allPoints[i].x - 1, x2 = allPoints[i].x + 1;
        int y1 = allPoints[i].y - 1, y2 = allPoints[i].y + 1;
        for (size_t j = i + 1; j < allPoints.size(); j++) {
            if (!kept[j]) {
                int px = allPoints[j].x, py = allPoints[j].y;
                if (px >= x1 && px <= x2 && py >= y1 && py <= y2) {
                    kept[j] = true;
                }
            }
        }
    }

    // 按位置排序（y优先、x次之），同步移动 bar id
    if (uniquePoints.size() > 1) {
        std::vector<size_t> indices(uniquePoints.size());
        for (size_t i = 0; i < indices.size(); i++) indices[i] = i;
        std::sort(indices.begin(), indices.end(), [&uniquePoints](size_t a, size_t b) {
            if (abs(uniquePoints[a].y - uniquePoints[b].y) > 2) return uniquePoints[a].y < uniquePoints[b].y;
            return uniquePoints[a].x < uniquePoints[b].x;
        });
        std::vector<Point> sortedPts(uniquePoints.size());
        std::vector<int> sortedIds(uniqueBarIds.size());
        for (size_t i = 0; i < indices.size(); i++) {
            sortedPts[i] = uniquePoints[indices[i]];
            sortedIds[i] = uniqueBarIds[indices[i]];
        }
        uniquePoints = sortedPts;
        uniqueBarIds = sortedIds;
    }

    // 输出结果到全局数组（上限 10000 个点）
    *count = std::min((int)uniquePoints.size(), 10000);
    for (int i = 0; i < *count; i++) {
        trajPixels[i].x = (float)uniquePoints[i].x;
        trajPixels[i].y = (float)uniquePoints[i].y;
        g_trajBarId[i] = uniqueBarIds[i];
    }

    LOG_INFO("Trajectory detection: raw=%d, unique=%d, output=%d points",
             (int)allPoints.size(), (int)uniquePoints.size(), *count);

    // 坐标变换到世界坐标（毫米）
    for (int i = 0; i < *count; i++) {
        g_trajWorld[i] = ImageToWorld(trajPixels[i], g_transform);
    }

    if (*count > 0) {
        LOG_DEBUG("  First: pixel(%.1f, %.1f) -> world(%.2f, %.2f)",
            trajPixels[0].x, trajPixels[0].y, g_trajWorld[0].x, g_trajWorld[0].y);
        LOG_DEBUG("  Last: pixel(%.1f, %.1f) -> world(%.2f, %.2f)",
            trajPixels[*count - 1].x, trajPixels[*count - 1].y,
            g_trajWorld[*count - 1].x, g_trajWorld[*count - 1].y);
    }

    // ============================================================
    // 网格步骤 6：最终彩色轨迹（黑底 + 每个暗条不同颜色）
    // ============================================================
    LOG_INFO("Step 6: building color trajectory image, w=%d, h=%d, count=%d", w, h, *count);
    {
        int dstChannels = 3;
        int dstRowSize = w * dstChannels;
        int imageSize = dstRowSize * h;
        g_stepImages[5].width = w;
        g_stepImages[5].height = h;
        g_stepImages[5].channels = dstChannels;
        g_stepImages[5].data = (unsigned char*)malloc(imageSize);
        memset(g_stepImages[5].data, 0, imageSize);  // 黑底

        // 用 3×3 白色方块标记每个轨迹点，颜色由 bar id 决定
        for (int i = 0; i < *count; i++) {
            int barIdx = g_trajBarId[i] % 16;
            const unsigned char* color = BAR_COLORS[barIdx];
            int cx = (int)trajPixels[i].x;
            int cy = (int)trajPixels[i].y;
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < w && y >= 0 && y < h) {
                        int off = y * dstRowSize + x * 3;
                        g_stepImages[5].data[off]     = color[0];  // B
                        g_stepImages[5].data[off + 1] = color[1];  // G
                        g_stepImages[5].data[off + 2] = color[2];  // R
                    }
                }
            }
        }
    }
    LOG_INFO("Step 6: color trajectory image complete");

    // ============================================================
    // 手动释放所有 OpenCV Mat 局部变量
    // 防止函数返回时自动析构链中因 CRT/堆不兼容导致崩溃
    // ============================================================
    LOG_INFO("Step 7: releasing OpenCV Mats...");
    innerGray.release();
    darkBinaryBeforeMorph.release();
    darkBinary.release();
    kernel2.release();
    mask.release();
    morphed.release();
    kernel.release();
    binaryBright.release();
    blurred.release();
    grayMat.release();
    // 释放 std::vector（可能持有大量内存）
    {
        std::vector<std::vector<Point>> empty1;
        darkContours.swap(empty1);
        std::vector<std::vector<Point>> empty2;
        outerContours.swap(empty2);
        std::vector<std::pair<double, int>> empty3;
        sortedBars.swap(empty3);
        std::vector<Point> empty4;
        allPoints.swap(empty4);
        std::vector<int> empty5;
        allBarIds.swap(empty5);
        std::vector<Point> empty6;
        uniquePoints.swap(empty6);
        std::vector<int> empty7;
        uniqueBarIds.swap(empty7);
        std::vector<bool> empty8;
        kept.swap(empty8);
    }
    LOG_INFO("Step 7: all OpenCV Mats released successfully");
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

            int btnY = 700, btnW = 150, btnH = 32, btnGap = 10;
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
            startX += btnW + btnGap;
            CreateWindow(L"BUTTON", L"7. TestImage",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY, btnW, btnH, hwnd, (HMENU)7, NULL, NULL);

            g_hwndImage = CreateWindow(L"STATIC", L"",
                WS_CHILD | WS_VISIBLE | SS_BLACKFRAME,
                20, 70, IMAGE_WIDTH, IMAGE_HEIGHT, hwnd, NULL, NULL, NULL);

            g_hwndStatus = CreateWindow(L"STATIC", L"Ready - Click buttons to start",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                20, IMAGE_HEIGHT + 75, 1000, 25, hwnd, NULL, NULL, NULL);

            HWND hTitle = CreateWindow(L"STATIC", L"9-Point Calibration GUI",
                WS_CHILD | WS_VISIBLE,
                IMAGE_WIDTH + 30, 20, 350, 35, hwnd, NULL, NULL, NULL);
            SendMessage(hTitle, WM_SETFONT, (WPARAM)hFontTitle, TRUE);
            SendMessage(g_hwndStatus, WM_SETFONT, (WPARAM)hFontText, TRUE);

            // Detection method selection
            HWND hMethodLabel = CreateWindow(L"STATIC", L"Detection Method:",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                IMAGE_WIDTH + 30, 680, 120, 25, hwnd, NULL, NULL, NULL);
            SendMessage(hMethodLabel, WM_SETFONT, (WPARAM)hFontText, TRUE);

            g_hwndMethodSelect = CreateWindow(L"COMBOBOX", L"",
                WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST | CBS_HASSTRINGS | WS_VSCROLL,
                IMAGE_WIDTH + 150, 678, 200, 100, hwnd, (HMENU)100, NULL, NULL);
            SendMessage(g_hwndMethodSelect, WM_SETFONT, (WPARAM)hFontText, TRUE);
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"Self-Algorithm");
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"OpenCV-Contour");
            SendMessage(g_hwndMethodSelect, CB_SETCURSEL, 0, 0);
        }
        return 0;

    // ---- 自动测试：加载暗条图并执行检测，弹出网格窗口 ----
    case WM_USER + 1:
        {
            LOG_INFO("=== Auto-test triggered ===");
            char filename[MAX_PATH];
            // 默认使用复制到英文路径的16暗条图（避免 fopen_s 中文编码问题）
            const char* autoTestPath = "D:\\work\\calibrate-api\\test_auto.bmp";
            strcpy_s(filename, sizeof(filename), autoTestPath);

            Image loadedImg = {0};
            if (LoadBMP(filename, &loadedImg)) {
                LOG_INFO("Auto-test: loaded %s (%dx%d)", filename, loadedImg.width, loadedImg.height);

                // 如果图片超过 IMAGE_WIDTH x IMAGE_HEIGHT 则缩放
                if (loadedImg.width > IMAGE_WIDTH || loadedImg.height > IMAGE_HEIGHT) {
                    Image scaledImg = {0};
                    ScaleImage(&loadedImg, &scaledImg, IMAGE_WIDTH, IMAGE_HEIGHT);
                    free(loadedImg.data);
                    if (g_image.data) free(g_image.data);
                    g_image = scaledImg;
                    LOG_INFO("Auto-test: scaled to %dx%d", g_image.width, g_image.height);
                } else {
                    if (g_image.data) free(g_image.data);
                    g_image = loadedImg;
                }

                g_step = 2;

                // 直接调用 DetectTrajectoryOpenCV（无需标定，走暗条检测流程）
                DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount);
                LOG_INFO("Auto-test: detected %d trajectory points", g_trajCount);

                // 显示网格调试窗口
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
                if (CreateCalibrationImage(&g_image, IMAGE_WIDTH, IMAGE_HEIGHT)) {
                    SaveBMP("calibration_9points.bmp", &g_image);
                    strcpy_s(statusText, sizeof(statusText), "Step 1: Calibration image generated and saved");
                    g_step = 1;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                    LOG_INFO("Calibration image generated: %dx%d", IMAGE_WIDTH, IMAGE_HEIGHT);
                } else {
                    LOG_ERROR("Failed to generate calibration image");
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
                    sprintf_s(msgBuf, sizeof(msgBuf), "Loading: %s", filename);
                    MessageBoxA(hwnd, msgBuf, "Debug", MB_OK);
                    
                    Image loadedImg = {0};
                    if (LoadBMP(filename, &loadedImg)) {
                        // Check if scaling is needed
                        if (loadedImg.width > IMAGE_WIDTH || loadedImg.height > IMAGE_HEIGHT) {
                            sprintf_s(msgBuf, sizeof(msgBuf), 
                                "Image is %dx%d\n\nAuto-scaling to %dx%d...",
                                loadedImg.width, loadedImg.height, IMAGE_WIDTH, IMAGE_HEIGHT);
                            MessageBoxA(hwnd, msgBuf, "Scaling", MB_OK);
                            
                            Image scaledImg = {0};
                            ScaleImage(&loadedImg, &scaledImg, IMAGE_WIDTH, IMAGE_HEIGHT);
                            free(loadedImg.data);
                            
                            if (g_image.data) free(g_image.data);
                            g_image = scaledImg;
                        } else {
                            if (g_image.data) free(g_image.data);
                            g_image = loadedImg;
                        }
                        
                        sprintf_s(statusText, sizeof(statusText), "Loaded: %s (%dx%d)", filename, g_image.width, g_image.height);
                        g_step = 2;
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                    } else {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s\n\nPlease select a valid 24-bit or 8-bit BMP image.", filename);
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
                    LOG_WARN("Attempted detection without loaded image");
                    MessageBoxA(hwnd, "Please complete Step 1 or Step 2 to load an image first.", "No Image", MB_OK | MB_ICONWARNING);
                } else {
                    // Get selected detection method
                    int sel = SendMessage(g_hwndMethodSelect, CB_GETCURSEL, 0, 0);
                    g_detectMethod = (DetectMethod)sel;
                    
                    const char* methodName = (g_detectMethod == METHOD_OPENCV) ? "OpenCV-Contour" : "Self-Algorithm";
                    LOG_INFO("Selected detection method: %s (index: %d)", methodName, sel);
                    sprintf_s(msgBuf, sizeof(msgBuf), "Using detection method: %s", methodName);
                    MessageBoxA(hwnd, msgBuf, "Detection Method", MB_OK);
                    
                    // Call appropriate detection function
                    if (g_detectMethod == METHOD_OPENCV) {
                        BlobDetectCircles(&g_image, g_detectedPts, &g_detectedCount);
                    } else {
                        DetectCircles(&g_image, g_detectedPts, &g_detectedCount);
                    }
                    
                    for (int i = 0; i < g_detectedCount && i < 9; i++) g_imagePts[i] = g_detectedPts[i];
                    DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0);  // 0=black
                    
                    if (g_detectedCount < 3) {
                        sprintf_s(msgBuf, sizeof(msgBuf), "Circle detection failed!\n\nDetected only %d circles.\nNeed at least 3 points for calibration.", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Error: Insufficient circles detected");
                        MessageBoxA(hwnd, msgBuf, "Detection Error", MB_OK | MB_ICONERROR);
                    } else {
                        // Check for collinearity of first 3 points
                        double px1 = g_imagePts[0].x, py1 = g_imagePts[0].y;
                        double px2 = g_imagePts[1].x, py2 = g_imagePts[1].y;
                        double px3 = g_imagePts[2].x, py3 = g_imagePts[2].y;
                        double det = px1*(py2 - py3) - px2*(py1 - py3) + px3*(py1 - py2);
                        if (fabs(det) < 1e-6) {
                            sprintf_s(msgBuf, sizeof(msgBuf), 
                                "Warning: First 3 detected points are collinear!\n\n"
                                "This will cause calibration to fail.\n\n"
                                "Determinant: %.6f (near zero)\n\n"
                                "Solution: Ensure 9-point grid pattern is clearly visible.",
                                det);
                            MessageBoxA(hwnd, msgBuf, "Collinearity Warning", MB_OK | MB_ICONWARNING);
                        }
                        
                        sprintf_s(msgBuf, sizeof(msgBuf), "Detected %d circles successfully!\nReady for calibration.", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Step 3: Detected 9 circles (green markers)");
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
                    strcpy_s(statusText, sizeof(statusText), "Error: Detect circles first");
                    LOG_WARN("Attempted calibration before circle detection");
                    MessageBoxA(hwnd, "Please complete Step 3 (Detect Circles) before calibration.", "Step Error", MB_OK | MB_ICONWARNING);
                } else {
                    // Debug: Show input points before calibration
                    sprintf_s(msgBuf, sizeof(msgBuf), 
                        "Calibration Input Points:\n\n"
                        "Image Points -> World Points:\n"
                        "0: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "1: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "2: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "3: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "4: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "5: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "6: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "7: (%.0f,%.0f) -> (%.0f,%.0f)\n"
                        "8: (%.0f,%.0f) -> (%.0f,%.0f)",
                        g_imagePts[0].x, g_imagePts[0].y, g_worldPts[0].x, g_worldPts[0].y,
                        g_imagePts[1].x, g_imagePts[1].y, g_worldPts[1].x, g_worldPts[1].y,
                        g_imagePts[2].x, g_imagePts[2].y, g_worldPts[2].x, g_worldPts[2].y,
                        g_imagePts[3].x, g_imagePts[3].y, g_worldPts[3].x, g_worldPts[3].y,
                        g_imagePts[4].x, g_imagePts[4].y, g_worldPts[4].x, g_worldPts[4].y,
                        g_imagePts[5].x, g_imagePts[5].y, g_worldPts[5].x, g_worldPts[5].y,
                        g_imagePts[6].x, g_imagePts[6].y, g_worldPts[6].x, g_worldPts[6].y,
                        g_imagePts[7].x, g_imagePts[7].y, g_worldPts[7].x, g_worldPts[7].y,
                        g_imagePts[8].x, g_imagePts[8].y, g_worldPts[8].x, g_worldPts[8].y);
                    MessageBoxA(hwnd, msgBuf, "Debug: Input Points", MB_OK | MB_ICONINFORMATION);

                    if (CalibrateNinePoint(g_imagePts, g_worldPts, 9, &g_transform)) {
                        CalculateError(g_imagePts, g_worldPts, 9, g_transform, &g_avgError, &g_maxError);
                        sprintf_s(msgBuf, sizeof(msgBuf), "Calibration Complete!\n\nX = %.4f*x + %.4f*y + %.2f\nY = %.4f*x + %.4f*y + %.2f\n\nAvg Error: %.4f mm\nMax Error: %.4f mm",
                            g_transform.a, g_transform.b, g_transform.c, g_transform.d, g_transform.e, g_transform.f, g_avgError, g_maxError);
                        DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 128);  // Gray markers
                        strcpy_s(statusText, sizeof(statusText), "Step 4: Calibration complete!");
                        g_step = 4;
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                        MessageBoxA(hwnd, msgBuf, "Calibration Results", MB_OK | MB_ICONINFORMATION);
                    } else {
                        double px1 = g_imagePts[0].x, py1 = g_imagePts[0].y;
                        double px2 = g_imagePts[1].x, py2 = g_imagePts[1].y;
                        double px3 = g_imagePts[2].x, py3 = g_imagePts[2].y;
                        double det = px1*(py2 - py3) - px2*(py1 - py3) + px3*(py1 - py2);
                        
                        sprintf_s(msgBuf, sizeof(msgBuf), 
                            "Calibration Failed!\n\n"
                            "Possible reasons:\n"
                            "1. Determinant near zero (det=%.6f)\n"
                            "   - Three points may be collinear\n"
                            "   - Points may be too close together\n\n"
                            "2. Insufficient points detected\n"
                            "   Expected: 9 points\n"
                            "   Detected: %d points\n\n"
                            "Solution: Re-detect circles or check image quality",
                            det, g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Error: Calibration failed - points collinear or insufficient");
                        MessageBoxA(hwnd, msgBuf, "Calibration Failed", MB_OK | MB_ICONERROR);
                    }
                }
                break;
            }

            case 5:  // Save Results
                LOG_INFO("Button 5: Save Results clicked");
                if (g_image.data) {
                    SaveBMP("calibration_result.bmp", &g_image);
                    LOG_DEBUG("Saved calibration_result.bmp");
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
                        LOG_DEBUG("Saved calibration_params.txt");
                    } else {
                        LOG_ERROR("Failed to save calibration_params.txt");
                    }
                }
                sprintf_s(msgBuf, sizeof(msgBuf), "Saved:\n- calibration_params.txt\n- calibration_result.bmp\n\nAvg Error: %.4f mm", g_avgError);
                strcpy_s(statusText, sizeof(statusText), "Results saved to files");
                MessageBoxA(hwnd, msgBuf, "Save Complete", MB_OK | MB_ICONINFORMATION);
                break;

            case 6: {  // Test Transform
                LOG_INFO("Button 6: Test Transform clicked");
                if (g_step < 4) {
                    strcpy_s(statusText, sizeof(statusText), "Error: Calibrate first");
                    LOG_WARN("Attempted transform test before calibration");
                    break;
                }
                Point2D testPixel = { 400, 300 };
                Point2D testWorld = ImageToWorld(testPixel, g_transform);
                Point2D backPixel = { g_transform.a * testWorld.x + g_transform.b * testWorld.y + g_transform.c,
                                     g_transform.d * testWorld.x + g_transform.e * testWorld.y + g_transform.f };
                sprintf_s(msgBuf, sizeof(msgBuf), "Test Transform:\n\nInput pixel: (%.0f, %.0f)\nOutput world: (%.2f, %.2f) mm\n\nRound-trip:\nInput: (%.0f, %.0f)\nOutput: (%.2f, %.2f)",
                    testPixel.x, testPixel.y, testWorld.x, testWorld.y,
                    testWorld.x, testWorld.y, backPixel.x, backPixel.y);
                strcpy_s(statusText, sizeof(statusText), "Transform test complete");
                MessageBoxA(hwnd, msgBuf, "Transform Test", MB_OK);
                break;
            }
                case 7: { // Load Image
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
                        sprintf_s(msgBuf, sizeof(msgBuf), "Loading: %s", filename);
                        MessageBoxA(hwnd, msgBuf, "Debug", MB_OK);

                        Image loadedImg = { 0 };
                        if (LoadBMP(filename, &loadedImg)) {
                            // Check if scaling is needed
                            if (loadedImg.width > IMAGE_WIDTH || loadedImg.height > IMAGE_HEIGHT) {
                                sprintf_s(msgBuf, sizeof(msgBuf),
                                    "Image is %dx%d\n\nAuto-scaling to %dx%d...",
                                    loadedImg.width, loadedImg.height, IMAGE_WIDTH, IMAGE_HEIGHT);
                                MessageBoxA(hwnd, msgBuf, "Scaling", MB_OK);

                                Image scaledImg = { 0 };
                                ScaleImage(&loadedImg, &scaledImg, IMAGE_WIDTH, IMAGE_HEIGHT);
                                free(loadedImg.data);

                                if (g_image.data) free(g_image.data);
                                g_image = scaledImg;
                            }
                            else {
                                if (g_image.data) free(g_image.data);
                                g_image = loadedImg;
                            }

                            sprintf_s(statusText, sizeof(statusText), "Loaded: %s (%dx%d)", filename, g_image.width, g_image.height);
                            LOG_INFO("Image loaded: %s (%dx%d)", filename, g_image.width, g_image.height);
                            
                            // Detect trajectory if calibration is available
                            if (g_step >= 4) {
                                LOG_INFO("Calibration available, starting trajectory detection...");
                                int sel = SendMessage(g_hwndMethodSelect, CB_GETCURSEL, 0, 0);
                                if (sel == METHOD_SELF) {
                                    DetectTrajectory(&g_image, g_trajPixels, &g_trajCount);
                                } else if (sel == METHOD_OPENCV) {
                                    DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount);
                                }
                                LOG_INFO("Trajectory detection finished: %d points", g_trajCount);
                                sprintf_s(msgBuf, sizeof(msgBuf), 
                                    "Detected %d trajectory points\n\n"
                                    "Trajectory coordinates (world):\n"
                                    "Start: (%.2f, %.2f)\n"
                                    "End: (%.2f, %.2f)",
                                    g_trajCount,
                                    g_trajCount > 0 ? g_trajWorld[0].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[0].y : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].y : 0);
                                MessageBoxA(hwnd, msgBuf, "Trajectory Detected", MB_OK);
                                
                                // Show trajectory in a new window
                                ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                                CreateTrajectoryWindow((HINSTANCE)GetWindowLongPtr(hwnd, GWLP_HINSTANCE));
                                strcat_s(statusText, sizeof(statusText), " | Trajectory in new window");
                            }
                            
                            InvalidateRect(g_hwndImage, NULL, FALSE);
                        }
                        else {
                            sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s\n\nPlease select a valid 24-bit or 8-bit BMP image.", filename);
                            MessageBoxA(hwnd, msgBuf, "Load Error", MB_OK | MB_ICONWARNING);
                            strcpy_s(statusText, sizeof(statusText), "Error: Cannot load image");
                        }
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

            // Draw info panel
            SetBkMode(hdc, TRANSPARENT);
            HFONT hOld = (HFONT)SelectObject(hdc, hFontMono);

            int infoX = IMAGE_WIDTH + 40, infoY = 80;
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
    // Save instance handle for later use
    g_hInstance = hInstance;
    
    // Initialize console logger
    InitConsole();
    LOG_INFO("Application starting...");
    LOG_INFO("Instance handle: %p", hInstance);
    
    WNDCLASS wc = {0};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"CalibrationGUI";
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);

    LOG_DEBUG("Registering window class...");
    RegisterClass(&wc);
    LOG_DEBUG("Window class registered successfully");

    LOG_DEBUG("Creating main window...");
    HWND hwnd = CreateWindow(L"CalibrationGUI", L"9-Point Calibration GUI",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1300, 900,
        NULL, NULL, hInstance, NULL);
    LOG_INFO("Main window created: %p", hwnd);

    ShowWindow(hwnd, SW_SHOWNORMAL);
    UpdateWindow(hwnd);
    LOG_INFO("Main window shown");

    // 启动后自动执行测试：加载图片 → 检测 → 显示网格窗口
    PostMessage(hwnd, WM_USER + 1, 0, 0);

    MSG msg;
    LOG_INFO("Entering message loop...");
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
    LOG_INFO("Application exiting...");
    
    // Cleanup
    CloseConsole();
    return (int)msg.wParam;
}
