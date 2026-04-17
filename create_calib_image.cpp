/**
 * Create a 9-point calibration test image
 * Black circles with white crosshairs at centers
 * Output: 800x600 BMP image
 */

#include <windows.h>
#include <stdio.h>
#include <math.h>

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

int main() {
    int width = 800;
    int height = 600;
    int rowSize = ((width * 3 + 3) / 4) * 4;
    int imageSize = rowSize * height;

    BMPHeader header = {0};
    header.type = 0x4D42;
    header.size = sizeof(BMPHeader) + sizeof(BMPInfoHeader) + imageSize;
    header.offset = sizeof(BMPHeader) + sizeof(BMPInfoHeader);

    BMPInfoHeader info = {0};
    info.size = sizeof(BMPInfoHeader);
    info.width = width;
    info.height = height;
    info.planes = 1;
    info.bitCount = 24;
    info.compression = 0;
    info.imageSize = imageSize;
    info.xPelsPerMeter = 2835;
    info.yPelsPerMeter = 2835;

    unsigned char* image = (unsigned char*)malloc(imageSize);
    if (!image) {
        printf("Memory allocation failed!\n");
        return 1;
    }

    // Fill white background
    for (int i = 0; i < imageSize; i += 3) {
        image[i] = 255;
        image[i+1] = 255;
        image[i+2] = 255;
    }

    // 3x3 grid layout
    int gridStartX = 100;
    int gridStartY = 100;
    int gridStepX = 300;
    int gridStepY = 200;
    int circleRadius = 50;
    int crossSize = 20;
    int crossThickness = 3;

    for (int row = 0; row < 3; row++) {
        for (int col = 0; col < 3; col++) {
            int cx = gridStartX + col * gridStepX;
            int cy = gridStartY + row * gridStepY;

            // Draw black circle
            for (int y = cy - circleRadius; y <= cy + circleRadius; y++) {
                for (int x = cx - circleRadius; x <= cx + circleRadius; x++) {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int dx = x - cx;
                    int dy = y - cy;
                    float dist = sqrt((float)(dx*dx + dy*dy));
                    if (dist <= circleRadius) {
                        int offset = y * rowSize + x * 3;
                        image[offset] = 0;
                        image[offset+1] = 0;
                        image[offset+2] = 0;
                    }
                }
            }

            // Draw white crosshair
            for (int x = cx - crossSize; x <= cx + crossSize; x++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int y = cy + t;
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int offset = y * rowSize + x * 3;
                    image[offset] = 255;
                    image[offset+1] = 255;
                    image[offset+2] = 255;
                }
            }
            for (int y = cy - crossSize; y <= cy + crossSize; y++) {
                for (int t = -crossThickness; t <= crossThickness; t++) {
                    int x = cx + t;
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int offset = y * rowSize + x * 3;
                    image[offset] = 255;
                    image[offset+1] = 255;
                    image[offset+2] = 255;
                }
            }

            printf("Circle %d: center=(%d, %d)\n", row*3+col+1, cx, cy);
        }
    }

    FILE* fp;
    if (fopen_s(&fp, "calibration_9points.bmp", "wb") != 0) {
        printf("Cannot create file!\n");
        free(image);
        return 1;
    }

    fwrite(&header, sizeof(BMPHeader), 1, fp);
    fwrite(&info, sizeof(BMPInfoHeader), 1, fp);
    fwrite(image, 1, imageSize, fp);
    fclose(fp);
    free(image);

    printf("\n========================================\n");
    printf("Image: calibration_9points.bmp\n");
    printf("Size: %d x %d pixels\n", width, height);
    printf("\nCircle centers:\n");
    printf("  (100,100) (400,100) (700,100)\n");
    printf("  (100,300) (400,300) (700,300)\n");
    printf("  (100,500) (400,500) (700,500)\n");
    printf("========================================\n");

    MessageBoxA(NULL,
        "Calibration image created!\n\n"
        "File: calibration_9points.bmp\n"
        "Size: 800 x 600 pixels\n\n"
        "9 black circles with white crosshairs\n"
        "in a 3x3 grid pattern.",
        "Success", MB_OK);

    return 0;
}
