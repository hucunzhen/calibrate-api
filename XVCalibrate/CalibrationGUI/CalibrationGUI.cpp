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

// Include headers
#include "..\\..\\include\\BlobAnalysis.h"
#include "..\\..\\include\\FitShape.h"

// ========== Console Logger ==========
static FILE* g_consoleFile = NULL;

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
} while(0)

// Log function variants
#define LOG_INFO(fmt, ...)   LOG("[INFO] " fmt, ##__VA_ARGS__)
#define LOG_WARN(fmt, ...)    LOG("[WARN] " fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...)   LOG("[ERROR] " fmt, ##__VA_ARGS__)
#define LOG_DEBUG(fmt, ...)   LOG("[DEBUG] " fmt, ##__VA_ARGS__)

// Detection method enum
enum DetectMethod {
    METHOD_SELF = 0,      // Self-implemented algorithm
    METHOD_BLOB = 1,      // BlobAnalysisPro library
    METHOD_FITSHAPE = 2,  // FitShape library
    METHOD_OPENCV = 3     // OpenCV-based algorithm
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
    int channels;  // 1=灰度, 3=彩色
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
static Point2D g_detectedPts[9];
static int g_detectedCount = 0;
static AffineTransform g_transform = {0};
static double g_avgError = 0;
static double g_maxError = 0;
static int g_step = 0;
static DetectMethod g_detectMethod = METHOD_SELF;

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

// ========== Image Functions ==========
// Create 8-bit grayscale calibration image
int CreateCalibrationImage(Image* img, int width, int height) {
    int rowSize = width;  // 8-bit: row size equals width
    img->width = width;
    img->height = height;
    img->channels = 1;  // 灰度图
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
    img->channels = bitCount / 8;  // 8位灰度=1通道, 24位彩色=3通道
    LOG_DEBUG("BMP dimensions: %dx%d, bitCount: %d, channels: %d", width, abs(height), bitCount, img->channels);
    
    int srcRowSize = ((img->width * bitCount / 8 + 3) / 4) * 4;  // Source row size with padding
    int dstRowSize = img->width * img->channels;                  // 原始格式：灰度=width, 彩色=width*3
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
        // Uncompressed 8-bit grayscale: direct copy
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
            // Direct copy for grayscale (no palette conversion)
            memcpy(dstRow, srcRow, img->width);
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
    int bitCount = img->channels * 8;  // 1通道=8位灰度, 3通道=24位彩色
    
    // BITMAPINFOHEADER(40) + 颜色表(256*4=1024)，总共1064字节
    BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    if (!pbmi) return;
    memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    
    pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pbmi->bmiHeader.biWidth = img->width;
    pbmi->bmiHeader.biHeight = -img->height;
    pbmi->bmiHeader.biPlanes = 1;
    pbmi->bmiHeader.biBitCount = (unsigned short)bitCount;
    pbmi->bmiHeader.biCompression = BI_RGB;
    
    // 8位灰度图需要颜色表
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
            int x1 = min(x0 + 1, src->width - 1);
            int y1 = min(y0 + 1, src->height - 1);
            double fx = srcX - x0;
            double fy = srcY - y0;
            
            // Clamp source coordinates
            x0 = min(x0, src->width - 1);
            y0 = min(y0, src->height - 1);
            
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

// ========== Circle Detection ==========
// Image structure: dark background, white calibration region with black circles, white crosshairs at centers
void DetectCircles(Image* img, Point2D* pts, int* count) {
    LOG_INFO("=== Starting Circle Detection ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);
    
    int srcRowSize = ((img->width * img->channels + 3) / 4) * 4;
    *count = 0;
    
    // Save debug image
    SaveBMP("debug_detect.bmp", img);
    
    // Helper lambda to get grayscale pixel value
    auto getGray = [&](int x, int y) -> int {
        int offset = y * srcRowSize + x * img->channels;
        if (img->channels == 1) {
            return img->data[offset];
        } else {
            return (img->data[offset] + img->data[offset+1] + img->data[offset+2]) / 3;
        }
    };
    
    // Step 1: Calculate global statistics to find white calibration region
    int minBrightness = 255, maxBrightness = 0;
    for (int y = 0; y < img->height; y += 10) {
        for (int x = 0; x < img->width; x += 10) {
            int brightness = getGray(x, y);
            if (brightness < minBrightness) minBrightness = brightness;
            if (brightness > maxBrightness) maxBrightness = brightness;
        }
    }
    
    // Step 2: Find the white calibration region using row/column analysis
    int whiteMinX = img->width, whiteMinY = img->height;
    int whiteMaxX = 0, whiteMaxY = 0;
    int brightThreshold = (minBrightness + maxBrightness * 3) / 4;
    
    // Count bright pixels per row and column
    int* rowBrightCount = (int*)calloc(img->height, sizeof(int));
    int* colBrightCount = (int*)calloc(img->width, sizeof(int));
    
    for (int y = 0; y < img->height; y++) {
        for (int x = 0; x < img->width; x++) {
            int brightness = getGray(x, y);
            if (brightness > brightThreshold) {
                rowBrightCount[y]++;
                colBrightCount[x]++;
            }
        }
    }
    
    // Find rows with significant bright content (calibration region rows)
    int significantRows = 0;
    int avgBrightPerRow = img->width / 10;  // Threshold for "significant" row
    
    // Find minY (first significant row)
    for (int y = 0; y < img->height; y++) {
        if (rowBrightCount[y] > avgBrightPerRow) {
            whiteMinY = y;
            break;
        }
    }
    
    // Find maxY (last significant row)
    for (int y = img->height - 1; y >= 0; y--) {
        if (rowBrightCount[y] > avgBrightPerRow) {
            whiteMaxY = y;
            break;
        }
    }
    
    // Find minX and maxX from columns
    for (int x = 0; x < img->width; x++) {
        if (colBrightCount[x] > avgBrightPerRow) {
            if (x < whiteMinX) whiteMinX = x;
            if (x > whiteMaxX) whiteMaxX = x;
        }
    }
    
    free(rowBrightCount);
    free(colBrightCount);
    
    // Expand bounds slightly to ensure coverage
    int expand = 5;
    whiteMinX = max(0, whiteMinX - expand);
    whiteMinY = max(0, whiteMinY - expand);
    whiteMaxX = min(img->width - 1, whiteMaxX + expand);
    whiteMaxY = min(img->height - 1, whiteMaxY + expand);
    
    // Debug: show the threshold values
    char dbg_th[256];
    sprintf_s(dbg_th, sizeof(dbg_th), "Thresholds - bright: %d, min: %d, max: %d\nRegion: (%d,%d) to (%d,%d)",
        brightThreshold, minBrightness, maxBrightness, whiteMinX, whiteMinY, whiteMaxX, whiteMaxY);
    MessageBoxA(NULL, dbg_th, "Debug", MB_OK);
    
    if (whiteMaxX - whiteMinX < 100 || whiteMaxY - whiteMinY < 100) {
        MessageBoxA(NULL, "White calibration region not found!", "Error", MB_OK);
        return;
    }
    
    // Draw the detected white region (green rectangle for color images)
    if (img->channels == 3) {
        for (int x = whiteMinX; x <= whiteMaxX; x++) {
            for (int t = -2; t <= 2; t++) {
                int y1 = whiteMinY + t;
                int y2 = whiteMaxY + t;
                if (x >= 0 && x < img->width) {
                    if (y1 >= 0 && y1 < img->height) {
                        int off = y1 * srcRowSize + x * 3;
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                    if (y2 >= 0 && y2 < img->height) {
                        int off = y2 * srcRowSize + x * 3;
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
            }
        }
        for (int y = whiteMinY; y <= whiteMaxY; y++) {
            for (int t = -2; t <= 2; t++) {
                int x1 = whiteMinX + t;
                int x2 = whiteMaxX + t;
                if (y >= 0 && y < img->height) {
                    if (x1 >= 0 && x1 < img->width) {
                        int off = y * srcRowSize + x1 * 3;
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                    if (x2 >= 0 && x2 < img->width) {
                        int off = y * srcRowSize + x2 * 3;
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
            }
        }
    }
    
    // Step 3: Within white region, find all white crosshair candidates
    // Strategy: Find all bright spots surrounded by dark (crosshairs in black circles)
    Point2D candidates[50];
    int candCount = 0;
    int crosshairThreshold = brightThreshold + 30;
    
    // Scan the white region for crosshair patterns
    for (int y = whiteMinY + 20; y < whiteMaxY - 20; y += 5) {
        for (int x = whiteMinX + 20; x < whiteMaxX - 20; x += 5) {
            int brightness = getGray(x, y);
            
            // This might be a crosshair if it's bright
            if (brightness > crosshairThreshold) {
                // Check if surrounded by dark (black circle)
                int darkCount = 0, brightCount = 0;
                for (int dy = -20; dy <= 20; dy += 4) {
                    for (int dx = -20; dx <= 20; dx += 4) {
                        if (abs(dx) <= 3 || abs(dy) <= 3) continue;  // Skip center cross
                        int nx = x + dx, ny = y + dy;
                        if (nx >= whiteMinX && nx < whiteMaxX && ny >= whiteMinY && ny < whiteMaxY) {
                            int lb = getGray(nx, ny);
                            if (lb < crosshairThreshold / 2) darkCount++;
                            else brightCount++;
                        }
                    }
                }
                
                // Crosshair: bright center, mostly dark surroundings
                if (darkCount > 15 && brightCount < 10 && candCount < 50) {
                    candidates[candCount].x = x;
                    candidates[candCount].y = y;
                    candCount++;
                }
            }
        }
    }
    
    // Show candidate count
    char dbg2[128];
    sprintf_s(dbg2, sizeof(dbg2), "Found %d crosshair candidates", candCount);
    MessageBoxA(NULL, dbg2, "Debug", MB_OK);
    
    if (candCount < 9) {
        MessageBoxA(NULL, "Not enough crosshairs found!", "Error", MB_OK);
        *count = 0;
        return;
    }
    
    // Remove duplicates (points too close together)
    Point2D filtered[50];
    int filteredCount = 0;
    int minDist = (whiteMaxX - whiteMinX) / 6;  // Minimum distance between points
    
    for (int i = 0; i < candCount; i++) {
        int isDup = 0;
        for (int j = 0; j < filteredCount; j++) {
            int dx = (int)(candidates[i].x - filtered[j].x);
            int dy = (int)(candidates[i].y - filtered[j].y);
            if (sqrt((double)dx*dx + dy*dy) < minDist) {
                isDup = 1;
                break;
            }
        }
        if (!isDup) {
            filtered[filteredCount++] = candidates[i];
        }
    }
    
    // Sort by Y to group into rows
    for (int i = 0; i < filteredCount; i++) {
        for (int j = i + 1; j < filteredCount; j++) {
            if (filtered[j].y < filtered[i].y) {
                Point2D tmp = filtered[i];
                filtered[i] = filtered[j];
                filtered[j] = tmp;
            }
        }
    }
    
    // Group into 3 rows based on actual Y positions
    // Find 3 Y clusters
    int row1Y = filtered[0].y;
    int row2Y = 0, row3Y = 0;
    
    for (int i = 1; i < filteredCount && i < 20; i++) {
        if (row2Y == 0 && filtered[i].y - filtered[0].y > (whiteMaxY - whiteMinY) / 4) {
            row2Y = filtered[i].y;
        }
        if (row2Y > 0 && row3Y == 0 && filtered[i].y - row2Y > (whiteMaxY - whiteMinY) / 4) {
            row3Y = filtered[i].y;
            break;
        }
    }
    
    // If we only found 2 rows, divide the height into 3 equal parts
    if (row3Y == 0) {
        row1Y = whiteMinY + (whiteMaxY - whiteMinY) / 6;
        row2Y = whiteMinY + (whiteMaxY - whiteMinY) / 2;
        row3Y = whiteMinY + (whiteMaxY - whiteMinY) * 5 / 6;
    }
    
    // Select 9 points: 3 from each row
    int foundCount = 0;
    for (int i = 0; i < filteredCount && foundCount < 9; i++) {
        int row = 0;
        double d1 = fabs(filtered[i].y - row1Y);
        double d2 = fabs(filtered[i].y - row2Y);
        double d3 = fabs(filtered[i].y - row3Y);
        
        if (d1 <= d2 && d1 <= d3) row = 0;
        else if (d2 <= d1 && d2 <= d3) row = 1;
        else row = 2;
        
        pts[foundCount] = filtered[i];
        foundCount++;
        
        // Only take 3 per row
        int countInRow = 0;
        for (int j = 0; j < foundCount - 1; j++) {
            double dj = fabs(pts[j].y - pts[foundCount-1].y);
            if (dj < (whiteMaxY - whiteMinY) / 4) countInRow++;
        }
        if (countInRow >= 3) {
            foundCount--;  // Remove this point, row is full
        }
    }
    
    // Sort final points: first by row (Y), then by column (X)
    for (int i = 0; i < foundCount; i++) {
        for (int j = i + 1; j < foundCount; j++) {
            int rowI = (int)(pts[i].y / ((whiteMaxY - whiteMinY) / 3));
            int rowJ = (int)(pts[j].y / ((whiteMaxY - whiteMinY) / 3));
            if (rowI > rowJ || (rowI == rowJ && pts[j].x < pts[i].x)) {
                Point2D tmp = pts[i];
                pts[i] = pts[j];
                pts[j] = tmp;
            }
        }
    }
    
    *count = (foundCount > 9) ? 9 : foundCount;
    
    LOG_INFO("Circle detection complete: %d points detected", *count);
    for (int i = 0; i < *count; i++) {
        LOG_DEBUG("  Point %d: (%.1f, %.1f)", i, pts[i].x, pts[i].y);
    }
    
    // Show results
    char msg[512];
    sprintf_s(msg, sizeof(msg), "Detected %d points:\n\nRow Y positions: %.0f, %.0f, %.0f\n\n",
        *count, row1Y, row2Y, row3Y);
    for (int i = 0; i < *count; i++) {
        char line[64];
        sprintf_s(line, sizeof(line), "%d: (%.0f, %.0f)\n", i + 1, pts[i].x, pts[i].y);
        strcat_s(msg, sizeof(msg), line);
    }
    MessageBoxA(NULL, msg, "Detection Results", MB_OK);
}

// ========== BlobAnalysisPro Detection ==========
// Use BlobAnalysisPro library to detect circle centers
void BlobDetectCircles(Image* img, Point2D* pts, int* count) {
    using namespace XVL;
    
    LOG_INFO("=== Starting BlobAnalysisPro Detection ===");
    LOG_DEBUG("Input image: %dx%d", img->width, img->height);
    
    *count = 0;
    
    // Convert our Image to XVImage format
    XVImage xvImage;
    memset(&xvImage, 0, sizeof(xvImage));
    xvImage.width = img->width;
    xvImage.height = img->height;
    xvImage.type = XVL::UInt8;  // 8位单通道灰度图
    xvImage.depth = 1;
    xvImage.pitch = img->width;  // 灰度图pitch=width，无额外填充
    
    // Convert to grayscale for XVImage
    unsigned char* grayData = (unsigned char*)malloc(img->width * img->height);
    if (img->channels == 1) {
        // Already grayscale, direct copy
        memcpy(grayData, img->data, img->width * img->height);
    } else {
        // Convert BGR to grayscale
        int srcRowSize = ((img->width * img->channels + 3) / 4) * 4;
        for (int y = 0; y < img->height; y++) {
            for (int x = 0; x < img->width; x++) {
                int srcOffset = y * srcRowSize + x * img->channels;
                int dstOffset = y * img->width + x;
                grayData[dstOffset] = (img->data[srcOffset] + img->data[srcOffset+1] + img->data[srcOffset+2]) / 3;
            }
        }
    }
    xvImage.data = grayData;
    
    printf("=== Adaptive Blob Detection ===\n");
    
    // ========== Step 1: Analyze histogram to find brightest region ==========
    int histogram[256] = {0};
    int totalPixels = img->width * img->height;
    
    for (int i = 0; i < totalPixels; i++) {
        histogram[grayData[i]]++;
    }
    
    // Find the peak (most frequent) brightness level
    int peakLevel = 128;
    int maxCount = 0;
    for (int i = 0; i < 256; i++) {
        if (histogram[i] > maxCount) {
            maxCount = histogram[i];
            peakLevel = i;
        }
    }
    printf("Histogram peak: %d (count=%d)\n", peakLevel, maxCount);
    
    // Adaptive threshold: Find brightest significant region
    // Use percentile method - find brightness where top X% pixels exist
    float brightPercentile = 0.70f;  // Target 70th percentile as "bright"
    int cumulative = 0;
    int brightThreshold = 128;
    int targetPixels = (int)(totalPixels * (1.0f - brightPercentile));
    
    for (int i = 255; i >= 0; i--) {
        cumulative += histogram[i];
        if (cumulative >= targetPixels) {
            brightThreshold = i;
            break;
        }
    }
    printf("Adaptive bright threshold: %d\n", brightThreshold);
    
    // Define ROI for the whole image
    XVRegion fullRoi;
    fullRoi.optional = NUL;
    fullRoi.frameWidth = img->width;
    fullRoi.frameHeight = img->height;
    
    // Step 2: Threshold to get bright regions
    XVThresholdImageToRegionMonoIn threshIn;
    threshIn.inImage = &xvImage;
    threshIn.inRegion = fullRoi;
    threshIn.inMin = (float)brightThreshold;
    threshIn.inMax = 255;
    
    XVThresholdImageToRegionMonoOut threshOut;
    int ret = XVThresholdImageToRegionMono(threshIn, threshOut);
    
    if (ret != 0) {
        printf("Threshold failed with code: %d\n", ret);
        free(grayData);
        return;
    }
    
    // Split region into blobs
    XVSplitRegionToBlobsIn splitIn;
    splitIn.inRegion = threshOut.outRegion;
    splitIn.inNeighborhood = 8;
    
    XVSplitRegionToBlobsOut splitOut;
    ret = XVSplitRegionToBlobs(splitIn, splitOut);
    
    if (ret != 0 || splitOut.outRegions.size() == 0) {
        printf("No blobs found in bright region\n");
        free(grayData);
        return;
    }
    
    // Find the largest blob (calibration area) by pixel count
    int maxArea = 0;
    int calibBlobIdx = 0;
    for (size_t i = 0; i < splitOut.outRegions.size(); i++) {
        int area = 0;
        for (size_t j = 0; j < splitOut.outRegions[i].arrayPointRun.size(); j++) {
            area += splitOut.outRegions[i].arrayPointRun[j].length;
        }
        if (area > maxArea) {
            maxArea = area;
            calibBlobIdx = i;
        }
    }
    
    printf("Found %d bright blobs, largest area: %d pixels\n", 
           (int)splitOut.outRegions.size(), maxArea);
    
    XVRegion calibRegion = splitOut.outRegions[calibBlobIdx];
    
    // ========== Step 3: Within calibration region, analyze dark regions ==========
    // Calculate histogram ONLY within calibRegion
    int darkHist[256] = {0};
    int calibPixels = 0;
    
    for (size_t i = 0; i < calibRegion.arrayPointRun.size(); i++) {
        int rx = calibRegion.arrayPointRun[i].x;
        int ry = calibRegion.arrayPointRun[i].y;
        int len = calibRegion.arrayPointRun[i].length;
        for (int x = rx; x < rx + len && x < img->width; x++) {
            int pixel = grayData[ry * img->width + x];
            darkHist[pixel]++;
            calibPixels++;
        }
    }
    
    // Find dark threshold - find the valley between dark and bright in histogram
    // Find first peak (dark) and second peak (bright background), use valley as threshold
    
    // Find dark peak (in lower half)
    int darkPeak = 0;
    int darkPeakVal = 0;
    for (int i = 0; i < 150; i++) {
        if (darkHist[i] > darkPeakVal) {
            darkPeakVal = darkHist[i];
            darkPeak = i;
        }
    }
    
    // Find bright peak (in upper half)
    int brightPeak = 255;
    int brightPeakVal = 0;
    for (int i = 150; i < 256; i++) {
        if (darkHist[i] > brightPeakVal) {
            brightPeakVal = darkHist[i];
            brightPeak = i;
        }
    }
    
    printf("Dark peak: %d, Bright peak: %d\n", darkPeak, brightPeak);
    
    // Find valley (minimum) between the two peaks
    int darkThreshold = (darkPeak + brightPeak) / 2;
    int minVal = darkHist[darkThreshold];
    for (int i = darkPeak + 1; i < brightPeak; i++) {
        if (darkHist[i] < minVal) {
            minVal = darkHist[i];
            darkThreshold = i;
        }
    }
    
    printf("Final dark threshold: %d\n", darkThreshold);
    
    // Draw the calibration region on image
    int dstRowSize = ((img->width * img->channels + 3) / 4) * 4;
    
    // Calculate bounding box
    int minX = img->width, minY = img->height, maxX = 0, maxY = 0;
    for (size_t i = 0; i < calibRegion.arrayPointRun.size(); i++) {
        int rx = calibRegion.arrayPointRun[i].x;
        int ry = calibRegion.arrayPointRun[i].y;
        int len = calibRegion.arrayPointRun[i].length;
        if (rx < minX) minX = rx;
        if (ry < minY) minY = ry;
        if (rx + len > maxX) maxX = rx + len;
        if (ry > maxY) maxY = ry;
    }
    
    // Draw filled calibRegion - semi-transparent overlay
    for (size_t i = 0; i < calibRegion.arrayPointRun.size(); i++) {
        int rx = calibRegion.arrayPointRun[i].x;
        int ry = calibRegion.arrayPointRun[i].y;
        int len = calibRegion.arrayPointRun[i].length;
        for (int x = rx; x < rx + len && x < img->width; x++) {
            if (x >= 0 && ry >= 0 && ry < img->height) {
                int off = ry * dstRowSize + x * img->channels;
                if (img->channels == 1) {
                    img->data[off] = min(255, img->data[off] + 100);
                } else {
                    img->data[off] = min(255, img->data[off] + 100);      // B
                    img->data[off + 1] = max(0, img->data[off + 1] - 50); // G
                    img->data[off + 2] = max(0, img->data[off + 2] - 50);  // R
                }
            }
        }
    }
    
    // Draw thick green border
    for (int x = minX; x <= maxX; x++) {
        for (int t = -2; t <= 2; t++) {
            int y1 = minY + t;
            int y2 = maxY + t;
            if (x >= 0 && x < img->width) {
                if (y1 >= 0 && y1 < img->height) {
                    int off = y1 * dstRowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = 255;
                    } else {
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
                if (y2 >= 0 && y2 < img->height) {
                    int off = y2 * dstRowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = 255;
                    } else {
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
            }
        }
    }
    for (int y = minY; y <= maxY; y++) {
        for (int t = -2; t <= 2; t++) {
            int x1 = minX + t;
            int x2 = maxX + t;
            if (y >= 0 && y < img->height) {
                if (x1 >= 0 && x1 < img->width) {
                    int off = y * dstRowSize + x1 * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = 255;
                    } else {
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
                if (x2 >= 0 && x2 < img->width) {
                    int off = y * dstRowSize + x2 * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = 255;
                    } else {
                        img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                    }
                }
            }
        }
    }
    
    printf("CalibRegion: area=%d, bbox=(%d,%d)-(%d,%d)\n", 
           (int)calibRegion.arrayPointRun.size(), minX, minY, maxX, maxY);
    
    // ========== Step 4: Find holes (9 circles) inside calibRegion ==========
    // Use threshold to get dark region within calibRegion
    XVThresholdImageToRegionMonoIn darkThreshIn;
    darkThreshIn.inImage = &xvImage;
    darkThreshIn.inRegion = calibRegion;
    darkThreshIn.inMin = 0;
    darkThreshIn.inMax = 255;  // Get all pixels (we'll find holes in this)
    
    XVThresholdImageToRegionMonoOut darkThreshOut;
    ret = XVThresholdImageToRegionMono(darkThreshIn, darkThreshOut);
    
    if (ret != 0) {
        printf("Threshold failed: %d\n", ret);
        free(grayData);
        return;
    }
    
    // Now use XVRegionHoles to find holes inside the dark region
    // The holes are the white circles inside the dark blob!
    XVRegionHolesIn holesIn;
    holesIn.inRegion = darkThreshOut.outRegion;
    holesIn.inNeighborhood = 8;
    holesIn.inMinHoleArea = 20;   // Minimum hole area
    holesIn.inMaxHoleArea = 999999; // Maximum hole area
    
    XVRegionHolesOut holesOut;
    ret = XVRegionHoles(holesIn, holesOut);
    
    printf("Found %d holes (circles) in dark region\n", (int)holesOut.outHoles.size());
    
    // ========== Step 5: Fit circles to hole edges for more accurate centers ==========
    // Use edge-based circle fitting instead of centroid for higher accuracy
    vector<Point2D> circleCenters;
    
    for (size_t i = 0; i < holesOut.outHoles.size(); i++) {
        // Get bounding box of the hole for approximate radius
        int holeMinX = img->width, holeMinY = img->height;
        int holeMaxX = 0, holeMaxY = 0;
        long long holePixelCount = 0;
        
        for (size_t j = 0; j < holesOut.outHoles[i].arrayPointRun.size(); j++) {
            int rx = holesOut.outHoles[i].arrayPointRun[j].x;
            int ry = holesOut.outHoles[i].arrayPointRun[j].y;
            int len = holesOut.outHoles[i].arrayPointRun[j].length;
            
            if (rx < holeMinX) holeMinX = rx;
            if (ry < holeMinY) holeMinY = ry;
            if (rx + len > holeMaxX) holeMaxX = rx + len;
            if (ry > holeMaxY) holeMaxY = ry;
            holePixelCount += len;
        }
        
        // Calculate approximate center (centroid) for initial estimate
        long long sumX = 0, sumY = 0;
        for (size_t j = 0; j < holesOut.outHoles[i].arrayPointRun.size(); j++) {
            int rx = holesOut.outHoles[i].arrayPointRun[j].x;
            int ry = holesOut.outHoles[i].arrayPointRun[j].y;
            int len = holesOut.outHoles[i].arrayPointRun[j].length;
            for (int k = 0; k < len; k++) {
                sumX += rx + k;
                sumY += ry;
            }
        }
        
        if (holePixelCount <= 0) continue;
        
        float approxCenterX = (float)sumX / holePixelCount;
        float approxCenterY = (float)sumY / holePixelCount;
        float approxRadius = max(holeMaxX - holeMinX, holeMaxY - holeMinY) / 2.0f;
        
        // Extract edge points of the hole for circle fitting
        vector<XVPoint2D> edgePoints;
        
        // Scan the perimeter of the hole to find edge points
        int cx = (int)approxCenterX;
        int cy = (int)approxCenterY;
        int scanRadius = (int)(approxRadius * 1.2f);
        int numAngles = 36;  // Sample 36 points around the circle
        
        for (int a = 0; a < numAngles; a++) {
            float angle = (float)a * 2.0f * 3.14159f / numAngles;
            
            // Scan from center outward to find edge
            for (int r = 0; r < scanRadius; r++) {
                int ex = (int)(cx + r * cos(angle));
                int ey = (int)(cy + r * sin(angle));
                
                if (ex < 0 || ex >= img->width || ey < 0 || ey >= img->height) break;
                
                int pixel = grayData[ey * img->width + ex];
                
                // Find transition from dark to bright (hole edge)
                if (r > 2) {
                    int prevEx = (int)(cx + (r-1) * cos(angle));
                    int prevEy = (int)(cy + (r-1) * sin(angle));
                    if (prevEx >= 0 && prevEx < img->width && prevEy >= 0 && prevEy < img->height) {
                        int prevPixel = grayData[prevEy * img->width + prevEx];
                        
                        // Dark to bright transition (edge of hole)
                        if (prevPixel < darkThreshold && pixel >= darkThreshold) {
                            // Use subpixel interpolation for better accuracy
                            if (r > 1) {
                                int prevPrevEx = (int)(cx + (r-2) * cos(angle));
                                int prevPrevEy = (int)(cy + (r-2) * sin(angle));
                                if (prevPrevEx >= 0 && prevPrevEx < img->width && 
                                    prevPrevEy >= 0 && prevPrevEy < img->height) {
                                    int prevPrevPixel = grayData[prevPrevEy * img->width + prevPrevEx];
                                    
                                    // Linear interpolation for subpixel position
                                    float delta = (float)(darkThreshold - prevPrevPixel);
                                    float totalDelta = (float)(pixel - prevPrevPixel);
                                    if (fabs(totalDelta) > 0.001f) {
                                        float frac = delta / totalDelta;
                                        float edgeR = r - 2 + frac;
                                        XVPoint2D pt;
                                        pt.x = cx + edgeR * cos(angle);
                                        pt.y = cy + edgeR * sin(angle);
                                        edgePoints.push_back(pt);
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }
        
        Point2D finalCenter;
        finalCenter.x = approxCenterX;
        finalCenter.y = approxCenterY;
        
        // If we have enough edge points, fit a circle for higher accuracy
        if (edgePoints.size() >= 8) {
            XVFitCircleToPointsIn fitIn;
            fitIn.inPoints = edgePoints;
            fitIn.inFlag = true;  // Local optimization
            fitIn.inDistance = 3.0f;  // Distance threshold for outliers
            fitIn.inIterations = 20;
            fitIn.inOutlier.optional = NUL;  // Disable outlier suppression
            
            XVFitCircleToPointsOut fitOut;
            int fitRet = XVFitCircleToPoints(fitIn, fitOut);
            
            if (fitRet == 0) {
                finalCenter.x = fitOut.outCircle.center.x;
                finalCenter.y = fitOut.outCircle.center.y;
                printf("  Hole %d: fitted=(%.2f, %.2f), radius=%.1f, edgePoints=%d\n", 
                       (int)i, finalCenter.x, finalCenter.y, fitOut.outCircle.radius, (int)edgePoints.size());
            } else {
                printf("  Hole %d: centroid=(%.1f, %.1f), radius=%.1f (fit failed)\n", 
                       (int)i, finalCenter.x, finalCenter.y, approxRadius);
            }
        } else {
            printf("  Hole %d: centroid=(%.1f, %.1f), radius=%.1f (insufficient edge points: %d)\n", 
                   (int)i, finalCenter.x, finalCenter.y, approxRadius, (int)edgePoints.size());
        }
        
        circleCenters.push_back(finalCenter);
        
        // ========== Draw hole (circle) on image ==========
        for (size_t j = 0; j < holesOut.outHoles[i].arrayPointRun.size(); j++) {
            int rx = holesOut.outHoles[i].arrayPointRun[j].x;
            int ry = holesOut.outHoles[i].arrayPointRun[j].y;
            int len = holesOut.outHoles[i].arrayPointRun[j].length;
            for (int x = rx; x < rx + len && x < img->width; x++) {
                if (x >= 0 && ry >= 0 && ry < img->height) {
                    int off = ry * dstRowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = (unsigned char)(img->data[off] * 0.5);
                    } else {
                        // Semi-transparent red overlay
                        img->data[off] = (unsigned char)(img->data[off] * 0.5);
                        img->data[off + 1] = (unsigned char)(img->data[off + 1] * 0.5);
                        img->data[off + 2] = (unsigned char)(min(255, img->data[off + 2] + 128));
                    }
                }
            }
        }
    }
    
    // Sort by Y then X
    sort(circleCenters.begin(), circleCenters.end(), [](const Point2D& a, const Point2D& b) {
        if (fabs(a.y - b.y) > 20) return a.y < b.y;
        return a.x < b.x;
    });
    
    // Take up to 9 points
    *count = min(9, (int)circleCenters.size());
    for (int i = 0; i < *count && i < 9; i++) {
        pts[i] = circleCenters[i];
    }
    
    free(grayData);
    
    printf("=== Detection Complete: %d points ===\n", *count);
    
    // Show results
    char msg[512];
    sprintf_s(msg, sizeof(msg), "Adaptive Blob Detection found %d points:\nBright threshold: %d\nDark threshold: %d", 
              *count, brightThreshold, darkThreshold);
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

// ========== Calibration (Least Squares) ==========
int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    LOG_INFO("=== Starting 9-Point Calibration ===");
    LOG_DEBUG("Number of points: %d", n);
    
    if (n < 3) {
        LOG_ERROR("Insufficient points for calibration (need at least 3)");
        return 0;  // 至少需要3个点确定仿射变换
    }
    
    LOG_DEBUG("Image Points -> World Points:");
    for (int i = 0; i < n; i++) {
        LOG_DEBUG("  %d: (%.2f, %.2f) -> (%.2f, %.2f)", 
            i, imagePts[i].x, imagePts[i].y, worldPts[i].x, worldPts[i].y);
    }

    // 构建正规方程 ATA * X = ATB
    double ATA[6][6] = { 0 };
    double ATB[6] = { 0 };

    for (int i = 0; i < n; i++) {
        double xi = imagePts[i].x, yi = imagePts[i].y;
        double Xi = worldPts[i].x, Yi = worldPts[i].y;

        // 第一组方程（world.x）
        ATA[0][0] += xi * xi;  ATA[0][1] += xi * yi;  ATA[0][2] += xi;
        ATA[1][0] += xi * yi;  ATA[1][1] += yi * yi;  ATA[1][2] += yi;
        ATA[2][0] += xi;       ATA[2][1] += yi;       ATA[2][2] += 1;  // 注意：应该是 1，不是 n

        // 第二组方程（world.y）
        ATA[3][3] += xi * xi;  ATA[3][4] += xi * yi;  ATA[3][5] += xi;
        ATA[4][3] += xi * yi;  ATA[4][4] += yi * yi;  ATA[4][5] += yi;
        ATA[5][3] += xi;       ATA[5][4] += yi;       ATA[5][5] += 1;  // 注意：应该是 1，不是 n

        ATB[0] += Xi * xi;  ATB[1] += Xi * yi;  ATB[2] += Xi;
        ATB[3] += Yi * xi;  ATB[4] += Yi * yi;  ATB[5] += Yi;
    }

    // 高斯消元（带部分主元选择）
    double X[6] = { 0 };

    for (int k = 0; k < 6; k++) {
        // 选主元
        int maxRow = k;
        for (int i = k + 1; i < 6; i++) {
            if (fabs(ATA[i][k]) > fabs(ATA[maxRow][k]))
                maxRow = i;
        }

        // 交换行
        if (maxRow != k) {
            for (int j = 0; j < 6; j++) {
                double tmp = ATA[k][j];
                ATA[k][j] = ATA[maxRow][j];
                ATA[maxRow][j] = tmp;
            }
            double tmp = ATB[k];
            ATB[k] = ATB[maxRow];
            ATB[maxRow] = tmp;
        }

        // 检查主元
        if (fabs(ATA[k][k]) < 1e-10) {
            return 0;  // 矩阵奇异，标定失败
        }

        // 消元
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
        double sum = ATB[i];
        for (int j = i + 1; j < 6; j++) {
            sum -= ATA[i][j] * X[j];
        }
        X[i] = sum / ATA[i][i];
    }

    trans->a = X[0];  trans->b = X[1];  trans->c = X[2];
    trans->d = X[3];  trans->e = X[4];  trans->f = X[5];

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

// ========== Trajectory Window ==========
void CreateTrajectoryWindow(HINSTANCE hInstance) {
    if (g_hwndTraj) {
        // Window already exists, just bring it to front and update
        ShowWindow(g_hwndTraj, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndTraj);
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        return;
    }

    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            if (g_trajImage.data) {
                int bitCount = g_trajImage.channels * 8;
                int rowSize = ((g_trajImage.width * g_trajImage.channels + 3) / 4) * 4;
                BITMAPINFO bmi = {0};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = g_trajImage.width;
                bmi.bmiHeader.biHeight = -g_trajImage.height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = (unsigned short)bitCount;
                bmi.bmiHeader.biCompression = BI_RGB;
                SetDIBitsToDevice(hdc, 0, 0, g_trajImage.width, g_trajImage.height, 0, 0, 0, g_trajImage.height, g_trajImage.data, &bmi, DIB_RGB_COLORS);
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
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    };
    wc.hInstance = hInstance;
    wc.lpszClassName = L"TrajectoryWindowClass";
    RegisterClass(&wc);

    g_hwndTraj = CreateWindowEx(0, L"TrajectoryWindowClass", L"Trajectory Display",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 
        g_trajImage.width > 0 ? g_trajImage.width + 100 : 640,
        g_trajImage.height > 0 ? g_trajImage.height + 150 : 480,
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
void DrawTrajectory(Image* img, Point2D* trajPixels, int count, int gray) {
    if (!img || !img->data || count < 2) return;
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;

    // Draw each point as a small square marker (3x3 pixels)
    for (int i = 0; i < count; i++) {
        int cx = (int)trajPixels[i].x;
        int cy = (int)trajPixels[i].y;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                int x = cx + dx;
                int y = cy + dy;
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

void ShowTrajectoryImage(Image* src, Point2D* trajPixels, int trajCount) {
    // Copy image data
    int rowSize = ((src->width * src->channels + 3) / 4) * 4;
    int imageSize = rowSize * src->height;
    
    if (g_trajImage.data) free(g_trajImage.data);
    g_trajImage.width = src->width;
    g_trajImage.height = src->height;
    g_trajImage.channels = src->channels;
    g_trajImage.data = (unsigned char*)malloc(imageSize);
    memset(g_trajImage.data, 0, imageSize);  // Black background
    
    // Draw trajectory points
    DrawTrajectory(&g_trajImage, trajPixels, trajCount, 255);  // White markers
    
    // Update window if exists, or create new
    if (g_hwndTraj) {
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        // Resize window to fit image (with extra margin)
        SetWindowPos(g_hwndTraj, HWND_TOP, 0, 0, 
            g_trajImage.width + 100, g_trajImage.height + 150, SWP_NOMOVE);
    }
}

// ========== FitShape Library Trajectory Detection ==========
using namespace XVL;

void DetectTrajectoryFitShape(Image* img, Point2D* trajPixels, int* count) {
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
    xvImage.type = XVL::UInt8;  // 关键：设置图像类型为8位单通道
    
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
    XVPoint2D axisStart = { 0, (float)(img->height / 2) };  // 从图像中间开始
    XVPoint2D axisEnd = { (float)img->width, (float)(img->height / 2) };  // 水平扫描
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
    stripeParams.minMagnitude = 20.0f;  // 条带检测阈值
    stripeParams.stripeType = XVStripeType::DARK;  // 检测暗条带（轨迹通常是暗线）
    stripeParams.minStripeWidth = 1.0f;  // 最小条带宽度
    stripeParams.maxStripeWidth = 50.0f;  // 最大条带宽度
    
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
                                  float angle, int pointCount, vector<Point>& outPoints) {
    float radius = width / 2.0f;  // semicircle radius
    float angleRad = angle * (float)CV_PI / 180.0f;
    
    // Calculate points per section
    // Semicircle perimeter = PI * radius, rectangle sides = 2 * length
    float totalPerimeter = 2.0f * (CV_PI * radius + length);
    int circlePoints = (int)(pointCount * (CV_PI * radius) / totalPerimeter);
    int linePoints = (int)(pointCount * length / totalPerimeter);
    circlePoints = max(10, circlePoints / 2);  // Each end semicircle
    
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
    LOG_INFO("=== Starting OpenCV Ellipse-Fit Trajectory Detection ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);
    
    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }
    
    int w = img->width, h = img->height;
    int srcRowSize = ((w * img->channels + 3) / 4) * 4;
    
    // Convert Image to OpenCV Mat (grayscale)
    Mat grayMat(h, w, CV_8UC1);
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            int off = y * srcRowSize + x * img->channels;
            if (img->channels == 1) {
                grayMat.at<uchar>(y, x) = img->data[off];
            } else {
                grayMat.at<uchar>(y, x) = (img->data[off] + img->data[off+1] + img->data[off+2]) / 3;
            }
        }
    }
    
    // Apply mild blur
    Mat blurMat;
    GaussianBlur(grayMat, blurMat, Size(3, 3), 0);
    
    // Canny edge detection
    Mat edgeMap;
    Canny(blurMat, edgeMap, 10, 30, 3);
    
    // Find all contours including nested ones
    vector<vector<Point>> contours;
    vector<Vec4i> hierarchy;
    findContours(edgeMap, contours, hierarchy, RETR_TREE, CHAIN_APPROX_NONE);
    
    // Store all trajectory points (original + fitted shapes)
    vector<Point> allPoints;
    int ellipseFitted = 0;
    int stadiumFitted = 0;
    
    // Process each contour
    for (size_t i = 0; i < contours.size(); i++) {
        const vector<Point>& contour = contours[i];
        int ptCount = (int)contour.size();
        
        // Need at least 5 points for fitEllipse
        if (ptCount < 5) continue;
        
        // Check if this is an internal contour (nested)
        bool isInternal = (hierarchy[i][3] >= 0);
        
        // Use minAreaRect to get bounding rectangle with angle
        if (ptCount >= 3) {
            RotatedRect box = minAreaRect(contour);
            
            float width = box.size.width;
            float height = box.size.height;
            
            // Ensure width >= height (long side is width)
            if (height > width) {
                swap(width, height);
            }
            
            // Calculate aspect ratio
            float aspectRatio = width / height;
            
            // If aspect ratio > 2.5, treat as stadium (long bar with rounded ends)
            if (aspectRatio > 2.5f && height > 5.0f) {
                // Stadium shape: length = width - height (center rectangle part)
                // width param = height (diameter of semicircles)
                float length = width - height;  // center rectangle length
                float diam = height;             // diameter for semicircles
                
                // Calculate point count based on perimeter
                int pointCount = max(50, (int)(2.0f * (CV_PI * diam / 2.0f + length) * 2.0f));
                
                vector<Point> stadiumPoints;
                GenerateStadiumShape(box.center, length, diam, box.angle, pointCount, stadiumPoints);
                
                // Clamp points to image bounds and add
                for (size_t k = 0; k < stadiumPoints.size(); k++) {
                    int px = max(0, min(w-1, stadiumPoints[k].x));
                    int py = max(0, min(h-1, stadiumPoints[k].y));
                    allPoints.push_back(Point(px, py));
                }
                stadiumFitted++;
            } else {
                // Small shape or circular - use ellipse fitting
                if (ptCount >= 6) {
                    RotatedRect ellipse = fitEllipse(contour);
                    
                    Size2f axes = ellipse.size;
                    float rx = axes.width / 2.0f;
                    float ry = axes.height / 2.0f;
                    if (ry > rx) swap(rx, ry);
                    
                    float perimeter = (float)(CV_PI * (3.0f * (rx + ry) - sqrt((3.0f * rx + ry) * (rx + 3.0f * ry))));
                    int ellipsePointCount = max(30, (int)(perimeter * 0.5f));
                    
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
                        }
                    }
                    ellipseFitted++;
                }
            }
        }
        
        // Also add original contour points (supplement)
        int step = isInternal ? 1 : 2;
        for (int j = 0; j < ptCount; j += step) {
            allPoints.push_back(contour[j]);
        }
    }
    
    LOG_INFO("Shape fitting: %d ellipses, %d stadiums, %d contours processed", 
             ellipseFitted, stadiumFitted, (int)contours.size());
    
    // Remove duplicate points (within 1-pixel radius)
    vector<Point> uniquePoints;
    vector<bool> kept(allPoints.size(), false);
    for (size_t i = 0; i < allPoints.size(); i++) {
        if (kept[i]) continue;
        uniquePoints.push_back(allPoints[i]);
        // Mark nearby points
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
    
    // Sort by position (y first, then x)
    if (uniquePoints.size() > 1) {
        sort(uniquePoints.begin(), uniquePoints.end(), [](const Point& a, const Point& b) {
            if (abs(a.y - b.y) > 2) return a.y < b.y;
            return a.x < b.x;
        });
    }
    
    // Output all unique points (up to array capacity: 10000)
    *count = min((int)uniquePoints.size(), 10000);
    for (int i = 0; i < *count; i++) {
        trajPixels[i].x = (float)uniquePoints[i].x;
        trajPixels[i].y = (float)uniquePoints[i].y;
    }
    
    LOG_INFO("Trajectory detection: raw=%d, unique=%d, output=%d points",
             (int)allPoints.size(), (int)uniquePoints.size(), *count);
    
    // Transform to world coordinates
    for (int i = 0; i < *count; i++) {
        g_trajWorld[i] = ImageToWorld(trajPixels[i], g_transform);
    }
    
    if (*count > 0) {
        LOG_DEBUG("  First: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
            trajPixels[0].x, trajPixels[0].y, g_trajWorld[0].x, g_trajWorld[0].y);
        LOG_DEBUG("  Last: pixel(%.1f, %.1f) -> world(%.2f, %.2f)", 
            trajPixels[*count-1].x, trajPixels[*count-1].y, 
            g_trajWorld[*count-1].x, g_trajWorld[*count-1].y);
    }
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
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"BlobAnalysisPro");
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"FitShape-Path");
            SendMessage(g_hwndMethodSelect, CB_ADDSTRING, 0, (LPARAM)L"OpenCV-Contour");
            SendMessage(g_hwndMethodSelect, CB_SETCURSEL, 0, 0);
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
                    
                    const char* methodName = (g_detectMethod == METHOD_BLOB) ? "BlobAnalysisPro" : 
                                            (g_detectMethod == METHOD_FITSHAPE) ? "FitShape-Path" :
                                            (g_detectMethod == METHOD_OPENCV) ? "OpenCV-Contour" : "Self-Algorithm";
                    LOG_INFO("Selected detection method: %s (index: %d)", methodName, sel);
                    sprintf_s(msgBuf, sizeof(msgBuf), "Using detection method: %s", methodName);
                    MessageBoxA(hwnd, msgBuf, "Detection Method", MB_OK);
                    
                    // Call appropriate detection function
                    if (g_detectMethod == METHOD_BLOB) {
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
                                if (sel == METHOD_SELF || sel == METHOD_BLOB) {
                                    DetectTrajectory(&g_image, g_trajPixels, &g_trajCount);
                                } else if (sel == METHOD_FITSHAPE) {
                                    DetectTrajectoryFitShape(&g_image, g_trajPixels, &g_trajCount);
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
