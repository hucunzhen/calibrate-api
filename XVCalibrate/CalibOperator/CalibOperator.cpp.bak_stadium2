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

    // 膨胀+腐蚀：先膨胀让暗条变粗融合小间隙，再腐蚀回来使轮廓光滑
    Mat dilateKernel = getStructuringElement(MORPH_ELLIPSE, Size(5, 5));
    dilate(darkBinary, darkBinary, dilateKernel);
    erode(darkBinary, darkBinary, dilateKernel);

    if (stepImages && stepImageCount >= 5) {
        stepImages[4].width = darkBinary.cols;
        stepImages[4].height = darkBinary.rows;
        stepImages[4].channels = 1;
        stepImages[4].data = (unsigned char*)malloc(darkBinary.cols * darkBinary.rows);
        memcpy(stepImages[4].data, darkBinary.data, darkBinary.cols * darkBinary.rows);
    }

    // 高斯模糊 + 重二值化：让暗条轮廓更光滑
    Mat blurredDark;
    GaussianBlur(darkBinary, blurredDark, Size(5, 5), 1.0);
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

    // 等间距轮廓采样
    std::vector<Point> allPoints;
    std::vector<int> allBarIds;

    for (int b = 0; b < targetBars; b++) {
        const std::vector<Point>& contour = darkContours[sortedBars[b].second];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;
        double perimeter = arcLength(contour, true);
        double spacing = 2.0;
        int sampleCount = std::max(10, (int)(perimeter / spacing));

        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = contour[j].x - contour[j-1].x;
            double dy = contour[j].y - contour[j-1].y;
            cumLen[j] = cumLen[j-1] + sqrt(dx*dx + dy*dy);
        }

        // 等间距采样 + 形状拟合
        std::vector<cv::Point2d> sampledPts;
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
                sampledPts.push_back(cv::Point2d((double)px, (double)py));
            }
        }

        // 分段形状拟合: 直线拟合到直线, 圆弧拟合到圆弧
        std::vector<cv::Point2d> fittedPts = fitSegmentsShape(sampledPts);
        for (size_t fi = 0; fi < fittedPts.size(); fi++) {
            int px = (int)std::round(fittedPts[fi].x);
            int py = (int)std::round(fittedPts[fi].y);
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

// 2. 高斯模糊 + OTSU二值化 + 形态学处理 + 提取外轮廓
void Step_PreprocessAndFindContours(cv::Mat* grayMat, cv::Mat* binaryBright,
                                     cv::Mat* morphed, cv::Mat* mask, cv::Mat* coloredMask,
                                     std::vector<std::vector<cv::Point>>* outerContours,

                                     int blurKsize, int morphKernelSize) {

    if (!grayMat || grayMat->empty()) return;
    int w = grayMat->cols, h = grayMat->rows;
    
    // 高斯模糊
    cv::Mat blurred;
    cv::GaussianBlur(*grayMat, blurred, cv::Size(7, 7), 1.5);
    
    // OTSU二值化
    cv::threshold(blurred, *binaryBright, 0, 255, THRESH_BINARY + THRESH_OTSU);
    
    // 形态学处理
    cv::Mat kernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(5, 5));
    morphologyEx(*binaryBright, *morphed, MORPH_CLOSE, kernel);
    morphologyEx(*morphed, *morphed, MORPH_OPEN, kernel);
    
    // 提取外轮廓
    findContours(*morphed, *outerContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    LOG_INFO("Step_PreprocessAndFindContours: found %d outer contours", (int)outerContours->size());
    
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
    
    blurred.release();
    kernel.release();
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

// 4. 暗条二值化
void Step_DetectDarkBars(cv::Mat* grayMat, cv::Mat* mask, int threshold,
                          cv::Mat* darkBinary) {
    // 工件内部保留原灰度，外部设为255（白色）
    *darkBinary = grayMat->clone();
    darkBinary->setTo(255, *mask == 0);
    
    // 暗条：低于阈值为255
    cv::threshold(*darkBinary, *darkBinary, threshold, 255, THRESH_BINARY_INV);
    darkBinary->setTo(0, *mask == 0);
}

// 5. 形态学清理
void Step_MorphologyCleanup(cv::Mat* darkBinary, int kernelSize, int blurKsize, double blurSigma) {


    cv::Mat kernel = getStructuringElement(MORPH_ELLIPSE, cv::Size(kernelSize, kernelSize));
    morphologyEx(*darkBinary, *darkBinary, MORPH_CLOSE, kernel);
    morphologyEx(*darkBinary, *darkBinary, MORPH_OPEN, kernel);

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

// 6. 提取暗条轮廓并按面积排序
int Step_FindAndSortDarkContours(cv::Mat* darkBinary, int width, int height,
                                   std::vector<std::pair<double, int>>* sortedBars,
                                   std::vector<std::vector<cv::Point>>* darkContours) {
    // 提取所有暗条轮廓
    findContours(*darkBinary, *darkContours, RETR_EXTERNAL, CHAIN_APPROX_NONE);
    
    // 自适应面积阈值
    double areaThreshold = (double)(width * height) * 0.002;
    if (areaThreshold < 500.0) areaThreshold = 500.0;
    
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

// 7. 等间距轮廓采样
void Step_SampleContoursEquidistant(std::vector<std::vector<cv::Point>>* darkContours,
                                     std::vector<std::pair<double, int>>* sortedBars,
                                     int targetBars, int width, int height,
                                     std::vector<cv::Point>* allPoints,
                                     std::vector<int>* allBarIds) {
    for (int b = 0; b < targetBars && b < (int)sortedBars->size(); b++) {
        const std::vector<cv::Point>& contour = (*darkContours)[(*sortedBars)[b].second];
        int ptCount = (int)contour.size();
        if (ptCount < 5) continue;
        
        // 等间距采样
        double perimeter = arcLength(contour, true);
        double spacing = 2.0;
        int sampleCount = std::max(10, (int)(perimeter / spacing));
        
        std::vector<double> cumLen(ptCount, 0.0);
        for (int j = 1; j < ptCount; j++) {
            double dx = contour[j].x - contour[j-1].x;
            double dy = contour[j].y - contour[j-1].y;
            cumLen[j] = cumLen[j-1] + sqrt(dx*dx + dy*dy);
        }
        
        // 等间距采样 + 形状拟合
        std::vector<cv::Point2d> sampledPts;
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
            if (px >= 0 && px < width && py >= 0 && py < height) {
                sampledPts.push_back(cv::Point2d((double)px, (double)py));
            }
        }

        // 分段形状拟合: 直线拟合到直线, 圆弧拟合到圆弧
        std::vector<cv::Point2d> fittedPts = fitSegmentsShape(sampledPts);
        for (size_t fi = 0; fi < fittedPts.size(); fi++) {
            int px = (int)std::round(fittedPts[fi].x);
            int py = (int)std::round(fittedPts[fi].y);
            if (px >= 0 && px < width && py >= 0 && py < height) {
                allPoints->push_back(cv::Point(px, py));
                allBarIds->push_back(b);
            }
        }
    }
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
