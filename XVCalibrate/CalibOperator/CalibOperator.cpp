/**
 * CalibOperator.cpp - 工业视觉标定算子库实现
 * 纯算法层，不依赖 Windows GUI (无 HWND/HDC/WM_*)
 * 依赖：OpenCV 4.x, C 标准库
 */

#include "CalibOperator.h"
#include <stdarg.h>
#include <time.h>

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

    int bitCount = img->channels * 8;
    int rowSize = ((img->width * img->channels + 3) / 4) * 4;
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
    fwrite(img->data, 1, rowSize * img->height, fp);
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
            fseek(fp, header.offset + srcY * srcRowSize, SEEK_SET);
            if (fread(srcRow, 1, srcRowSize, fp) != (size_t)srcRowSize) {
                LOG_ERROR("Failed to read row %d", y);
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
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);

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
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);

    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    Mat darkAdaptive;
    adaptiveThreshold(innerGray, darkAdaptive, 255,
                      ADAPTIVE_THRESH_GAUSSIAN_C, THRESH_BINARY_INV, 31, 10);
    darkAdaptive.setTo(0, calibMask == 0);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_OPEN, kernel2);
    morphologyEx(darkAdaptive, darkAdaptive, MORPH_CLOSE, kernel2);

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
            circleCandidates.push_back(info);
            if (circularity > 0.5f) circularOnes.push_back(info);
        }
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

    // Step 2: Crosshair refinement
    int foundCount = 0;
    for (int i = 0; i < (int)selected.size() && foundCount < 9; i++) {
        Point2f roughCenter = selected[i].center;
        float radius = selected[i].radius;
        int cx = (int)round(roughCenter.x);
        int cy = (int)round(roughCenter.y);
        int searchR = (int)(radius * 0.8);

        int x0 = std::max(0, cx - searchR);
        int y0 = std::max(0, cy - searchR);
        int x1 = std::min(w - 1, cx + searchR);
        int y1 = std::min(h - 1, cy + searchR);
        if (x1 <= x0 || y1 <= y0) continue;

        Mat roi = grayMat(Range(y0, y1 + 1), Range(x0, x1 + 1));
        int roiCx = cx - x0, roiCy = cy - y0;

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

        if (brightCrosshair) {
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

void BlobDetectCircles(Image* img, Point2D* pts, int* count) {
    LOG_INFO("=== Starting OpenCV Adaptive Blob Detection ===");
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

    // Step 1: OTSU threshold to find bright calibration region
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(5, 5), 1.0);

    Mat brightBinary;
    threshold(blurred, brightBinary, 0, 255, THRESH_BINARY + THRESH_OTSU);

    Mat kernel = getStructuringElement(MORPH_ELLIPSE, Size(7, 7));
    morphologyEx(brightBinary, brightBinary, MORPH_CLOSE, kernel);
    morphologyEx(brightBinary, brightBinary, MORPH_OPEN, kernel);

    std::vector<std::vector<Point>> brightContours;
    findContours(brightBinary, brightContours, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE);

    if (brightContours.empty()) {
        LOG_ERROR("No bright calibration region found");
        return;
    }

    int maxIdx = 0;
    double maxArea = 0;
    for (size_t i = 0; i < brightContours.size(); i++) {
        double a = contourArea(brightContours[i]);
        if (a > maxArea) { maxArea = a; maxIdx = (int)i; }
    }

    Mat calibMask = Mat::zeros(h, w, CV_8UC1);
    drawContours(calibMask, brightContours, maxIdx, Scalar(255), FILLED);

    // Draw calib region border on original image
    Rect bbox = boundingRect(brightContours[maxIdx]);
    int dstRowSize = ((w * img->channels + 3) / 4) * 4;

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

    // Step 2: Find dark circles within calibration region
    Mat innerGray = grayMat.clone();
    innerGray.setTo(128, calibMask == 0);

    Mat darkBinary;
    threshold(innerGray, darkBinary, 0, 255, THRESH_BINARY_INV + THRESH_OTSU);
    darkBinary.setTo(0, calibMask == 0);

    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);

    std::vector<std::vector<Point>> darkContours;
    findContours(darkBinary, darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);

    // Step 3: Filter and fit circles
    std::vector<Point2D> circleCenters;

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

        Point2f center;
        float radius;
        minEnclosingCircle(contour, center, radius);

        Point2D pt;
        pt.x = center.x;
        pt.y = center.y;
        circleCenters.push_back(pt);

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

    std::sort(circleCenters.begin(), circleCenters.end(), [](const Point2D& a, const Point2D& b) {
        if (fabs(a.y - b.y) > 20) return a.y < b.y;
        return a.x < b.x;
    });

    *count = std::min(9, (int)circleCenters.size());
    for (int i = 0; i < *count && i < 9; i++) {
        pts[i] = circleCenters[i];
    }

    LOG_INFO("Blob detection complete: %d points", *count);
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

// ========== OpenCV 16 暗条轨迹检测 ==========

void DetectTrajectoryOpenCV(Image* img, Point2D* trajPixels, int* count,
                            Image* stepImages, int* stepBarIds, int stepImageCount) {
    LOG_INFO("=== Starting OpenCV 16-DarkBars Trajectory Detection ===");

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

    // ---- 步骤 2：高斯模糊 + OTSU 二值化 ----
    Mat blurred;
    GaussianBlur(grayMat, blurred, Size(7, 7), 1.5);

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
    morphologyEx(binaryBright, morphed, MORPH_CLOSE, kernel);
    morphologyEx(morphed, morphed, MORPH_OPEN, kernel);

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

    // ---- 步骤 4：暗条二值化 ----
    Mat innerGray = grayMat.clone();
    innerGray.setTo(255, mask == 0);

    Mat darkBinary;
    threshold(innerGray, darkBinary, 45, 255, THRESH_BINARY_INV);
    darkBinary.setTo(0, mask == 0);

    Mat darkBinaryBeforeMorph = darkBinary.clone();

    if (stepImages && stepImageCount >= 4) {
        stepImages[3].width = darkBinaryBeforeMorph.cols;
        stepImages[3].height = darkBinaryBeforeMorph.rows;
        stepImages[3].channels = 1;
        stepImages[3].data = (unsigned char*)malloc(darkBinaryBeforeMorph.cols * darkBinaryBeforeMorph.rows);
        memcpy(stepImages[3].data, darkBinaryBeforeMorph.data, darkBinaryBeforeMorph.cols * darkBinaryBeforeMorph.rows);
    }

    // ---- 步骤 5：形态学清理 ----
    Mat kernel2 = getStructuringElement(MORPH_ELLIPSE, Size(3, 3));
    morphologyEx(darkBinary, darkBinary, MORPH_CLOSE, kernel2);
    morphologyEx(darkBinary, darkBinary, MORPH_OPEN, kernel2);

    if (stepImages && stepImageCount >= 5) {
        stepImages[4].width = darkBinary.cols;
        stepImages[4].height = darkBinary.rows;
        stepImages[4].channels = 1;
        stepImages[4].data = (unsigned char*)malloc(darkBinary.cols * darkBinary.rows);
        memcpy(stepImages[4].data, darkBinary.data, darkBinary.cols * darkBinary.rows);
    }

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

    // 等间距轮廓采样
    std::vector<Point> allPoints;
    std::vector<int> allBarIds;

    for (int b = 0; b < targetBars; b++) {
        const std::vector<Point>& contour = darkContours[sortedBars[b].second];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;

        for (int j = 0; j < ptCount; j++) {
            allPoints.push_back(contour[j]);
            allBarIds.push_back(b);
        }

        double perimeter = arcLength(contour, true);
        double spacing = 2.0;
        int sampleCount = std::max(10, (int)(perimeter / spacing));

        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = contour[j].x - contour[j-1].x;
            double dy = contour[j].y - contour[j-1].y;
            cumLen[j] = cumLen[j-1] + sqrt(dx*dx + dy*dy);
        }

        for (int s = 0; s < sampleCount; s++) {
            double targetLen = perimeter * s / sampleCount;
            int lo = 0, hi = ptCount - 1;
            while (lo < hi - 1) {
                int mid = (lo + hi) / 2;
                if (cumLen[mid] <= targetLen) lo = mid;
                else hi = mid;
            }
            double segLen = cumLen[hi] - cumLen[lo];
            double t = (segLen > 0.001) ? (targetLen - cumLen[lo]) / segLen : 0.0;
            int px = (int)(contour[lo].x + t * (contour[hi].x - contour[lo].x));
            int py = (int)(contour[lo].y + t * (contour[hi].y - contour[lo].y));
            if (px >= 0 && px < w && py >= 0 && py < h) {
                allPoints.push_back(Point(px, py));
                allBarIds.push_back(b);
            }
        }
    }

    LOG_INFO("Contour sampling: %d dark bars processed, raw points=%d",
             targetBars, (int)allPoints.size());

    // Mask 验证
    {
        int totalBefore = (int)allPoints.size();
        int keptCount = 0;
        for (size_t i = 0; i < allPoints.size(); i++) {
            int px = allPoints[i].x;
            int py = allPoints[i].y;
            if (px >= 0 && px < w && py >= 0 && py < h) {
                if (darkBinary.at<unsigned char>(py, px) != 0) {
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
