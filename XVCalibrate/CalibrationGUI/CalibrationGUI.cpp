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

// Include headers
#include "..\\..\\include\\BlobAnalysis.h"
#include "..\\..\\include\\FitShape.h"

// Detection method enum
enum DetectMethod {
    METHOD_SELF = 0,    // Self-implemented algorithm
    METHOD_BLOB = 1     // BlobAnalysisPro library
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
static HWND g_hwndMethodSelect;
static Image g_image = {0};
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

int SaveBMP(const char* filename, Image* img) {
    FILE* fp;
    if (fopen_s(&fp, filename, "wb") != 0) return 0;

    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    BMPHeader header = { 0 };
    header.type = 0x4D42;
    header.size = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + rowSize * img->height;
    header.offset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);

    BMPInfoHeader info = { 0 };
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
int LoadBMP(const char* filename, Image* img) {
    FILE* fp;
    if (fopen_s(&fp, filename, "rb") != 0) {
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

    // Check BMP signature "BM"
    if (header.type != 0x4D42) {
        char buf[256];
        sprintf_s(buf, sizeof(buf), "Not a BMP file\nSignature: 0x%04X (expected 0x4D42)", header.type);
        MessageBoxA(NULL, buf, "Format Error", MB_OK);
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
    int srcRowSize = ((img->width * bitCount / 8 + 3) / 4) * 4;  // Source row size
    int dstRowSize = ((img->width * 3 + 3) / 4) * 4;              // Always output 24-bit
    int imageSize = dstRowSize * img->height;
    
    // For 8-bit BMP, read palette (256 entries, 4 bytes each: B, G, R, Reserved)
    unsigned char palette[256][4] = {0};
    if (bitCount == 8) {
        // Palette starts at byte 54 (14 + 40) in standard BMP
        fseek(fp, 54, SEEK_SET);
        if (fread(palette, 4, 256, fp) != 256) {
            // Try reading from header.offset if palette is at different position
            fseek(fp, header.offset, SEEK_SET);
            if (fread(palette, 4, 256, fp) != 256) {
                MessageBoxA(NULL, "Cannot read color palette", "Read Error", MB_OK);
                fclose(fp);
                return 0;
            }
        }
    }
    
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
        
        // Convert indexed to RGB and write all rows
        for (int row = 0; row < img->height; row++) {
            unsigned char* dstRow = firstRow + row * dstRowStride;
            for (int col = 0; col < img->width; col++) {
                int paletteIdx = rowBuffer[col];
                dstRow[col * 3 + 0] = palette[paletteIdx][0];  // B
                dstRow[col * 3 + 1] = palette[paletteIdx][1];  // G
                dstRow[col * 3 + 2] = palette[paletteIdx][2];  // R
            }
        }
        free(rowBuffer);
    } else if (bitCount == 8 && compression == 0) {
        // Uncompressed 8-bit
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
            for (int x = 0; x < img->width; x++) {
                int paletteIdx = srcRow[x];
                dstRow[x * 3 + 0] = palette[paletteIdx][0];
                dstRow[x * 3 + 1] = palette[paletteIdx][1];
                dstRow[x * 3 + 2] = palette[paletteIdx][2];
            }
        }
        free(srcRow);
    }
    
    fclose(fp);
    
    // Save debug image to verify loading
    SaveBMP("debug_loaded.bmp", img);
    
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

// ========== Image Scaling ==========
void ScaleImage(Image* src, Image* dst, int dstWidth, int dstHeight) {
    dst->width = dstWidth;
    dst->height = dstHeight;
    int dstRowSize = ((dstWidth * 3 + 3) / 4) * 4;
    dst->data = (unsigned char*)malloc(dstRowSize * dstHeight);
    if (!dst->data) return;
    
    int srcRowSize = ((src->width * 3 + 3) / 4) * 4;
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
            
            unsigned char* p00 = src->data + y0 * srcRowSize + x0 * 3;
            unsigned char* p10 = src->data + y0 * srcRowSize + x1 * 3;
            unsigned char* p01 = src->data + y1 * srcRowSize + x0 * 3;
            unsigned char* p11 = src->data + y1 * srcRowSize + x1 * 3;
            
            unsigned char* pdst = dst->data + y * dstRowSize + x * 3;
            for (int c = 0; c < 3; c++) {
                double val = (1-fx)*(1-fy)*p00[c] + fx*(1-fy)*p10[c] + (1-fx)*fy*p01[c] + fx*fy*p11[c];
                pdst[c] = (unsigned char)max(0, min(255, val));
            }
        }
    }
}

// ========== Circle Detection ==========
// Image structure: dark background, white calibration region with black circles, white crosshairs at centers
void DetectCircles(Image* img, Point2D* pts, int* count) {
    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    *count = 0;
    
    // Save debug image
    SaveBMP("debug_detect.bmp", img);
    
    // Step 1: Calculate global statistics to find white calibration region
    int minBrightness = 255, maxBrightness = 0;
    for (int y = 0; y < img->height; y += 10) {
        for (int x = 0; x < img->width; x += 10) {
            int offset = y * rowSize + x * 3;
            int brightness = (img->data[offset] + img->data[offset+1] + img->data[offset+2]) / 3;
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
            int offset = y * rowSize + x * 3;
            int brightness = (img->data[offset] + img->data[offset+1] + img->data[offset+2]) / 3;
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
    
    // Draw the detected white region (green rectangle)
    for (int x = whiteMinX; x <= whiteMaxX; x++) {
        for (int t = -2; t <= 2; t++) {
            int y1 = whiteMinY + t;
            int y2 = whiteMaxY + t;
            if (x >= 0 && x < img->width) {
                if (y1 >= 0 && y1 < img->height) {
                    int off = y1 * rowSize + x * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                }
                if (y2 >= 0 && y2 < img->height) {
                    int off = y2 * rowSize + x * 3;
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
                    int off = y * rowSize + x1 * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                }
                if (x2 >= 0 && x2 < img->width) {
                    int off = y * rowSize + x2 * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
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
            int offset = y * rowSize + x * 3;
            int brightness = img->data[offset] + img->data[offset+1] + img->data[offset+2];
            
            // This might be a crosshair if it's bright
            if (brightness > crosshairThreshold * 3) {
                // Check if surrounded by dark (black circle)
                int darkCount = 0, brightCount = 0;
                for (int dy = -20; dy <= 20; dy += 4) {
                    for (int dx = -20; dx <= 20; dx += 4) {
                        if (abs(dx) <= 3 || abs(dy) <= 3) continue;  // Skip center cross
                        int nx = x + dx, ny = y + dy;
                        if (nx >= whiteMinX && nx < whiteMaxX && ny >= whiteMinY && ny < whiteMaxY) {
                            int noff = ny * rowSize + nx * 3;
                            int lb = img->data[noff] + img->data[noff+1] + img->data[noff+2];
                            if (lb < crosshairThreshold * 2) darkCount++;
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
    
    *count = 0;
    
    // Convert our Image to XVImage format
    XVImage xvImage;
    xvImage.width = img->width;
    xvImage.height = img->height;
    xvImage.type = XVL::UInt8;
    xvImage.depth = 1;
    xvImage.pitch = ((img->width + 3) / 4) * 4;
    
    // Convert BGR to grayscale for XVImage
    unsigned char* grayData = (unsigned char*)malloc(img->width * img->height * 4);
    int rowSize = ((img->width * 3 + 3) / 4) * 4;
    for (int y = 0; y < img->height; y++) {
        for (int x = 0; x < img->width; x++) {
            int srcOffset = y * rowSize + x * 3;
            int dstOffset = y * img->width + x;
            grayData[dstOffset] = (img->data[srcOffset] + img->data[srcOffset+1] + img->data[srcOffset+2]) / 3;
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
    int dstRowSize = ((img->width * 3 + 3) / 4) * 4;
    
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
    
    // Draw filled calibRegion with semi-transparent blue
    for (size_t i = 0; i < calibRegion.arrayPointRun.size(); i++) {
        int rx = calibRegion.arrayPointRun[i].x;
        int ry = calibRegion.arrayPointRun[i].y;
        int len = calibRegion.arrayPointRun[i].length;
        for (int x = rx; x < rx + len && x < img->width; x++) {
            if (x >= 0 && ry >= 0 && ry < img->height) {
                int off = ry * dstRowSize + x * 3;
                unsigned char bgR = img->data[off + 2];
                unsigned char bgG = img->data[off + 1];
                unsigned char bgB = img->data[off];
                img->data[off] = min(255, bgB + 100);
                img->data[off + 1] = max(0, bgG - 50);
                img->data[off + 2] = max(0, bgR - 50);
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
                    int off = y1 * dstRowSize + x * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                }
                if (y2 >= 0 && y2 < img->height) {
                    int off = y2 * dstRowSize + x * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
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
                    int off = y * dstRowSize + x1 * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
                }
                if (x2 >= 0 && x2 < img->width) {
                    int off = y * dstRowSize + x2 * 3;
                    img->data[off] = 0; img->data[off+1] = 255; img->data[off+2] = 0;
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
                    int off = ry * dstRowSize + x * 3;
                    // Semi-transparent red overlay
                    img->data[off] = (unsigned char)(img->data[off] * 0.5);
                    img->data[off + 1] = (unsigned char)(img->data[off + 1] * 0.5);
                    img->data[off + 2] = (unsigned char)(min(255, img->data[off + 2] + 128));
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

// ========== Calibration (Least Squares) ==========
int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    if (n < 3) return 0;  // 至少需要3个点确定仿射变换

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
            SendMessage(g_hwndMethodSelect, CB_SETCURSEL, 0, 0);
        }
        return 0;

    case WM_COMMAND:
        {
            int btnId = LOWORD(wParam);
            char msgBuf[1024 * 10];

            switch (btnId) {
            case 1:  // Generate Image
                if (g_image.data) free(g_image.data);
                g_image.data = NULL;
                if (CreateCalibrationImage(&g_image, IMAGE_WIDTH, IMAGE_HEIGHT)) {
                    SaveBMP("calibration_9points.bmp", &g_image);
                    strcpy_s(statusText, sizeof(statusText), "Step 1: Calibration image generated and saved");
                    g_step = 1;
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                }
                break;

            case 2: { // Load Image
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
                if (!g_image.data) {
                    strcpy_s(statusText, sizeof(statusText), "Error: No image loaded");
                    MessageBoxA(hwnd, "Please complete Step 1 or Step 2 to load an image first.", "No Image", MB_OK | MB_ICONWARNING);
                } else {
                    // Get selected detection method
                    int sel = SendMessage(g_hwndMethodSelect, CB_GETCURSEL, 0, 0);
                    g_detectMethod = (sel == 1) ? METHOD_BLOB : METHOD_SELF;
                    
                    const char* methodName = (g_detectMethod == METHOD_BLOB) ? "BlobAnalysisPro" : "Self-Algorithm";
                    sprintf_s(msgBuf, sizeof(msgBuf), "Using detection method: %s", methodName);
                    MessageBoxA(hwnd, msgBuf, "Detection Method", MB_OK);
                    
                    // Call appropriate detection function
                    if (g_detectMethod == METHOD_BLOB) {
                        BlobDetectCircles(&g_image, g_detectedPts, &g_detectedCount);
                    } else {
                        DetectCircles(&g_image, g_detectedPts, &g_detectedCount);
                    }
                    
                    for (int i = 0; i < g_detectedCount && i < 9; i++) g_imagePts[i] = g_detectedPts[i];
                    DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0, 255, 0);
                    
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
                if (g_step < 3) {
                    strcpy_s(statusText, sizeof(statusText), "Error: Detect circles first");
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
                        DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0, 200, 255);
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
        CW_USEDEFAULT, CW_USEDEFAULT, 1300, 900,
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
