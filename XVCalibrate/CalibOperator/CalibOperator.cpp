/**
 * CalibOperator.cpp - 工业视觉标定算子库实现
 * 纯算法层，不依赖 Windows GUI (无 HWND/HDC/WM_*)
 * 依赖：OpenCV 4.x, C 标准库
 */

#include "CalibOperator.h"
#include <stdarg.h>
#include <time.h>
#include <string>
#include <vector>
#include <map>
#include <cstdio>
#include <sstream>
#include <cmath>
#include <algorithm>

using namespace cv;

// ========== 日志回调 ==========

static LogCallback g_logCallback = NULL;

void CalibSetLogCallback(LogCallback cb) {
    g_logCallback = cb;
}

void CalibDefaultLog(int level, const char* fmt, ...) {
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
#ifdef _WIN32
    localtime_s(&t, &now);
#else
    localtime_r(&now, &t);
#endif
    char timeStr[32];
    strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &t);

    va_list args;
    va_start(args, fmt);
    fprintf(stdout, "[%s] %s ", timeStr, prefix);
    vfprintf(stdout, fmt, args);
    fprintf(stdout, "\n");
    fflush(stdout);
    va_end(args);
}

// 内部日志宏（使用回调机制）
#define OP_LOG(level, fmt, ...) do { \
    if (g_logCallback) { \
        g_logCallback(level, fmt, ##__VA_ARGS__); \
    } else { \
        CalibDefaultLog(level, fmt, ##__VA_ARGS__); \
    } \
} while(0)

#define LOG_INFO(fmt, ...)   OP_LOG(0, fmt, ##__VA_ARGS__)
#define LOG_WARN(fmt, ...)   OP_LOG(1, fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...)  OP_LOG(2, fmt, ##__VA_ARGS__)
#define LOG_DEBUG(fmt, ...)  OP_LOG(3, fmt, ##__VA_ARGS__)

// ========== 常量：BAR_COLORS ==========

const unsigned char BAR_COLORS[16][3] = {
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

// ========== 内部工具函数 ==========

// 将 OpenCV Mat（单通道灰度或3通道BGR）转换为 Image（3通道BGR）
void MatToImageBGR(const cv::Mat& src, Image* dst) {
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

// ========== 图像 I/O ==========

int CreateCalibrationImage(Image* img, int width, int height) {
    int rowSize = width;
    img->width = width;
    img->height = height;
    img->channels = 1;
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
                        img->data[y * rowSize + x] = 0;
                    }
                }
            }

            for (int x = cx - crossSize; x <= cx + crossSize; x++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int y = cy + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        img->data[y * rowSize + x] = 255;
                    }
                }
            }
            for (int y = cy - crossSize; y <= cy + crossSize; y++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int x = cx + t;
                    if (x >= 0 && x < width && y >= 0 && y < height) {
                        img->data[y * rowSize + x] = 255;
                    }
                }
            }
        }
    }
    return 1;
}

int SaveBMP(const char* filename, Image* img) {
    FILE* fp;
#ifdef _WIN32
    if (fopen_s(&fp, filename, "wb") != 0) return 0;
#else
    fp = fopen(filename, "wb");
    if (!fp) return 0;
#endif

    // 内存中的行是紧凑的 width*channels；BMP 要求每行字节数为 4 的倍数。
    // 错误地对整块 fwrite(rowSize*height) 会在 stride 不一致时写出损坏文件（PIL 报 truncated 等）。
    int bitCount = img->channels * 8;
    int srcRowBytes = img->width * img->channels;
    int bmpRowBytes = ((srcRowBytes + 3) / 4) * 4;
    // 8bpp BMP 必须在 InfoHeader 之后写入 256×RGBQUAD，否则文件无效；PIL/OpenCV 读到的像素会错位或近似全黑。
    const int paletteBytes = (img->channels == 1) ? (256 * 4) : 0;

    BMPHeader header = { 0 };
    header.type = 0x4D42;
    header.size = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + paletteBytes + bmpRowBytes * img->height;
    header.offset = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + paletteBytes;

    BMPInfoHeader info = { 0 };
    info.size = sizeof(BMPInfoHeader);
    info.width = img->width;
    // 负高度 = top-down DIB：文件第 0 行对应内存第 0 行（与预览/ToBitmap 一致）。
    // 正高度 bottom-up 会把内存顶行写到文件底行，外部查看器与流程内显示上下颠倒。
    info.height = -img->height;
    info.planes = 1;
    info.bitCount = (unsigned short)bitCount;
    info.imageSize = bmpRowBytes * img->height;
    if (img->channels == 1) {
        info.colorsUsed = 256;
        info.colorsImportant = 256;
    }

    fwrite(&header, sizeof(header), 1, fp);
    fwrite(&info, sizeof(info), 1, fp);

    if (paletteBytes > 0) {
        unsigned char quad[4];
        for (int i = 0; i < 256; i++) {
            quad[0] = quad[1] = quad[2] = (unsigned char)i;
            quad[3] = 0;
            if (fwrite(quad, 1, 4, fp) != 4) {
                fclose(fp);
                return 0;
            }
        }
    }

    std::vector<unsigned char> row(static_cast<size_t>(bmpRowBytes));
    const unsigned char* src = img->data;
    for (int y = 0; y < img->height; y++) {
        memcpy(row.data(), src, static_cast<size_t>(srcRowBytes));
        if (bmpRowBytes > srcRowBytes)
            memset(row.data() + srcRowBytes, 0, static_cast<size_t>(bmpRowBytes - srcRowBytes));
        if (fwrite(row.data(), 1, static_cast<size_t>(bmpRowBytes), fp) != static_cast<size_t>(bmpRowBytes)) {
            fclose(fp);
            return 0;
        }
        src += srcRowBytes;
    }
    fclose(fp);
    return 1;
}

int LoadBMP(const char* filename, Image* img) {
    LOG_INFO("Loading BMP file: %s", filename);
    FILE* fp;
#ifdef _WIN32
    if (fopen_s(&fp, filename, "rb") != 0) {
        LOG_ERROR("Cannot open file: %s", filename);
        return 0;
    }
#else
    fp = fopen(filename, "rb");
    if (!fp) {
        LOG_ERROR("Cannot open file: %s", filename);
        return 0;
    }
#endif

    BMPHeader header;
    memset(img, 0, sizeof(*img));

    if (fread(&header, sizeof(header), 1, fp) != 1) {
        LOG_ERROR("Cannot read BMP header");
        fclose(fp);
        return 0;
    }

    if (header.type != 0x4D42) {
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

        if (foundOffset >= 0) {
            LOG_ERROR("BMP signature found at offset %d (expected at 0). First bytes: %02X %02X %02X %02X",
                foundOffset, buf[0], buf[1], buf[2], buf[3]);
        } else {
            LOG_ERROR("Not a BMP file. First 16 bytes: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
                buf[0], buf[1], buf[2], buf[3], buf[4], buf[5], buf[6], buf[7],
                buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15]);
        }
        fclose(fp);
        return 0;
    }

    unsigned char dibHeader[64];
    int dibHeaderSize;

    fseek(fp, 14, SEEK_SET);
    if (fread(&dibHeaderSize, 4, 1, fp) != 1) {
        LOG_ERROR("Cannot read DIB header size");
        fclose(fp);
        return 0;
    }

    fseek(fp, 14, SEEK_SET);
    if (fread(dibHeader, dibHeaderSize, 1, fp) != 1) {
        LOG_ERROR("Cannot read DIB header");
        fclose(fp);
        return 0;
    }

    int width = *(int*)&dibHeader[4];
    int height = *(int*)&dibHeader[8];
    int bitCount = *(short*)&dibHeader[14];
    int compression = *(int*)&dibHeader[16];

    if (width <= 0 || width > 10000 || height == 0 || height > 10000) {
        LOG_ERROR("Invalid dimensions: %dx%d", width, height);
        fclose(fp);
        return 0;
    }

    if (bitCount != 8 && bitCount != 24) {
        LOG_ERROR("Only 8-bit or 24-bit BMP supported, file is %d-bit", bitCount);
        fclose(fp);
        return 0;
    }

    if (compression != 0 && compression != 1) {
        LOG_ERROR("Unsupported compression format: %d", compression);
        fclose(fp);
        return 0;
    }

    img->width = width;
    img->height = abs(height);
    img->channels = bitCount / 8;
    LOG_DEBUG("BMP dimensions: %dx%d, bitCount: %d, channels: %d", width, abs(height), bitCount, img->channels);

    int srcRowSize = ((img->width * bitCount / 8 + 3) / 4) * 4;
    int dstRowSize = img->width * img->channels;
    int imageSize = dstRowSize * img->height;
    LOG_DEBUG("Image size: %d bytes, rowSize: %d", imageSize, dstRowSize);

    img->data = (unsigned char*)malloc(imageSize);
    if (!img->data) {
        LOG_ERROR("Memory allocation failed");
        fclose(fp);
        return 0;
    }
    memset(img->data, 0, imageSize);

    fseek(fp, header.offset, SEEK_SET);

    int topDown = (height < 0);
    int dstRowStride = topDown ? dstRowSize : -dstRowSize;
    unsigned char* firstRow = topDown ? img->data : img->data + (img->height - 1) * dstRowSize;

    if (bitCount == 24 && compression == 0) {
        unsigned char* srcRow = (unsigned char*)malloc(srcRowSize);
        if (!srcRow) {
            LOG_ERROR("Memory allocation failed");
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
                LOG_ERROR("Failed to read row %d", y);
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
            LOG_ERROR("Memory allocation failed");
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
                y++;
                x = 0;
            } else if (byte1 == 0 && byte2 == 1) {
                break;
            } else if (byte1 == 0 && byte2 == 2) {
                unsigned char dx, dy;
                if (fread(&dx, 1, 1, fp) != 1) break;
                if (fread(&dy, 1, 1, fp) != 1) break;
                x += dx;
                y += dy;
            } else if (byte1 > 0) {
                int count = byte1;
                if (x + count > img->width) count = img->width - x;
                memset(rowBuffer + x, byte2, count);
                x += count;
                if ((byte1 & 1) == 1) {
                    fseek(fp, 1, SEEK_CUR);
                }
            } else {
                int count = byte2;
                if (x + count > img->width) count = img->width - x;
                if (fread(rowBuffer + x, 1, count, fp) != (size_t)count) break;
                x += count;
                if ((byte2 & 1) == 1) {
                    fseek(fp, 1, SEEK_CUR);
                }
            }

            if (x > img->width) x = img->width;
        }

        for (int row = 0; row < img->height; row++) {
            unsigned char* dstRow = firstRow + row * dstRowStride;
            memcpy(dstRow, rowBuffer, img->width);
        }
        free(rowBuffer);
    } else if (bitCount == 8 && compression == 0) {
        unsigned char palette[256];
        {
            long colorTableOffset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);
            fseek(fp, colorTableOffset, SEEK_SET);
            unsigned int colorsUsed = *(unsigned int*)&dibHeader[32];
            int numColors = (colorsUsed > 0 && colorsUsed <= 256) ? (int)colorsUsed : 256;
            for (int i = 0; i < numColors; i++) {
                unsigned char bgra[4];
                if (fread(bgra, 1, 4, fp) != 4) {
                    palette[i] = i;
                } else {
                    palette[i] = (unsigned char)(0.299f * bgra[2] + 0.587f * bgra[1] + 0.114f * bgra[0] + 0.5f);
                }
            }
            for (int i = numColors; i < 256; i++) {
                palette[i] = i;
            }
        }

        unsigned char* srcRow = (unsigned char*)malloc(srcRowSize);
        if (!srcRow) {
            LOG_ERROR("Memory allocation failed");
            free(img->data);
            img->data = NULL;
            fclose(fp);
            return 0;
        }

        for (int y = 0; y < img->height; y++) {
            unsigned char* dstRow = firstRow + y * dstRowStride;
            int srcY = topDown ? y : (img->height - 1 - y);
            long seekPos = header.offset + srcY * srcRowSize;
            fseek(fp, seekPos, SEEK_SET);
            size_t bytesRead = fread(srcRow, 1, srcRowSize, fp);
            if (bytesRead != (size_t)srcRowSize) {
                LOG_ERROR("Failed to read row %d: seek=%ld, expected=%d, got=%zu, feof=%d, ferror=%d",
                          y, seekPos, srcRowSize, bytesRead, feof(fp), ferror(fp));
                free(srcRow);
                free(img->data);
                img->data = NULL;
                fclose(fp);
                return 0;
            }
            for (int x = 0; x < img->width; x++) {
                dstRow[x] = palette[srcRow[x]];
            }
        }
        free(srcRow);
    }

    fclose(fp);

    // Save debug image
    SaveBMP("debug_loaded.bmp", img);
    LOG_INFO("BMP file loaded successfully: %dx%d", img->width, img->height);

    return 1;
}

int LoadImageFile(const char* filename, Image* img) {
    if (!filename || !img) return 0;
    memset(img, 0, sizeof(*img));
    Mat m = imread(filename, IMREAD_UNCHANGED);
    if (m.empty()) {
        LOG_ERROR("LoadImageFile: cannot read %s", filename);
        return 0;
    }
    Mat bgr;
    if (m.channels() == 1)
        cvtColor(m, bgr, COLOR_GRAY2BGR);
    else if (m.channels() == 4)
        cvtColor(m, bgr, COLOR_BGRA2BGR);
    else if (m.channels() == 3)
        bgr = m;
    else {
        LOG_ERROR("LoadImageFile: unsupported channels=%d", m.channels());
        return 0;
    }
    MatToImageBGR(bgr, img);
    LOG_INFO("LoadImageFile: %s -> %dx%d", filename, img->width, img->height);
    return 1;
}

// ========== 圆检测 ==========

void DetectCircles(Image* img, Point2D* pts, int* count) {
    LOG_INFO("=== Starting Circle Detection (Contour + Crosshair) ===");
    LOG_DEBUG("Input image: %dx%d, channels: %d", img->width, img->height, img->channels);

    *count = 0;
    int w = img->width, h = img->height;

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

    // Step 0: Find calibration region using OTSU
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(5, 5), 1.0);
    Mat brightBinary;
    threshold(blurred, brightBinary, 0, 255, THRESH_BINARY + THRESH_OTSU);

    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(7, 7));
    // 先闭后开
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);
    // 先开后闭
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);

    std::vector<std::vector<Point>> brightContours;
    findContours(brightBinary, brightContours, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE);

    if (brightContours.empty()) {
        LOG_ERROR("No calibration region found (OTSU)");
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
        LOG_ERROR("Calibration region too small");
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

    Mat calibMask = Mat::zeros(h, w, CV_8UC1);
    drawContours(calibMask, brightContours, maxIdx, Scalar(255), FILLED);

    // Step 1: Find dark circles using contour analysis
    Mat innerGray = grayMat.clone();
    innerGray.setTo(128, calibMask == 0);

    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));

    Mat darkBinary;
    threshold(innerGray, darkBinary, 0, 255, THRESH_BINARY_INV + THRESH_OTSU);
    darkBinary.setTo(0, calibMask == 0);
    // 先开后闭
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);
    // 先闭后开
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);

    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    Mat darkAdaptive;
    adaptiveThreshold(innerGray, darkAdaptive, 255,
                      ADAPTIVE_THRESH_GAUSSIAN_C, THRESH_BINARY_INV, 31, 10);
    darkAdaptive.setTo(0, calibMask == 0);
    // 先开后闭
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_OPEN, kernel2);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_CLOSE, kernel2);
    // 先闭后开
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_CLOSE, kernel2);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_OPEN, kernel2);

    std::vector<std::vector<Point>> adaptiveContours;
    findContours(darkAdaptive, adaptiveContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

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

    bool usedAdaptive = false;
    if (adaptiveCircular > otsuCircular) {
        darkContours = adaptiveContours;
        usedAdaptive = true;
    }
    LOG_INFO("Dark contours: %d (used=%s, circular: OTSU=%d, Adaptive=%d)",
             (int)darkContours.size(), usedAdaptive ? "adaptive" : "OTSU",
             otsuCircular, adaptiveCircular);

    struct CircleInfo {
        Point2f center;
        float radius;
        float circularity;
        double area;
        int contourIdx;    // 追踪原始轮廓索引，用于后续 fitEllipse
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
        info.contourIdx = (int)i;
        circleCandidates.push_back(info);
    }

    std::vector<CircleInfo> circularOnes;
    for (size_t i = 0; i < circleCandidates.size(); i++) {
        if (circleCandidates[i].circularity > 0.5f)
            circularOnes.push_back(circleCandidates[i]);
    }

    std::vector<CircleInfo> selected;

    if (circularOnes.size() >= 9) {
        std::sort(circularOnes.begin(), circularOnes.end(),
            [](const CircleInfo& a, const CircleInfo& b) { return a.radius < b.radius; });
        float medianR = circularOnes[circularOnes.size() / 2].radius;

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
            info.contourIdx = (int)i;
            circleCandidates.push_back(info);
            if (circularity > 0.5f) circularOnes.push_back(info);
        }
        // fallback 路径：将 darkContours 切换为 adaptiveContours，
        // 使后续精化步骤中 contourIdx 索引正确对应
        darkContours = adaptiveContours;
        usedAdaptive = true;

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
        }
    }

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
    }

    if (selected.size() < 3) {
        LOG_ERROR("Not enough circles detected!");
        return;
    }

    // Step 2: 亚像素圆心精化 (fitEllipse + moments)
    int foundCount = 0;
    for (int i = 0; i < (int)selected.size() && foundCount < 9; i++) {
        Point2f roughCenter = selected[i].center;

        // --- 方法 1: cv::fitEllipse 椭圆拟合 (需要 >= 6 个轮廓点) ---
        Point2f ellipseCenter = roughCenter;
        bool hasEllipse = false;
        int cidx = selected[i].contourIdx;
        if (cidx >= 0 && cidx < (int)darkContours.size() &&
            darkContours[cidx].size() >= 6) {
            try {
                RotatedRect rect = fitEllipse(darkContours[cidx]);
                // 过滤明显不合理的拟合（尺寸偏离粗估计太多）
                float fitR = (float)((rect.size.width + rect.size.height) / 4.0);
                if (fitR > 0 && fitR < roughCenter.x * 0.5f) { // 半径合理性检查
                    ellipseCenter = rect.center;
                    hasEllipse = true;
                }
            } catch (...) {
                // fitEllipse 可能抛异常（退化情况）
            }
        }

        // --- 方法 2: cv::moments 空间矩质心 ---
        Point2f momentsCenter = roughCenter;
        if (cidx >= 0 && cidx < (int)darkContours.size()) {
            Moments m = moments(darkContours[cidx]);
            if (m.m00 > 0) {
                momentsCenter.x = (float)(m.m10 / m.m00);
                momentsCenter.y = (float)(m.m01 / m.m00);
            }
        }

        // --- 融合：取两者平均 (两者精度都在 0.1~0.2 像素，平均更稳健) ---
        if (hasEllipse) {
            pts[foundCount].x = (ellipseCenter.x + momentsCenter.x) / 2.0;
            pts[foundCount].y = (ellipseCenter.y + momentsCenter.y) / 2.0;
        } else {
            pts[foundCount].x = momentsCenter.x;
            pts[foundCount].y = momentsCenter.y;
        }

        LOG_DEBUG("  Hole %d: rough(%.1f,%.1f) -> refined(%.2f,%.2f)%s",
                  foundCount, roughCenter.x, roughCenter.y,
                  pts[foundCount].x, pts[foundCount].y,
                  hasEllipse ? " [fitEllipse+moment]" : " [moment only]");
        foundCount++;
    }

    // Step 3: Sort into 3x3 grid
    for (int i = 0; i < foundCount; i++) {
        for (int j = i + 1; j < foundCount; j++) {
            if (pts[j].y < pts[i].y) {
                Point2D tmp = pts[i]; pts[i] = pts[j]; pts[j] = tmp;
            }
        }
    }

    if (foundCount > 3) {
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
}

static int ImageRowStrideBytes(int width, int channels) {
    int rowBytes = width * channels;
    if (rowBytes % 4 != 0)
        rowBytes = ((rowBytes / 4) + 1) * 4;
    return rowBytes;
}

/// 从 Image 克隆为连续 cv::Mat（兼容紧凑存储与 4 字节对齐行步长）。
static Mat ImageToMatClone(const Image* img) {
    if (!img || !img->data || img->width <= 0 || img->height <= 0)
        return Mat();
    int w = img->width, h = img->height, ch = img->channels;
    if (ch != 1 && ch != 3)
        return Mat();

    int rowBytes = w * ch;
    int stride = ImageRowStrideBytes(w, ch);
    int cvType = (ch == 1) ? CV_8UC1 : CV_8UC3;
    Mat out(h, w, cvType);
    const unsigned char* src = img->data;

    if (stride > rowBytes) {
        for (int y = 0; y < h; ++y)
            memcpy(out.ptr(y), src + (size_t)y * (size_t)stride, (size_t)rowBytes);
    } else {
        memcpy(out.data, src, (size_t)rowBytes * (size_t)h);
    }
    return out;
}

// 线段方向角 [0, π)
static double SegmentDirectionRad(const Vec4i& s) {
    double dx = (double)(s[2] - s[0]), dy = (double)(s[3] - s[1]);
    double a = std::atan2(dy, dx);
    if (a < 0) a += CV_PI;
    if (a >= CV_PI) a -= CV_PI;
    return a;
}

static double SegmentLengthPx(const Vec4i& s) {
    double dx = (double)(s[2] - s[0]), dy = (double)(s[3] - s[1]);
    return std::sqrt(dx * dx + dy * dy);
}

// 跑道线：在全部 Hough 线段中找“主导方向”，再按法向距离 ρ 分桶，取前 stripCount 个桶（典型两侧平行边=2）
static void ExtractRunwayLineSegments(
    const std::vector<Vec4i>& linesP,
    double angleTolDeg,
    int rhoBinPx,
    int stripCount,
    int maxOut,
    std::vector<Vec4i>& outSegs,
    std::string& jsonOut)
{
    outSegs.clear();
    jsonOut = "[]";
    if (linesP.empty() || stripCount <= 0 || rhoBinPx <= 0 || maxOut <= 0)
        return;

    const double binW = angleTolDeg * CV_PI / 180.0;
    if (binW <= 1e-9)
        return;
    const int numAngleBins = (int)std::ceil(CV_PI / binW);
    if (numAngleBins <= 0)
        return;

    std::vector<double> angleW((size_t)numAngleBins, 0.0);
    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        int b = (int)std::floor(a / binW);
        if (b < 0) b = 0;
        if (b >= numAngleBins) b = numAngleBins - 1;
        angleW[(size_t)b] += L;
    }
    int bestBin = 0;
    for (int i = 1; i < numAngleBins; ++i) {
        if (angleW[(size_t)i] > angleW[(size_t)bestBin])
            bestBin = i;
    }
    if (angleW[(size_t)bestBin] < 1.0)
        return;

    double phiStar = (bestBin + 0.5) * binW;            // 主导边线方向
    double thetaH = phiStar + CV_PI / 2.0;              // 法向角（Hough 的 θ）
    while (thetaH >= CV_PI) thetaH -= CV_PI;
    while (thetaH < 0) thetaH += CV_PI;
    double cth = std::cos(thetaH), sth = std::sin(thetaH);

    struct RhoAcc {
        double w = 0;
        std::vector<Vec4i> segs;
    };
    std::map<int, RhoAcc> rhoMap;

    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        int b = (int)std::floor(a / binW);
        if (b < 0) b = 0;
        if (b >= numAngleBins) b = numAngleBins - 1;
        if (b != bestBin)
            continue;

        double mx = 0.5 * (s[0] + s[2]), my = 0.5 * (s[1] + s[3]);
        double rho = mx * cth + my * sth;
        int rhoBin = (int)std::floor(rho / (double)rhoBinPx);

        RhoAcc& acc = rhoMap[rhoBin];
        acc.w += L;
        acc.segs.push_back(s);
    }

    std::vector<std::pair<double, int>> rhoOrder;
    rhoOrder.reserve(rhoMap.size());
    for (const auto& kv : rhoMap)
        rhoOrder.push_back({ kv.second.w, kv.first });
    std::sort(rhoOrder.begin(), rhoOrder.end(),
        [](const std::pair<double, int>& A, const std::pair<double, int>& B) { return A.first > B.first; });

    std::vector<Vec4i> picked;
    for (size_t i = 0; i < rhoOrder.size() && (int)i < stripCount; ++i) {
        int rb = rhoOrder[i].second;
        const auto& segs = rhoMap[rb].segs;
        picked.insert(picked.end(), segs.begin(), segs.end());
    }

    if ((int)picked.size() > maxOut)
        picked.resize((size_t)maxOut);
    outSegs = std::move(picked);

    std::ostringstream jb;
    jb << '[';
    for (size_t i = 0; i < outSegs.size(); ++i) {
        if (i) jb << ',';
        const Vec4i& s = outSegs[i];
        jb << '[' << s[0] << ',' << s[1] << ',' << s[2] << ',' << s[3] << ']';
    }
    jb << ']';
    jsonOut = jb.str();
}

static bool MergeSegmentsToVec4i(const std::vector<Vec4i>& segs, Vec4i& out) {
    std::vector<Point2f> pts;
    for (const auto& s : segs) {
        pts.push_back(Point2f((float)s[0], (float)s[1]));
        pts.push_back(Point2f((float)s[2], (float)s[3]));
    }
    if (pts.size() < 2)
        return false;
    Vec4f line;
    fitLine(pts, line, DIST_L2, 0, 0.01, 0.01);
    float vx = line[0], vy = line[1], x0 = line[2], y0 = line[3];
    double smin = 1e30, smax = -1e30;
    for (const auto& p : pts) {
        double t = (double)((p.x - x0) * vx + (p.y - y0) * vy);
        smin = std::min(smin, t);
        smax = std::max(smax, t);
    }
    Point2f p1(x0 + vx * (float)smin, y0 + vy * (float)smin);
    Point2f p2(x0 + vx * (float)smax, y0 + vy * (float)smax);
    out[0] = cvRound(p1.x);
    out[1] = cvRound(p1.y);
    out[2] = cvRound(p2.x);
    out[3] = cvRound(p2.y);
    return true;
}

// 体育场直道：主导平行方向 + ρ 分桶，取权重最高的两个桶，各合并为一条长线段
static bool ExtractStadiumTwoStraights(
    const std::vector<Vec4i>& linesP,
    double angleTolDeg,
    int rhoBinPx,
    Vec4i& straightA,
    Vec4i& straightB,
    double& phiStarOut)
{
    if (linesP.empty() || rhoBinPx <= 0)
        return false;

    const double binW = angleTolDeg * CV_PI / 180.0;
    if (binW <= 1e-9)
        return false;
    const int numAngleBins = (int)std::ceil(CV_PI / binW);
    if (numAngleBins <= 0)
        return false;

    std::vector<double> angleW((size_t)numAngleBins, 0.0);
    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        int b = (int)std::floor(a / binW);
        if (b < 0) b = 0;
        if (b >= numAngleBins) b = numAngleBins - 1;
        angleW[(size_t)b] += L;
    }
    int bestBin = 0;
    for (int i = 1; i < numAngleBins; ++i) {
        if (angleW[(size_t)i] > angleW[(size_t)bestBin])
            bestBin = i;
    }
    if (angleW[(size_t)bestBin] < 1.0)
        return false;

    phiStarOut = (bestBin + 0.5) * binW;
    double thetaH = phiStarOut + CV_PI / 2.0;
    while (thetaH >= CV_PI) thetaH -= CV_PI;
    while (thetaH < 0) thetaH += CV_PI;
    double cth = std::cos(thetaH), sth = std::sin(thetaH);

    struct RhoAcc {
        double w = 0;
        std::vector<Vec4i> segs;
    };
    std::map<int, RhoAcc> rhoMap;

    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        int b = (int)std::floor(a / binW);
        if (b < 0) b = 0;
        if (b >= numAngleBins) b = numAngleBins - 1;
        if (b != bestBin)
            continue;

        double mx = 0.5 * (s[0] + s[2]), my = 0.5 * (s[1] + s[3]);
        double rho = mx * cth + my * sth;
        int rhoBin = (int)std::floor(rho / (double)rhoBinPx);

        RhoAcc& acc = rhoMap[rhoBin];
        acc.w += L;
        acc.segs.push_back(s);
    }

    std::vector<std::pair<double, int>> rhoOrder;
    rhoOrder.reserve(rhoMap.size());
    for (const auto& kv : rhoMap)
        rhoOrder.push_back({ kv.second.w, kv.first });
    std::sort(rhoOrder.begin(), rhoOrder.end(),
        [](const std::pair<double, int>& A, const std::pair<double, int>& B) { return A.first > B.first; });

    if (rhoOrder.size() < 2)
        return false;
    const auto& segs0 = rhoMap[rhoOrder[0].second].segs;
    const auto& segs1 = rhoMap[rhoOrder[1].second].segs;
    if (!MergeSegmentsToVec4i(segs0, straightA))
        return false;
    if (!MergeSegmentsToVec4i(segs1, straightB))
        return false;
    return true;
}

// 已知直道方向 phiStar，对近似平行的线段做 ρ 分桶并取最强的两桶合并（用于圆轴已定后的直边）
static bool ExtractTwoStraightsGivenPhi(
    const std::vector<Vec4i>& linesP,
    double phiStar,
    double angleTolDeg,
    int rhoBinPx,
    Vec4i& straightA,
    Vec4i& straightB)
{
    if (linesP.empty() || rhoBinPx <= 0)
        return false;
    double tolRad = angleTolDeg * CV_PI / 180.0;
    if (tolRad <= 1e-9)
        return false;

    double thetaH = phiStar + CV_PI / 2.0;
    while (thetaH >= CV_PI) thetaH -= CV_PI;
    while (thetaH < 0) thetaH += CV_PI;
    double cth = std::cos(thetaH), sth = std::sin(thetaH);

    struct RhoAcc {
        double w = 0;
        std::vector<Vec4i> segs;
    };
    std::map<int, RhoAcc> rhoMap;

    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        double ad = std::fabs(a - phiStar);
        double angDiff = std::min(ad, CV_PI - ad);
        if (angDiff > tolRad)
            continue;

        double mx = 0.5 * (s[0] + s[2]), my = 0.5 * (s[1] + s[3]);
        double rho = mx * cth + my * sth;
        int rhoBin = (int)std::floor(rho / (double)rhoBinPx);

        RhoAcc& acc = rhoMap[rhoBin];
        acc.w += L;
        acc.segs.push_back(s);
    }

    std::vector<std::pair<double, int>> rhoOrder;
    rhoOrder.reserve(rhoMap.size());
    for (const auto& kv : rhoMap)
        rhoOrder.push_back({ kv.second.w, kv.first });
    std::sort(rhoOrder.begin(), rhoOrder.end(),
        [](const std::pair<double, int>& A, const std::pair<double, int>& B) { return A.first > B.first; });

    if (rhoOrder.size() < 2)
        return false;
    const auto& segs0 = rhoMap[rhoOrder[0].second].segs;
    const auto& segs1 = rhoMap[rhoOrder[1].second].segs;
    if (!MergeSegmentsToVec4i(segs0, straightA))
        return false;
    if (!MergeSegmentsToVec4i(segs1, straightB))
        return false;
    return true;
}

// 在半径相容的圆对中选「最大匹配」：score = 圆心距 × min(r1,r2)，平局取更大圆心距
static bool PickBestStadiumCirclePair(const std::vector<Vec3f>& cand,
    double minSepPx,
    double radiusTolFrac,
    int& outIdxLo,
    int& outIdxHi,
    double& outScore)
{
    outIdxLo = outIdxHi = -1;
    outScore = 0.0;
    double bestScore = -1.0;
    double bestDist = -1.0;
    const int n = (int)cand.size();
    for (int i = 0; i < n; ++i) {
        for (int j = i + 1; j < n; ++j) {
            float ri = cand[i][2], rj = cand[j][2];
            if (ri < 1.f || rj < 1.f)
                continue;
            double rBig = std::max((double)ri, (double)rj);
            if (std::fabs((double)ri - (double)rj) > radiusTolFrac * rBig)
                continue;
            double dx = (double)cand[j][0] - (double)cand[i][0];
            double dy = (double)cand[j][1] - (double)cand[i][1];
            double dist = std::sqrt(dx * dx + dy * dy);
            if (dist < minSepPx)
                continue;
            double score = dist * (double)std::min(ri, rj);
            if (score > bestScore + 1e-9 || (std::fabs(score - bestScore) <= 1e-9 && dist > bestDist)) {
                bestScore = score;
                bestDist = dist;
                outIdxLo = i;
                outIdxHi = j;
            }
        }
    }
    if (outIdxLo < 0)
        return false;
    outScore = bestScore;
    return true;
}

// projUx/projUy：沿跑道轴（通常取两端圆心方向）做「夹在两端之间」的投影；phiStar：线段平行方向角 [0,π)
static void FilterSegmentsBetweenCircleEnds(
    const std::vector<Vec4i>& linesP,
    Point2f CLo,
    Point2f CHi,
    double projUx,
    double projUy,
    float rLo,
    float rHi,
    double phiStar,
    double angleTolDeg,
    double bandMarginFrac,
    std::vector<Vec4i>& out)
{
    out.clear();
    double loP = (double)CLo.x * projUx + (double)CLo.y * projUy;
    double hiP = (double)CHi.x * projUx + (double)CHi.y * projUy;
    if (loP > hiP)
        std::swap(loP, hiP);
    double span = hiP - loP;
    double margin = bandMarginFrac * span;
    double capMargin = 0.35 * (double)std::max(rLo, rHi);
    if (margin < capMargin)
        margin = capMargin;
    if (margin > span * 0.45)
        margin = span * 0.45;
    double loBand = loP + margin;
    double hiBand = hiP - margin;
    if (loBand >= hiBand) {
        loBand = loP + 0.08 * span;
        hiBand = hiP - 0.08 * span;
    }

    double tolRad = angleTolDeg * CV_PI / 180.0;

    for (const auto& s : linesP) {
        double L = SegmentLengthPx(s);
        if (L < 1.0)
            continue;
        double a = SegmentDirectionRad(s);
        double ad = std::fabs(a - phiStar);
        double angDiff = std::min(ad, CV_PI - ad);
        if (angDiff > tolRad)
            continue;
        double mx = 0.5 * (s[0] + s[2]), my = 0.5 * (s[1] + s[3]);
        double pm = mx * projUx + my * projUy;
        if (pm >= loBand && pm <= hiBand)
            out.push_back(s);
    }
}

static void RefineStadiumCirclePoles(const std::vector<Vec3f>& allCircles,
    double radTolFrac,
    double gatePx,
    Point2f& CLo,
    Point2f& CHi,
    float& rLo,
    float& rHi)
{
    auto fuse = [&](Point2f refC, float refR, Point2f& outC, float& outR) {
        double sx = 0.0, sy = 0.0, sw = 0.0;
        std::vector<float> rs;
        rs.reserve(allCircles.size());
        for (const auto& c : allCircles) {
            float x = c[0], y = c[1], rr = c[2];
            if (rr < 1.f)
                continue;
            double dx = (double)x - (double)refC.x;
            double dy = (double)y - (double)refC.y;
            if (dx * dx + dy * dy > gatePx * gatePx)
                continue;
            double rBig = std::max((double)rr, (double)refR);
            if (std::fabs((double)rr - (double)refR) > radTolFrac * rBig)
                continue;
            double dist = std::sqrt(dx * dx + dy * dy);
            double w = 1.0 / (1.0 + dist);
            sx += (double)x * w;
            sy += (double)y * w;
            sw += w;
            rs.push_back(rr);
        }
        if (rs.empty()) {
            outC = refC;
            outR = refR;
            return;
        }
        outC.x = (float)(sx / sw);
        outC.y = (float)(sy / sw);
        size_t mid = rs.size() / 2;
        std::nth_element(rs.begin(), rs.begin() + (std::vector<float>::difference_type)mid, rs.end());
        outR = rs[mid];
    };

    Point2f inLo = CLo, inHi = CHi;
    float rlIn = rLo, rhIn = rHi;
    fuse(inLo, rlIn, CLo, rLo);
    fuse(inHi, rhIn, CHi, rHi);
}

static void RefineStadiumPhiAndStraightsFromHoughLines(
    const std::vector<Vec4i>& linesP,
    Point2f CLo,
    Point2f CHi,
    float rLo,
    float rHi,
    double projUx,
    double projUy,
    double angleTolDeg,
    int rhoBinPx,
    double& phiStarInOut,
    double& uxOut,
    double& uyOut,
    Vec4i& s1,
    Vec4i& s2)
{
    double basePhi = phiStarInOut;
    double bestPhi = basePhi;
    double bestScore = -1.0;
    std::vector<Vec4i> bestSegs;
    std::vector<Vec4i> tmp;
    const double stepDeg = 0.75;
    const int K = 8;
    const double relaxTolDeg = angleTolDeg + 1.25;

    for (int k = -K; k <= K; ++k) {
        double tryPhi = basePhi + k * stepDeg * CV_PI / 180.0;
        while (tryPhi < 0)
            tryPhi += CV_PI;
        while (tryPhi >= CV_PI)
            tryPhi -= CV_PI;

        FilterSegmentsBetweenCircleEnds(linesP, CLo, CHi, projUx, projUy, rLo, rHi, tryPhi, relaxTolDeg, 0.14, tmp);
        double sc = 0.0;
        for (const auto& s : tmp)
            sc += SegmentLengthPx(s);
        if (sc > bestScore + 1e-6) {
            bestScore = sc;
            bestPhi = tryPhi;
            bestSegs = tmp;
        }
    }

    bool ok = ExtractTwoStraightsGivenPhi(bestSegs, bestPhi, angleTolDeg, rhoBinPx, s1, s2);
    if (!ok)
        ok = ExtractTwoStraightsGivenPhi(bestSegs, bestPhi, relaxTolDeg, rhoBinPx, s1, s2);
    if (!ok)
        return;

    phiStarInOut = bestPhi;
    uxOut = std::cos(bestPhi);
    uyOut = std::sin(bestPhi);
}

static double MedianRadius(std::vector<float>& radii) {
    if (radii.empty())
        return 0.0;
    size_t mid = radii.size() / 2;
    std::nth_element(radii.begin(), radii.begin() + (std::vector<float>::difference_type)mid, radii.end());
    return (double)radii[mid];
}

static double AngleDegPt(Point2f C, Point2f P) {
    return std::atan2((double)(P.y - C.y), (double)(P.x - C.x)) * 180.0 / CV_PI;
}

// 直线 M + t*u（|u|=1）与圆 |X-C|=r 的交点参数 t；返回交点个数 0/1/2
static int LineCircleIntersectTs(Point2f M, double ux, double uy, Point2f C, float r, double outT[2]) {
    double wx = (double)M.x - (double)C.x;
    double wy = (double)M.y - (double)C.y;
    double b = wx * ux + wy * uy;
    double ww = wx * wx + wy * wy;
    double rr = (double)r * (double)r;
    double disc = b * b - (ww - rr);
    const double eps = 1e-8;
    if (disc < -eps)
        return 0;
    if (disc < 0)
        disc = 0;
    double sd = std::sqrt(disc);
    outT[0] = -b - sd;
    outT[1] = -b + sd;
    if (sd < eps)
        return 1;
    return 2;
}

static Point2f LinePointAtT(Point2f M, double ux, double uy, double t) {
    return Point2f((float)(M.x + t * ux), (float)(M.y + t * uy));
}

// 在直线与圆的交点中选：沿 track 轴 (tu,tv) 投影最小或最大的一点（体育场一端）
static bool PickLineCircleJunction(Point2f M, double ux, double uy, Point2f C, float r,
    double tu, double tv, bool minProjOnTrack, Point2f& out) {
    double ts[2];
    int n = LineCircleIntersectTs(M, ux, uy, C, r, ts);
    if (n == 0)
        return false;
    auto proj = [&](double t) {
        Point2f P = LinePointAtT(M, ux, uy, t);
        return (double)P.x * tu + (double)P.y * tv;
    };
    if (n == 1) {
        out = LinePointAtT(M, ux, uy, ts[0]);
        return true;
    }
    double p0 = proj(ts[0]), p1 = proj(ts[1]);
    if (minProjOnTrack)
        out = (p0 <= p1) ? LinePointAtT(M, ux, uy, ts[0]) : LinePointAtT(M, ux, uy, ts[1]);
    else
        out = (p0 >= p1) ? LinePointAtT(M, ux, uy, ts[0]) : LinePointAtT(M, ux, uy, ts[1]);
    return true;
}

// 圆上从 A 到 B：两段弧中选离 awayFrom 更远的一段；用折线绘制，端点与 A、B 重合以利闭合
static void DrawCircularArcPolylineBulgingAway(Mat& bgr, Point2f C, float r, Point2f A, Point2f B, Point2f awayFrom,
    const Scalar& col, int thick,
    double* outA0Deg, double* outA1Deg)
{
    double a0 = AngleDegPt(C, A);
    double a1 = AngleDegPt(C, B);
    double d = a1 - a0;
    while (d <= 0)
        d += 360.0;
    while (d > 360.0)
        d -= 360.0;
    double mid1 = a0 + d * 0.5;
    double radM1 = mid1 * CV_PI / 180.0;
    Point2f Mc((float)(C.x + r * std::cos(radM1)), (float)(C.y + r * std::sin(radM1)));
    double d2 = 360.0 - d;
    double mid2 = a1 + d2 * 0.5;
    while (mid2 >= 360.0)
        mid2 -= 360.0;
    double radM2 = mid2 * CV_PI / 180.0;
    Point2f M2((float)(C.x + r * std::cos(radM2)), (float)(C.y + r * std::sin(radM2)));
    double dist1 = cv::norm(Mc - awayFrom);
    double dist2 = cv::norm(M2 - awayFrom);
    double startAng, endAng;
    if (dist1 >= dist2) {
        startAng = a0;
        endAng = a1;
        if (endAng <= startAng)
            endAng += 360.0;
    } else {
        startAng = a1;
        endAng = a0 + 360.0;
    }
    if (outA0Deg) *outA0Deg = startAng;
    if (outA1Deg) *outA1Deg = endAng;

    double deltaDeg = endAng - startAng;
    double span = std::fabs(deltaDeg) * (double)r / 10.0;
    if (span < 12.0) span = 12.0;
    if (span > 160.0) span = 160.0;
    int N = (int)std::round(span);
    std::vector<Point> pts;
    pts.reserve((size_t)N + 1);
    pts.push_back(Point(cvRound(A.x), cvRound(A.y)));
    for (int i = 1; i < N; ++i) {
        double angDeg = startAng + deltaDeg * (double)i / (double)N;
        double rad = angDeg * CV_PI / 180.0;
        pts.push_back(Point(cvRound(C.x + r * std::cos(rad)), cvRound(C.y + r * std::sin(rad))));
    }
    pts.push_back(Point(cvRound(B.x), cvRound(B.y)));
    std::vector<std::vector<Point>> pl;
    pl.push_back(std::move(pts));
    polylines(bgr, pl, false, col, thick, LINE_AA);
}

static bool HoughPrepareWorkGrayAndBgr(const Mat& src, int blurKsize, Mat& workGray, Mat& bgrBase) {
    if (src.empty())
        return false;
    Mat gray;
    if (src.channels() == 3)
        cvtColor(src, gray, COLOR_BGR2GRAY);
    else if (src.channels() == 1)
        gray = src.clone();
    else
        return false;
    workGray = gray;
    if (blurKsize >= 3 && (blurKsize % 2) == 1)
        GaussianBlur(gray, workGray, Size(blurKsize, blurKsize), 0);
    if (src.channels() == 3)
        bgrBase = src.clone();
    else
        cvtColor(workGray, bgrBase, COLOR_GRAY2BGR);
    return true;
}

bool ParseHoughLinesJsonUtf8(const char* json, std::vector<Vec4i>& out) {
    out.clear();
    if (!json || !json[0])
        return false;
    std::string s(json);
    size_t pos = 0;
    while (true) {
        size_t lb = s.find('[', pos);
        if (lb == std::string::npos)
            break;
        size_t rb = s.find(']', lb + 1);
        if (rb == std::string::npos)
            break;
        std::string chunk = s.substr(lb + 1, rb - lb - 1);
        int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        if (std::sscanf(chunk.c_str(), "%d,%d,%d,%d", &x1, &y1, &x2, &y2) == 4)
            out.push_back(Vec4i(x1, y1, x2, y2));
        pos = rb + 1;
    }
    return !out.empty();
}

bool ParseHoughCirclesJsonUtf8(const char* json, std::vector<Vec3f>& out) {
    out.clear();
    if (!json || !json[0])
        return false;
    std::string s(json);
    size_t pos = 0;
    while (true) {
        size_t lb = s.find('[', pos);
        if (lb == std::string::npos)
            break;
        size_t rb = s.find(']', lb + 1);
        if (rb == std::string::npos)
            break;
        std::string chunk = s.substr(lb + 1, rb - lb - 1);
        double xf = 0, yf = 0, rf = 0;
        if (std::sscanf(chunk.c_str(), "%lf,%lf,%lf", &xf, &yf, &rf) == 3)
            out.push_back(Vec3f((float)xf, (float)yf, (float)rf));
        pos = rb + 1;
    }
    return !out.empty();
}

void HoughCirclesOnMat(const Mat& src, Mat& dstBgr,
    std::vector<Point2D>& circleCenters,
    int blurKsize,
    double hcDp, double hcMinDist, double hcParam1, double hcParam2, int hcMinR, int hcMaxR,
    std::string* circlesJsonOut)
{
    circleCenters.clear();
    if (circlesJsonOut)
        circlesJsonOut->clear();
    Mat work, bgr;
    if (!HoughPrepareWorkGrayAndBgr(src, blurKsize, work, bgr))
        return;

    std::vector<Vec3f> circles;
    HoughCircles(work, circles, HOUGH_GRADIENT, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR);

    std::ostringstream jb;
    if (circlesJsonOut)
        jb << '[';

    for (size_t i = 0; i < circles.size(); ++i) {
        float x = circles[i][0], y = circles[i][1], r = circles[i][2];
        circleCenters.push_back(Point2D{ (double)x, (double)y });
        Point center(cvRound(x), cvRound(y));
        int radius = cvRound(r);
        circle(bgr, center, radius, Scalar(0, 255, 0), 2, LINE_AA);
        circle(bgr, center, 3, Scalar(0, 255, 255), -1, LINE_AA);
        if (circlesJsonOut) {
            if (i)
                jb << ',';
            jb << '[' << x << ',' << y << ',' << r << ']';
        }
    }
    if (circlesJsonOut) {
        jb << ']';
        *circlesJsonOut = jb.str();
    }
    dstBgr = std::move(bgr);
}

/** Bresenham 沿线采样：落在图像内且 gray>edgeMin 的像素占比，用于霍夫线段排序。 */
static double HoughLineEdgeCoverageRatio(const Mat& edgeGray, int x1, int y1, int x2, int y2, int edgeMin) {
    if (edgeGray.empty() || edgeGray.type() != CV_8UC1)
        return 0.0;
    LineIterator it(edgeGray, Point(x1, y1), Point(x2, y2), 8);
    int total = 0, hit = 0;
    for (int i = 0; i < it.count; ++i, ++it) {
        Point p = it.pos();
        if ((unsigned)p.x >= (unsigned)edgeGray.cols || (unsigned)p.y >= (unsigned)edgeGray.rows)
            continue;
        total++;
        if ((int)edgeGray.at<uint8_t>(p.y, p.x) > edgeMin)
            hit++;
    }
    if (total <= 0)
        return 0.0;
    return (double)hit / (double)total;
}

static double HoughLineSegLenPx(const Vec4i& s) {
    double dx = (double)(s[2] - s[0]), dy = (double)(s[3] - s[1]);
    return std::hypot(dx, dy);
}

void HoughLinesOnMat(const Mat& src, Mat& dstBgr,
    std::string& linesJson,
    int* lineSegmentCountOut,
    double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap,
    int maxLinesOut,
    int coverageMatchHalfWidthPx)
{
    linesJson.clear();
    linesJson = "[]";
    if (lineSegmentCountOut) *lineSegmentCountOut = 0;

    if (src.empty())
        return;
    Mat gray;
    if (src.channels() == 3)
        cvtColor(src, gray, COLOR_BGR2GRAY);
    else if (src.channels() == 1)
        gray = src.clone();
    else
        return;
    Mat edges = gray;
    Mat bgr;
    if (src.channels() == 3)
        bgr = src.clone();
    else
        cvtColor(gray, bgr, COLOR_GRAY2BGR);

    const int edgeHitMin = 0;
    Mat edgesForHough;
    if (coverageMatchHalfWidthPx > 0) {
        Mat bin;
        threshold(edges, bin, edgeHitMin, 255, THRESH_BINARY);
        int k = 2 * coverageMatchHalfWidthPx + 1;
        k = std::max(3, k | 1);
        Mat ker = getStructuringElement(MORPH_ELLIPSE, Size(k, k));
        dilate(bin, edgesForHough, ker);
    } else {
        edgesForHough = edges;
    }

    std::vector<Vec4i> linesP;
    double thetaRad = hlThetaDeg * CV_PI / 180.0;
    if (thetaRad <= 1e-9)
        thetaRad = CV_PI / 180.0;
    HoughLinesP(edgesForHough, linesP, hlRho, thetaRad, hlThreshold, hlMinLen, hlMaxGap);

    const int covPixMin = coverageMatchHalfWidthPx > 0 ? 0 : edgeHitMin;

    struct ScoredSeg {
        Vec4i seg;
        double len;
        double cov;
    };
    std::vector<ScoredSeg> ranked;
    ranked.reserve(linesP.size());
    for (const auto& s : linesP) {
        double len = HoughLineSegLenPx(s);
        double cov = HoughLineEdgeCoverageRatio(edgesForHough, s[0], s[1], s[2], s[3], covPixMin);
        ranked.push_back({ s, len, cov });
    }
    std::sort(ranked.begin(), ranked.end(), [](const ScoredSeg& a, const ScoredSeg& b) {
        if (a.len != b.len)
            return a.len > b.len;
        return a.cov > b.cov;
    });

    int cap = maxLinesOut > 0 ? maxLinesOut : 500;
    std::ostringstream jb;
    jb << '[';
    int nl = 0;
    for (size_t i = 0; i < ranked.size() && nl < cap; ++i) {
        int x1 = ranked[i].seg[0], y1 = ranked[i].seg[1], x2 = ranked[i].seg[2], y2 = ranked[i].seg[3];
        line(bgr, Point(x1, y1), Point(x2, y2), Scalar(0, 0, 255), 2, LINE_AA);
        if (nl) jb << ',';
        jb << '[' << x1 << ',' << y1 << ',' << x2 << ',' << y2 << ']';
        nl++;
    }
    jb << ']';
    linesJson = jb.str();
    if (lineSegmentCountOut) *lineSegmentCountOut = nl;
    dstBgr = std::move(bgr);
}

void HoughRunwayOnMat(const Mat& src, Mat& dstBgr,
    std::string& runwayLinesJson,
    int* runwaySegCountOut,
    int blurKsize,
    double cannyTh1, double cannyTh2,
    double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap,
    int maxLinesOut,
    double runwayAngleTolDeg,
    int runwayRhoBinPx,
    int runwayStripCount,
    int maxRunwayLinesOut,
    int runwayShapeMode,
    double hcDp, double hcMinDist, double hcParam1, double hcParam2, int hcMinR, int hcMaxR,
    const std::vector<Vec4i>* optLinesP,
    const std::vector<Vec3f>* optCircles)
{
    runwayLinesJson.clear();
    runwayLinesJson = "[]";
    if (runwaySegCountOut) *runwaySegCountOut = 0;

    Mat work, bgr;
    if (!HoughPrepareWorkGrayAndBgr(src, blurKsize, work, bgr))
        return;

    std::vector<Vec4i> linesP;
    if (optLinesP && !optLinesP->empty())
        linesP = *optLinesP;
    else {
        Mat edges;
        Canny(work, edges, cannyTh1, cannyTh2);
        double thetaRad = hlThetaDeg * CV_PI / 180.0;
        if (thetaRad <= 1e-9)
            thetaRad = CV_PI / 180.0;
        HoughLinesP(edges, linesP, hlRho, thetaRad, hlThreshold, hlMinLen, hlMaxGap);
    }

    (void)maxLinesOut;

    if (runwayShapeMode != 0) {
        std::vector<Vec3f> circles;
        if (optCircles && !optCircles->empty())
            circles = *optCircles;
        else
            HoughCircles(work, circles, HOUGH_GRADIENT, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR);

        const double radiusTolFrac = 0.35;
        int w = bgr.cols, h = bgr.rows;
        double minEndSep = std::max(30.0, 0.05 * (double)std::min(w, h));

        std::vector<Vec3f> cand;
        cand.reserve(circles.size());
        if (!circles.empty()) {
            std::vector<float> radii;
            radii.reserve(circles.size());
            for (const auto& c : circles)
                radii.push_back(c[2]);
            double rMed = MedianRadius(radii);
            if (rMed > 1e-6) {
                for (const auto& c : circles) {
                    double r = c[2];
                    if (r > 1e-6 && std::fabs(r - rMed) <= radiusTolFrac * rMed)
                        cand.push_back(c);
                }
            }
            if ((int)cand.size() < 2) {
                cand = circles;
            }
        }

        int pairILo = -1, pairIHi = -1;
        double pairScore = 0.0;
        bool havePair = PickBestStadiumCirclePair(cand, minEndSep, radiusTolFrac, pairILo, pairIHi, pairScore);

        Vec4i s1, s2;
        double phiStar = 0.0;
        double ux = 1.0, uy = 0.0;
        Point2f CLo(0.f, 0.f), CHi(0.f, 0.f);
        float rLo = 0.f, rHi = 0.f;

        if (havePair) {
            const Vec3f& ca = cand[pairILo];
            const Vec3f& cb = cand[pairIHi];
            double dx = (double)cb[0] - (double)ca[0];
            double dy = (double)cb[1] - (double)ca[1];
            double len = std::sqrt(dx * dx + dy * dy);
            if (len >= 1e-6) {
                ux = dx / len;
                uy = dy / len;
                double pa = (double)ca[0] * ux + (double)ca[1] * uy;
                double pb = (double)cb[0] * ux + (double)cb[1] * uy;
                if (pa <= pb) {
                    CLo = Point2f(ca[0], ca[1]);
                    CHi = Point2f(cb[0], cb[1]);
                    rLo = ca[2];
                    rHi = cb[2];
                } else {
                    CLo = Point2f(cb[0], cb[1]);
                    CHi = Point2f(ca[0], ca[1]);
                    rLo = cb[2];
                    rHi = ca[2];
                }
                phiStar = std::atan2(uy, ux);
                if (phiStar < 0)
                    phiStar += CV_PI;
                if (phiStar >= CV_PI)
                    phiStar -= CV_PI;

                std::vector<Vec4i> bandSegs;
                bool got = false;
                double bandUx = ux, bandUy = uy;
                const double margins[] = { 0.22, 0.12, 0.04 };
                for (double mf : margins) {
                    FilterSegmentsBetweenCircleEnds(linesP, CLo, CHi, bandUx, bandUy, rLo, rHi, phiStar, runwayAngleTolDeg, mf, bandSegs);
                    if (ExtractTwoStraightsGivenPhi(bandSegs, phiStar, runwayAngleTolDeg, runwayRhoBinPx, s1, s2)) {
                        got = true;
                        break;
                    }
                }
                if (!got) {
                    std::vector<Vec4i> paraOnly;
                    paraOnly.reserve(linesP.size());
                    double tolRad = runwayAngleTolDeg * CV_PI / 180.0;
                    for (const auto& s : linesP) {
                        if (SegmentLengthPx(s) < 1.0)
                            continue;
                        double a = SegmentDirectionRad(s);
                        double ad = std::fabs(a - phiStar);
                        double angDiff = std::min(ad, CV_PI - ad);
                        if (angDiff <= tolRad)
                            paraOnly.push_back(s);
                    }
                    got = ExtractTwoStraightsGivenPhi(paraOnly, phiStar, runwayAngleTolDeg, runwayRhoBinPx, s1, s2);
                }
                if (!got)
                    havePair = false;
                else if (!circles.empty()) {
                    double gatePx = std::max(15.0, 0.06 * (double)std::min(w, h));
                    RefineStadiumCirclePoles(circles, radiusTolFrac, gatePx, CLo, CHi, rLo, rHi);
                    double flen = std::hypot((double)CHi.x - (double)CLo.x, (double)CHi.y - (double)CLo.y);
                    if (flen >= 1e-6) {
                        bandUx = ((double)CHi.x - (double)CLo.x) / flen;
                        bandUy = ((double)CHi.y - (double)CLo.y) / flen;
                        RefineStadiumPhiAndStraightsFromHoughLines(linesP, CLo, CHi, rLo, rHi, bandUx, bandUy,
                            runwayAngleTolDeg, runwayRhoBinPx, phiStar, ux, uy, s1, s2);
                    }
                }
            } else {
                havePair = false;
            }
        }

        if (!havePair) {
            double phTmp = 0.0;
            if (!ExtractStadiumTwoStraights(linesP, runwayAngleTolDeg, runwayRhoBinPx, s1, s2, phTmp)) {
                runwayLinesJson = "{\"shape\":\"stadium\",\"closed\":false,\"straights\":[],\"arcs\":[]}";
                dstBgr = std::move(bgr);
                return;
            }
            phiStar = phTmp;
            ux = std::cos(phiStar);
            uy = std::sin(phiStar);
        }

        float mx1 = 0.5f * (float)(s1[0] + s1[2]), my1 = 0.5f * (float)(s1[1] + s1[3]);
        float mx2 = 0.5f * (float)(s2[0] + s2[2]), my2 = 0.5f * (float)(s2[1] + s2[3]);
        Point2f trackMid(0.5f * (mx1 + mx2), 0.5f * (my1 + my2));

        std::ostringstream jb;
        jb.setf(std::ios::fixed);
        jb << "{\"shape\":\"stadium\"";

        const Scalar magenta(255, 0, 255);
        const int thick = 3;

        bool closedOk = havePair && rLo >= 1.f && rHi >= 1.f;
        if (closedOk) {
            Point2f M1(mx1, my1), M2(mx2, my2);
            Point2f H11, H21, H12, H22;
            bool geomOk = PickLineCircleJunction(M1, ux, uy, CLo, rLo, ux, uy, true, H11)
                && PickLineCircleJunction(M2, ux, uy, CLo, rLo, ux, uy, true, H12)
                && PickLineCircleJunction(M1, ux, uy, CHi, rHi, ux, uy, false, H21)
                && PickLineCircleJunction(M2, ux, uy, CHi, rHi, ux, uy, false, H22);
            if (!geomOk)
                closedOk = false;
            else {
                line(bgr, Point(cvRound(H11.x), cvRound(H11.y)), Point(cvRound(H21.x), cvRound(H21.y)), magenta, thick, LINE_AA);
                line(bgr, Point(cvRound(H22.x), cvRound(H22.y)), Point(cvRound(H12.x), cvRound(H12.y)), magenta, thick, LINE_AA);

                double aHi0 = 0, aHi1 = 0, aLo0 = 0, aLo1 = 0;
                DrawCircularArcPolylineBulgingAway(bgr, CHi, rHi, H21, H22, trackMid, magenta, thick, &aHi0, &aHi1);
                DrawCircularArcPolylineBulgingAway(bgr, CLo, rLo, H12, H11, trackMid, magenta, thick, &aLo0, &aLo1);

                jb << ",\"closed\":true";
                jb << ",\"pairScore\":" << pairScore;
                jb << ",\"straights\":[[" << cvRound(H11.x) << ',' << cvRound(H11.y) << ',' << cvRound(H21.x) << ',' << cvRound(H21.y)
                   << "],[" << cvRound(H22.x) << ',' << cvRound(H22.y) << ',' << cvRound(H12.x) << ',' << cvRound(H12.y) << "]]";
                jb << ",\"arcs\":[";
                jb << "{\"cx\":" << CHi.x << ",\"cy\":" << CHi.y << ",\"r\":" << rHi
                   << ",\"a0Deg\":" << aHi0 << ",\"a1Deg\":" << aHi1 << "}";
                jb << ',';
                jb << "{\"cx\":" << CLo.x << ",\"cy\":" << CLo.y << ",\"r\":" << rLo
                   << ",\"a0Deg\":" << aLo0 << ",\"a1Deg\":" << aLo1 << "}";
                jb << "]";
                jb << ",\"rawStraights\":[[" << s1[0] << ',' << s1[1] << ',' << s1[2] << ',' << s1[3]
                   << "],[" << s2[0] << ',' << s2[1] << ',' << s2[2] << ',' << s2[3] << "]]";
                jb << '}';
                runwayLinesJson = jb.str();
                if (runwaySegCountOut)
                    *runwaySegCountOut = 4;
                dstBgr = std::move(bgr);
                return;
            }
        }

        jb << ",\"closed\":false";
        jb << ",\"straights\":[[" << s1[0] << ',' << s1[1] << ',' << s1[2] << ',' << s1[3]
           << "],[" << s2[0] << ',' << s2[1] << ',' << s2[2] << ',' << s2[3] << "]],\"arcs\":[]}";
        runwayLinesJson = jb.str();
        line(bgr, Point(s1[0], s1[1]), Point(s1[2], s1[3]), magenta, thick, LINE_AA);
        line(bgr, Point(s2[0], s2[1]), Point(s2[2], s2[3]), magenta, thick, LINE_AA);
        if (runwaySegCountOut)
            *runwaySegCountOut = 2;
        dstBgr = std::move(bgr);
        return;
    }

    std::vector<Vec4i> runwaySegs;
    ExtractRunwayLineSegments(linesP, runwayAngleTolDeg, runwayRhoBinPx, runwayStripCount,
        maxRunwayLinesOut > 0 ? maxRunwayLinesOut : 200, runwaySegs, runwayLinesJson);
    if (runwaySegCountOut) *runwaySegCountOut = (int)runwaySegs.size();
    for (const auto& s : runwaySegs) {
        line(bgr, Point(s[0], s[1]), Point(s[2], s[3]), Scalar(255, 0, 255), 3, LINE_AA);
    }
    dstBgr = std::move(bgr);
}

int HoughCirclesDetect(Image* src, Image* dstOverlay,
    Point2D* circlePts, int* circleCount, int maxCircles,
    char* circlesJsonOut, int circlesJsonBufSize,
    int blurKsize,
    double hcDp, double hcMinDist, double hcParam1, double hcParam2, int hcMinR, int hcMaxR)
{
    if (!src || !src->data || !dstOverlay || !circlePts || maxCircles <= 0)
        return -1;
    if (circleCount) *circleCount = 0;
    if (circlesJsonOut && circlesJsonBufSize > 0)
        circlesJsonOut[0] = '\0';

    Mat m = ImageToMatClone(src);
    if (m.empty())
        return -1;

    std::vector<Point2D> centers;
    Mat out;
    std::string cj;
    std::string* pj = (circlesJsonOut && circlesJsonBufSize > 0) ? &cj : nullptr;
    HoughCirclesOnMat(m, out, centers, blurKsize, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR, pj);

    if (circlesJsonOut && circlesJsonBufSize > 0 && !cj.empty()) {
        size_t cap = (size_t)circlesJsonBufSize - 1;
        size_t n = std::min(cj.size(), cap);
        if (n > 0)
            memcpy(circlesJsonOut, cj.data(), n);
        circlesJsonOut[n] = '\0';
    }

    int nc = (int)std::min(centers.size(), (size_t)maxCircles);
    if (circleCount) *circleCount = nc;
    for (int i = 0; i < nc; ++i)
        circlePts[i] = centers[i];

    MatToImageBGR(out, dstOverlay);
    return 0;
}

int HoughLinesDetect(Image* src, Image* dstOverlay,
    char* linesJsonOut, int linesJsonBufSize,
    int* lineSegmentCountOut,
    double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap,
    int maxLinesOut,
    int coverageMatchHalfWidthPx)
{
    if (!src || !src->data || !dstOverlay)
        return -1;
    if (linesJsonOut && linesJsonBufSize > 0)
        linesJsonOut[0] = '\0';
    if (lineSegmentCountOut) *lineSegmentCountOut = 0;

    Mat m = ImageToMatClone(src);
    if (m.empty())
        return -1;

    std::string lj;
    Mat out;
    int nseg = 0;
    HoughLinesOnMat(m, out, lj, &nseg, hlRho, hlThetaDeg, hlThreshold, hlMinLen, hlMaxGap, maxLinesOut,
        coverageMatchHalfWidthPx);

    if (lineSegmentCountOut) *lineSegmentCountOut = nseg;
    if (linesJsonOut && linesJsonBufSize > 0) {
        size_t cap = (size_t)linesJsonBufSize - 1;
        size_t n = std::min(lj.size(), cap);
        if (n > 0)
            memcpy(linesJsonOut, lj.data(), n);
        linesJsonOut[n] = '\0';
    }

    MatToImageBGR(out, dstOverlay);
    return 0;
}

int HoughRunwayDetect(Image* src, Image* dstOverlay,
    char* runwayJsonOut, int runwayJsonBufSize,
    int* runwayLineCountOut,
    int blurKsize,
    double cannyTh1, double cannyTh2,
    double hlRho, double hlThetaDeg, int hlThreshold, double hlMinLen, double hlMaxGap,
    int maxLinesOut,
    double runwayAngleTolDeg,
    int runwayRhoBinPx,
    int runwayStripCount,
    int maxRunwayLinesOut,
    int runwayShapeMode,
    double hcDp, double hcMinDist, double hcParam1, double hcParam2, int hcMinR, int hcMaxR,
    const char* linesJsonUtf8,
    const char* circlesJsonUtf8)
{
    if (!src || !src->data || !dstOverlay)
        return -1;
    if (runwayLineCountOut) *runwayLineCountOut = 0;
    if (runwayJsonOut && runwayJsonBufSize > 0)
        runwayJsonOut[0] = '\0';

    Mat m = ImageToMatClone(src);
    if (m.empty())
        return -1;

    std::vector<Vec4i> optLinesParsed;
    std::vector<Vec3f> optCirclesParsed;
    const std::vector<Vec4i>* plines = nullptr;
    const std::vector<Vec3f>* pcirc = nullptr;
    if (linesJsonUtf8 && linesJsonUtf8[0] && ParseHoughLinesJsonUtf8(linesJsonUtf8, optLinesParsed))
        plines = &optLinesParsed;
    if (circlesJsonUtf8 && circlesJsonUtf8[0] && ParseHoughCirclesJsonUtf8(circlesJsonUtf8, optCirclesParsed))
        pcirc = &optCirclesParsed;

    std::string rj;
    Mat out;
    int rwc = 0;
    HoughRunwayOnMat(m, out, rj, &rwc, blurKsize, cannyTh1, cannyTh2,
        hlRho, hlThetaDeg, hlThreshold, hlMinLen, hlMaxGap, maxLinesOut,
        runwayAngleTolDeg, runwayRhoBinPx, runwayStripCount, maxRunwayLinesOut,
        runwayShapeMode, hcDp, hcMinDist, hcParam1, hcParam2, hcMinR, hcMaxR,
        plines, pcirc);

    if (runwayLineCountOut) *runwayLineCountOut = rwc;
    if (runwayJsonOut && runwayJsonBufSize > 0) {
        size_t cap = (size_t)runwayJsonBufSize - 1;
        size_t n = std::min(rj.size(), cap);
        if (n > 0)
            memcpy(runwayJsonOut, rj.data(), n);
        runwayJsonOut[n] = '\0';
    }

    MatToImageBGR(out, dstOverlay);
    return 0;
}

void DrawDetectedCircles(Image* img, Point2D* pts, int count, int gray) {
    if (!img || !img->data) return;
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;
    for (int i = 0; i < count; i++) {
        int cx = (int)pts[i].x, cy = (int)pts[i].y;
        for (int x = cx - 15; x <= cx + 15; x++) {
            for (int t = -3; t <= 3; t++) {
                int y = cy + t;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int off = y * rowSize + x * img->channels;
                    if (img->channels == 1) {
                        img->data[off] = (unsigned char)gray;
                    } else {
                        img->data[off] = (unsigned char)gray;
                        img->data[off + 1] = (unsigned char)gray;
                        img->data[off + 2] = (unsigned char)gray;
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
                        img->data[off] = (unsigned char)gray;
                        img->data[off + 1] = (unsigned char)gray;
                        img->data[off + 2] = (unsigned char)gray;
                    }
                }
            }
        }
    }
}

// ========== 标定 ==========

int CalibrateNinePoint(Point2D* imagePts, Point2D* worldPts, int n, AffineTransform* trans) {
    LOG_INFO("=== Starting 9-Point Calibration (cv::solve) ===");

    if (n < 3) {
        LOG_ERROR("Insufficient points for calibration (need at least 3)");
        return 0;
    }

    for (int i = 0; i < n; i++) {
        LOG_DEBUG("  %d: (%.2f, %.2f) -> (%.2f, %.2f)",
            i, imagePts[i].x, imagePts[i].y, worldPts[i].x, worldPts[i].y);
    }

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

// ========== 轨迹检测 ==========

// 全局变量（DetectTrajectory 需要）
static AffineTransform g_transform = {0};
static Point2D g_trajWorld[CALIB_MAX_TRAJ_POINTS];

// 设置全局变换矩阵（GUI 层在标定后调用）
// 暂时通过 DetectTrajectory 内部使用，未来可通过参数传递
void CalibSetTransform(AffineTransform trans) {
    g_transform = trans;
}

void DetectTrajectory(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== Starting Self Trajectory Detection ===");

    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }

    int srcRowSize = ((img->width * img->channels + 3) / 4) * 4;
    *count = 0;

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

    unsigned char* edgeMap = (unsigned char*)malloc(w * h);
    for (int i = 0; i < w * h; i++) {
        edgeMap[i] = (gradMag[i] > gradThreshold) ? 255 : 0;
    }

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

    bool* visited = (bool*)calloc(w * h, sizeof(bool));
    int maxCount = CALIB_MAX_TRAJ_POINTS;

    int cx = startX, cy = startY;
    while (cx >= 0 && cy >= 0 && cx < w && cy < h && *count < maxCount) {
        visited[cy * w + cx] = true;
        trajPixels[*count].x = cx;
        trajPixels[*count].y = cy;
        (*count)++;

        float dx = gradDirX[cy * w + cx];
        float dy = gradDirY[cy * w + cx];

        int stepX = (fabs(dx) > 0.1f) ? (dx > 0 ? 1 : -1) : 0;
        int stepY = (fabs(dy) > 0.1f) ? (dy > 0 ? 1 : -1) : 0;

        if (stepX == 0 && stepY == 0) {
            int nextX = -1, nextY = -1;
            int bestMag = gradThreshold;
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
                break;
            }
        } else {
            int newX = cx + stepX;
            int newY = cy + stepY;

            if (newX >= 0 && newX < w && newY >= 0 && newY < h &&
                !visited[newY * w + newX] && edgeMap[newY * w + newX]) {
                cx = newX;
                cy = newY;
            } else {
                if (newX >= 0 && newX < w && !visited[cy * w + newX] && edgeMap[cy * w + newX]) {
                    cx = newX;
                } else if (newY >= 0 && newY < h && !visited[newY * w + cx] && edgeMap[newY * w + cx]) {
                    cy = newY;
                } else {
                    break;
                }
            }
        }
    }

    free(visited);
    free(gradMag);
    free(gradDirX);
    free(gradDirY);
    free(edgeMap);

    for (int i = 0; i < *count; i++) {
        g_trajWorld[i] = ImageToWorld(trajPixels[i], g_transform);
    }

    LOG_INFO("Self trajectory detection complete: %d points", *count);
}


// ========== 形状拟合辅助函数 ==========

// 最小二乘拟合圆: (x-a)^2 + (y-b)^2 = r^2
// 返回 (cx, cy, r) 或 (-1,-1,-1) 如果失败
static cv::Vec3d fitCircleLeastSquares(const std::vector<cv::Point2d>& pts) {
    int n = (int)pts.size();
    if (n < 3) return cv::Vec3d(-1, -1, -1);

    double sumX = 0, sumY = 0;
    for (int i = 0; i < n; i++) { sumX += pts[i].x; sumY += pts[i].y; }
    double meanX = sumX / n, meanY = sumY / n;

    cv::Mat A(n, 3, CV_64F);
    cv::Mat B(n, 1, CV_64F);
    for (int i = 0; i < n; i++) {
        A.at<double>(i, 0) = pts[i].x - meanX;
        A.at<double>(i, 1) = pts[i].y - meanY;
        A.at<double>(i, 2) = 1.0;
        double x2y2 = pts[i].x * pts[i].x + pts[i].y * pts[i].y;
        B.at<double>(i, 0) = x2y2 - (meanX * meanX + meanY * meanY);
    }

    cv::Mat X;
    bool ok = cv::solve(A, B, X, cv::DECOMP_SVD);
    if (!ok || X.empty()) return cv::Vec3d(-1, -1, -1);

    double a = X.at<double>(0);
    double b = X.at<double>(1);
    double c = X.at<double>(2);

    double cx = -a / 2.0 + meanX;
    double cy = -b / 2.0 + meanY;
    double rSq = cx * cx + cy * cy - c;
    if (rSq < 0) rSq = 0;
    double r = sqrt(rSq);

    return cv::Vec3d(cx, cy, r);
}

// 对一组采样点进行分段形状拟合
// angleThreshold: 转角阈值(弧度), 默认 0.05 (~3度)
// minSegPoints: 段内最少点数, 默认 5
static std::vector<cv::Point2d> fitSegmentsShape(
    const std::vector<cv::Point2d>& pts)
{
    // PCA + 转角检测 + 直线/圆弧拟合：
    // 1. PCA 求主方向和法线方向
    // 2. 沿法线投影 d，用中位数分成正半和负半
    // 3. 每半按主方向 t 排序，计算转角
    // 4. 转角小的段→直线拟合，转角大的段→最小二乘圆拟合
    // 5. 合并短段，保证连续闭合
    // 适用于任意方向的暗条

    int n = (int)pts.size();
    if (n < 6) return pts;

    // PCA
    double cx = 0, cy = 0;
    for (int i = 0; i < n; i++) { cx += pts[i].x; cy += pts[i].y; }
    cx /= n; cy /= n;

    double Cxx = 0, Cxy = 0, Cyy = 0;
    for (int i = 0; i < n; i++) {
        double dx = pts[i].x - cx, dy = pts[i].y - cy;
        Cxx += dx * dx; Cxy += dx * dy; Cyy += dy * dy;
    }
    Cxx /= n; Cxy /= n; Cyy /= n;

    double trace = Cxx + Cyy;
    double det = Cxx * Cyy - Cxy * Cxy;
    double disc = sqrt(std::max(0.0, trace * trace / 4.0 - det));
    double lambda1 = trace / 2.0 + disc;

    double vx, vy;
    if (fabs(Cxy) > 1e-10) { vx = lambda1 - Cyy; vy = Cxy; }
    else { vx = (Cxx >= Cyy) ? 1 : 0; vy = (Cxx >= Cyy) ? 0 : 1; }
    double vlen = sqrt(vx * vx + vy * vy);
    if (vlen < 1e-10) return pts;
    vx /= vlen; vy /= vlen;
    double nx = -vy, ny = vx;

    // 法线投影 d
    std::vector<double> dVals(n);
    for (int i = 0; i < n; i++) {
        double dx = pts[i].x - cx, dy = pts[i].y - cy;
        dVals[i] = dx * nx + dy * ny;
    }
    std::vector<double> dSorted = dVals;
    std::nth_element(dSorted.begin(), dSorted.begin() + n / 2, dSorted.end());
    double medianD = dSorted[n / 2];

    // 分正半和负半
    std::vector<int> posIdx, negIdx;
    for (int i = 0; i < n; i++) {
        if (dVals[i] >= medianD) posIdx.push_back(i);
        else negIdx.push_back(i);
    }

    // 对每半做直线+圆弧拟合
    std::vector<cv::Point2d> result(n);
    for (int i = 0; i < n; i++) result[i] = pts[i];

    auto fitHalf = [&](const std::vector<int>& indices) {
        int nh = (int)indices.size();
        if (nh < 6) return;

        // 按 t 排序
        std::vector<std::pair<double, int>> tOrder(nh);
        for (int i = 0; i < nh; i++) {
            int idx = indices[i];
            double dx = pts[idx].x - cx, dy = pts[idx].y - cy;
            tOrder[i] = std::make_pair(dx * vx + dy * vy, idx);
        }
        std::sort(tOrder.begin(), tOrder.end());

        std::vector<int> sortedOrigIdx(nh);
        std::vector<cv::Point2d> sortedPts(nh);
        for (int i = 0; i < nh; i++) {
            sortedOrigIdx[i] = tOrder[i].second;
            sortedPts[i] = pts[tOrder[i].second];
        }

        // 计算转角
        std::vector<double> angles(nh, 0.0);
        for (int i = 1; i < nh - 1; i++) {
            double dx1 = sortedPts[i].x - sortedPts[i-1].x;
            double dy1 = sortedPts[i].y - sortedPts[i-1].y;
            double dx2 = sortedPts[i+1].x - sortedPts[i].x;
            double dy2 = sortedPts[i+1].y - sortedPts[i].y;
            double len1 = sqrt(dx1*dx1 + dy1*dy1);
            double len2 = sqrt(dx2*dx2 + dy2*dy2);
            if (len1 < 0.001 || len2 < 0.001) { angles[i] = 0; continue; }
            double cross = dx1*dy2 - dy1*dx2;
            double dot = dx1*dx2 + dy1*dy2;
            angles[i] = fabs(atan2(cross, dot));
        }

        // 分段：arc 段 vs line 段
        double angleThresh = 0.10;  // ~5.7 degrees
        int minSegLen = 4;

        std::vector<bool> isArc(nh, false);
        for (int i = 0; i < nh; i++)
            isArc[i] = (angles[i] > angleThresh);

        // 合并短段
        bool changed = true;
        while (changed) {
            changed = false;
            int i = 0;
            while (i < nh) {
                int j = i;
                while (j < nh && isArc[j] == isArc[i]) j++;
                if (j - i < minSegLen) {
                    bool leftType = (i > 0) ? isArc[i-1] : !isArc[i];
                    bool rightType = (j < nh) ? isArc[j] : !isArc[i];
                    int leftLen = i;
                    int rightLen = nh - j;
                    bool mergeType = (leftLen >= rightLen) ? leftType : rightType;
                    for (int k = i; k < j; k++) isArc[k] = mergeType;
                    changed = true;
                }
                i = j;
            }
        }

        // 构建最终段列表
        struct Seg { int start; int end; bool arc; };
        std::vector<Seg> segments;
        int segStart = 0;
        for (int i = 1; i < nh; i++) {
            if (isArc[i] != isArc[segStart]) {
                segments.push_back({segStart, i, isArc[segStart]});
                segStart = i;
            }
        }
        segments.push_back({segStart, nh, isArc[segStart]});

        // 对每段做拟合
        for (size_t s = 0; s < segments.size(); s++) {
            int si = segments[s].start;
            int ei = segments[s].end;
            int segLen = ei - si;

            if (segLen < 2) continue;

            if (!segments[s].arc) {
                // 直线拟合
                std::vector<cv::Point2f> segPts(segLen);
                for (int k = 0; k < segLen; k++)
                    segPts[k] = cv::Point2f((float)sortedPts[si+k].x, (float)sortedPts[si+k].y);

                cv::Vec4f lp;
                cv::fitLine(segPts, lp, cv::DIST_L2, 0, 0.01, 0.01);
                double fvx = lp[0], fvy = lp[1], fx0 = lp[2], fy0 = lp[3];

                for (int k = 0; k < segLen; k++) {
                    int origIdx = sortedOrigIdx[si + k];
                    double t = (pts[origIdx].x - fx0) * fvx + (pts[origIdx].y - fy0) * fvy;
                    result[origIdx] = cv::Point2d(fx0 + t * fvx, fy0 + t * fvy);
                }
            } else {
                // 圆弧拟合（用 fitCircleLeastSquares）
                std::vector<cv::Point2d> arcPts(segLen);
                for (int k = 0; k < segLen; k++)
                    arcPts[k] = sortedPts[si + k];

                cv::Vec3d circle = fitCircleLeastSquares(arcPts);
                double ccx = circle[0], ccy = circle[1], cr = circle[2];

                if (cr > 0.5 && cr < 100000) {
                    // 检查拟合误差
                    double totalErr = 0;
                    for (int k = 0; k < segLen; k++) {
                        double d = sqrt((arcPts[k].x-ccx)*(arcPts[k].x-ccx) +
                                        (arcPts[k].y-ccy)*(arcPts[k].y-ccy));
                        totalErr += fabs(d - cr);
                    }
                    double avgErr = totalErr / segLen;

                    if (avgErr < cr * 0.3) {
                        // 用圆弧替换
                        double startA = atan2(arcPts[0].y - ccy, arcPts[0].x - ccx);
                        double endA = atan2(arcPts[segLen-1].y - ccy, arcPts[segLen-1].x - ccx);
                        double delta = endA - startA;
                        if (delta > CV_PI) delta -= 2 * CV_PI;
                        if (delta < -CV_PI) delta += 2 * CV_PI;

                        for (int k = 0; k < segLen; k++) {
                            int origIdx = sortedOrigIdx[si + k];
                            double a = startA + delta * k / (segLen - 1);
                            result[origIdx] = cv::Point2d(ccx + cr * cos(a), ccy + cr * sin(a));
                        }
                    } else {
                        // 圆拟合误差太大，退化为直线
                        std::vector<cv::Point2f> segPts(segLen);
                        for (int k = 0; k < segLen; k++)
                            segPts[k] = cv::Point2f((float)arcPts[k].x, (float)arcPts[k].y);
                        cv::Vec4f lp;
                        cv::fitLine(segPts, lp, cv::DIST_L2, 0, 0.01, 0.01);
                        double fvx = lp[0], fvy = lp[1], fx0 = lp[2], fy0 = lp[3];
                        for (int k = 0; k < segLen; k++) {
                            int origIdx = sortedOrigIdx[si + k];
                            double t = (pts[origIdx].x - fx0) * fvx + (pts[origIdx].y - fy0) * fvy;
                            result[origIdx] = cv::Point2d(fx0 + t * fvx, fy0 + t * fvy);
                        }
                    }
                }
            }
        }
    };

    fitHalf(posIdx);
    fitHalf(negIdx);

    return result;
}

// ---- Stadium (capsule) shape fitting helpers ----

static cv::Vec4d ransacFitCircle(const std::vector<cv::Point2d>& pts,
                                  int maxIter, double thresh) {
    int n = (int)pts.size();
    if (n < 3) return cv::Vec4d(0, 0, 0, 0);
    cv::RNG rng(42);
    cv::Vec4d best(0, 0, 0, 0);
    for (int iter = 0; iter < maxIter; iter++) {
        int i1 = rng.uniform(0, n);
        int i2 = rng.uniform(0, n);
        int i3 = rng.uniform(0, n);
        if (i1 == i2 || i2 == i3 || i1 == i3) continue;
        double ax = pts[i1].x, ay = pts[i1].y;
        double bx = pts[i2].x, by = pts[i2].y;
        double cx_ = pts[i3].x, cy_ = pts[i3].y;
        double D = 2.0 * (ax * (by - cy_) + bx * (cy_ - ay) + cx_ * (ay - by));
        if (fabs(D) < 1e-10) continue;
        double ux = ((ax*ax + ay*ay) * (by - cy_) + (bx*bx + by*by) * (cy_ - ay) + (cx_*cx_ + cy_*cy_) * (ay - by)) / D;
        double uy = ((ax*ax + ay*ay) * (cx_ - bx) + (bx*bx + by*by) * (ax - cx_) + (cx_*cx_ + cy_*cy_) * (bx - ax)) / D;
        double r = sqrt((ax - ux)*(ax - ux) + (ay - uy)*(ay - uy));
        int inliers = 0;
        for (int j = 0; j < n; j++) {
            double dist = fabs(sqrt((pts[j].x - ux)*(pts[j].x - ux) +
                                   (pts[j].y - uy)*(pts[j].y - uy)) - r);
            if (dist < thresh) inliers++;
        }
        if (inliers > best[3]) best = cv::Vec4d(ux, uy, r, (double)inliers);
    }
    return best;
}

static cv::Vec<double, 5> ransacFitLine(const std::vector<cv::Point2d>& pts,
                                         int maxIter, double thresh) {
    int n = (int)pts.size();
    if (n < 2) return cv::Vec<double, 5>(0, 0, 0, 0, 0);
    cv::RNG rng(0);
    cv::Vec<double, 5> best(0, 0, 0, 0, 0);
    for (int iter = 0; iter < maxIter; iter++) {
        int i1 = rng.uniform(0, n);
        int i2 = rng.uniform(0, n);
        if (i1 == i2) i2 = (i2 + 1) % n;
        double dx = pts[i2].x - pts[i1].x;
        double dy = pts[i2].y - pts[i1].y;
        double len = sqrt(dx * dx + dy * dy);
        if (len < 1e-10) continue;
        double vx = dx / len, vy = dy / len;
        double x0 = pts[i1].x, y0 = pts[i1].y;
        int inliers = 0;
        for (int j = 0; j < n; j++) {
            double dist = fabs((pts[j].x - x0) * vy - (pts[j].y - y0) * vx);
            if (dist < thresh) inliers++;
        }
        if (inliers > best[4]) {
            std::vector<cv::Point2f> inPts;
            for (int j = 0; j < n; j++) {
                double dist = fabs((pts[j].x - x0) * vy - (pts[j].y - y0) * vx);
                if (dist < thresh)
                    inPts.push_back(cv::Point2f((float)pts[j].x, (float)pts[j].y));
            }
            if ((int)inPts.size() >= 2) {
                cv::Vec4f lp;
                cv::fitLine(inPts, lp, cv::DIST_L2, 0, 0.01, 0.01);
                best = cv::Vec<double, 5>(lp[0], lp[1], lp[2], lp[3], (double)inliers);
            }
        }
    }
    return best;
}

static std::vector<cv::Point2d> resampleByArcLength(
    const std::vector<cv::Point2d>& dense, int targetCount) {
    int N = (int)dense.size();
    if (N < 2 || targetCount < 2) return dense;
    std::vector<double> cumLen(N + 1, 0.0);
    for (int i = 1; i <= N; i++) {
        double dx = dense[i % N].x - dense[(i - 1) % N].x;
        double dy = dense[i % N].y - dense[(i - 1) % N].y;
        cumLen[i] = cumLen[i - 1] + sqrt(dx * dx + dy * dy);
    }
    double totalLen = cumLen[N];
    if (totalLen < 1e-10) return dense;
    std::vector<cv::Point2d> result(targetCount);
    for (int i = 0; i < targetCount; i++) {
        double targetLen = totalLen * i / targetCount;
        int lo = 0, hi = N;
        while (lo < hi - 1) {
            int mid = (lo + hi) / 2;
            if (cumLen[mid] <= targetLen) lo = mid;
            else hi = mid;
        }
        double segLen = cumLen[hi] - cumLen[lo];
        double t = (segLen > 1e-10) ? (targetLen - cumLen[lo]) / segLen : 0.0;
        result[i] = cv::Point2d(
            dense[lo % N].x + t * (dense[hi % N].x - dense[lo % N].x),
            dense[lo % N].y + t * (dense[hi % N].y - dense[lo % N].y)
        );
    }
    return result;
}

static std::vector<cv::Point2d> generateStadiumCurve(
    double acx, double acy, double ar,
    double bcx, double bcy, double br,
    double lvx, double lvy, double lx0, double ly0,
    double rvx, double rvy, double rx0, double ry0,
    int pointsPerSegment) {
    // Stadium shape: 4 segments — Arc A (semicircle) | Line 1 | Arc B (semicircle) | Line 2
    // Both semicircles sweep in the SAME rotational direction (true mirror).
    // Arc angles are determined by the center-to-center direction.
    std::vector<cv::Point2d> curve;
    curve.reserve(pointsPerSegment * 4);

    // Center-to-center direction (A -> B)
    double linkDx = bcx - acx, linkDy = bcy - acy;
    double linkLen = sqrt(linkDx * linkDx + linkDy * linkDy);
    if (linkLen < 1.0) return curve;
    double linkAngle = atan2(linkDy, linkDx);  // direction A->B

    // Perpendicular (normal) to link: rotate linkAngle by -90 deg
    double perpAngle = linkAngle - CV_PI / 2.0;
    // Normal vector (perpendicular)
    double perpX = cos(perpAngle);
    double perpY = sin(perpAngle);

    // Determine which line is on which side using line1 ref point
    // Project (lx0-acy, ly0-acy) onto the perpendicular direction
    double l1RelX = lx0 - acx, l1RelY = ly0 - acy;
    double l1Proj = l1RelX * perpX + l1RelY * perpY;

    // side1 = the side where line1 is
    // side1 direction from center A = perpAngle if l1Proj > 0, else perpAngle+PI
    double side1AngleFromA;
    if (l1Proj >= 0) {
        side1AngleFromA = perpAngle;
    } else {
        side1AngleFromA = perpAngle + CV_PI;
    }
    double side2AngleFromA = side1AngleFromA + CV_PI;  // opposite side

    // Arc A: semicircle from side2 to side1, sweeping through (linkAngle + PI)
    // i.e., going the "away from B" direction
    // delta: we need to go from side2AngleFromA to side1AngleFromA
    //       by sweeping away from B (through linkAngle + PI)
    double arcADelta = -CV_PI;  // always sweep -180 deg (CW in standard coords)
    // Verify: starting from side2, sweeping -PI should reach side1
    // side1AngleFromA = side2AngleFromA - PI? Yes! Because side2 = side1 + PI
    // So starting at side2, delta=-PI -> end at side2-PI = side1. Correct.

    // Arc B: same sweep direction as Arc A (mirrored)
    // From B's perspective, side1 is in the same rotational direction
    double side1AngleFromB = side1AngleFromA;  // same absolute angle (parallel sides)
    double side2AngleFromB = side2AngleFromA;  // same absolute angle
    // Arc B goes from side1AngleFromB to side2AngleFromB, sweeping through (linkAngle)
    // Starting at side1, delta=-PI -> end at side1-PI = side2. Correct.

    // === Segment 1: Arc A (semicircle at A) ===
    // From side2 around to side1 (away from B), exclude endpoint
    int nA = std::max(2, pointsPerSegment);
    for (int i = 0; i < nA; i++) {
        double t = (double)i / nA;
        double a = side2AngleFromA + arcADelta * t;
        curve.push_back(cv::Point2d(acx + ar * cos(a), acy + ar * sin(a)));
    }

    // === Segment 2: Line 1 (side1, from A to B), exclude both endpoints ===
    int nL = std::max(2, pointsPerSegment);
    double aEndX = acx + ar * cos(side1AngleFromA);
    double aEndY = acy + ar * sin(side1AngleFromA);
    double bStartX = bcx + br * cos(side1AngleFromB);
    double bStartY = bcy + br * sin(side1AngleFromB);
    for (int i = 1; i < nL; i++) {
        double t = (double)i / nL;
        curve.push_back(cv::Point2d(
            aEndX + t * (bStartX - aEndX),
            aEndY + t * (bStartY - aEndY)));
    }

    // === Segment 3: Arc B (semicircle at B) ===
    // From side1 around to side2 (away from A), exclude endpoint
    int nB = std::max(2, pointsPerSegment);
    for (int i = 0; i < nB; i++) {
        double t = (double)i / nB;
        double a = side1AngleFromB + arcADelta * t;
        curve.push_back(cv::Point2d(bcx + br * cos(a), bcy + br * sin(a)));
    }

    // === Segment 4: Line 2 (side2, from B back to A), include endpoint ===
    double bEndX = bcx + br * cos(side2AngleFromB);
    double bEndY = bcy + br * sin(side2AngleFromB);
    double aStartX = acx + ar * cos(side2AngleFromA);
    double aStartY = acy + ar * sin(side2AngleFromA);
    for (int i = 1; i <= nL; i++) {
        double t = (double)i / nL;
        curve.push_back(cv::Point2d(
            bEndX + t * (aStartX - bEndX),
            bEndY + t * (aStartY - bEndY)));
    }
    // Line 2 endpoint = aStartX,aStartY = Arc A start point. Curve is closed.

    return curve;
}

/**
 * fitStadiumShape - 跑道形(stadium)轨迹拟合
 * 
 * 【适用场景】
 *   工件上的暗条轨迹呈"跑道形"——两端是半圆弧，中间是两条平行直线。
 *   例如: [   半圆A   ]====直线1====[   半圆B   ]====直线2====[回到半圆A]
 * 
 * 【算法流程】
 *   Step 1: PCA主成分分析 → 求得主方向(vx,vy)和法线方向(nx,ny)
 *   Step 2: 沿主方向投影所有点到t轴，用t的中位数将点分成两侧
 *   Step 3: 用t值筛选出两端点(endAPts, endBPts)和中间点(midPts)
 *   Step 4: 对两端点用RANSAC拟合圆 → 得到两个半圆的圆心(acx,acy)和半径(r)
 *   Step 5: 对中间点按法线方向分成两组 → 分别用RANSAC拟合两条直线
 *   Step 6: 生成密集的跑道曲线 → 等弧长重采样为n个点
 *   Step 7: 如果RANSAC失败，fallback为椭圆拟合
 * 
 * 【参数】
 *   pts: 输入的采样点序列（按原始顺序排列）
 *   返回: 拟合后的点序列（点数与输入相同）
 */
static std::vector<cv::Point2d> fitStadiumShape(const std::vector<cv::Point2d>& pts) {
    const int n = (int)pts.size();
    if (n < 8) return pts;  // 跑道形至少需要8个点
    
    // === Step 1: PCA主成分分析 ===
    // 计算所有点的中心(cx, cy)
    double cx = 0, cy = 0;
    for (int i = 0; i < n; i++) { cx += pts[i].x; cy += pts[i].y; }
    cx /= n; cy /= n;
    
    // 计算协方差矩阵
    // Cxx = E[(x-cx)²], Cxy = E[(x-cx)(y-cy)], Cyy = E[(y-cy)²]
    double Cxx = 0, Cxy = 0, Cyy = 0;
    for (int i = 0; i < n; i++) {
        double dx = pts[i].x - cx, dy = pts[i].y - cy;
        Cxx += dx * dx; Cxy += dx * dy; Cyy += dy * dy;
    }
    Cxx /= n; Cxy /= n; Cyy /= n;
    
    // 求协方差矩阵的特征值和特征向量
    // 协方差矩阵: [Cxx Cxy; Cxy Cyy]
    // 特征值 λ = (trace ± sqrt(trace² - 4*det)) / 2
    double tr = Cxx + Cyy;
    double det = Cxx * Cyy - Cxy * Cxy;
    double disc = sqrt(std::max(0.0, tr * tr / 4.0 - det));
    double lambda1 = tr / 2.0 + disc;  // 最大特征值对应主方向
    
    // 计算主方向特征向量 (vx, vy)
    // (Cxx-λ, Cxy) · (vx, vy) = 0  =>  vx = λ-Cyy, vy = Cxy
    double vx, vy;
    if (fabs(Cxy) > 1e-10) { vx = lambda1 - Cyy; vy = Cxy; }
    else { vx = (Cxx >= Cyy) ? 1.0 : 0.0; vy = (Cxx >= Cyy) ? 0.0 : 1.0; }
    
    // 归一化主方向向量
    double vlen = sqrt(vx * vx + vy * vy);
    if (vlen < 1e-10) return pts;
    vx /= vlen; vy /= vlen;
    
    // 法线方向 = 主方向旋转90° (nx, ny)
    double nx = -vy, ny = vx;
    
    // === Step 2: 沿主方向投影到t轴 ===
    // 将每个点投影到主方向上，得到标量坐标t
    // t = (p - center) · (vx, vy) = (px-cx)*vx + (py-cy)*vy
    std::vector<std::pair<double, int>> tProj(n);
    for (int i = 0; i < n; i++) {
        double dx = pts[i].x - cx, dy = pts[i].y - cy;
        tProj[i] = std::make_pair(dx * vx + dy * vy, i);
    }
    std::sort(tProj.begin(), tProj.end());  // 按t值排序
    
    // 计算t的极值范围
    double tMin = tProj[0].first;
    double tMax = tProj[n - 1].first;
    double tRange = tMax - tMin;
    if (tRange < 2.0) return pts;  // 范围太小，不是有效的跑道形
    
    // === Step 3: 筛选端部点和中间点 ===
    // 用20%和80%阈值分割点:
    //   t ∈ [tMin, tThresh1]  → 端部A的点 (endAPts)
    //   t ∈ [tThresh1, tThresh2] → 中间部分的点 (midPts)
    //   t ∈ [tThresh2, tMax]  → 端部B的点 (endBPts)
    //
    // 示意图:
    //   |----20%----|--------60%--------|----20%----|
    //   tMin     tThresh1            tThresh2      tMax
    //   └─endAPts─┘   └─────midPts─────┘───endBPts─┘
    double tThresh1 = tMin + tRange * 0.20;
    double tThresh2 = tMax - tRange * 0.20;
    
    std::vector<cv::Point2d> endAPts, endBPts, midPts;
    for (int i = 0; i < n; i++) {
        double t = tProj[i].first;
        if (t <= tThresh1) endAPts.push_back(pts[tProj[i].second]);  // 左端部
        else if (t >= tThresh2) endBPts.push_back(pts[tProj[i].second]);  // 右端部
        else midPts.push_back(pts[tProj[i].second]);  // 中间部分
    }
    
    // === Step 4: RANSAC圆弧拟合 ===
    // 对两端点分别用RANSAC算法拟合圆
    // 圆参数: Vec4d(centerX, centerY, radius, inlierCount)
    double ransacT = tRange * 0.05;  // 距离阈值: 5%的t范围
    if (ransacT < 1.0) ransacT = 1.0;
    
    cv::Vec4d circleA = ransacFitCircle(endAPts, 300, ransacT);
    cv::Vec4d circleB = ransacFitCircle(endBPts, 300, ransacT);
    
    // === Step 5: RANSAC直线拟合 ===
    // 将中间点按法线方向分成两组 (直线1侧和直线2侧)
    // 点在法线正侧 → side1; 点在法线负侧 → side2
    //
    //      法线方向 (nx, ny)
    //           ↑
    //    side1 ←------→ side2 (两条平行线)
    std::vector<cv::Point2d> side1, side2;
    for (size_t i = 0; i < midPts.size(); i++) {
        double dx = midPts[i].x - cx, dy = midPts[i].y - cy;
        // 投影到法线: d = (p-center) · (nx, ny)
        if (dx * nx + dy * ny >= 0) side1.push_back(midPts[i]);
        else side2.push_back(midPts[i]);
    }
    
    // 对两侧分别用RANSAC拟合直线
    // 直线参数: Vec<double,5>(vx, vy, x0, y0, inlierCount)
    cv::Vec<double, 5> line1 = ransacFitLine(side1, 300, ransacT);
    cv::Vec<double, 5> line2 = ransacFitLine(side2, 300, ransacT);
    
    // === Step 6: 检查拟合质量 ===
    // 如果RANSAC内点数太少，说明拟合失败
    // circleA[3], circleB[3] 是内点计数
    // line1[4], line2[4] 是内点计数
    if (circleA[3] < 3 || circleB[3] < 3 || line1[4] < 3 || line2[4] < 3) {
        // RANSAC失败，fallback为椭圆拟合
        std::vector<cv::Point2f> fpts;
        for (int i = 0; i < n; i++) fpts.push_back(cv::Point2f((float)pts[i].x, (float)pts[i].y));
        if ((int)fpts.size() >= 5) {
            cv::RotatedRect ell = cv::fitEllipse(fpts);
            std::vector<cv::Point2d> result(n);
            for (int i = 0; i < n; i++) {
                // 在椭圆上均匀采样n个点
                double angle = 2.0 * CV_PI * i / n;
                double a = ell.angle * CV_PI / 180.0;
                double ex = ell.size.width / 2.0, ey = ell.size.height / 2.0;
                // 旋转椭圆的参数方程
                result[i] = cv::Point2d(
                    ell.center.x + ex * cos(angle) * cos(a) - ey * sin(angle) * sin(a),
                    ell.center.y + ex * cos(angle) * sin(a) + ey * sin(angle) * cos(a));
            }
            return result;
        }
        return pts;  // 无法拟合，返回原始点
    }
    
    // === Step 7: 生成跑道曲线 + 等弧长重采样 ===
    // 根据拟合的圆心和直线参数生成密集的跑道形曲线
    // 曲线顺序: 圆A弧 → 直线1 → 圆B弧 → 圆B另一弧 → 直线2 → 圆A另一弧 (闭合)
    std::vector<cv::Point2d> dense = generateStadiumCurve(
        circleA[0], circleA[1], circleA[2],   // 圆A: (圆心x, 圆心y, 半径)
        circleB[0], circleB[1], circleB[2],      // 圆B: (圆心x, 圆心y, 半径)
        line1[0], line1[1], line1[2], line1[3],   // 直线1: (方向vx, vy, 点x0, y0)
        line2[0], line2[1], line2[2], line2[3],   // 直线2: (方向vx, vy, 点x0, y0)
        200);  // 生成200个密集点
    
    if ((int)dense.size() < 10) return pts;  // 生成失败，返回原始点
    
    // 等弧长重采样: 将密集曲线均匀重采样为n个点
    // 这样每个相邻点之间的弧长都相等
    return resampleByArcLength(dense, n);
}





// ========== OpenCV 16 暗条轨迹检测 ==========

void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount,
                            bool useContourMode, int fitMode, double approxEpsilon) {
    LOG_INFO("=== Starting OpenCV 16-DarkBars Trajectory Detection ===");
    const bool kEnableWatershedBeforeStep2 = false;  // 可选开关：在步骤2前启用分水岭预分割

    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }

    // 释放之前的步骤图
    if (stepImages) {
        for (int i = 0; i < stepImageCount; i++) {
            if (stepImages[i].data) {
                free(stepImages[i].data);
                stepImages[i].data = NULL;
            }
            stepImages[i] = {0};
        }
    }

    int w = img->width, h = img->height;

    // 转换输入图像为 OpenCV Mat 灰度图
    Mat grayMat;
    if (img->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;
        Mat tmp(h, w, CV_8UC1, img->data, pitch);
        grayMat = tmp.clone();
        tmp.release();
    } else {
        int srcRS = ((w * img->channels + 3) / 4) * 4;
        Mat colorMat(h, w, CV_8UC3, img->data, srcRS);
        cvtColor(colorMat, grayMat, COLOR_BGR2GRAY);
        colorMat.release();
    }

    // ---- 步骤 1：原始灰度图 ----
    if (stepImages && stepImageCount >= 1) {
        stepImages[0].width = grayMat.cols;
        stepImages[0].height = grayMat.rows;
        stepImages[0].channels = 1;
        stepImages[0].data = (unsigned char*)malloc(grayMat.cols * grayMat.rows);
        memcpy(stepImages[0].data, grayMat.data, grayMat.cols * grayMat.rows);
    }

    // ---- 步骤 1.5（可选）：分水岭预分割 ----
    // 目的：在复杂背景下先分离主要工件区域，再进入步骤2的 OTSU 二值化。
    Mat step2Input = grayMat;
    if (kEnableWatershedBeforeStep2) {
        Mat wsBlurred;
        GaussianBlur(grayMat, wsBlurred, Size(5, 5), 1.0);

        Mat wsBinary;
        threshold(wsBlurred, wsBinary, 0, 255, THRESH_BINARY + THRESH_OTSU);

        Mat wsKernel = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));
        Mat sureBg;
        morphologyEx(wsBinary, sureBg, MORPH_DILATE, wsKernel, Point(-1, -1), 2);

        Mat dist;
        distanceTransform(wsBinary, dist, DIST_L2, 5);
        normalize(dist, dist, 0.0, 1.0, NORM_MINMAX);

        Mat sureFg;
        threshold(dist, sureFg, 0.35, 1.0, THRESH_BINARY);
        sureFg.convertTo(sureFg, CV_8U, 255);

        Mat unknown;
        subtract(sureBg, sureFg, unknown);

        Mat markers;
        connectedComponents(sureFg, markers);
        markers += 1;
        markers.setTo(0, unknown == 255);

        Mat wsColor;
        cvtColor(grayMat, wsColor, COLOR_GRAY2BGR);
        watershed(wsColor, markers);

        Mat wsMask = markers > 1;  // 忽略背景(1)与边界(-1)
        step2Input = Mat::zeros(grayMat.size(), grayMat.type());
        grayMat.copyTo(step2Input, wsMask);

        int wsPixels = countNonZero(wsMask);
        if (wsPixels <= 0) {
            LOG_WARN("Watershed mask empty, fallback to original gray image.");
            step2Input = grayMat;
        } else {
            LOG_INFO("Watershed enabled before Step 2, kept pixels: %d", wsPixels);
        }
    }

    // ---- 步骤 2：高斯模糊 + OTSU 二值化 ----
    Mat blurred;
    GaussianBlur(step2Input, blurred, Size(7, 7), 1.5);

    Mat binaryBright;
    threshold(blurred, binaryBright, 0, 255, THRESH_BINARY + THRESH_OTSU);

    if (stepImages && stepImageCount >= 2) {
        stepImages[1].width = binaryBright.cols;
        stepImages[1].height = binaryBright.rows;
        stepImages[1].channels = 1;
        stepImages[1].data = (unsigned char*)malloc(binaryBright.cols * binaryBright.rows);
        memcpy(stepImages[1].data, binaryBright.data, binaryBright.cols * binaryBright.rows);
    }

    // 形态学处理
    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(5, 5));
    Mat morphed;
    // 先闭后开
    morphologyEx(binaryBright, morphed, MORPH_CLOSE, kernel);
    morphologyEx(morphed, morphed, MORPH_OPEN, kernel);
    // 先开后闭
    morphologyEx(morphed, morphed, MORPH_OPEN, kernel);
    morphologyEx(morphed, morphed, MORPH_CLOSE, kernel);

    // 提取外轮廓
    std::vector<std::vector<Point>> outerContours;
    findContours(morphed, outerContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    if (outerContours.empty()) {
        LOG_ERROR("No workpiece contour found!");
        *count = 0;
        return;
    }

    int maxOuterIdx = 0;
    double maxOuterArea = 0.0;
    for (size_t i = 0; i < outerContours.size(); i++) {
        double a = contourArea(outerContours[i]);
        if (a > maxOuterArea) {
            maxOuterArea = a;
            maxOuterIdx = (int)i;
        }
    }

    // 工件 mask
    Mat mask = Mat::zeros(h, w, CV_8UC1);
    drawContours(mask, outerContours, maxOuterIdx, Scalar(255), FILLED);

    // ---- 步骤 3：工件 mask ----
    if (stepImages && stepImageCount >= 3) {
        stepImages[2].width = mask.cols;
        stepImages[2].height = mask.rows;
        stepImages[2].channels = 1;
        stepImages[2].data = (unsigned char*)malloc(mask.cols * mask.rows);
        memcpy(stepImages[2].data, mask.data, mask.cols * mask.rows);
    }

    // ---- 步骤 4：暗条二值化 + 外围跑道区域检测 ----
    const int kDarkThreshold = 50;       // 暗条上限（灰度 >= 50 不算暗条）
    const int kDarkMinThreshold = 5;     // 暗条下限（灰度 < 5 为纯黑背景噪声，排除）
    const int kOuterThreshold = 0;       // 0 = 自适应 Otsu

    Mat innerGray = grayMat.clone();
    innerGray.setTo(255, mask == 0);

    Mat darkBinary;
    Mat outerMask;
    Step_DetectDarkBars(&grayMat, &mask, kDarkThreshold, kDarkMinThreshold, kOuterThreshold, &darkBinary, &outerMask);

    Mat darkBinaryBeforeMorph = darkBinary.clone();

    if (stepImages && stepImageCount >= 4) {
        stepImages[3].width = darkBinaryBeforeMorph.cols;
        stepImages[3].height = darkBinaryBeforeMorph.rows;
        stepImages[3].channels = 1;
        stepImages[3].data = (unsigned char*)malloc(darkBinaryBeforeMorph.cols * darkBinaryBeforeMorph.rows);
        memcpy(stepImages[3].data, darkBinaryBeforeMorph.data, darkBinaryBeforeMorph.cols * darkBinaryBeforeMorph.rows);
    }

    // ---- 步骤 5：形态学清理 ----
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(5, 5));
    // 先闭后开：先填充细小孔洞，再去除孤立噪点
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    // 先开后闭：先去除孤立噪点，再填充细小孔洞
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);

    // 膨胀+腐蚀：先膨胀让暗条变粗融合小间隙，再腐蚀回来使轮廓光滑
    Mat dilateKernel = getStructuringElement(MORPH_ELLIPSE, Size(7, 7));
    dilate(darkBinary, darkBinary, dilateKernel);
    erode(darkBinary, darkBinary, dilateKernel);

    const double kSamplingSpacing = 3.0;  // 等弧长采样间距(像素)





    // 高斯模糊 + 重二值化：让暗条轮廓更光滑
    Mat blurredDark;
    GaussianBlur(darkBinary, blurredDark, Size(9, 9), 2.0);
    threshold(blurredDark, darkBinary, 128, 255, THRESH_BINARY);
    blurredDark.release();

    // 提取所有暗条轮廓
    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    // 自适应面积阈值
    double areaThreshold = (double)(w * h) * 0.002;
    if (areaThreshold < 500.0) areaThreshold = 500.0;
    LOG_INFO("Adaptive area threshold: %.0f (image %dx%d)", areaThreshold, w, h);

    std::vector<std::pair<double, int>> sortedBars;
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
    int targetBars = std::min(barCount, CALIB_MAX_BARS);
    LOG_INFO("Dark bars found: %d (taking top %d)", barCount, targetBars);

    // 等间距轮廓采样（等弧长采样，spacing=3像素）
    std::vector<Point> allPoints;
    std::vector<int> allBarIds;

    Step_SampleContoursEquidistant(&darkContours, &sortedBars, targetBars,
                                     w, h, &allPoints, &allBarIds,
                                     3.0);

    // 步骤5可视化：暗条区域(255) + 暗条轮廓线(192)
    if (stepImages && stepImageCount >= 5) {
        Mat step5Vis = Mat::zeros(h, w, CV_8UC1);
        step5Vis.setTo(255, darkBinary);  // 暗条区域=白
        // 暗条轮廓叠加显示（用轮廓线标记各暗条）
        for (int b = 0; b < targetBars && b < (int)sortedBars.size(); b++) {
            drawContours(step5Vis, darkContours, sortedBars[b].second, Scalar(192), 1);
        }
        stepImages[4].width = step5Vis.cols;
        stepImages[4].height = step5Vis.rows;
        stepImages[4].channels = 1;
        stepImages[4].data = (unsigned char*)malloc(step5Vis.cols * step5Vis.rows);
        memcpy(stepImages[4].data, step5Vis.data, step5Vis.cols * step5Vis.rows);
        step5Vis.release();
    }

    LOG_INFO("Contour sampling: %d dark bars processed, raw points=%d",
             targetBars, (int)allPoints.size());
    Step_FitShape(&allPoints, &allBarIds, w, h, fitMode, approxEpsilon);

    // Mask 验证：窄带模式下验证点在工件范围内；原始模式下验证点在暗条区域内
    {
        int totalBefore = (int)allPoints.size();
        int keptCount = 0;
        for (size_t i = 0; i < allPoints.size(); i++) {
            int px = allPoints[i].x;
            int py = allPoints[i].y;
            if (px >= 0 && px < w && py >= 0 && py < h) {
                // 验证点在暗条区域内
                bool valid = (darkBinary.at<unsigned char>(py, px) != 0);
                if (valid) {
                    allPoints[keptCount] = allPoints[i];
                    allBarIds[keptCount] = allBarIds[i];
                    keptCount++;
                }
            }
        }
        allPoints.resize(keptCount);
        allBarIds.resize(keptCount);
        LOG_INFO("Mask verification: kept %d / %d points", keptCount, totalBefore);
    }

    // 去重 + 排序
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

    // 输出结果
    *count = std::min((int)uniquePoints.size(), CALIB_MAX_TRAJ_POINTS);
    for (int i = 0; i < *count; i++) {
        trajPixels[i].x = (float)uniquePoints[i].x;
        trajPixels[i].y = (float)uniquePoints[i].y;
        if (stepBarIds) stepBarIds[i] = uniqueBarIds[i];
    }

    LOG_INFO("Trajectory detection: raw=%d, unique=%d, output=%d points",
             (int)allPoints.size(), (int)uniquePoints.size(), *count);

    // 坐标变换
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

    // ---- 步骤 6：彩色轨迹 ----
    if (stepImages && stepImageCount >= 6) {
        int dstChannels = 3;
        int dstRowSize = w * dstChannels;
        int imageSize = dstRowSize * h;
        stepImages[5].width = w;
        stepImages[5].height = h;
        stepImages[5].channels = dstChannels;
        stepImages[5].data = (unsigned char*)malloc(imageSize);
        memset(stepImages[5].data, 0, imageSize);

        for (int i = 0; i < *count; i++) {
            int barIdx = stepBarIds ? stepBarIds[i] % 16 : 0;
            const unsigned char* color = BAR_COLORS[barIdx];
            int cx = (int)trajPixels[i].x;
            int cy = (int)trajPixels[i].y;
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < w && y >= 0 && y < h) {
                        int off = y * dstRowSize + x * 3;
                        stepImages[5].data[off]     = color[0];  // B
                        stepImages[5].data[off + 1] = color[1];  // G
                        stepImages[5].data[off + 2] = color[2];  // R
                    }
                }
            }
        }
    }

    // 释放 OpenCV Mat
    innerGray.release();
    darkBinaryBeforeMorph.release();
    outerMask.release();
    darkBinary.release();
    kernel2.release();
    mask.release();
    morphed.release();
    kernel.release();
    binaryBright.release();
    blurred.release();
    grayMat.release();
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
    LOG_INFO("OpenCV trajectory detection complete");
}

// ========== DetectTrajectoryOpenCV 分步骤接口实现 ==========

// 1. 图像转灰度
void Step_ConvertToGrayscale(Image* src, cv::Mat* dstGray) {
    int w = src->width, h = src->height;
    if (src->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;
        cv::Mat tmp(h, w, CV_8UC1, src->data, pitch);
        *dstGray = tmp.clone();
        tmp.release();
    } else {
        int srcRS = ((w * src->channels + 3) / 4) * 4;
        cv::Mat colorMat(h, w, CV_8UC3, src->data, srcRS);
        cvtColor(colorMat, *dstGray, COLOR_BGR2GRAY);
        colorMat.release();
    }
}

// 1b（可选）. CLAHE 对比度受限自适应直方图均衡化
// 在灰度转换后、轮廓检测前增强局部对比度，使暗条边缘更清晰。
// clipLimit: 对比度限制阈值（典型值 2.0~4.0），0 或负数表示不启用。
// tileGridSize: 分块大小（像素），通常 8x8 或 16x16。
void Step_ApplyCLAHE(cv::Mat* grayMat, double clipLimit, int tileGridSize) {
    if (!grayMat || grayMat->empty()) return;
    if (clipLimit <= 0.0) return;  // 不启用

    int tgs = tileGridSize > 0 ? tileGridSize : 8;
    cv::Ptr<cv::CLAHE> clahe = cv::createCLAHE(clipLimit, cv::Size(tgs, tgs));
    clahe->apply(*grayMat, *grayMat);
    LOG_INFO("Step_ApplyCLAHE: clipLimit=%.1f, tileGridSize=%d", clipLimit, tgs);
}

// 1.5（可选）. 分水岭预分割
// 在复杂背景下先分离主要工件区域，返回过滤后的灰度图。
// 若分水岭结果为空，则回退到原始 grayMat。
void Step_WatershedPresegment(const cv::Mat& grayMat, cv::Mat* step2Output, bool enableWatershed) {
    if (!enableWatershed) {
        *step2Output = grayMat.clone();
        return;
    }

    LOG_INFO("Step_WatershedPresegment: enabled");

    cv::Mat wsBlurred;
    cv::GaussianBlur(grayMat, wsBlurred, cv::Size(5, 5), 1.0);

    cv::Mat wsBinary;
    cv::threshold(wsBlurred, wsBinary, 0, 255, cv::THRESH_BINARY + cv::THRESH_OTSU);

    cv::Mat wsKernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::Mat sureBg;
    cv::morphologyEx(wsBinary, sureBg, cv::MORPH_DILATE, wsKernel, cv::Point(-1, -1), 2);

    cv::Mat dist;
    cv::distanceTransform(wsBinary, dist, cv::DIST_L2, 5);
    cv::normalize(dist, dist, 0.0, 1.0, cv::NORM_MINMAX);

    cv::Mat sureFg;
    cv::threshold(dist, sureFg, 0.35, 1.0, cv::THRESH_BINARY);
    sureFg.convertTo(sureFg, CV_8U, 255);

    cv::Mat unknown;
    cv::subtract(sureBg, sureFg, unknown);

    cv::Mat markers;
    cv::connectedComponents(sureFg, markers);
    markers += 1;
    markers.setTo(0, unknown == 255);

    cv::Mat wsColor;
    cv::cvtColor(grayMat, wsColor, cv::COLOR_GRAY2BGR);
    cv::watershed(wsColor, markers);

    cv::Mat wsMask = markers > 1;  // 忽略背景(1)与边界(-1)
    int wsPixels = cv::countNonZero(wsMask);

    if (wsPixels <= 0) {
        LOG_WARN("Step_WatershedPresegment: watershed mask empty, fallback to original gray image.");
        *step2Output = grayMat.clone();
    } else {
        *step2Output = cv::Mat::zeros(grayMat.size(), grayMat.type());
        grayMat.copyTo(*step2Output, wsMask);
        LOG_INFO("Step_WatershedPresegment: kept pixels: %d", wsPixels);
    }
}

// 2b（可选）. Canny 边缘检测
void Step_DetectCannyEdges(const cv::Mat& grayMat, cv::Mat* edgeMap,
                            double lowThreshold, double highThreshold, int blurKsize,
                            int bilateralD, double bilateralSigmaColor, double bilateralSigmaSpace,
                            int nlmeansH, int nlmeansTemplateSize, int nlmeansSearchSize) {
    if (grayMat.empty() || !edgeMap) return;

    int w = grayMat.cols, h = grayMat.rows;

    cv::Mat src = grayMat;
    cv::Mat blurred;

    // 第一步：双边滤波（保留边缘，平滑噪声，增强大结构）
    if (bilateralD > 0) {
        cv::Mat bilateral;
        int d = bilateralD;
        double sc = bilateralSigmaColor > 0 ? bilateralSigmaColor : 75;
        double ss = bilateralSigmaSpace > 0 ? bilateralSigmaSpace : 75;
        cv::bilateralFilter(grayMat, bilateral, d, sc, ss);
        LOG_INFO("Step_DetectCannyEdges: bilateralFilter d=%d, sigmaColor=%.0f, sigmaSpace=%.0f",
                 d, sc, ss);
        src = bilateral;
        bilateral.release();
    }

    // 第二步：非局部均值去噪（去除纹理噪声，保留结构边缘）
    if (nlmeansH > 0) {
        cv::Mat denoised;
        int hVal = nlmeansH;
        int tSize = nlmeansTemplateSize > 1 ? nlmeansTemplateSize : 7;
        int sSize = nlmeansSearchSize > 1 ? nlmeansSearchSize : 21;
        // 确保奇数
        if ((tSize % 2) == 0) tSize++;
        if ((sSize % 2) == 0) sSize++;
        cv::fastNlMeansDenoising(src, denoised, hVal, tSize, sSize);
        LOG_INFO("Step_DetectCannyEdges: fastNlMeansDenoising h=%d, templateWin=%d, searchWin=%d",
                 hVal, tSize, sSize);
        src = denoised;
        denoised.release();
    }

    // 第三步：可选高斯模糊
    if (blurKsize > 0 && (blurKsize % 2) == 1) {
        cv::GaussianBlur(src, blurred, cv::Size(blurKsize, blurKsize), 0);
        src = blurred;
    }

    // 始终先计算自适应阈值（用于日志和乘数模式）
    cv::Mat medMat;
    grayMat.convertTo(medMat, CV_64F);
    std::vector<double> pixels(medMat.reshape(1, 1));
    std::nth_element(pixels.begin(), pixels.begin() + pixels.size() / 2, pixels.end());
    double median = pixels[pixels.size() / 2];

    double adaptiveLow = std::max(0.0, median * 0.66);
    double adaptiveHigh = adaptiveLow * 2.0;

    // 阈值决定逻辑：
    //   low > 0: 使用手动值（high也必须 > 0）
    //   low == 0 且 high > 0: high 作为乘数因子，自适应阈值 *= high
    //   low == 0 且 high == 0: 完全自适应
    double multiplier = 1.0;
    if (lowThreshold > 0.0) {
        // 手动模式：直接使用用户给定的阈值
        LOG_INFO("Step_DetectCannyEdges: manual thresholds: low=%.1f, high=%.1f", lowThreshold, highThreshold);
    } else if (highThreshold > 0.0) {
        // 乘数模式：highThreshold 作为乘数因子（>1减少边缘，<1增加边缘）
        multiplier = highThreshold;
        lowThreshold = adaptiveLow * multiplier;
        highThreshold = adaptiveHigh * multiplier;
        LOG_INFO("Step_DetectCannyEdges: multiplier=%.2f -> low=%.1f, high=%.1f (median=%.1f)",
                 multiplier, lowThreshold, highThreshold, median);
    } else {
        // 完全自适应
        lowThreshold = adaptiveLow;
        highThreshold = adaptiveHigh;
        LOG_INFO("Step_DetectCannyEdges: adaptive thresholds: low=%.1f, high=%.1f (median=%.1f)",
                 lowThreshold, highThreshold, median);
    }

    cv::Mat edges;
    cv::Canny(src, edges, lowThreshold, highThreshold);

    *edgeMap = edges.clone();

    // 统计边缘像素数量
    int edgePixels = cv::countNonZero(*edgeMap);
    LOG_INFO("Step_DetectCannyEdges: output=%dx%d, edge pixels=%d (%.2f%%)",
             w, h, edgePixels, 100.0 * edgePixels / (w * h));

    blurred.release();
    medMat.release();
}

// 2. 高斯模糊 + OTSU二值化 + 形态学处理 + 提取外轮廓
// minArea: 最小轮廓面积阈值，小于此值的轮廓被过滤掉 (0=不过滤)
void Step_PreprocessAndFindContours(cv::Mat* grayMat, cv::Mat* binaryBright,
                                     cv::Mat* morphed, cv::Mat* mask, cv::Mat* coloredMask,
                                     std::vector<std::vector<cv::Point>>* outerContours,

                                     int blurKsize, int morphKernelSize,
                                     bool enableWatershed, double minArea) {

    if (!grayMat || grayMat->empty()) return;
    int w = grayMat->cols, h = grayMat->rows;

    // 步骤 1.5（可选）：分水岭预分割
    cv::Mat step2Input;
    Step_WatershedPresegment(*grayMat, &step2Input, enableWatershed);
    
    // 高斯模糊（使用传入参数）
    int bk = (blurKsize > 0 && (blurKsize % 2) == 1) ? blurKsize : 7;
    cv::Mat blurred;
    cv::GaussianBlur(step2Input, blurred, cv::Size(bk, bk), 1.5);
    LOG_INFO("Step_PreprocessAndFindContours: blurKsize=%d", bk);
    
    // OTSU二值化
    cv::threshold(blurred, *binaryBright, 0, 255, THRESH_BINARY + THRESH_OTSU);
    
    // 形态学处理（使用传入参数）
    int mk = (morphKernelSize > 0 && (morphKernelSize % 2) == 1) ? morphKernelSize : 5;
    cv::Mat kernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(mk, mk));
    // 先闭后开
    morphologyEx(*binaryBright, *morphed, MORPH_CLOSE, kernel);
    morphologyEx(*morphed, *morphed, MORPH_OPEN, kernel);
    // 先开后闭
    morphologyEx(*morphed, *morphed, MORPH_OPEN, kernel);
    morphologyEx(*morphed, *morphed, MORPH_CLOSE, kernel);
    LOG_INFO("Step_PreprocessAndFindContours: morphKernelSize=%d", mk);
    
    // 提取外轮廓
    findContours(*morphed, *outerContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    LOG_INFO("Step_PreprocessAndFindContours: found %d outer contours", (int)outerContours->size());
    
    // 按面积过滤小轮廓
    if (minArea > 0 && !outerContours->empty()) {
        std::vector<std::vector<cv::Point>> filtered;
        for (size_t i = 0; i < outerContours->size(); i++) {
            double a = contourArea((*outerContours)[i]);
            if (a >= minArea) {
                filtered.push_back(std::move((*outerContours)[i]));
            }
        }
        int removed = (int)outerContours->size() - (int)filtered.size();
        if (removed > 0) {
            *outerContours = std::move(filtered);
            LOG_INFO("  Filtered %d contours smaller than %.1f, remaining %d", removed, minArea, (int)outerContours->size());
        }
    }
    
    // 生成mask（二值）
    *mask = cv::Mat::zeros(h, w, CV_8UC1);
    
    // 生成彩色mask
    *coloredMask = cv::Mat::zeros(h, w, CV_8UC3);
    
    if (!outerContours->empty()) {
        // 找最大轮廓
        double maxArea = 0.0;
        int maxIdx = 0;
        for (size_t i = 0; i < outerContours->size(); i++) {
            double a = contourArea((*outerContours)[i]);
            if (a > maxArea) {
                maxArea = a;
                maxIdx = (int)i;
            }
        }
        LOG_INFO("  Max contour: idx=%d, area=%.1f", maxIdx, maxArea);
        
        // 绘制最大轮廓（白色mask）
        drawContours(*mask, *outerContours, maxIdx, cv::Scalar(255), FILLED);
        
        // 绘制所有轮廓（彩色）
        for (size_t i = 0; i < outerContours->size(); i++) {
            int colorIdx = i % 16;
            const cv::Scalar color(BAR_COLORS[colorIdx][0], BAR_COLORS[colorIdx][1], BAR_COLORS[colorIdx][2]);
            double a = contourArea((*outerContours)[i]);
            if ((int)i == maxIdx) {
                // 最大轮廓用绿色填充
                drawContours(*coloredMask, *outerContours, (int)i, cv::Scalar(0, 255, 0), FILLED);
                // 画边界线（白色粗线）
                drawContours(*coloredMask, *outerContours, (int)i, cv::Scalar(255, 255, 255), 3);
            } else {
                // 其他轮廓用颜色填充并画边界
                drawContours(*coloredMask, *outerContours, (int)i, color, FILLED);
                drawContours(*coloredMask, *outerContours, (int)i, cv::Scalar(255, 255, 255), 2);
            }
        }
    }
    
    step2Input.release();
    blurred.release();
    kernel.release();
}

// 2.5 生成 Mask：从外轮廓列表中找到面积最大的轮廓，填充内部区域作为 mask
// outerContours: Step2 输出的外轮廓列表
// width, height: 图像尺寸
// mask: 输出 mask（CV_8UC1，最大轮廓内部=255，其余=0）
void Step_CreateMaskFromLargestContour(const std::vector<std::vector<cv::Point>>& outerContours,
                                        int width, int height, cv::Mat* mask, int contourIdx) {
    *mask = cv::Mat::zeros(height, width, CV_8UC1);
    if (outerContours.empty()) {
        LOG_WARN("Step_CreateMaskFromLargestContour: outerContours is empty!");
        return;
    }

    int targetIdx = contourIdx;
    if (targetIdx < 0) {
        // contourIdx < 0 时自动选面积最大的轮廓
        int maxIdx = 0;
        double maxArea = 0.0;
        for (size_t i = 0; i < outerContours.size(); i++) {
            double a = cv::contourArea(outerContours[i]);
            if (a > maxArea) {
                maxArea = a;
                maxIdx = (int)i;
            }
        }
        targetIdx = maxIdx;
        LOG_INFO("Step_CreateMaskFromLargestContour: auto-selected largest idx=%d area=%.1f", targetIdx, maxArea);
    }

    if (targetIdx < 0 || targetIdx >= (int)outerContours.size()) {
        LOG_WARN("Step_CreateMaskFromLargestContour: contourIdx=%d out of range [0, %d), total contours=%d",
                 contourIdx, (int)outerContours.size(), (int)outerContours.size());
        return;
    }

    double area = cv::contourArea(outerContours[targetIdx]);
    LOG_INFO("Step_CreateMaskFromLargestContour: found %d contours, using idx=%d area=%.1f",
             (int)outerContours.size(), targetIdx, area);

    // 只填充选中的那条轮廓内部，不包含背景和其他轮廓
    cv::drawContours(*mask, outerContours, targetIdx, cv::Scalar(255), cv::FILLED);
}

// 3. 根据外轮廓生成工件mask
void Step_CreateWorkpieceMask(std::vector<std::vector<cv::Point>>* outerContours,
                               int maxIdx, int width, int height, cv::Mat* mask) {
    *mask = cv::Mat::zeros(height, width, CV_8UC1);
    LOG_INFO("Step_CreateWorkpieceMask: outerContours size=%d, maxIdx=%d, size=%dx%d",
             (int)outerContours->size(), maxIdx, width, height);
    if (!outerContours->empty()) {
        // 如果 maxIdx <= 0，自动查找最大轮廓
        int targetIdx = maxIdx;
        if (targetIdx <= 0) {
            double maxArea = 0.0;
            targetIdx = 0;
            for (size_t i = 0; i < outerContours->size(); i++) {
                double a = contourArea((*outerContours)[i]);
                if (a > maxArea) {
                    maxArea = a;
                    targetIdx = (int)i;
                }
            }
            LOG_INFO("  Auto-selected max contour: idx=%d, area=%.1f", targetIdx, maxArea);
        }
        
        if (targetIdx >= 0 && targetIdx < (int)outerContours->size()) {
            double area = contourArea((*outerContours)[targetIdx]);
            LOG_INFO("  Drawing contour %d with area %.1f", targetIdx, area);
            drawContours(*mask, *outerContours, targetIdx, cv::Scalar(255), FILLED);
        }
    } else {
        LOG_WARN("  Cannot draw contour: outerContours is empty!");
    }
}

// 4. 暗条二值化 + 外围跑道区域提取
// darkThreshold: 暗条上限阈值（灰度 >= darkThreshold 不算暗条）
// darkMinThreshold: 暗条下限阈值（灰度 < darkMinThreshold 为纯黑背景噪声，排除），0=不过滤
// outerThreshold: 外围跑道阈值（darkThreshold <= gray < outerThreshold 为外围区域，0=自适应Otsu）
void Step_DetectDarkBars(cv::Mat* grayMat, cv::Mat* mask, int darkThreshold,
                          int darkMinThreshold, int outerThreshold,
                          cv::Mat* darkBinary, cv::Mat* outerMask) {
    // 工件内部保留原灰度，外部设为255（白色）
    *darkBinary = grayMat->clone();
    darkBinary->setTo(255, *mask == 0);
    
    // 暗条上限：低于 darkThreshold 的为255（暗条）
    cv::threshold(*darkBinary, *darkBinary, darkThreshold, 255, THRESH_BINARY_INV);
    // 暗条下限：低于 darkMinThreshold 的为0（排除纯黑背景噪声）
    if (darkMinThreshold > 0) {
        cv::Mat noiseMask;
        cv::threshold(*grayMat, noiseMask, darkMinThreshold - 1, 255, THRESH_BINARY_INV);
        darkBinary->setTo(0, (noiseMask != 0) & (*mask != 0));
        noiseMask.release();
        LOG_INFO("Step_DetectDarkBars: darkMinThreshold=%d, excluding pure-black background noise", darkMinThreshold);
    }
    darkBinary->setTo(0, *mask == 0);

    // 外围跑道区域：darkThreshold <= gray < outerThreshold
    *outerMask = cv::Mat::zeros(grayMat->rows, grayMat->cols, CV_8UC1);
    if (outerThreshold > darkThreshold) {
        cv::Mat innerGray = grayMat->clone();
        innerGray.setTo(255, *mask == 0);
        // 灰度在 [darkThreshold, outerThreshold) 区间的像素
        cv::Mat aboveDark, belowOuter;
        cv::threshold(innerGray, aboveDark, darkThreshold - 1, 255, THRESH_BINARY);  // >= darkThreshold
        cv::threshold(innerGray, belowOuter, outerThreshold - 1, 255, THRESH_BINARY_INV);  // < outerThreshold
        cv::bitwise_and(aboveDark, belowOuter, *outerMask);
        outerMask->setTo(0, *mask == 0);
        aboveDark.release();
        belowOuter.release();
        innerGray.release();

        // 去掉已被暗条覆盖的区域（外围不应包含暗条本身）
        cv::bitwise_and(*outerMask, 255 - *darkBinary, *outerMask);
        
        int outerPixels = cv::countNonZero(*outerMask);
        LOG_INFO("Step_DetectDarkBars: outer band pixels=%d (darkThreshold=%d, outerThreshold=%d)",
                 outerPixels, darkThreshold, outerThreshold);
    } else if (outerThreshold == 0) {
        // 自适应模式：在工件内部用 Otsu 找分界
        cv::Mat innerGray = grayMat->clone();
        innerGray.setTo(255, *mask == 0);
        
        // 只取工件内部像素计算 Otsu
        cv::Mat innerMasked;
        innerGray.copyTo(innerMasked, *mask);
        
        double otsuVal = cv::threshold(innerMasked, innerMasked, 0, 255, THRESH_BINARY | THRESH_OTSU);
        LOG_INFO("Step_DetectDarkBars: Otsu threshold=%.1f", otsuVal);
        
        // 外围区域：暗条阈值 <= gray < Otsu阈值
        int adaptiveOuter = (int)otsuVal;
        if (adaptiveOuter > darkThreshold + 10) {  // 确保有足够区间
            cv::Mat aboveDark, belowOuter;
            cv::threshold(innerGray, aboveDark, darkThreshold - 1, 255, THRESH_BINARY);
            cv::threshold(innerGray, belowOuter, adaptiveOuter - 1, 255, THRESH_BINARY_INV);
            cv::bitwise_and(aboveDark, belowOuter, *outerMask);
            outerMask->setTo(0, *mask == 0);
            cv::bitwise_and(*outerMask, 255 - *darkBinary, *outerMask);
            aboveDark.release();
            belowOuter.release();
            
            int outerPixels = cv::countNonZero(*outerMask);
            LOG_INFO("  Adaptive outer band: pixels=%d (outerThreshold=%d)", outerPixels, adaptiveOuter);
        }
        
        innerGray.release();
        innerMasked.release();
    }
}

// 4.5 基于轮廓查找的外围跑道轮廓提取 (Canvas 方案)
// 对每个暗条的 darkBinary 轮廓做膨胀 → findContours → 得到精确的外围跑道边界轮廓
// outerExpandPixels: 膨胀半径(像素)，用于覆盖外围跑道区域
// outerContours: 输出每个暗条的外围跑道轮廓（与 darkContours 一一对应）
// darkContours: 输入暗条轮廓
// sortedBars: 暗条排序索引
// targetBars: 要处理的暗条数量
// workMask: 工件 mask
void Step_DetectOuterContour(const std::vector<std::vector<cv::Point>>& darkContours,
                              const std::vector<std::pair<double, int>>& sortedBars,
                              int targetBars, int width, int height,
                              int outerExpandPixels,
                              const cv::Mat& workMask,
                              std::vector<std::vector<cv::Point>>* outerContours) {
    outerContours->clear();
    if (outerExpandPixels <= 0) {
        LOG_WARN("Step_DetectOuterContour: outerExpandPixels=%d, skip", outerExpandPixels);
        return;
    }

    // 构建膨胀核
    int kSize = 2 * outerExpandPixels + 1;
    cv::Mat expandKernel = getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(kSize, kSize));

    // 构建窄带核（比外围膨胀小，用于确定暗条+窄带区域，从外围轮廓中减去）
    int narrowKSize = outerExpandPixels + 1;  // 窄带约 outerExpandPixels/2
    cv::Mat narrowKernel = getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(2 * narrowKSize + 1, 2 * narrowKSize + 1));

    // 合并所有暗条的 filled mask（用于排除内部暗条间的膨胀交叉区域）
    cv::Mat allBarsMask = cv::Mat::zeros(height, width, CV_8UC1);
    for (int b = 0; b < targetBars && b < (int)sortedBars.size(); b++) {
        drawContours(allBarsMask, darkContours, sortedBars[b].second, cv::Scalar(255), FILLED);
    }

    // 对所有暗条整体膨胀一次
    cv::Mat allExpanded;
    dilate(allBarsMask, allExpanded, expandKernel);

    // 整体窄带区域 = 膨胀 - 原暗条（用于后续去除暗条内部）
    cv::Mat allNarrow;
    dilate(allBarsMask, allNarrow, narrowKernel);
    cv::Mat narrowBand = allNarrow - allBarsMask;

    // 外围跑道整体区域 = 大膨胀 - 小膨胀（窄带）- 原暗条
    cv::Mat outerRegion = allExpanded - allNarrow;
    outerRegion.setTo(0, workMask == 0);

    // 去掉暗条本身
    outerRegion -= allBarsMask;

    // 对外围整体区域做连通域分析
    std::vector<std::vector<cv::Point>> regionContours;
    cv::findContours(outerRegion, regionContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);

    LOG_INFO("Step_DetectOuterContour: found %d outer region contours (expand=%dpx)",
             (int)regionContours.size(), outerExpandPixels);

    // 对每个暗条独立找其外围轮廓
    for (int b = 0; b < targetBars && b < (int)sortedBars.size(); b++) {
        cv::Mat singleMask = cv::Mat::zeros(height, width, CV_8UC1);
        drawContours(singleMask, darkContours, sortedBars[b].second, cv::Scalar(255), FILLED);

        // 该暗条的专属外围 = 整体外围区域 ∩ 该暗条膨胀邻域
        cv::Mat singleExpanded;
        dilate(singleMask, singleExpanded, expandKernel);

        // 该暗条的外围区域：在大膨胀内，但不在窄带内，不在暗条内
        cv::Mat singleNarrow;
        dilate(singleMask, singleNarrow, narrowKernel);
        cv::Mat singleOuter = singleExpanded - singleNarrow;
        singleOuter.setTo(0, workMask == 0);
        singleOuter -= singleMask;

        // 清理：形态学去噪
        cv::Mat cleanKernel = getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
        morphologyEx(singleOuter, singleOuter, cv::MORPH_OPEN, cleanKernel);
        morphologyEx(singleOuter, singleOuter, cv::MORPH_CLOSE, cleanKernel);

        // 找该暗条专属外围的轮廓
        std::vector<std::vector<cv::Point>> singleContours;
        findContours(singleOuter, singleContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);

        if (singleContours.empty()) {
            outerContours->push_back(std::vector<cv::Point>());
            LOG_DEBUG("  Bar %d: no outer contour found", b);
        } else {
            // 取最大的外围轮廓（可能有多片，取最大的一片）
            int bestIdx = 0;
            double bestArea = 0;
            for (size_t c = 0; c < singleContours.size(); c++) {
                double a = contourArea(singleContours[c]);
                if (a > bestArea) {
                    bestArea = a;
                    bestIdx = (int)c;
                }
            }

            // 转换为 CHAIN_APPROX_NONE 以获得完整轮廓点（用于后续等弧长采样）
            cv::Mat contourMask = cv::Mat::zeros(height, width, CV_8UC1);
            drawContours(contourMask, singleContours, bestIdx, cv::Scalar(255), cv::FILLED);
            std::vector<std::vector<cv::Point>> fullContours;
            findContours(contourMask, fullContours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);

            if (!fullContours.empty()) {
                outerContours->push_back(fullContours[0]);
                LOG_DEBUG("  Bar %d: outer contour %d pts, area=%.0f",
                         b, (int)fullContours[0].size(), bestArea);
            } else {
                outerContours->push_back(singleContours[bestIdx]);
                LOG_DEBUG("  Bar %d: outer contour %d pts (fallback), area=%.0f",
                         b, (int)singleContours[bestIdx].size(), bestArea);
            }
            contourMask.release();
        }

        singleMask.release();
        singleExpanded.release();
        singleNarrow.release();
        singleOuter.release();
        cleanKernel.release();
    }

    allBarsMask.release();
    expandKernel.release();
    narrowKernel.release();
    allExpanded.release();
    allNarrow.release();
    narrowBand.release();
    outerRegion.release();

    LOG_INFO("Step_DetectOuterContour: processed %d bars", targetBars);
}

// 5. 形态学清理
void Step_MorphologyCleanup(cv::Mat* darkBinary, int kernelSize, int blurKsize, double blurSigma) {


    cv::Mat kernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(kernelSize, kernelSize));
    // 先闭后开
    morphologyEx(*darkBinary, *darkBinary, MORPH_OPEN, kernel);
    morphologyEx(*darkBinary, *darkBinary, MORPH_OPEN, kernel);
    morphologyEx(*darkBinary, *darkBinary, MORPH_CLOSE, kernel);
    morphologyEx(*darkBinary, *darkBinary, MORPH_CLOSE, kernel);

    // 膨胀+腐蚀：先膨胀让暗条变粗融合小间隙，再腐蚀回来使轮廓光滑
    int dilateSize = kernelSize + 2;
    cv::Mat dilateKernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(dilateSize, dilateSize));
    dilate(*darkBinary, *darkBinary, dilateKernel);
    erode(*darkBinary, *darkBinary, dilateKernel);

    // 高斯模糊 + 重二值化：让暗条轮廓更光滑
    cv::Mat blurredDark;
    cv::GaussianBlur(*darkBinary, blurredDark, cv::Size(blurKsize, blurKsize), blurSigma);

    cv::threshold(blurredDark, *darkBinary, 128, 255, cv::THRESH_BINARY);
    blurredDark.release();

    kernel.release();
    dilateKernel.release();
}

// 5.5 边界约束膨胀：将暗条沿 Canny 边缘膨胀到下一条跑道型边界
// 原理：逐圈膨胀，计算每圈与 Canny 边缘的重合度（overlap ratio）。
//       当重合度达到峰值后开始下降时停止，即找到"刚好贴上下一条边界"的最佳位置。
//       跑道型边界在 Canny 图上是一圈连续边缘，重合度会在到达边界时达到最大。
// darkBinary: 输入/输出，morph后的暗条二值图，会被原地修改
// edgeMap: Canny边缘图（边缘=255，非边缘=0）
// expandPixels: 最大膨胀半径（像素）
// mask: 工件mask，膨胀结果会被限制在工件范围内
// expandedMask: 输出，膨胀后的区域 = expanded - darkBinary（即新增区域）
void Step_ExpandToEdgeBoundary(cv::Mat* darkBinary, const cv::Mat& edgeMap,
                                int expandPixels, const cv::Mat& mask,
                                cv::Mat* expandedMask) {
    if (darkBinary->empty() || edgeMap.empty()) {
        LOG_WARN("Step_ExpandToEdgeBoundary: empty input, skip");
        if (expandedMask) expandedMask->release();
        return;
    }

    int w = darkBinary->cols, h = darkBinary->rows;
    LOG_INFO("Step_ExpandToEdgeBoundary: input darkBinary %dx%d, edgeMap %dx%d, expand=%d px",
             w, h, edgeMap.cols, expandPixels);

    // Step A: 对 darkBinary 做大半径膨胀，确定最大可达范围
    int kSize = 2 * expandPixels + 1;
    cv::Mat dilKernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(kSize, kSize));
    cv::Mat dilated;
    dilate(*darkBinary, dilated, dilKernel);
    dilKernel.release();

    // Step B: 逐圈膨胀，计算每圈的边缘重合度
    cv::Mat bestResult = darkBinary->clone();
    cv::Mat visited = *darkBinary > 0;  // 从暗条开始
    int totalBorderPixels = 0;

    double bestOverlap = 0.0;   // 历史最大重合度
    int    bestRing = 0;         // 历史最大重合度对应的圈数
    double prevOverlap = 0.0;    // 上一圈的重合度

    // 用于平滑重合度曲线的滑动窗口
    std::vector<double> overlapHistory;

    for (int ring = 1; ring <= expandPixels; ring++) {
        // 当前 frontier: visited 中边缘像素的邻域
        cv::Mat frontier;
        {
            cv::Mat k1 = getStructuringElement(MORPH_CROSS, cv::Size(3, 3));
            dilate(visited, frontier, k1);
            k1.release();
            frontier -= visited;
            frontier -= (*darkBinary > 0);
            frontier &= (dilated > 0);
            if (!mask.empty()) {
                frontier &= (mask > 0);
            }
        }

        int frontierPixels = cv::countNonZero(frontier);
        if (frontierPixels == 0) break;

        // 计算这一圈 frontier 与 Canny 边缘的重合度
        // 重合度 = frontier 中属于 Canny 边缘的像素数 / frontier 总像素数
        cv::Mat frontierEdge = frontier & (edgeMap > 0);
        int edgePixels = cv::countNonZero(frontierEdge);
        double overlap = (double)edgePixels / (double)frontierPixels;

        // 累加到 visited 和当前结果（每圈都加，不跳过边缘像素）
        visited |= (frontier > 0);
        totalBorderPixels += frontierPixels;
        frontierEdge.release();

        LOG_INFO("  ring %d: frontier=%d, edge_hit=%d, overlap=%.4f",
                 ring, frontierPixels, edgePixels, overlap);

        overlapHistory.push_back(overlap);

        // 判断是否达到峰值：当前 overlap >= 历史最大
        if (overlap >= bestOverlap) {
            bestOverlap = overlap;
            bestRing = ring;
            bestResult = visited.clone();  // 保存当前最佳结果
        }

        prevOverlap = overlap;
        frontier.release();
    }

    // Step C: 使用重合度峰值对应的结果
    LOG_INFO("Step_ExpandToEdgeBoundary: best ring=%d, best overlap=%.4f, border pixels=%d",
             bestRing, bestOverlap, cv::countNonZero(bestResult - *darkBinary));

    // Step D: 生成增量区域 expandedMask
    if (expandedMask) {
        *expandedMask = bestResult - *darkBinary;
    }

    // 用最佳结果更新 darkBinary
    *darkBinary = bestResult;

    dilated.release();
    visited.release();
    bestResult.release();
}

// 6. 提取暗条轮廓并按面积排序
int Step_FindAndSortDarkContours(cv::Mat* darkBinary, int width, int height,
                                   std::vector<std::pair<double, int>>* sortedBars,
                                   std::vector<std::vector<cv::Point>>* darkContours,
                                   double minAreaPixels) {
    // 提取所有暗条轮廓
    findContours(*darkBinary, *darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    double areaThreshold;
    if (minAreaPixels >= 0.0) {
        areaThreshold = minAreaPixels;
    } else {
        areaThreshold = (double)(width * height) * 0.002;
        if (areaThreshold < 500.0) areaThreshold = 500.0;
    }

    for (size_t i = 0; i < darkContours->size(); i++) {
        double area = contourArea((*darkContours)[i]);
        if (area > areaThreshold) {
            sortedBars->push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(sortedBars->begin(), sortedBars->end(),
         [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
             return a.first > b.first;
         });
    return (int)sortedBars->size();
}

// 7. 等间距轮廓采样（纯 OpenCV 方案）
// spacing: 采样间距（像素），沿每条暗条轮廓等弧长采样
// 算法：cv::arcLength 计算周长 → 等弧长插值 → 输出像素坐标
void Step_SampleContoursEquidistant(std::vector<std::vector<cv::Point>>* darkContours,
                                     std::vector<std::pair<double, int>>* sortedBars,
                                     int targetBars, int width, int height,
                                     std::vector<cv::Point>* allPoints,
                                     std::vector<int>* allBarIds,
                                     double spacing) {
    allPoints->clear();
    allBarIds->clear();

    for (int b = 0; b < targetBars && b < (int)sortedBars->size(); b++) {
        int idx = (*sortedBars)[b].second;
        if (idx < 0 || idx >= (int)darkContours->size()) {
            LOG_WARN("  Bar %d: sortedBars index %d out of range (darkContours count=%d), skip",
                     b, idx, (int)darkContours->size());
            continue;
        }
        const std::vector<cv::Point>& contour = (*darkContours)[idx];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;

        // 计算轮廓周长，确定采样数
        double perimeter = cv::arcLength(contour, true);
        if (perimeter < 1.0) continue;
        int sampleCount = std::max(10, (int)(perimeter / spacing));

        // 累计弧长数组
        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = contour[j].x - contour[j-1].x;
            double dy = contour[j].y - contour[j-1].y;
            cumLen[j] = cumLen[j-1] + std::sqrt(dx*dx + dy*dy);
        }

        // 等弧长插值采样
        for (int s = 0; s < sampleCount; s++) {
            double targetLen = perimeter * s / sampleCount;
            // 二分查找插值段
            int lo = 0, hi = ptCount - 1;
            while (lo < hi - 1) {
                int mid = (lo + hi) / 2;
                if (cumLen[mid] <= targetLen) lo = mid;
                else hi = mid;
            }
            double segLen = cumLen[hi] - cumLen[lo];
            double t = (segLen > 1e-6) ? (targetLen - cumLen[lo]) / segLen : 0.0;
            int px = (int)std::round(contour[lo].x + t * (contour[hi].x - contour[lo].x));
            int py = (int)std::round(contour[lo].y + t * (contour[hi].y - contour[lo].y));
            if (px >= 0 && px < width && py >= 0 && py < height) {
                allPoints->push_back(cv::Point(px, py));
                allBarIds->push_back(b);
            }
        }

        LOG_INFO("  Bar %d: contour=%d pts, perimeter=%.1f, sampled=%d (spacing=%.1f)",
                 b, ptCount, perimeter, sampleCount, spacing);
    }

    LOG_INFO("Step_SampleContoursEquidistant: total %d points from %d bars",
             (int)allPoints->size(), std::min(targetBars, (int)sortedBars->size()));
}

// 独立轮廓采样：从二值图直接采样，不依赖 stepDetector 上下文
int SampleContoursFromBinary(const unsigned char* binaryData, int width, int height,
                              int targetBars, double spacing, int minArea,
                              Point2D* outPoints, int* outBarIds, int maxPoints) {
    if (!binaryData || width <= 0 || height <= 0 || !outPoints) return 0;

    cv::Mat binary(height, width, CV_8UC1, const_cast<unsigned char*>(binaryData));

    // findContours
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(binary, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);
    LOG_INFO("SampleContoursFromBinary: found %d contours", (int)contours.size());

    // 按面积排序
    std::vector<std::pair<double, int>> sortedBars;
    for (size_t i = 0; i < contours.size(); i++) {
        double a = cv::contourArea(contours[i]);
        if (a >= (double)minArea) {
            sortedBars.push_back(std::make_pair(a, (int)i));
        }
    }
    std::sort(sortedBars.begin(), sortedBars.end(),
              [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
                  return a.first > b.first;
              });
    LOG_INFO("  After area filter (min=%.0f): %d contours, taking top %d",
             (double)minArea, (int)sortedBars.size(), targetBars);

    int totalPts = 0;
    int takeBars = std::min(targetBars, (int)sortedBars.size());

    for (int b = 0; b < takeBars && totalPts < maxPoints; b++) {
        int idx = sortedBars[b].second;
        if (idx < 0 || idx >= (int)contours.size()) continue;
        const std::vector<cv::Point>& contour = contours[idx];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;

        double perimeter = cv::arcLength(contour, true);
        if (perimeter < 1.0) continue;
        int sampleCount = std::max(10, (int)(perimeter / spacing));

        // 累计弧长数组
        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = contour[j].x - contour[j-1].x;
            double dy = contour[j].y - contour[j-1].y;
            cumLen[j] = cumLen[j-1] + std::sqrt(dx*dx + dy*dy);
        }

        // 等弧长插值采样
        for (int s = 0; s < sampleCount && totalPts < maxPoints; s++) {
            double targetLen = perimeter * s / sampleCount;
            int lo = 0, hi = ptCount - 1;
            while (lo < hi - 1) {
                int mid = (lo + hi) / 2;
                if (cumLen[mid] <= targetLen) lo = mid;
                else hi = mid;
            }
            double segLen = cumLen[hi] - cumLen[lo];
            double t = (segLen > 1e-6) ? (targetLen - cumLen[lo]) / segLen : 0.0;
            int px = (int)std::round(contour[lo].x + t * (contour[hi].x - contour[lo].x));
            int py = (int)std::round(contour[lo].y + t * (contour[hi].y - contour[lo].y));
            if (px >= 0 && px < width && py >= 0 && py < height) {
                outPoints[totalPts].x = (float)px;
                outPoints[totalPts].y = (float)py;
                if (outBarIds) outBarIds[totalPts] = b;
                totalPts++;
            }
        }
        LOG_INFO("  Bar %d: contour=%d pts, perimeter=%.1f, sampled=%d",
                 b, ptCount, perimeter, sampleCount);
    }

    LOG_INFO("SampleContoursFromBinary: total %d points from %d bars", totalPts, takeBars);
    return totalPts;
}

int SampleContoursFromPoints(const int* flatX, const int* flatY,
                             const int* contourLengths, int numContours,
                             int targetBars, int width, int height,
                             double spacing,
                             Point2D* outPoints, int* outBarIds, int maxPoints) {
    if (!flatX || !flatY || !contourLengths || numContours <= 0 || !outPoints) return 0;
    int totalPts = 0;
    int takeBars = std::min(targetBars, numContours);

    int offset = 0;
    for (int b = 0; b < takeBars && totalPts < maxPoints; b++) {
        int ptCount = contourLengths[b];
        if (ptCount < 5) { offset += ptCount; continue; }

        // 构造累积弧长数组
        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = flatX[offset + j] - flatX[offset + j - 1];
            double dy = flatY[offset + j] - flatY[offset + j - 1];
            cumLen[j] = cumLen[j - 1] + std::sqrt(dx * dx + dy * dy);
        }

        double perimeter = cumLen[ptCount - 1];
        if (perimeter < 1.0) { offset += ptCount; continue; }
        int sampleCount = std::max(10, (int)(perimeter / spacing));

        for (int s = 0; s < sampleCount && totalPts < maxPoints; s++) {
            double targetLen = perimeter * s / sampleCount;
            int lo = 0, hi = ptCount - 1;
            while (lo < hi - 1) {
                int mid = (lo + hi) / 2;
                if (cumLen[mid] <= targetLen) lo = mid;
                else hi = mid;
            }
            double segLen = cumLen[hi] - cumLen[lo];
            double t = (segLen > 1e-6) ? (targetLen - cumLen[lo]) / segLen : 0.0;
            int px = (int)std::round(flatX[offset + lo] + t * (flatX[offset + hi] - flatX[offset + lo]));
            int py = (int)std::round(flatY[offset + lo] + t * (flatY[offset + hi] - flatY[offset + lo]));
            if (px >= 0 && px < width && py >= 0 && py < height) {
                outPoints[totalPts].x = (double)px;
                outPoints[totalPts].y = (double)py;
                outBarIds[totalPts] = b;
                totalPts++;
            }
        }
        LOG_INFO("  Bar %d: contour=%d pts, perimeter=%.1f, sampled=%d (spacing=%.1f)",
                 b, ptCount, perimeter, sampleCount, spacing);
        offset += ptCount;
    }
    LOG_INFO("SampleContoursFromPoints: total %d points from %d bars", totalPts, takeBars);
    return totalPts;
}


// 7.5 Shape fitting: group by barId, fit each bar with stadium shape or approxPolyDP
// fitMode: 0=stadium(默认), 1=approxPolyDP多边形拟合
// approxEpsilon: fitMode=1时有效, approxPolyDP的epsilon参数, 0=自适应(弧长的2%)
void Step_FitShape(std::vector<cv::Point>* allPoints, std::vector<int>* allBarIds,
                   int width, int height, int fitMode, double approxEpsilon) {
    if (allPoints->empty() || allBarIds->size() != allPoints->size()) return;
    int maxBarId = 0;
    for (size_t i = 0; i < allBarIds->size(); i++) {
        if ((*allBarIds)[i] > maxBarId) maxBarId = (*allBarIds)[i];
    }
    std::vector<std::vector<int>> barGroups(maxBarId + 1);
    for (size_t i = 0; i < allBarIds->size(); i++) {
        barGroups[(*allBarIds)[i]].push_back((int)i);
    }
    int fittedBars = 0;
    for (int b = 0; b <= maxBarId; b++) {
        if ((int)barGroups[b].size() < 8) continue;

        if (fitMode == 1) {
            // ===== 曲率去噪：删除局部曲率异常大的点 =====
            // 原理：密集等弧长采样点上，噪声会导致局部曲率突变。
            //       用 Menger 曲率计算每个点的局部曲率，
            //       MAD 自适应阈值筛选偏离点。
            std::vector<cv::Point> barPts(barGroups[b].size());
            for (size_t k = 0; k < barGroups[b].size(); k++) {
                int idx = barGroups[b][k];
                barPts[k] = (*allPoints)[idx];
            }

            int N = (int)barPts.size();
            int k = 5;  // 邻域窗口大小

            if (N < 2 * k + 1) {
                LOG_WARN("Step_FitShape: bar %d too few points for curvature (%d), skipped",
                         b, N);
                continue;
            }

            // 1. 计算每个点的局部曲率（Menger curvature）
            std::vector<double> curv(N);
            for (int i = 0; i < N; i++) {
                cv::Point prev = barPts[((i - k) % N + N) % N];
                cv::Point curr = barPts[i];
                cv::Point next = barPts[(i + k) % N];

                double a = cv::norm(prev - curr);
                double bLen = cv::norm(curr - next);
                double c = cv::norm(prev - next);

                double s = (a + bLen + c) / 2.0;
                double areaSq = s * (s - a) * (s - bLen) * (s - c);
                double area = (areaSq > 0) ? sqrt(areaSq) : 0.0;
                double denom = a * bLen * c;
                curv[i] = (denom > 1e-12) ? 2.0 * area / denom : 0.0;
            }

            // 2. 自适应阈值（MAD：绝对中位差，对异常值鲁棒）
            std::vector<double> sortedCurv = curv;
            std::sort(sortedCurv.begin(), sortedCurv.end());
            double median = sortedCurv[N / 2];

            std::vector<double> absDev(N);
            for (int i = 0; i < N; i++) absDev[i] = fabs(curv[i] - median);
            std::sort(absDev.begin(), absDev.end());
            double mad = absDev[N / 2];

            double sensitivity = 4.0;  // 越小越激进删除
            double threshold = median + sensitivity * std::max(mad, 1e-12);

            LOG_INFO("Step_FitShape: bar %d curvature: N=%d, median=%.6f, mad=%.6f, threshold=%.6f",
                     b, N, median, mad, threshold);

            // 3. 标记删除（连续高曲率段保护：连续 > 2 个高曲率点视为真实特征）
            std::vector<bool> remove(N, false);
            for (int i = 0; i < N; i++) {
                if (curv[i] <= threshold) continue;
                // 检查连续高曲率段长度
                int runLen = 0;
                for (int j = 0; j < 3; j++) {
                    int idx = (i + j) % N;
                    if (curv[idx] > threshold) runLen++;
                }
                if (runLen <= 2) remove[i] = true;
            }

            // 4. 紧缩保留的点
            int kept = 0;
            for (size_t ki = 0; ki < barGroups[b].size(); ki++) {
                if (!remove[ki]) {
                    int srcIdx = barGroups[b][ki];
                    int dstIdx = barGroups[b][kept];
                    if (srcIdx != dstIdx) {
                        (*allPoints)[dstIdx] = (*allPoints)[srcIdx];
                        (*allBarIds)[dstIdx] = (*allBarIds)[srcIdx];
                    }
                    kept++;
                }
            }
            barGroups[b].resize(kept);

            LOG_INFO("Step_FitShape: bar %d curvature filter: kept %d / %d points (sensitivity=%.1f)",
                     b, kept, N, sensitivity);
            fittedBars++;
        } else {
            // ===== stadium 跑道型拟合（默认） =====
            std::vector<cv::Point2d> barPts(barGroups[b].size());
            for (size_t k = 0; k < barGroups[b].size(); k++) {
                int idx = barGroups[b][k];
                barPts[k] = cv::Point2d((double)(*allPoints)[idx].x,
                                         (double)(*allPoints)[idx].y);
            }
            std::vector<cv::Point2d> fittedPts = fitStadiumShape(barPts);
            if ((int)fittedPts.size() != (int)barPts.size()) {
                LOG_WARN("Step_FitShape: bar %d size mismatch (%d vs %d), skipped",
                         b, (int)fittedPts.size(), (int)barPts.size());
                continue;
            }
            for (size_t k = 0; k < fittedPts.size(); k++) {
                int idx = barGroups[b][k];
                int px = (int)std::round(fittedPts[k].x);
                int py = (int)std::round(fittedPts[k].y);
                if (px < 0) px = 0;
                if (px >= width) px = width - 1;
                if (py < 0) py = 0;
                if (py >= height) py = height - 1;
                (*allPoints)[idx] = cv::Point(px, py);
            }
            fittedBars++;
        }
    }
    const char* modeStr = (fitMode == 1) ? "curvature" : "stadium";
    LOG_INFO("Step_FitShape: fitted %d / %d bars (%d total points), mode=%s",
             fittedBars, maxBarId + 1, (int)allPoints->size(), modeStr);
}

// 8. Mask验证采样点
void Step_VerifyByMask(std::vector<cv::Point>* allPoints, std::vector<int>* allBarIds,
                       cv::Mat* darkBinary, int width, int height) {
    int keptCount = 0;
    for (size_t i = 0; i < allPoints->size(); i++) {
        int px = (*allPoints)[i].x;
        int py = (*allPoints)[i].y;
        if (px >= 0 && px < width && py >= 0 && py < height) {
            if (darkBinary->at<unsigned char>(py, px) != 0) {
                (*allPoints)[keptCount] = (*allPoints)[i];
                (*allBarIds)[keptCount] = (*allBarIds)[i];
                keptCount++;
            }
        }
    }
    allPoints->resize(keptCount);
    allBarIds->resize(keptCount);
}

// 9. 去重 + 按Y/X排序
void Step_DeduplicateAndSort(std::vector<cv::Point>* points, std::vector<int>* barIds) {
    std::vector<cv::Point> uniquePoints;
    std::vector<int> uniqueBarIds;
    std::vector<bool> kept(points->size(), false);
    
    for (size_t i = 0; i < points->size(); i++) {
        if (kept[i]) continue;
        uniquePoints.push_back((*points)[i]);
        uniqueBarIds.push_back((*barIds)[i]);
        int x1 = (*points)[i].x - 1, x2 = (*points)[i].x + 1;
        int y1 = (*points)[i].y - 1, y2 = (*points)[i].y + 1;
        for (size_t j = i + 1; j < points->size(); j++) {
            if (!kept[j]) {
                int px = (*points)[j].x, py = (*points)[j].y;
                if (px >= x1 && px <= x2 && py >= y1 && py <= y2) {
                    kept[j] = true;
                }
            }
        }
    }
    
    if (uniquePoints.size() > 1) {
        std::vector<size_t> indices(uniquePoints.size());
        for (size_t i = 0; i < indices.size(); i++) indices[i] = i;
        std::sort(indices.begin(), indices.end(), [&uniquePoints](size_t a, size_t b) {
            if (abs(uniquePoints[a].y - uniquePoints[b].y) > 2) return uniquePoints[a].y < uniquePoints[b].y;
            return uniquePoints[a].x < uniquePoints[b].x;
        });
        std::vector<cv::Point> sortedPts(uniquePoints.size());
        std::vector<int> sortedIds(uniqueBarIds.size());
        for (size_t i = 0; i < indices.size(); i++) {
            sortedPts[i] = uniquePoints[indices[i]];
            sortedIds[i] = uniqueBarIds[indices[i]];
        }
        uniquePoints = sortedPts;
        uniqueBarIds = sortedIds;
    }
    
    *points = uniquePoints;
    *barIds = uniqueBarIds;
}

// 10. 转换为轨迹点输出
void Step_ConvertToOutput(std::vector<cv::Point>* uniquePoints, 
                          std::vector<int>* uniqueBarIds,
                          Point2D* trajPixels, int* count, int* stepBarIds) {
    *count = std::min((int)uniquePoints->size(), CALIB_MAX_TRAJ_POINTS);
    for (int i = 0; i < *count; i++) {
        trajPixels[i].x = (float)(*uniquePoints)[i].x;
        trajPixels[i].y = (float)(*uniquePoints)[i].y;
        if (stepBarIds) stepBarIds[i] = (*uniqueBarIds)[i];
    }
}

// 11. 绘制彩色轨迹
void Step_DrawColoredTrajectory(Point2D* trajPixels, int count, int* stepBarIds,
                                 int width, int height, unsigned char* outputData) {
    int dstRowSize = width * 3;
    memset(outputData, 0, dstRowSize * height);
    
    for (int i = 0; i < count; i++) {
        int barIdx = stepBarIds ? stepBarIds[i] % 16 : 0;
        const unsigned char* color = BAR_COLORS[barIdx];
        int cx = (int)trajPixels[i].x;
        int cy = (int)trajPixels[i].y;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && x < width && y >= 0 && y < height) {
                    int off = y * dstRowSize + x * 3;
                    outputData[off]     = color[0];  // B
                    outputData[off + 1] = color[1];  // G
                    outputData[off + 2] = color[2];  // R
                }
            }
        }
    }
}

// ========== 空洞轨迹检测（新方案） ==========

void DetectHollowTrajectory(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount,
                            int blurKsize, int morphKernelSize,
                            int targetHollows, int bandWidth,
                            bool useContourMode, int outerExpandPixels,
                            double grayMergeRatio,
                            int hollowGrayLow, int hollowGrayHigh) {
    LOG_INFO("=== Starting Hollow Trajectory Detection ===");

    if (!img || !img->data) {
        LOG_ERROR("Invalid image data!");
        *count = 0;
        return;
    }

    // 释放之前的步骤图
    if (stepImages) {
        for (int i = 0; i < stepImageCount; i++) {
            if (stepImages[i].data) { free(stepImages[i].data); stepImages[i].data = NULL; }
            stepImages[i] = {0};
        }
    }

    int w = img->width, h = img->height;

    // ---- 转灰度 ----
    Mat grayMat;
    if (img->channels == 1) {
        int pitch = ((w + 3) / 4) * 4;
        Mat tmp(h, w, CV_8UC1, img->data, pitch);
        grayMat = tmp.clone();
        tmp.release();
    } else {
        int srcRS = ((w * img->channels + 3) / 4) * 4;
        Mat colorMat(h, w, CV_8UC3, img->data, srcRS);
        cvtColor(colorMat, grayMat, COLOR_BGR2GRAY);
        colorMat.release();
    }

    // ---- 步骤图 1：灰度图 ----
    if (stepImages && stepImageCount >= 1) {
        stepImages[0].width = w; stepImages[0].height = h; stepImages[0].channels = 1;
        stepImages[0].data = (unsigned char*)malloc(w * h);
        memcpy(stepImages[0].data, grayMat.data, w * h);
    }

    // ---- 步骤 1：高斯模糊 + OTSU 二值化 ----
    if (blurKsize % 2 == 0) blurKsize++;
    if (blurKsize < 3) blurKsize = 3;
    if (morphKernelSize % 2 == 0) morphKernelSize++;
    if (morphKernelSize < 3) morphKernelSize = 3;

    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(blurKsize, blurKsize), 1.5);

    Mat binaryBright;
    threshold(grayMat, binaryBright, 0, 255, THRESH_BINARY + THRESH_OTSU);

    // ---- 提取最大外轮廓 → 工件 mask ----
    std::vector<std::vector<Point>> outerContours;
    findContours(binaryBright, outerContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    if (outerContours.empty()) {
        LOG_ERROR("No workpiece contour found!");
        *count = 0;
        return;
    }

    int maxOuterIdx = 0;
    double maxOuterArea = 0.0;
    for (size_t i = 0; i < outerContours.size(); i++) {
        double a = contourArea(outerContours[i]);
        if (a > maxOuterArea) { maxOuterArea = a; maxOuterIdx = (int)i; }
    }

    Mat mask = Mat::zeros(h, w, CV_8UC1);
    drawContours(mask, outerContours, maxOuterIdx, Scalar(255), FILLED);

    // ---- 步骤图 3：mask ----
    if (stepImages && stepImageCount >= 3) {
        stepImages[2].width = w; stepImages[2].height = h; stepImages[2].channels = 1;
        stepImages[2].data = (unsigned char*)malloc(w * h);
        memcpy(stepImages[2].data, mask.data, w * h);
    }

    // ---- 步骤 2：在 mask 内部反求空洞 ----
    // 重新用 hollowGrayLow-hollowGrayHigh 固定阈值对 mask 内区域二值化（而非 OTSU），
    // 更精确匹配原图中目标区域的灰度特征（灰度 8-80）
    Mat innerRegion = grayMat.clone();
    innerRegion.setTo(255, mask == 0);  // mask 外设为白色

    // hollowGrayLow <= gray <= hollowGrayHigh → 255（目标轮廓），其余 → 0（背景）
    Mat binaryDark;
    binaryDark = (innerRegion >= hollowGrayLow) & (innerRegion <= hollowGrayHigh);
    LOG_INFO("Hollow gray range: [%d, %d]", hollowGrayLow, hollowGrayHigh);
    // mask 外区域也置 0
    binaryDark.setTo(0, mask == 0);
    innerRegion.release();

    // 形态学处理：先闭后开 + 先开后闭
    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(morphKernelSize, morphKernelSize));
    Mat morphed;
    morphologyEx(binaryDark, morphed, MORPH_OPEN, kernel);
    morphologyEx(morphed, morphed, MORPH_OPEN, kernel);
    morphologyEx(morphed, morphed, MORPH_CLOSE, kernel);
    morphologyEx(morphed, morphed, MORPH_CLOSE, kernel);
 
    // ---- 步骤图 2：固定阈值二值化（用于空洞检测） ----
    if (stepImages && stepImageCount >= 2) {
        stepImages[1].width = w; stepImages[1].height = h; stepImages[1].channels = 1;
        stepImages[1].data = (unsigned char*)malloc(w * h);
        memcpy(stepImages[1].data, morphed.data, w * h);
    }

    // binaryDark 中 hollowGrayLow~hollowGrayHigh 区域为 255（目标），直接取外轮廓即为空洞
    std::vector<std::vector<Point>> hierarchyContours;
    findContours(morphed, hierarchyContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    // 按面积降序排列，取前 targetHollows 个
    std::vector<std::pair<double, int>> sortedHollows;
    for (size_t i = 0; i < hierarchyContours.size(); i++) {
        double area = contourArea(hierarchyContours[i]);
        if (area > 100) {  // 过滤极小噪声
            sortedHollows.push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(sortedHollows.begin(), sortedHollows.end(),
              [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
                  return a.first > b.first;
              });

    int hollowCount = (int)sortedHollows.size();
    int takeHollows = std::min(hollowCount, std::min(targetHollows, CALIB_MAX_BARS));
    LOG_INFO("Hollows found: %d (taking top %d)", hollowCount, takeHollows);

    if (takeHollows == 0) {
        LOG_ERROR("No hollows found inside workpiece!");
        *count = 0;
        return;
    }

    // ---- 步骤 3：构建空洞二值图 ----
    Mat hollowBinary = Mat::zeros(h, w, CV_8UC1);
    for (int k = 0; k < takeHollows; k++) {
        int idx = sortedHollows[k].second;
        drawContours(hollowBinary, hierarchyContours, idx, Scalar(255), FILLED);
    }

    // 限制空洞在 mask 内
    hollowBinary.setTo(0, mask == 0);

    binaryDark.release();

    // ---- 步骤图 4：空洞二值图（合并后） ----
    if (stepImages && stepImageCount >= 4) {
        stepImages[3].width = w; stepImages[3].height = h; stepImages[3].channels = 1;
        stepImages[3].data = (unsigned char*)malloc(w * h);
        memcpy(stepImages[3].data, hollowBinary.data, w * h);
    }

    // ---- 步骤图 5：彩色空洞标注（每个空洞不同颜色） ----
    if (stepImages && stepImageCount >= 5) {
        Mat colorHollow = Mat::zeros(h, w, CV_8UC3);
        for (int k = 0; k < takeHollows; k++) {
            int idx = sortedHollows[k].second;
            int colorIdx = k % 16;
            const unsigned char* clr = BAR_COLORS[colorIdx];
            Scalar color(clr[0], clr[1], clr[2]);  // BGR
            drawContours(colorHollow, hierarchyContours, idx, color, FILLED);
            // 画白色边界线
            drawContours(colorHollow, hierarchyContours, idx, Scalar(255, 255, 255), 1);
        }
        // 限制在 mask 内
        Mat colorMask3;
        cvtColor(mask, colorMask3, COLOR_GRAY2BGR);
        colorHollow.setTo(Scalar(0, 0, 0), mask == 0);
        colorMask3.release();

        stepImages[4].width = w; stepImages[4].height = h; stepImages[4].channels = 3;
        int dataLen = w * h * 3;
        stepImages[4].data = (unsigned char*)malloc(dataLen);
        memcpy(stepImages[4].data, colorHollow.data, dataLen);
        colorHollow.release();
    }

    // ---- 步骤 5：形态学清理 ----
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(morphKernelSize, morphKernelSize));
    morphologyEx(hollowBinary, hollowBinary, MORPH_CLOSE, kernel2);
    morphologyEx(hollowBinary, hollowBinary, MORPH_OPEN, kernel2);
    morphologyEx(hollowBinary, hollowBinary, MORPH_OPEN, kernel2);
    morphologyEx(hollowBinary, hollowBinary, MORPH_CLOSE, kernel2);

    //// 膨胀+腐蚀让边缘光滑
    Mat dilateKernel = getStructuringElement(MORPH_ELLIPSE, Size(morphKernelSize + 2, morphKernelSize + 2));
    erode(hollowBinary, hollowBinary, dilateKernel);
    dilate(hollowBinary, hollowBinary, dilateKernel);

    dilateKernel.release();

    // 高斯模糊 + 重二值化
    //Mat blurredHollow;
    //GaussianBlur(hollowBinary, blurredHollow, Size(morphKernelSize + 4, morphKernelSize + 4), 2.0);
    //threshold(blurredHollow, hollowBinary, 128, 255, THRESH_BINARY);
    //blurredHollow.release();

    hollowBinary.setTo(0, mask == 0);  // 确保仍在 mask 内

    //// ---- 提取空洞轮廓并排序 ----
    std::vector<std::vector<Point>> hollowContours;
    findContours(hollowBinary, hollowContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    double areaThreshold = (double)(w * h) * 0.002;
    if (areaThreshold < 500.0) areaThreshold = 500.0;

    std::vector<std::pair<double, int>> sortedBars;
    for (size_t i = 0; i < hollowContours.size(); i++) {
        double area = contourArea(hollowContours[i]);
        if (area > areaThreshold) {
            sortedBars.push_back(std::make_pair(area, (int)i));
        }
    }
    std::sort(sortedBars.begin(), sortedBars.end(),
              [](const std::pair<double, int>& a, const std::pair<double, int>& b) {
                  return a.first > b.first;
              });

    int barCount = (int)sortedBars.size();
    int targetBars = std::min(barCount, CALIB_MAX_BARS);
    LOG_INFO("Hollow bars after morph: %d (taking top %d)", barCount, targetBars);

    if (targetBars == 0) {
        LOG_ERROR("No hollow bars after morphology!");
        *count = 0;
        return;
    }

    // ---- 等间距轮廓采样 ----
    std::vector<Point> allPoints;
    std::vector<int> allBarIds;

    Step_SampleContoursEquidistant(&hollowContours, &sortedBars, targetBars,
                                     w, h, &allPoints, &allBarIds,
                                     3.0);


    LOG_INFO("Contour sampling: %d hollow bars, raw points=%d", targetBars, (int)allPoints.size());

    //// ---- 拟合 ----
    //Step_FitShape(&allPoints, &allBarIds, w, h, 0, 0.0);

    // ---- Mask 验证 ----
    {
        int keptCount = 0;
        for (size_t i = 0; i < allPoints.size(); i++) {
            int px = allPoints[i].x, py = allPoints[i].y;
            if (px >= 0 && px < w && py >= 0 && py < h) {
                bool valid = (bandWidth > 0)
                    ? (mask.at<unsigned char>(py, px) != 0)
                    : (hollowBinary.at<unsigned char>(py, px) != 0);
                if (valid) {
                    allPoints[keptCount] = allPoints[i];
                    allBarIds[keptCount] = allBarIds[i];
                    keptCount++;
                }
            }
        }
        allPoints.resize(keptCount);
        allBarIds.resize(keptCount);
    }

    //// ---- 去重 + 排序 ----
    //Step_DeduplicateAndSort(&allPoints, &allBarIds);

    // ---- 等间距重采样到固定 1000 个点 ----
    {
        int targetCount = 1000;
        int rawCount = (int)allPoints.size();
        if (rawCount > targetCount && targetCount > 0) {
            // 计算累积弧长
            std::vector<double> cumLen(rawCount, 0.0);
            for (int i = 1; i < rawCount; i++) {
                double dx = (double)allPoints[i].x - allPoints[i-1].x;
                double dy = (double)allPoints[i].y - allPoints[i-1].y;
                cumLen[i] = cumLen[i-1] + sqrt(dx*dx + dy*dy);
            }
            double totalLen = cumLen[rawCount - 1];
            LOG_INFO("Resampling trajectory: %d -> %d points (total arc length=%.1f)", rawCount, targetCount, totalLen);

            std::vector<Point> resampled(targetCount);
            std::vector<int> resampledBarIds(targetCount);
            for (int i = 0; i < targetCount; i++) {
                double targetLen = totalLen * i / targetCount;
                // 二分查找
                int lo = 0, hi = rawCount - 1;
                while (lo < hi - 1) {
                    int mid = (lo + hi) / 2;
                    if (cumLen[mid] <= targetLen) lo = mid;
                    else hi = mid;
                }
                double segLen = cumLen[hi] - cumLen[lo];
                double t = (segLen > 0.001) ? (targetLen - cumLen[lo]) / segLen : 0.0;
                resampled[i].x = (int)(allPoints[lo].x + t * (allPoints[hi].x - allPoints[lo].x));
                resampled[i].y = (int)(allPoints[lo].y + t * (allPoints[hi].y - allPoints[lo].y));
                resampledBarIds[i] = allBarIds[lo];
            }
            allPoints = resampled;
            allBarIds = resampledBarIds;
        }
        else if (rawCount < targetCount && rawCount > 1) {
            // 点数不足 1000 时，不强制补充，保持原始点数
            LOG_INFO("Trajectory has only %d points (< target %d), keeping original", rawCount, targetCount);
        }
    }

    // ---- 输出 ----
    int outCount = 0;
    Step_ConvertToOutput(&allPoints, &allBarIds, trajPixels, &outCount, stepBarIds);
    *count = outCount;

    LOG_INFO("Hollow trajectory detection complete: %d points", outCount);

    // 释放临时 Mat
    //blurred.release();
    binaryBright.release();
    //morphed.release();
    //kernel.release();
    mask.release();
    hollowBinary.release();
    kernel2.release();
}

void DetectTrajectoryFitShape(Image* img, Point2D* trajPixels, int* count) {
    LOG_INFO("=== FitShape Trajectory (deprecated, using OpenCV) ===");
    DetectTrajectoryOpenCV(img, trajPixels, count, NULL, NULL, 0);
}

void DrawTrajectoryColored(Image* img, Point2D* trajPixels, int count, int* barIds) {
    if (!img || !img->data || count < 2) return;
    if (img->channels < 3) return;
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

void DrawTrajectoryGrayscale(Image* img, Point2D* trajPixels, int count, int grayValue) {
    if (!img || !img->data || count < 1) return;
    if (img->channels != 1) return;

    int rowSize = ((img->width * img->channels + 3) / 4) * 4;
    unsigned char gray = (unsigned char)(grayValue < 0 ? 0 : (grayValue > 255 ? 255 : grayValue));

    for (int i = 0; i < count; i++) {
        int cx = (int)trajPixels[i].x;
        int cy = (int)trajPixels[i].y;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && x < img->width && y >= 0 && y < img->height) {
                    int off = y * rowSize + x;
                    img->data[off] = gray;
                }
            }
        }
    }
}

// ========== Chessboard / camera intrinsics ==========

namespace {

static void SplitSemicolonPaths(const char* delimStr, std::vector<std::string>& out) {
    out.clear();
    if (!delimStr) return;
    std::string s(delimStr);
    size_t start = 0;
    while (start < s.size()) {
        size_t semi = s.find(';', start);
        std::string part = (semi == std::string::npos) ? s.substr(start) : s.substr(start, semi - start);
        while (!part.empty() && (part.front() == ' ' || part.front() == '\t')) part.erase(part.begin());
        while (!part.empty() && (part.back() == ' ' || part.back() == '\t')) part.pop_back();
        if (!part.empty()) out.push_back(part);
        if (semi == std::string::npos) break;
        start = semi + 1;
    }
}

// cornerPreprocessMode: 0=auto（原图→CLAHE→轻模糊+CLAHE→SB 回退）, 1=none, 2=clahe
static constexpr int kChessboardPreprocessAuto = 0;
static constexpr int kChessboardPreprocessNone = 1;
static constexpr int kChessboardPreprocessClahe = 2;

static cv::Mat MakeClaheGray(const cv::Mat& gray, double clipLimit, int tileSize) {
    double clip = clipLimit > 0.0 ? clipLimit : 2.5;
    int tgs = tileSize > 0 ? tileSize : 8;
    cv::Mat out;
    cv::Ptr<cv::CLAHE> clahe = cv::createCLAHE(clip, cv::Size(tgs, tgs));
    clahe->apply(gray, out);
    return out;
}

static bool FindChessboardCornersClassic(const cv::Mat& gray, const cv::Size& pattern,
    std::vector<cv::Point2f>& corners, int fastCheck) {
    int flags = cv::CALIB_CB_ADAPTIVE_THRESH | cv::CALIB_CB_NORMALIZE_IMAGE;
    if (fastCheck) flags |= cv::CALIB_CB_FAST_CHECK;
    corners.clear();
    return cv::findChessboardCorners(gray, pattern, corners, flags);
}

static bool FindChessboardCornersSb(const cv::Mat& gray, const cv::Size& pattern,
    std::vector<cv::Point2f>& corners) {
#if CV_VERSION_MAJOR >= 4
    corners.clear();
    const int flags = cv::CALIB_CB_NORMALIZE_IMAGE | cv::CALIB_CB_EXHAUSTIVE;
    return cv::findChessboardCornersSB(gray, pattern, corners, flags);
#else
    (void)gray;
    (void)pattern;
    corners.clear();
    return false;
#endif
}

static bool TryFindChessboardOnImage(const cv::Mat& gray, const cv::Size& pattern,
    std::vector<cv::Point2f>& corners, int fastCheck,
    int cornerPreprocessMode, double claheClipLimit, int claheTileSize,
    cv::Mat& outRefineGray) {
    const int need = pattern.width * pattern.height;

    struct Attempt {
        cv::Mat image;
        bool classic;
        bool fast;
    };
    std::vector<Attempt> attempts;
    attempts.reserve(6);

    auto pushClassic = [&](const cv::Mat& img, bool fast) {
        attempts.push_back({ img, true, fast });
    };
    auto pushSb = [&](const cv::Mat& img) {
        attempts.push_back({ img, false, false });
    };

    if (cornerPreprocessMode == kChessboardPreprocessNone) {
        pushClassic(gray, fastCheck != 0);
    } else if (cornerPreprocessMode == kChessboardPreprocessClahe) {
        pushClassic(MakeClaheGray(gray, claheClipLimit, claheTileSize), false);
    } else {
        pushClassic(gray, false);
        pushClassic(MakeClaheGray(gray, claheClipLimit, claheTileSize), false);
        cv::Mat blurred;
        cv::GaussianBlur(gray, blurred, cv::Size(3, 3), 0.0);
        pushClassic(MakeClaheGray(blurred, claheClipLimit, claheTileSize), false);
        pushSb(gray);
        pushSb(MakeClaheGray(gray, claheClipLimit, claheTileSize));
    }

    for (const auto& att : attempts) {
        if (att.image.empty())
            continue;
        bool ok = att.classic
            ? FindChessboardCornersClassic(att.image, pattern, corners, att.fast ? 1 : 0)
            : FindChessboardCornersSb(att.image, pattern, corners);
        if (ok && (int)corners.size() == need) {
            outRefineGray = att.image;
            return true;
        }
    }
    return false;
}

static int FindChessboardCornersGrayMat(const cv::Mat& gray, int boardCols, int boardRows,
    std::vector<cv::Point2f>& corners, int refineSubPix, int fastCheck,
    int cornerPreprocessMode = kChessboardPreprocessAuto,
    double claheClipLimit = 2.5, int claheTileSize = 8) {
    if (gray.empty() || gray.type() != CV_8UC1 || boardCols < 2 || boardRows < 2) return -1;
    const cv::Size pattern(boardCols, boardRows);
    const int cornerCount = boardCols * boardRows;
    const double estSqPx = std::min((double)gray.cols / boardCols, (double)gray.rows / boardRows);
    // 小方格（如 0.5 mm 仅占 4~5 px）须放大后再检；超大点数棋盘限制放大倍数以免耗时过长
    const int maxScale = (cornerCount > 2000) ? 4 : 8;
    int startScale = 1;
    while (estSqPx * startScale < 12.0 && startScale < maxScale)
        startScale *= 2;

    bool found = false;
    int usedScale = 1;
    cv::Mat refineGray;
    for (int tryScale = startScale; tryScale <= maxScale && !found; tryScale *= 2) {
        cv::Mat work;
        if (tryScale > 1)
            cv::resize(gray, work, cv::Size(), tryScale, tryScale, cv::INTER_CUBIC);
        else
            work = gray;
        found = TryFindChessboardOnImage(work, pattern, corners, fastCheck,
            cornerPreprocessMode, claheClipLimit, claheTileSize, refineGray);
        if (found)
            usedScale = tryScale;
    }
    if (!found)
        return 1;

    if (usedScale > 1) {
        const float inv = 1.0f / (float)usedScale;
        for (auto& p : corners) {
            p.x *= inv;
            p.y *= inv;
        }
        refineGray = gray;
    }

    if (refineSubPix) {
        int win = (int)std::max(3.0, std::min(11.0, estSqPx * 0.75));
        if (win % 2 == 0) --win;
        cv::TermCriteria criteria(cv::TermCriteria::EPS + cv::TermCriteria::COUNT, 30, 0.001);
        cv::cornerSubPix(refineGray, corners, cv::Size(win, win), cv::Size(-1, -1), criteria);
    }
    return 0;
}

} // namespace

int FindChessboardCornersGrayBuffer(const unsigned char* grayRowMajor, int width, int height, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck,
    int cornerPreprocessMode, double claheClipLimit, int claheTileSize) {
    if (!grayRowMajor || width <= 0 || height <= 0 || !outCount || maxPts < boardCols * boardRows) return -1;
    cv::Mat gray(height, width, CV_8UC1, const_cast<unsigned char*>(grayRowMajor));
    cv::Mat grayClone = gray.clone();
    std::vector<cv::Point2f> corners;
    int rc = FindChessboardCornersGrayMat(grayClone, boardCols, boardRows, corners, refineSubPix, fastCheck,
        cornerPreprocessMode, claheClipLimit, claheTileSize);
    if (rc != 0) {
        *outCount = 0;
        return rc;
    }
    int n = (int)corners.size();
    *outCount = n;
    if (outPts && n <= maxPts) {
        for (int i = 0; i < n; ++i) {
            outPts[i].x = corners[i].x;
            outPts[i].y = corners[i].y;
        }
    }
    return 0;
}

int FindChessboardCorners(Image* img, int boardCols, int boardRows,
    Point2D* outPts, int* outCount, int maxPts, int refineSubPix, int fastCheck,
    int cornerPreprocessMode, double claheClipLimit, int claheTileSize) {
    if (!img || !img->data || !outCount || boardCols < 2 || boardRows < 2 || maxPts < boardCols * boardRows) return -1;
    cv::Mat gray;
    Step_ConvertToGrayscale(img, &gray);
    std::vector<cv::Point2f> corners;
    int rc = FindChessboardCornersGrayMat(gray, boardCols, boardRows, corners, refineSubPix, fastCheck,
        cornerPreprocessMode, claheClipLimit, claheTileSize);
    if (rc != 0) {
        *outCount = 0;
        return rc;
    }
    int n = (int)corners.size();
    *outCount = n;
    if (outPts && n <= maxPts) {
        for (int i = 0; i < n; ++i) {
            outPts[i].x = corners[i].x;
            outPts[i].y = corners[i].y;
        }
    }
    return 0;
}

void DrawChessboardCorners(Image* img, Point2D* pts, int count, int boardCols, int boardRows) {
    if (!img || !img->data || count < 1 || boardCols < 2 || boardRows < 2) return;
    cv::Mat bgr;
    if (img->channels == 1) {
        cv::Mat gray;
        Step_ConvertToGrayscale(img, &gray);
        cvtColor(gray, bgr, COLOR_GRAY2BGR);
    } else {
        int w = img->width, h = img->height;
        int srcRS = ((w * 3 + 3) / 4) * 4;
        cv::Mat colorMat(h, w, CV_8UC3, img->data, srcRS);
        bgr = colorMat.clone();
    }
    std::vector<cv::Point2f> cvPts;
    cvPts.reserve(count);
    for (int i = 0; i < count; ++i)
        cvPts.emplace_back((float)pts[i].x, (float)pts[i].y);
    bool patternFound = (count == boardCols * boardRows);
    cv::drawChessboardCorners(bgr, cv::Size(boardCols, boardRows), cvPts, patternFound);
    MatToImageBGR(bgr, img);
}

namespace {

static void AppendEscapedJsonString(std::string& dst, const std::string& s) {
    dst.push_back('"');
    for (unsigned char c : s) {
        if (c == '\\' || c == '"') {
            dst.push_back('\\');
            dst.push_back((char)c);
        } else if (c == '\n')
            dst += "\\n";
        else if (c == '\r')
            dst += "\\r";
        else if (c == '\t')
            dst += "\\t";
        else
            dst.push_back((char)c);
    }
    dst.push_back('"');
}

static void AppendG(std::string& dst, double x) {
    char b[96];
    snprintf(b, sizeof(b), "%.9g", x);
    dst += b;
}

static void AppendG(std::string& dst, int x) {
    dst += std::to_string(x);
}

static double ComputeReprojRms(const std::vector<cv::Point3f>& objPts,
    const std::vector<cv::Point2f>& imgPts,
    const cv::Mat& rvec, const cv::Mat& tvec,
    const cv::Mat& cameraMatrix, const cv::Mat& distCoeffs) {
    if (objPts.empty() || objPts.size() != imgPts.size())
        return 0.0;
    std::vector<cv::Point2f> projected;
    cv::projectPoints(objPts, rvec, tvec, cameraMatrix, distCoeffs, projected);
    double sumSq = 0.0;
    for (size_t i = 0; i < imgPts.size(); ++i) {
        double dx = (double)imgPts[i].x - (double)projected[i].x;
        double dy = (double)imgPts[i].y - (double)projected[i].y;
        sumSq += dx * dx + dy * dy;
    }
    return std::sqrt(sumSq / (double)imgPts.size());
}

} // namespace

int CalibrateCameraChessboardMultiview(const char* pathsDelimited, int boardCols, int boardRows, double squareSize,
    double* outFx, double* outFy, double* outCx, double* outCy,
    double* outK1, double* outK2, double* outP1, double* outP2, double* outK3,
    double* outRms,
    char* fullCalibrationJsonOut, int fullCalibrationJsonOutSize,
    int cornerPreprocessMode, double claheClipLimit, int claheTileSize) {
    if (!pathsDelimited || boardCols < 2 || boardRows < 2 || squareSize <= 0.0) return -1;

    std::vector<std::string> paths;
    SplitSemicolonPaths(pathsDelimited, paths);
    if (paths.empty()) return -2;

    std::vector<cv::Point3f> templateObj;
    templateObj.reserve(boardCols * boardRows);
    for (int r = 0; r < boardRows; ++r)
        for (int c = 0; c < boardCols; ++c)
            templateObj.emplace_back((float)(c * squareSize), (float)(r * squareSize), 0.f);

    std::vector<std::vector<cv::Point3f>> objPtsVec;
    std::vector<std::vector<cv::Point2f>> imgPtsVec;
    std::vector<std::string> usedPaths;
    std::vector<std::string> failedPaths;
    cv::Size imageSize;

    for (const auto& path : paths) {
        cv::Mat img = cv::imread(path, cv::IMREAD_COLOR);
        if (img.empty()) {
            failedPaths.push_back(path);
            continue;
        }
        cv::Mat gray;
        cv::cvtColor(img, gray, cv::COLOR_BGR2GRAY);
        std::vector<cv::Point2f> corners;
        if (FindChessboardCornersGrayMat(gray, boardCols, boardRows, corners, 1, 0,
                cornerPreprocessMode, claheClipLimit, claheTileSize) != 0) {
            failedPaths.push_back(path);
            continue;
        }
        if ((int)corners.size() != boardCols * boardRows) {
            failedPaths.push_back(path);
            continue;
        }
        usedPaths.push_back(path);
        imgPtsVec.push_back(corners);
        objPtsVec.push_back(templateObj);
        imageSize = gray.size();
    }

    if (imgPtsVec.size() < 3) return -3;

    cv::Mat cameraMatrix = cv::Mat::eye(3, 3, CV_64F);
    cv::Mat distCoeffs = cv::Mat::zeros(5, 1, CV_64F);
    std::vector<cv::Mat> rvecs, tvecs;
    int flags = cv::CALIB_FIX_K4 | cv::CALIB_FIX_K5 | cv::CALIB_FIX_K6;
    double rms = cv::calibrateCamera(objPtsVec, imgPtsVec, imageSize, cameraMatrix, distCoeffs, rvecs, tvecs, flags);

    double fx = cameraMatrix.at<double>(0, 0);
    double fy = cameraMatrix.at<double>(1, 1);
    double cx = cameraMatrix.at<double>(0, 2);
    double cy = cameraMatrix.at<double>(1, 2);
    double k1 = distCoeffs.at<double>(0);
    double k2 = distCoeffs.rows > 1 ? distCoeffs.at<double>(1) : 0.0;
    double p1 = distCoeffs.rows > 2 ? distCoeffs.at<double>(2) : 0.0;
    double p2 = distCoeffs.rows > 3 ? distCoeffs.at<double>(3) : 0.0;
    double k3 = distCoeffs.rows > 4 ? distCoeffs.at<double>(4) : 0.0;

    if (outFx) *outFx = fx;
    if (outFy) *outFy = fy;
    if (outCx) *outCx = cx;
    if (outCy) *outCy = cy;
    if (outK1) *outK1 = k1;
    if (outK2) *outK2 = k2;
    if (outP1) *outP1 = p1;
    if (outP2) *outP2 = p2;
    if (outK3) *outK3 = k3;
    if (outRms) *outRms = rms;

    if (fullCalibrationJsonOut && fullCalibrationJsonOutSize > 0) {
        std::string j;
        j.reserve(4096 + usedPaths.size() * 256);
        j += "{\"intrinsics\":{";
        j += "\"fx\":"; AppendG(j, fx);
        j += ",\"fy\":"; AppendG(j, fy);
        j += ",\"cx\":"; AppendG(j, cx);
        j += ",\"cy\":"; AppendG(j, cy);
        j += ",\"k1\":"; AppendG(j, k1);
        j += ",\"k2\":"; AppendG(j, k2);
        j += ",\"p1\":"; AppendG(j, p1);
        j += ",\"p2\":"; AppendG(j, p2);
        j += ",\"k3\":"; AppendG(j, k3);
        j += ",\"rms\":"; AppendG(j, rms);
        j += "},\"extrinsicsPerView\":[";
        for (size_t i = 0; i < rvecs.size(); ++i) {
            if (i) j += ',';
            j += "{\"imagePath\":";
            AppendEscapedJsonString(j, usedPaths[i]);
            const cv::Mat& rv = rvecs[i];
            const cv::Mat& tv = tvecs[i];
            double viewRms = ComputeReprojRms(objPtsVec[i], imgPtsVec[i], rv, tv, cameraMatrix, distCoeffs);
            j += ",\"reprojRms\":";
            AppendG(j, viewRms);
            cv::Point3f boardCenter(
                (float)((boardCols - 1) * squareSize * 0.5),
                (float)((boardRows - 1) * squareSize * 0.5),
                0.f);
            std::vector<cv::Point3f> boardCenterObj = { boardCenter };
            std::vector<cv::Point2f> boardCenterImg;
            cv::projectPoints(boardCenterObj, rv, tv, cameraMatrix, distCoeffs, boardCenterImg);
            j += ",\"boardCenterPx\":[";
            AppendG(j, boardCenterImg[0].x);
            j += ',';
            AppendG(j, boardCenterImg[0].y);
            j += "]";
            cv::Mat Rview;
            cv::Rodrigues(rv, Rview);
            double nz = Rview.at<double>(2, 2);
            double clamped = nz;
            if (clamped > 1.0) clamped = 1.0;
            if (clamped < -1.0) clamped = -1.0;
            double tiltDeg = std::acos(std::abs(clamped)) * 180.0 / CV_PI;
            j += ",\"tiltDeg\":";
            AppendG(j, tiltDeg);
            j += ",\"rvec\":[";
            AppendG(j, rv.at<double>(0));
            j += ',';
            AppendG(j, rv.at<double>(1));
            j += ',';
            AppendG(j, rv.at<double>(2));
            j += "],\"tvec\":[";
            AppendG(j, tv.at<double>(0));
            j += ',';
            AppendG(j, tv.at<double>(1));
            j += ',';
            AppendG(j, tv.at<double>(2));
            j += "]}";
        }
            j += "],\"boardSpec\":{\"cols\":";
        AppendG(j, boardCols);
        j += ",\"rows\":";
        AppendG(j, boardRows);
        j += ",\"squareSizeMm\":";
        AppendG(j, squareSize);
        j += "},\"imageSize\":{\"width\":";
        AppendG(j, imageSize.width);
        j += ",\"height\":";
        AppendG(j, imageSize.height);
        j += "},\"failedImagePaths\":[";
        for (size_t i = 0; i < failedPaths.size(); ++i) {
            if (i) j += ',';
            AppendEscapedJsonString(j, failedPaths[i]);
        }
        j += "],\"calibrationStats\":{\"attemptedCount\":";
        AppendG(j, (int)paths.size());
        j += ",\"successfulCount\":";
        AppendG(j, (int)usedPaths.size());
        j += ",\"failedCount\":";
        AppendG(j, (int)failedPaths.size());
        j += "}";
        j += ",\"convention\":\"OpenCV calibrateCamera: P_cam = R*P_board + t; board plane Z=0, units same as squareSize (e.g. mm).\"}";
        if ((int)j.size() + 1 > fullCalibrationJsonOutSize)
            fullCalibrationJsonOut[0] = '\0';
        else
            memcpy(fullCalibrationJsonOut, j.c_str(), j.size() + 1);
    }

    return 0;
}

static bool GrabDoubleInSpan(const std::string& span, const char* key, double* out) {
    std::string pat = std::string("\"") + key + "\":";
    size_t p = span.find(pat);
    if (p == std::string::npos)
        return false;
    p += pat.size();
    *out = std::strtod(span.c_str() + p, nullptr);
    return true;
}

/** @return 0 ok; -2 intrinsics; -3 extrinsics header; -4..-8 per-view parse */
int ParseCalibrationJsonForView(const std::string& json, int viewIndex,
    double* fx, double* fy, double* cx, double* cy,
    double* k1, double* k2, double* p1, double* p2, double* k3,
    double rvec[3], double tvec[3]) {
    size_t i0 = json.find("\"intrinsics\"");
    if (i0 == std::string::npos)
        return -2;
    size_t i1 = json.find("\"extrinsicsPerView\"", i0);
    if (i1 == std::string::npos)
        i1 = json.size();
    const std::string head = json.substr(i0, i1 - i0);
    if (!GrabDoubleInSpan(head, "fx", fx) || !GrabDoubleInSpan(head, "fy", fy) ||
        !GrabDoubleInSpan(head, "cx", cx) || !GrabDoubleInSpan(head, "cy", cy) ||
        !GrabDoubleInSpan(head, "k1", k1) || !GrabDoubleInSpan(head, "k2", k2) ||
        !GrabDoubleInSpan(head, "p1", p1) || !GrabDoubleInSpan(head, "p2", p2) ||
        !GrabDoubleInSpan(head, "k3", k3))
        return -2;

    size_t exPos = json.find("\"extrinsicsPerView\"");
    if (exPos == std::string::npos)
        return -3;
    size_t arr = json.find('[', exPos);
    if (arr == std::string::npos)
        return -3;
    size_t cursor = arr + 1;
    for (int vi = 0; vi <= viewIndex; ++vi) {
        size_t rvKey = json.find("\"rvec\":[", cursor);
        if (rvKey == std::string::npos)
            return -4;
        size_t rv = rvKey + 8;
        while (rv < json.size() && (json[rv] == ' ' || json[rv] == '\t'))
            rv++;
        int np = std::sscanf(json.c_str() + rv, "%lf,%lf,%lf", &rvec[0], &rvec[1], &rvec[2]);
        if (np != 3)
            return -5;
        size_t tvKey = json.find("\"tvec\":[", rv);
        if (tvKey == std::string::npos)
            return -6;
        size_t tv = tvKey + 8;
        while (tv < json.size() && (json[tv] == ' ' || json[tv] == '\t'))
            tv++;
        np = std::sscanf(json.c_str() + tv, "%lf,%lf,%lf", &tvec[0], &tvec[1], &tvec[2]);
        if (np != 3)
            return -7;
        size_t closeBracket = json.find(']', tv);
        if (closeBracket == std::string::npos)
            return -8;
        cursor = closeBracket + 1;
    }
    return 0;
}

struct ViewPoseD {
    double rvec[3];
    double tvec[3];
};

/** @return 0 ok; -3 extrinsics header; -5..-8 parse; -10 empty array */
static int ParseAllExtrinsicsPoses(const std::string& json, std::vector<ViewPoseD>& out) {
    out.clear();
    size_t exPos = json.find("\"extrinsicsPerView\"");
    if (exPos == std::string::npos)
        return -3;
    size_t arr = json.find('[', exPos);
    if (arr == std::string::npos)
        return -3;
    size_t cursor = arr + 1;
    for (;;) {
        size_t rvKey = json.find("\"rvec\":[", cursor);
        if (rvKey == std::string::npos)
            break;
        ViewPoseD p{};
        size_t rv = rvKey + 8;
        while (rv < json.size() && (json[rv] == ' ' || json[rv] == '\t'))
            rv++;
        if (std::sscanf(json.c_str() + rv, "%lf,%lf,%lf", &p.rvec[0], &p.rvec[1], &p.rvec[2]) != 3)
            return -5;
        size_t tvKey = json.find("\"tvec\":[", rv);
        if (tvKey == std::string::npos)
            return -6;
        size_t tv = tvKey + 8;
        while (tv < json.size() && (json[tv] == ' ' || json[tv] == '\t'))
            tv++;
        if (std::sscanf(json.c_str() + tv, "%lf,%lf,%lf", &p.tvec[0], &p.tvec[1], &p.tvec[2]) != 3)
            return -7;
        out.push_back(p);
        size_t closeBracket = json.find(']', tv);
        if (closeBracket == std::string::npos)
            return -8;
        cursor = closeBracket + 1;
    }
    if (out.empty())
        return -10;
    return 0;
}

static int ParseCalibrationJsonIntrinsicsOnly(const std::string& json,
    double* fx, double* fy, double* cx, double* cy,
    double* k1, double* k2, double* p1, double* p2, double* k3) {
    size_t i0 = json.find("\"intrinsics\"");
    if (i0 == std::string::npos)
        return -2;
    size_t i1 = json.find("\"extrinsicsPerView\"", i0);
    if (i1 == std::string::npos)
        i1 = json.size();
    const std::string head = json.substr(i0, i1 - i0);
    if (!GrabDoubleInSpan(head, "fx", fx) || !GrabDoubleInSpan(head, "fy", fy) ||
        !GrabDoubleInSpan(head, "cx", cx) || !GrabDoubleInSpan(head, "cy", cy) ||
        !GrabDoubleInSpan(head, "k1", k1) || !GrabDoubleInSpan(head, "k2", k2) ||
        !GrabDoubleInSpan(head, "p1", p1) || !GrabDoubleInSpan(head, "p2", p2) ||
        !GrabDoubleInSpan(head, "k3", k3))
        return -2;
    return 0;
}

/** viewIndex=-1：多视图平均旋转 + 棋盘中心落在光轴 (cx,cy) 的对称外参。 */
static int BuildOpticalAxisSymmetricPose(const std::vector<ViewPoseD>& views,
    int boardCols, int boardRows, double squareSizeMm,
    double rvecOut[3], double tvecOut[3]) {
    if (views.empty() || boardCols < 2 || boardRows < 2 || squareSizeMm <= 0.0)
        return -10;
    cv::Mat Rsum = cv::Mat::zeros(3, 3, CV_64F);
    cv::Point3f boardCenter(
        (float)((boardCols - 1) * squareSizeMm * 0.5),
        (float)((boardRows - 1) * squareSizeMm * 0.5),
        0.f);
    cv::Mat Pboard = (cv::Mat_<double>(3, 1) << (double)boardCenter.x, (double)boardCenter.y, 0.0);
    double zsum = 0.0;
    for (const auto& p : views) {
        cv::Mat rv = (cv::Mat_<double>(3, 1) << p.rvec[0], p.rvec[1], p.rvec[2]);
        cv::Mat tv = (cv::Mat_<double>(3, 1) << p.tvec[0], p.tvec[1], p.tvec[2]);
        cv::Mat R;
        cv::Rodrigues(rv, R);
        Rsum += R;
        cv::Mat Pcam = R * Pboard + tv;
        zsum += Pcam.at<double>(2);
    }
    cv::SVD svd(Rsum, cv::SVD::FULL_UV);
    cv::Mat Rmean = svd.u * svd.vt;
    if (cv::determinant(Rmean) < 0) {
        cv::Mat fix = cv::Mat::eye(3, 3, CV_64F);
        fix.at<double>(2, 2) = -1.0;
        Rmean = svd.u * fix * svd.vt;
    }
    cv::Mat rvMean;
    cv::Rodrigues(Rmean, rvMean);
    rvecOut[0] = rvMean.at<double>(0);
    rvecOut[1] = rvMean.at<double>(1);
    rvecOut[2] = rvMean.at<double>(2);
    const double z0 = zsum / (double)views.size();
    cv::Mat t = (cv::Mat_<double>(3, 1) << 0.0, 0.0, z0) - Rmean * Pboard;
    tvecOut[0] = t.at<double>(0);
    tvecOut[1] = t.at<double>(1);
    tvecOut[2] = t.at<double>(2);
    return 0;
}

static int ParseCalibrationJsonForOpticalAxis(const std::string& json,
    int boardCols, int boardRows, double squareSizeMm,
    double* fx, double* fy, double* cx, double* cy,
    double* k1, double* k2, double* p1, double* p2, double* k3,
    double rvec[3], double tvec[3]) {
    int ir = ParseCalibrationJsonIntrinsicsOnly(json, fx, fy, cx, cy, k1, k2, p1, p2, k3);
    if (ir != 0)
        return ir;
    std::vector<ViewPoseD> views;
    int pr = ParseAllExtrinsicsPoses(json, views);
    if (pr != 0)
        return pr;
    return BuildOpticalAxisSymmetricPose(views, boardCols, boardRows, squareSizeMm, rvec, tvec);
}

static int ParseBoardSpecFromJson(const std::string& json, int& boardCols, int& boardRows, double& squareSizeMm) {
    size_t pos = json.find("\"boardSpec\"");
    if (pos == std::string::npos)
        return -11;
    size_t end = json.find('}', pos);
    if (end == std::string::npos)
        return -11;
    const std::string span = json.substr(pos, end - pos + 1);
    double dc = 0, dr = 0, ds = 0;
    if (!GrabDoubleInSpan(span, "cols", &dc) || !GrabDoubleInSpan(span, "rows", &dr) ||
        !GrabDoubleInSpan(span, "squareSizeMm", &ds))
        return -11;
    boardCols = (int)std::lround(dc);
    boardRows = (int)std::lround(dr);
    squareSizeMm = ds;
    if (boardCols < 2 || boardRows < 2 || squareSizeMm <= 0.0)
        return -11;
    return 0;
}

int PixelsToChessboardPlaneXY(double fx, double fy, double cx, double cy,
    double k1, double k2, double p1, double p2, double k3,
    const double rvec[3], const double tvec[3],
    const Point2D* pixels, Point2D* worldXYOut, int count) {
    if (!pixels || !worldXYOut || count <= 0)
        return -1;

    cv::Mat cam = (cv::Mat_<double>(3, 3) << fx, 0.0, cx, 0.0, fy, cy, 0.0, 0.0, 1.0);
    cv::Mat dist = (cv::Mat_<double>(5, 1) << k1, k2, p1, p2, k3);
    cv::Mat rv = (cv::Mat_<double>(3, 1) << rvec[0], rvec[1], rvec[2]);
    cv::Mat tv = (cv::Mat_<double>(3, 1) << tvec[0], tvec[1], tvec[2]);
    cv::Mat R;
    cv::Rodrigues(rv, R);
    cv::Mat n = R.col(2);
    double nx = n.at<double>(0), ny = n.at<double>(1), nz = n.at<double>(2);
    double tx = tv.at<double>(0), ty = tv.at<double>(1), tz = tv.at<double>(2);
    double n_dot_t = nx * tx + ny * ty + nz * tz;

    std::vector<cv::Point2f> distPts, normPts;
    distPts.reserve((size_t)count);
    for (int i = 0; i < count; ++i)
        distPts.emplace_back((float)pixels[i].x, (float)pixels[i].y);
    cv::undistortPoints(distPts, normPts, cam, dist);

    cv::Mat Rt = R.t();

    for (int i = 0; i < count; ++i) {
        double xn = normPts[(size_t)i].x;
        double yn = normPts[(size_t)i].y;
        double vx = xn, vy = yn, vz = 1.0;
        double n_dot_v = nx * vx + ny * vy + nz * vz;
        if (fabs(n_dot_v) < 1e-12)
            return -10;
        double lambda = n_dot_t / n_dot_v;
        double Xc = lambda * vx, Yc = lambda * vy, Zc = lambda * vz;
        cv::Mat Pc = (cv::Mat_<double>(3, 1) << Xc, Yc, Zc);
        cv::Mat Pb = Rt * (Pc - tv);
        worldXYOut[i].x = Pb.at<double>(0);
        worldXYOut[i].y = Pb.at<double>(1);
    }
    return 0;
}

int PixelsToChessboardPlaneXYFromCalibrationJson(const char* calibrationJsonUtf8, int viewIndex,
    const Point2D* pixels, Point2D* worldXYOut, int count) {
    if (!calibrationJsonUtf8 || !pixels || !worldXYOut || count <= 0 || viewIndex < -1)
        return -1;
    std::string json(calibrationJsonUtf8);
    double fx, fy, cx, cy, k1, k2, p1, p2, k3;
    double rvec[3], tvec[3];
    int pr;
    if (viewIndex == -1) {
        int cols = 0, rows = 0;
        double sq = 0;
        int br = ParseBoardSpecFromJson(json, cols, rows, sq);
        if (br != 0)
            return br;
        pr = ParseCalibrationJsonForOpticalAxis(json, cols, rows, sq,
            &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, rvec, tvec);
    } else {
        pr = ParseCalibrationJsonForView(json, viewIndex, &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, rvec, tvec);
    }
    if (pr != 0)
        return pr;
    return PixelsToChessboardPlaneXY(fx, fy, cx, cy, k1, k2, p1, p2, k3, rvec, tvec, pixels, worldXYOut, count);
}

static void MatToImagePreserveChannels(const cv::Mat& src, Image* dst) {
    if (!dst) return;
    if (src.empty()) return;
    cv::Mat cont = src.isContinuous() ? src : src.clone();
    int w = cont.cols, h = cont.rows, ch = cont.channels();
    if (ch != 1 && ch != 3) {
        MatToImageBGR(cont, dst);
        return;
    }
    int rowBytes = w * ch;
    int stride = ImageRowStrideBytes(w, ch);
    if (dst->data) free(dst->data);
    dst->width = w;
    dst->height = h;
    dst->channels = ch;
    dst->data = (unsigned char*)malloc((size_t)stride * (size_t)h);
    if (!dst->data) return;
    if (stride == rowBytes) {
        memcpy(dst->data, cont.data, (size_t)rowBytes * (size_t)h);
        return;
    }
    for (int y = 0; y < h; ++y)
        memcpy(dst->data + (size_t)y * (size_t)stride, cont.ptr(y), (size_t)rowBytes);
}

static int ParseCalibrationJsonIntrinsics(const std::string& json,
    double* fx, double* fy, double* cx, double* cy,
    double* k1, double* k2, double* p1, double* p2, double* k3) {
    size_t i0 = json.find("\"intrinsics\"");
    if (i0 == std::string::npos)
        return -2;
    size_t i1 = json.find("\"extrinsicsPerView\"", i0);
    if (i1 == std::string::npos)
        i1 = json.size();
    const std::string head = json.substr(i0, i1 - i0);
    if (!GrabDoubleInSpan(head, "fx", fx) || !GrabDoubleInSpan(head, "fy", fy) ||
        !GrabDoubleInSpan(head, "cx", cx) || !GrabDoubleInSpan(head, "cy", cy) ||
        !GrabDoubleInSpan(head, "k1", k1) || !GrabDoubleInSpan(head, "k2", k2) ||
        !GrabDoubleInSpan(head, "p1", p1) || !GrabDoubleInSpan(head, "p2", p2) ||
        !GrabDoubleInSpan(head, "k3", k3))
        return -2;
    return 0;
}

int UndistortImageWithIntrinsics(const Image* src, Image* dst,
    double fx, double fy, double cx, double cy,
    double k1, double k2, double p1, double p2, double k3,
    double alpha) {
    if (!src || !dst || !src->data)
        return -1;
    cv::Mat m = ImageToMatClone(src);
    if (m.empty())
        return -2;

    cv::Mat cam = (cv::Mat_<double>(3, 3) << fx, 0.0, cx, 0.0, fy, cy, 0.0, 0.0, 1.0);
    cv::Mat dist = (cv::Mat_<double>(5, 1) << k1, k2, p1, p2, k3);
    cv::Mat undistorted;
    if (alpha >= 0.0 && alpha <= 1.0) {
        cv::Mat newCam = cv::getOptimalNewCameraMatrix(cam, dist, m.size(), alpha, m.size());
        cv::undistort(m, undistorted, cam, dist, newCam);
    } else {
        cv::undistort(m, undistorted, cam, dist);
    }
    if (undistorted.empty())
        return -3;

    MatToImagePreserveChannels(undistorted, dst);
    return dst->data ? 0 : -4;
}

int UndistortImageFromCalibrationJson(const Image* src, Image* dst, const char* calibrationJsonUtf8, double alpha) {
    if (!calibrationJsonUtf8 || !calibrationJsonUtf8[0])
        return -1;
    double fx, fy, cx, cy, k1, k2, p1, p2, k3;
    int pr = ParseCalibrationJsonIntrinsics(std::string(calibrationJsonUtf8), &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3);
    if (pr != 0)
        return pr;
    return UndistortImageWithIntrinsics(src, dst, fx, fy, cx, cy, k1, k2, p1, p2, k3, alpha);
}

static void BoardOuterQuadFromCorners(const std::vector<cv::Point2f>& corners, int boardCols, int boardRows,
    std::vector<cv::Point2f>& quadOut) {
    auto at = [&](int row, int col) -> cv::Point2f {
        return corners[(size_t)row * (size_t)boardCols + (size_t)col];
    };
    quadOut = {
        at(0, 0),
        at(0, boardCols - 1),
        at(boardRows - 1, boardCols - 1),
        at(boardRows - 1, 0)
    };
}

/** 整图模式：保持 src 尺寸，仅在棋盘四边形内写入鸟瞰校正后的像素，板外不变形。 */
static bool WarpChessboardPerspectiveInPlaceFullFrame(cv::Mat& out, const cv::Mat& src,
    const std::vector<cv::Point2f>& srcQuad, const cv::Mat& H_img_to_board, int boardW, int boardH) {
    if (src.empty() || boardW < 2 || boardH < 2 || srcQuad.size() != 4)
        return false;
    cv::Mat boardPatch;
    cv::warpPerspective(src, boardPatch, H_img_to_board, cv::Size(boardW, boardH),
        cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
    if (boardPatch.empty())
        return false;

    cv::Mat H_board_to_img;
    if (!cv::invert(H_img_to_board, H_board_to_img, cv::DECOMP_LU))
        return false;

    cv::Mat warpedFull;
    cv::warpPerspective(boardPatch, warpedFull, H_board_to_img, src.size(),
        cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
    if (warpedFull.empty() || warpedFull.type() != src.type())
        return false;

    cv::Mat mask(src.rows, src.cols, CV_8UC1, cv::Scalar(0));
    std::vector<cv::Point> poly(4);
    for (int i = 0; i < 4; ++i)
        poly[i] = cv::Point((int)std::lround(srcQuad[i].x), (int)std::lround(srcQuad[i].y));
    cv::fillConvexPoly(mask, poly, cv::Scalar(255));

    out = src.clone();
    warpedFull.copyTo(out, mask);
    return true;
}

int WarpImageToChessboardPlane(const Image* src, Image* dst,
    double fx, double fy, double cx, double cy,
    double k1, double k2, double p1, double p2, double k3,
    const double rvec[3], const double tvec[3],
    int boardCols, int boardRows, double squareSizeMm, double pxPerMm,
    int perspectiveOutputMode, int assumeUndistortedInput, int outputSizeMode) {
    if (!src || !dst || !src->data || !rvec || !tvec)
        return -1;
    if (boardCols < 2 || boardRows < 2 || squareSizeMm <= 0.0 || pxPerMm <= 0.0)
        return -2;
    if (perspectiveOutputMode < 0 || perspectiveOutputMode > 2)
        return -9;
    if (assumeUndistortedInput < 0 || assumeUndistortedInput > 1)
        return -9;
    if (outputSizeMode < 0 || outputSizeMode > 1)
        return -9;

    cv::Mat m = ImageToMatClone(src);
    if (m.empty())
        return -3; // 无法读取图像缓冲（channels 须为 1 或 3）

    cv::Mat cam = (cv::Mat_<double>(3, 3) << fx, 0.0, cx, 0.0, fy, cy, 0.0, 0.0, 1.0);
    cv::Mat dist = (cv::Mat_<double>(5, 1) << k1, k2, p1, p2, k3);
    cv::Mat rv = (cv::Mat_<double>(3, 1) << rvec[0], rvec[1], rvec[2]);
    cv::Mat tv = (cv::Mat_<double>(3, 1) << tvec[0], tvec[1], tvec[2]);

    std::vector<cv::Point3f> obj;
    obj.reserve((size_t)boardCols * (size_t)boardRows);
    for (int i = 0; i < boardRows; ++i) {
        for (int j = 0; j < boardCols; ++j)
            obj.emplace_back((float)(j * squareSizeMm), (float)(i * squareSizeMm), 0.f);
    }

    const cv::Mat distProj = assumeUndistortedInput ? cv::Mat::zeros(5, 1, CV_64F) : dist;
    std::vector<cv::Point2f> imgAll;
    cv::projectPoints(obj, rv, tv, cam, distProj, imgAll);
    if (imgAll.size() < (size_t)boardCols * (size_t)boardRows)
        return -4;

    auto at = [&](int row, int col) -> cv::Point2f {
        return imgAll[(size_t)row * (size_t)boardCols + (size_t)col];
    };

    std::vector<cv::Point2f> srcCorners = imgAll;

    std::vector<cv::Point2f> srcPts;
    BoardOuterQuadFromCorners(srcCorners, boardCols, boardRows, srcPts);

    auto edgeLen = [](const cv::Point2f& a, const cv::Point2f& b) -> float {
        const float dx = a.x - b.x;
        const float dy = a.y - b.y;
        return std::sqrt(dx * dx + dy * dy);
    };

    int outW = (int)std::lround((boardCols - 1) * squareSizeMm * pxPerMm);
    int outH = (int)std::lround((boardRows - 1) * squareSizeMm * pxPerMm);
    // board_pixels: 按标定外参投影的棋盘边长定尺寸（生产图无棋盘，不做 findChessboardCorners）
    if (outputSizeMode == 1) {
        const double horiz = 0.5 * ((double)edgeLen(srcPts[0], srcPts[1]) + (double)edgeLen(srcPts[3], srcPts[2]));
        const double vert = 0.5 * ((double)edgeLen(srcPts[0], srcPts[3]) + (double)edgeLen(srcPts[1], srcPts[2]));
        const double scaleW = horiz / (double)(boardCols - 1);
        const double scaleH = vert / (double)(boardRows - 1);
        const double scale = 0.5 * (scaleW + scaleH);
        outW = (int)std::lround(scale * (double)(boardCols - 1));
        outH = (int)std::lround(scale * (double)(boardRows - 1));
    }
    if (outW < 8 || outH < 8)
        return -5;

    const double cellPx = (double)outW / (double)(boardCols - 1);
    std::vector<cv::Point2f> dstGrid;
    dstGrid.reserve((size_t)boardCols * (size_t)boardRows);
    for (int i = 0; i < boardRows; ++i) {
        for (int j = 0; j < boardCols; ++j)
            dstGrid.emplace_back((float)(j * cellPx), (float)(i * cellPx));
    }

    std::vector<cv::Point2f> dstPts = {
        cv::Point2f(0.f, 0.f),
        cv::Point2f((float)outW, 0.f),
        cv::Point2f((float)outW, (float)outH),
        cv::Point2f(0.f, (float)outH)
    };

    cv::Mat H = cv::findHomography(srcCorners, dstGrid, cv::RANSAC, 3.0);
    if (H.empty())
        H = cv::getPerspectiveTransform(srcPts, dstPts);
    cv::Mat warped;
    // 0=board 裁剪；1=local 原图尺寸仅板内；2=plane 整图按标定板平面单应（共面场景）
    if (perspectiveOutputMode == 1) {
        if (!WarpChessboardPerspectiveInPlaceFullFrame(warped, m, srcPts, H, outW, outH))
            return -6;
    } else if (perspectiveOutputMode == 2) {
        // 整图共面：与 local 共用同一 H，但全图采样；用均匀缩放落入原图尺寸，避免画布宽高各自拉伸
        std::vector<cv::Point2f> imgCorners = {
            cv::Point2f(0.f, 0.f),
            cv::Point2f((float)m.cols, 0.f),
            cv::Point2f((float)m.cols, (float)m.rows),
            cv::Point2f(0.f, (float)m.rows)
        };
        std::vector<cv::Point2f> mapped;
        cv::perspectiveTransform(imgCorners, mapped, H);
        std::vector<cv::Point2f> allPts = dstPts;
        allPts.insert(allPts.end(), mapped.begin(), mapped.end());
        float minX = allPts[0].x, minY = allPts[0].y, maxX = minX, maxY = minY;
        for (const auto& p : allPts) {
            minX = std::min(minX, p.x);
            maxX = std::max(maxX, p.x);
            minY = std::min(minY, p.y);
            maxY = std::max(maxY, p.y);
        }
        const double planeW = (double)maxX - (double)minX;
        const double planeH = (double)maxY - (double)minY;
        if (planeW < 1e-3 || planeH < 1e-3)
            return -6;
        const double scale = std::min((double)(m.cols - 1) / planeW, (double)(m.rows - 1) / planeH);
        const double tx = -minX * scale + 0.5 * ((double)m.cols - planeW * scale);
        const double ty = -minY * scale + 0.5 * ((double)m.rows - planeH * scale);
        cv::Mat T = (cv::Mat_<double>(3, 3) << scale, 0.0, tx, 0.0, scale, ty, 0.0, 0.0, 1.0);
        H = T * H;
        cv::warpPerspective(m, warped, H, m.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
        if (warped.empty())
            return -6;
    } else {
        cv::warpPerspective(m, warped, H, cv::Size(outW, outH), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
        if (warped.empty())
            return -6;
    }

    MatToImagePreserveChannels(warped, dst);
    return dst->data ? 0 : -7;
}

int WarpImageToChessboardPlaneFromCalibrationJson(const Image* src, Image* dst,
    const char* calibrationJsonUtf8, int viewIndex,
    int boardCols, int boardRows, double squareSizeMm, double pxPerMm,
    int perspectiveOutputMode, int assumeUndistortedInput, int outputSizeMode) {
    if (!calibrationJsonUtf8 || !calibrationJsonUtf8[0] || viewIndex < -1)
        return -1;
    if (perspectiveOutputMode < 0 || perspectiveOutputMode > 2)
        return -9;
    std::string json(calibrationJsonUtf8);
    double fx, fy, cx, cy, k1, k2, p1, p2, k3;
    double rvec[3], tvec[3];
    int pr;
    if (viewIndex == -1) {
        pr = ParseCalibrationJsonForOpticalAxis(json, boardCols, boardRows, squareSizeMm,
            &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, rvec, tvec);
    } else {
        pr = ParseCalibrationJsonForView(json, viewIndex, &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, rvec, tvec);
    }
    if (pr != 0)
        return pr;
    return WarpImageToChessboardPlane(src, dst, fx, fy, cx, cy, k1, k2, p1, p2, k3, rvec, tvec,
        boardCols, boardRows, squareSizeMm, pxPerMm, perspectiveOutputMode, assumeUndistortedInput, outputSizeMode);
}
