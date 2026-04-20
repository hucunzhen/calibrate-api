/**
 * ============================================================
 * CalibrationGUI.cpp - 九点标定工具主程序
 * ============================================================
 * 
 * 功能说明:
 *   本程序实现了一个Windows GUI应用程序，用于完成机器视觉中的
 *   九点标定（9-Point Calibration）操作，将像素坐标转换为世界坐标。
 * 
 * 主要功能模块:
 *   1. 标定图像生成 - 生成带有9个圆点的标准标定图案
 *   2. 图像加载 - 从文件加载标定图像
 *   3. 圆点检测 - 使用OpenCV检测图像中的圆点中心
 *   4. 九点标定 - 根据像素坐标和世界坐标计算仿射变换矩阵
 *   5. 结果保存 - 保存标定参数和结果图像
 *   6. 坐标变换测试 - 测试像素坐标到世界坐标的转换
 *   7. 轨迹检测 - 检测图像中的运动轨迹
 * 
 * 依赖项:
 *   - CalibOperator算子库：提供核心的图像处理和标定算法
 *   - Windows API：窗口创建、消息循环等GUI功能
 *   - OpenCV：图像处理基础功能
 * 
 * 编译环境:
 *   - Visual Studio 2019+
 *   - Windows SDK
 * 
 * 作者: 自动生成
 * 版本: 1.0
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

/**
 * ============================================================
 * 第一节: 日志系统
 * ============================================================
 * 
 * 日志级别说明:
 *   LOG_INFO  - 信息日志，记录正常业务流程
 *   LOG_WARN  - 警告日志，记录异常但可恢复的情况
 *   LOG_ERROR - 错误日志，记录严重错误
 *   LOG_DEBUG - 调试日志，仅用于开发调试
 * 
 * 日志输出位置:
 *   1. 控制台窗口（实时显示）
 *   2. debug_autotest.log 文件（持久化存储）
 * 
 */

// 全局日志文件指针，用于将日志写入文件
static FILE* g_logFile = NULL;

// =============================================================
// 日志宏定义（必须在使用前声明）
// =============================================================

// 基础日志宏，生成带时间戳的日志
#define LOG(fmt, ...) do { \
    time_t now = time(NULL); \
    struct tm t; localtime_s(&t, &now); \
    char timeStr[32]; \
    strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &t); \
    printf("[%s] " fmt "\n", timeStr, ##__VA_ARGS__); \
    fflush(stdout); \
    if (g_logFile) { fprintf(g_logFile, "[%s] " fmt "\n", timeStr, ##__VA_ARGS__); fflush(g_logFile); } \
} while(0)

// 信息日志 - 记录正常业务流程
#define LOG_INFO(fmt, ...)   LOG("[INFO] " fmt, ##__VA_ARGS__)

// 警告日志 - 记录异常但可恢复的情况
#define LOG_WARN(fmt, ...)    LOG("[WARN] " fmt, ##__VA_ARGS__)

// 错误日志 - 记录严重错误或失败操作
#define LOG_ERROR(fmt, ...)   LOG("[ERROR] " fmt, ##__VA_ARGS__)

// 调试日志 - 仅用于开发调试，生产环境可能禁用
#define LOG_DEBUG(fmt, ...)   LOG("[DEBUG] " fmt, ##__VA_ARGS__)

/**
 * OperatorLogCallback - CalibOperator算子库的日志回调函数
 * 
 * 功能说明:
 *   此函数被CalibOperator库内部调用，用于将算子层的日志信息
 *   统一输出到本程序的控制台和日志文件。
 * 
 * 参数说明:
 *   level - 日志级别 (0=INFO, 1=WARN, 2=ERROR, 3=DEBUG)
 *   fmt   - 格式字符串，类似于printf的格式
 *   ...   - 可变参数列表
 * 
 * 实现细节:
 *   1. 根据日志级别添加对应的前缀标签
 *   2. 获取当前时间戳，格式化为 HH:MM:SS
 *   3. 使用vsnprintf_s安全地格式化日志消息
 *   4. 同时输出到控制台(stdout)和日志文件
 * 
 * 注意:
 *   使用va_list处理可变参数，需要先va_start初始化，
 *   最后va_end清理
 */
static void OperatorLogCallback(int level, const char* fmt, ...) {
    const char* prefix = "";
    switch (level) {
        case 0: prefix = "[INFO] "; break;
        case 1: prefix = "[WARN] "; break;
        case 2: prefix = "[ERROR]"; break;
        case 3: prefix = "[DEBUG]"; break;
        default: prefix = "[LOG] "; break;
    }
    
    // 日志缓冲区，用于组装完整的日志行
    char buf[1024];
    time_t now = time(NULL);
    struct tm t;
    localtime_s(&t, &now);
    char timeStr[32];
    strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &t);
    
    // 组装日志前缀：[HH:MM:SS] [LEVEL]
    int n = sprintf_s(buf, sizeof(buf), "%s %s ", timeStr, prefix);
    
    // 将格式化的日志内容追加到日志缓冲区
    va_list args;
    va_start(args, fmt);
    int written = vsnprintf_s(buf + n, sizeof(buf) - n, _TRUNCATE, fmt, args);
    va_end(args);
    
    // 添加换行符完成一行日志
    strcat_s(buf, sizeof(buf), "\n");

    // ==================== 日志输出 ====================
    // 1. 输出到控制台（实时显示，便于调试）
    printf("%s", buf);
    fflush(stdout);  // 立即刷新，确保日志实时显示
    
    // 2. 输出到日志文件（持久化存储）
    if (g_logFile) {
        fprintf(g_logFile, "%s", buf);
        fflush(g_logFile);  // 立即刷新，防止程序崩溃时日志丢失
    }
}

/**
 * GetExeDir - 获取当前可执行文件所在目录
 * 
 * 功能说明:
 *   提取可执行文件的完整路径，然后截取目录部分。
 *   用于定位程序运行时的相对资源文件（如test_auto.bmp）。
 * 
 * 参数说明:
 *   outDir  - 输出参数，存储目录路径字符串
 *   maxLen  - outDir缓冲区的最大长度
 * 
 * 算法步骤:
 *   1. 调用GetModuleFileNameA获取当前进程的EXE文件完整路径
 *   2. 从路径字符串末尾向前查找最后一个反斜杠'\'
 *   3. 在反斜杠位置截断，保留目录部分
 * 
 * 示例:
 *   输入: "D:\MyApp\bin\CalibrationGUI.exe"
 *   输出: "D:\MyApp\bin"
 */
static void GetExeDir(char* outDir, int maxLen) {
    // 获取当前可执行文件的完整路径
    char exePath[MAX_PATH];
    GetModuleFileNameA(NULL, exePath, MAX_PATH);
    LOG_DEBUG("GetExeDir: exePath = %s", exePath);
    
    // 查找最后一个路径分隔符
    char* lastSlash = strrchr(exePath, '\\');
    if (lastSlash) {
        *lastSlash = '\0';  // 截断，保留目录部分
        strncpy_s(outDir, maxLen, exePath, maxLen - 1);
        LOG_DEBUG("GetExeDir: directory = %s", outDir);
    } else {
        outDir[0] = '\0';
        LOG_WARN("GetExeDir: failed to extract directory from path");
    }
}

// =============================================================
// 第二节: 控制台初始化和日志宏定义
// =============================================================

// 控制台文件指针，用于标准输入输出重定向
static FILE* g_consoleFile = NULL;

/**
 * InitConsole - 初始化调试控制台
 * 
 * 功能说明:
 *   分配一个新的控制台窗口，并将标准输出重定向到该控制台。
 *   同时创建日志文件用于记录所有输出信息。
 * 
 * 实现步骤:
 *   1. 调用AllocConsole分配新的控制台窗口
 *   2. 重定向stdout和stdin到控制台
 *   3. 设置控制台标题和编码（UTF-8支持中文显示）
 *   4. 在exe目录下创建debug_autotest.log日志文件
 * 
 * 日志文件位置:
 *   <exe所在目录>\debug_autotest.log
 * 
 * 注意事项:
 *   AllocConsole可能会失败（如已在控制台模式下运行），
 *   程序应能容忍这种情况继续运行
 */
void InitConsole() {
    LOG_INFO("========================================");
    LOG_INFO("InitConsole: Starting console initialization");
    
    // 尝试分配新的控制台窗口
    if (AllocConsole()) {
        LOG_INFO("InitConsole: Console window allocated successfully");
        
        // 重定向标准输出到控制台
        freopen_s(&g_consoleFile, "CONOUT$", "w", stdout);
        // 重定向标准输入到控制台
        freopen_s(&g_consoleFile, "CONIN$", "r", stdin);
        
        // 设置控制台标题
        SetConsoleTitle(L"Calibration GUI - Debug Log");
        // 设置控制台输出编码为UTF-8，支持中文
        SetConsoleOutputCP(CP_UTF8);
        
        // 输出初始化信息
        printf("=== Console Logger Initialized ===\n");
        printf("Build Time: %s\n", __TIME__);
        printf("Date: %s\n", __DATE__);
        printf("==================================\n");
    } else {
        LOG_WARN("InitConsole: Failed to allocate console (may already have one)");
    }
    
    // 获取exe所在目录
    char exeDir[MAX_PATH];
    GetExeDir(exeDir, MAX_PATH);
    
    // 构造日志文件完整路径
    char logPath[MAX_PATH];
    sprintf_s(logPath, sizeof(logPath), "%s\\debug_autotest.log", exeDir);
    
    // 打开日志文件（覆盖模式，每次启动清空旧日志）
    g_logFile = fopen(logPath, "w");
    if (g_logFile) {
        LOG_INFO("InitConsole: Log file opened: %s", logPath);
        fprintf(g_logFile, "=== Auto-test log started ===\n");
        fprintf(g_logFile, "Build Time: %s\n", __TIME__);
        fprintf(g_logFile, "Build Date: %s\n", __DATE__);
        fprintf(g_logFile, "==================================\n");
        fflush(g_logFile);
    } else {
        LOG_ERROR("InitConsole: Failed to open log file: %s", logPath);
    }
    
    LOG_INFO("InitConsole: Console initialization completed");
}

/**
 * CloseConsole - 关闭控制台并清理资源
 * 
 * 功能说明:
 *   关闭日志文件，释放控制台相关资源。
 *   应在程序退出前调用。
 */
void CloseConsole() {
    LOG_INFO("CloseConsole: Closing console and releasing resources");
    
    if (g_consoleFile) {
        fclose(g_consoleFile);
        g_consoleFile = NULL;
        LOG_DEBUG("CloseConsole: Console file closed");
    }
    
    if (g_logFile) {
        LOG_INFO("CloseConsole: Final log entry before shutdown");
        fclose(g_logFile);
        g_logFile = NULL;
        LOG_DEBUG("CloseConsole: Log file closed");
    }
}

/**
 * ============================================================
 * 第四节: 全局变量定义
 * ============================================================
 * 
 * 程序状态说明:
 *   g_step 表示当前工作流程进度：
 *     0 = 初始状态
 *     1 = 已生成标定图像
 *     2 = 已加载图像
 *     3 = 已检测圆点
 *     4 = 已完成标定
 * 
 * 标定原理:
 *   九点标定使用仿射变换，将像素坐标(x,y)转换为世界坐标(X,Y)：
 *     X = a*x + b*y + c
 *     Y = d*x + e*y + f
 *   其中(a,b,c,d,e,f)是待求解的6个参数。
 *   已知9组像素-世界坐标对应点，可以唯一确定这6个参数。
 */

// =============================================================
// 窗口句柄 - 存储各个窗口的句柄
// =============================================================
static HINSTANCE g_hInstance;          // 程序实例句柄
static HWND g_hwndMain;                 // 主窗口句柄（当前未使用）
static HWND g_hwndImage;                // 主图像显示区域窗口
static HWND g_hwndStatus;               // 状态栏文本窗口
static HWND g_hwndMethodSelect;         // 检测方法下拉选择框
static HWND g_hwndTraj = NULL;          // 轨迹显示子窗口（可关闭）
static HWND g_hwndSobel = NULL;         // Sobel边缘显示子窗口（可关闭）

// =============================================================
// 图像数据 - 存储各种处理阶段的图像
// =============================================================
static Image g_image = {0};             // 当前主图像（用户加载或生成的图像）
static Image g_trajImage = {0};         // 轨迹窗口的网格拼接图像
static Image g_sobelImage = {0};        // Sobel边缘检测结果图像

// =============================================================
// 轨迹检测中间结果 - 存储6步处理的中间图像
// =============================================================
/**
 * STEP_COUNT - 轨迹检测中间步骤的数量
 * 
 * 各步骤说明:
 *   Step 0 - 原始灰度图: 将彩色图像转为灰度
 *   Step 1 - 高斯+OTSU二值化: 模糊去噪后用OTSU自动阈值二值化
 *   Step 2 - 工件掩码: 提取工件区域的mask
 *   Step 3 - 暗条二值化: 检测图像中的暗色条状物
 *   Step 4 - 形态学清理: 开闭运算去除噪声和毛刺
 *   Step 5 - 轨迹着色图: 最终检测到的轨迹点
 */
#define STEP_COUNT 6
static Image g_stepImages[STEP_COUNT] = {0};  // 6步中间结果图像

// 各步骤的标签名称（显示在网格上方）
static const char* g_stepLabels[STEP_COUNT] = {
    "1. Original Grayscale",        // 原始灰度图
    "2. Gaussian+OTSU Binary",      // 高斯模糊 + OTSU自动二值化
    "3. Workpiece Mask",            // 工件区域掩码
    "4. Dark Bar Binary",          // 暗条二值化
    "5. Morph Cleaned Bars",       // 形态学清理后的暗条
    "6. Trajectory (colored)"      // 轨迹点（彩色显示）
};

// 网格布局参数
static const int GRID_COLS = 2;       // 网格列数
static const int GRID_ROWS = 3;       // 网格行数
static const int LABEL_HEIGHT = 22;  // 标签区域高度
static const int GRID_PADDING = 6;   // 网格间距

// =============================================================
// 标定数据 - 存储标定相关的数据
// =============================================================
static Point2D g_detectedPts[9];      // 检测到的图像坐标（9个圆心）
static int g_detectedCount = 0;      // 实际检测到的圆点数量
static AffineTransform g_transform = {0};  // 仿射变换参数（6个系数）
static double g_avgError = 0;        // 平均误差（毫米）
static double g_maxError = 0;        // 最大误差（毫米）
static int g_step = 0;               // 当前工作流程步骤
static DetectMethod g_detectMethod = METHOD_OPENCV;  // 当前使用的检测方法

// 世界坐标（物理坐标，单位：毫米）
// 标定板的9个已知点位置，呈3x3网格分布
static Point2D g_worldPts[9] = {
    {100, 100}, {400, 100}, {700, 100},    // 第一行：左上、中上、右上
    {100, 300}, {400, 300}, {700, 300},   // 第二行：左中、中心、右中
    {100, 500}, {400, 500}, {700, 500}    // 第三行：左下、中下、右下
};

static Point2D g_imagePts[9];       // 对应的图像坐标（待检测）

// =============================================================
// 轨迹数据 - 存储运动轨迹检测结果
// =============================================================
/**
 * 轨迹数据结构说明:
 *   轨迹由一系列离散的点组成，每个点记录：
 *     - 像素坐标 (g_trajPixels)
 *     - 对应的世界坐标 (g_trajWorld)
 *     - 所属的暗条ID (g_trajBarId)
 * 
 * 应用场景:
 *   用于跟踪工件或运动物体在相机视野中的运动轨迹，
 *   并将其从像素坐标转换为物理世界坐标。
 */
static Point2D g_trajPixels[50000];  // 轨迹点的像素坐标
static Point2D g_trajWorld[50000];   // 轨迹点的世界坐标
static int g_trajCount = 0;          // 轨迹点的数量
static int g_trajBarId[50000];       // 每个点所属的暗条编号

/**
 * ============================================================
 * 第五节: 图像渲染函数
 * ============================================================
 * 
 * 图像显示原理:
 *   本程序使用Windows GDI进行图像渲染，通过BITMAPINFO结构
 *   描述图像格式，然后调用SetDIBitsToDevice将图像数据
 *   直接绘制到设备上下文(DC)中。
 * 
 * 支持的图像格式:
 *   - 8位灰度图像（单通道）
 *   - 24位彩色图像（三通道BGR）
 *   - 32位彩色图像（四通道BGRA）
 */

/**
 * DrawImageToHDC - 将Image结构绘制到指定的设备上下文
 * 
 * 功能说明:
 *   将内存中的Image图像数据渲染到Windows DC上显示。
 *   支持灰度和彩色图像的自动处理。
 * 
 * 参数说明:
 *   hdc  - Windows设备上下文句柄
 *   img  - 指向Image结构的指针
 *   x, y - 绘制位置的左上角坐标
 * 
 * 实现细节:
 *   1. 分配BITMAPINFO结构（含颜色表空间）
 *   2. 设置BMP信息头（宽、高、位深等）
 *   3. 对于8位灰度图，初始化256级灰度颜色表
 *   4. 调用SetDIBitsToDevice进行实际绘制
 * 
 * 注意事项:
 *   - img不能为空且必须包含有效的data指针
 *   - 内存分配失败时函数直接返回
 */
void DrawImageToHDC(HDC hdc, Image* img, int x, int y) {
    // 参数检查
    if (!img || !img->data) {
        LOG_WARN("DrawImageToHDC: Invalid image (img=%p, data=%p)", 
                 img, img ? img->data : NULL);
        return;
    }
    
    // 计算图像位深度
    int bitCount = img->channels * 8;
    LOG_DEBUG("DrawImageToHDC: Drawing %dx%d %d-channel image at (%d,%d)", 
              img->width, img->height, img->channels, x, y);

    // 分配BMP信息结构（包含颜色表空间）
    BITMAPINFO* pbmi = (BITMAPINFO*)malloc(sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));
    if (!pbmi) {
        LOG_ERROR("DrawImageToHDC: Failed to allocate BITMAPINFO");
        return;
    }
    memset(pbmi, 0, sizeof(BITMAPINFOHEADER) + 256 * sizeof(RGBQUAD));

    // ==================== 设置BMP信息头 ====================
    pbmi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);       // 结构大小
    pbmi->bmiHeader.biWidth = img->width;                    // 图像宽度
    pbmi->bmiHeader.biHeight = -img->height;                 // 负值=自顶向下扫描
    pbmi->bmiHeader.biPlanes = 1;                            // 平面数（恒为1）
    pbmi->bmiHeader.biBitCount = (unsigned short)bitCount;   // 每像素位数
    pbmi->bmiHeader.biCompression = BI_RGB;                  // 无压缩

    // ==================== 灰度颜色表初始化 ====================
    // 对于8位灰度图，需要设置256级灰度的调色板
    // 使其能正确显示为灰度而不是伪彩色
    if (bitCount == 8) {
        for (int i = 0; i < 256; i++) {
            pbmi->bmiColors[i].rgbBlue = (unsigned char)i;
            pbmi->bmiColors[i].rgbGreen = (unsigned char)i;
            pbmi->bmiColors[i].rgbRed = (unsigned char)i;
            pbmi->bmiColors[i].rgbReserved = 0;
        }
        LOG_DEBUG("DrawImageToHDC: Grayscale color table initialized");
    }

    // ==================== 执行绘制 ====================
    SetDIBitsToDevice(hdc, x, y, img->width, img->height, 
                      0, 0, 0, img->height, img->data, pbmi, DIB_RGB_COLORS);
    
    LOG_DEBUG("DrawImageToHDC: Image drawn successfully");
    free(pbmi);  // 释放BMP结构内存
}

/**
 * ============================================================
 * 第六节: 网格展示工具函数
 * ============================================================
 * 
 * 网格展示功能:
 *   将轨迹检测的6个中间步骤图像拼接成一个2x3网格，
 *   便于用户直观查看整个图像处理流程和每一步的效果。
 * 
 * 布局结构:
 *   +------------------+------------------+
 *   | Step 0: 灰度图    | Step 1: 二值化    |
 *   +------------------+------------------+
 *   | Step 2: 工件掩码  | Step 3: 暗条      |
 *   +------------------+------------------+
 *   | Step 4: 形态学    | Step 5: 轨迹着色  |
 *   +------------------+------------------+
 */

/**
 * FreeStepImages - 释放所有步骤图像的内存
 * 
 * 功能说明:
 *   遍历g_stepImages数组，释放每个Image结构中分配的data内存。
 *   调用此函数后，所有步骤图像都将变为空（data=NULL）。
 * 
 * 调用时机:
 *   - 加载新图像前
 *   - 窗口销毁时
 *   - 程序退出时
 * 
 * 注意事项:
 *   - Image结构本身不会被释放，仅释放其data数据
 *   - 多次调用是安全的（空指针free无害）
 */
static void FreeStepImages() {
    int freedCount = 0;
    for (int i = 0; i < STEP_COUNT; i++) {
        if (g_stepImages[i].data) {
            free(g_stepImages[i].data);
            g_stepImages[i].data = NULL;  // 避免悬空指针
            freedCount++;
        }
    }
    LOG_DEBUG("FreeStepImages: Freed %d step images", freedCount);
}

/**
 * ============================================================
 * 第七节: 轨迹显示子窗口
 * ============================================================
 * 
 * 子窗口功能:
 *   创建一个独立的子窗口，以2x3网格形式展示轨迹检测的6个中间步骤。
 *   支持滚动条查看大图像，以及鼠标滚轮缩放。
 * 
 * 窗口特性:
 *   - 可关闭和重新打开
 *   - 支持水平和垂直滚动
 *   - 支持鼠标滚轮滚动
 *   - 自动计算合适的窗口大小
 */

/**
 * CreateTrajectoryWindow - 创建轨迹显示子窗口
 * 
 * 功能说明:
 *   如果窗口已存在则显示之，否则创建新窗口。
 *   窗口类使用Lambda表达式定义窗口过程。
 * 
 * 参数说明:
 *   hInstance - 程序实例句柄
 * 
 * 窗口消息处理:
 *   WM_SIZE     - 窗口大小改变时调整滚动条范围
 *   WM_HSCROLL - 水平滚动
 *   WM_VSCROLL - 垂直滚动
 *   WM_MOUSEWHEEL - 鼠标滚轮滚动
 *   WM_PAINT   - 绘制图像内容
 *   WM_DESTROY - 销毁窗口时释放资源
 */
void CreateTrajectoryWindow(HINSTANCE hInstance) {
    LOG_INFO("CreateTrajectoryWindow: Creating/activating trajectory window");
    LOG_DEBUG("CreateTrajectoryWindow: hInstance=%p, current g_hwndTraj=%p", 
              hInstance, g_hwndTraj);
    
    // ==================== 窗口已存在，直接显示 ====================
    if (g_hwndTraj) {
        LOG_DEBUG("CreateTrajectoryWindow: Window exists, bringing to front");
        ShowWindow(g_hwndTraj, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndTraj);
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        return;
    }
    
    LOG_INFO("CreateTrajectoryWindow: Creating new trajectory window");

    // ==================== 注册窗口类 ====================
    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        // 静态滚动位置变量，跨消息保持状态
        static int g_scrollX = 0, g_scrollY = 0;
        
        LOG_DEBUG("TrajectoryWindow: msg=0x%04X", msg);

        // ==================== WM_SIZE: 窗口大小改变 ====================
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
        
        // ==================== WM_HSCROLL/WM_VSCROLL: 滚动条消息 ====================
        else if (msg == WM_HSCROLL || msg == WM_VSCROLL) {
            LOG_DEBUG("TrajectoryWindow: Scroll message received");
            
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
        
        // ==================== WM_PAINT: 重绘图像 ====================
        else if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            LOG_DEBUG("TrajectoryWindow: WM_PAINT - Redrawing content");

            // ==================== 绘制轨迹图像 ====================
            if (g_trajImage.data) {
                LOG_DEBUG("TrajectoryWindow: Drawing image %dx%d", 
                          g_trajImage.width, g_trajImage.height);
                
                // 设置视口原点（用于滚动）
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

                // ==================== 计算网格布局参数 ====================
                int srcImgW = g_stepImages[0].width > 0 ? g_stepImages[0].width : 800;
                int srcImgH = g_stepImages[0].height > 0 ? g_stepImages[0].height : 600;
                int cellW = srcImgW / GRID_COLS;
                int cellH = (int)(cellW * (double)srcImgH / srcImgW + 0.5);
                int cellTotalH = LABEL_HEIGHT + cellH;
                LOG_DEBUG("TrajectoryWindow: Grid layout - cellW=%d, cellH=%d", cellW, cellH);

                // ==================== 绘制步骤标签 ====================
                SetBkMode(hdc, TRANSPARENT);
                // 创建粗体等宽字体用于标签显示
                HFONT hFont = CreateFontA(14, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                    DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                    CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, "Consolas");
                HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);
                SetTextColor(hdc, RGB(255, 255, 0));  // 黄色文字

                // 遍历6个步骤，在每个网格单元格上方绘制标签
                for (int s = 0; s < STEP_COUNT; s++) {
                    int col = s % GRID_COLS;
                    int row = s / GRID_COLS;
                    int labelX = GRID_PADDING + col * (cellW + GRID_PADDING);
                    int labelY = GRID_PADDING + row * (cellTotalH + GRID_PADDING);
                    TextOutA(hdc, labelX + 4, labelY + 3, 
                             g_stepLabels[s], (int)strlen(g_stepLabels[s]));
                    LOG_DEBUG("TrajectoryWindow: Drew label %d: %s", s, g_stepLabels[s]);
                }

                SelectObject(hdc, hOldFont);
                DeleteObject(hFont);

                SetViewportOrgEx(hdc, 0, 0, NULL);  // 恢复视口
            } else {
                // 图像为空时显示占位文字
                RECT rc; GetClientRect(hwnd, &rc);
                FillRect(hdc, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdc, TRANSPARENT);
                TextOutA(hdc, 10, 10, "No trajectory image", 19);
                LOG_DEBUG("TrajectoryWindow: No image to display");
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        
        // ==================== WM_DESTROY: 窗口销毁 ====================
        else if (msg == WM_DESTROY) {
            LOG_INFO("TrajectoryWindow: Window being destroyed");
            
            g_hwndTraj = NULL;  // 清空窗口句柄
            
            // 释放图像内存
            if (g_trajImage.data) {
                free(g_trajImage.data);
                g_trajImage.data = NULL;
                LOG_DEBUG("TrajectoryWindow: Freed trajectory image");
            }
            
            // 释放所有步骤图像
            FreeStepImages();
            LOG_DEBUG("TrajectoryWindow: Freed step images");
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    };
    wc.hInstance = hInstance;
    wc.lpszClassName = L"TrajectoryWindowClass";
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    
    // 注册窗口类
    if (!RegisterClass(&wc)) {
        LOG_ERROR("CreateTrajectoryWindow: Failed to register window class");
        return;
    }
    LOG_DEBUG("CreateTrajectoryWindow: Window class registered");

    // 计算窗口初始大小（基于内容图像尺寸）
    int contentW = g_trajImage.width > 0 ? g_trajImage.width : 640;
    int contentH = g_trajImage.height > 0 ? g_trajImage.height : 480;
    int initW = contentW + 20;   // 留出边距
    int initH = contentH + 50;   // 留出标题栏空间
    
    // 限制最大窗口大小
    if (initW > 1280) initW = 1280;
    if (initH > 900) initH = 900;
    
    LOG_DEBUG("CreateTrajectoryWindow: Window size %dx%d", initW, initH);

    // 创建窗口
    g_hwndTraj = CreateWindowEx(0, L"TrajectoryWindowClass", L"Detection Steps (2x3 Grid)",
        WS_OVERLAPPEDWINDOW | WS_VSCROLL | WS_HSCROLL | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, initW, initH,
        NULL, NULL, hInstance, NULL);
    
    if (g_hwndTraj) {
        LOG_INFO("CreateTrajectoryWindow: Window created successfully, hwnd=%p", g_hwndTraj);
    } else {
        LOG_ERROR("CreateTrajectoryWindow: Failed to create window");
    }
}

/**
 * ============================================================
 * 第八节: Sobel边缘显示窗口
 * ============================================================
 * 
 * Sobel算子说明:
 *   Sobel算子是一种离散微分算子，用于边缘检测。
 *   它通过计算图像亮度函数的梯度来检测边缘。
 *   X方向和Y方向分别计算后取模得到边缘强度。
 */

/**
 * CreateSobelWindow - 创建Sobel边缘显示窗口
 * 
 * 功能说明:
 *   创建一个简单的窗口用于显示Sobel边缘检测的结果图像。
 *   如果窗口已存在则直接显示。
 * 
 * 参数说明:
 *   hInstance - 程序实例句柄
 */
void CreateSobelWindow(HINSTANCE hInstance) {
    LOG_INFO("CreateSobelWindow: Creating/activating Sobel window");
    
    // 如果窗口已存在，直接显示
    if (g_hwndSobel) {
        LOG_DEBUG("CreateSobelWindow: Window exists, bringing to front");
        ShowWindow(g_hwndSobel, SW_SHOWNORMAL);
        SetForegroundWindow(g_hwndSobel);
        InvalidateRect(g_hwndSobel, NULL, FALSE);
        return;
    }
    
    LOG_DEBUG("CreateSobelWindow: Creating new Sobel window");

    // ==================== 注册Sobel窗口类 ====================
    WNDCLASS wc = {0};
    wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) -> LRESULT {
        LOG_DEBUG("SobelWindow: msg=0x%04X", msg);
        
        // WM_PAINT: 绘制Sobel边缘图像
        if (msg == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            LOG_DEBUG("SobelWindow: WM_PAINT - Redrawing Sobel image");
            
            if (g_sobelImage.data) {
                // 绘制Sobel边缘图像
                int bitCount = g_sobelImage.channels * 8;
                int rowSize = ((g_sobelImage.width * g_sobelImage.channels + 3) / 4) * 4;
                BITMAPINFO bmi = {0};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = g_sobelImage.width;
                bmi.bmiHeader.biHeight = -g_sobelImage.height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = (unsigned short)bitCount;
                bmi.bmiHeader.biCompression = BI_RGB;
                
                LOG_DEBUG("SobelWindow: Drawing image %dx%d", 
                         g_sobelImage.width, g_sobelImage.height);
                
                SetDIBitsToDevice(hdc, 0, 0, g_sobelImage.width, g_sobelImage.height, 
                                 0, 0, 0, g_sobelImage.height, 
                                 g_sobelImage.data, &bmi, DIB_RGB_COLORS);
            } else {
                // 无图像时显示占位文字
                RECT rc; GetClientRect(hwnd, &rc);
                FillRect(hdc, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdc, TRANSPARENT);
                TextOutA(hdc, 10, 10, "No Sobel image", 14);
                LOG_DEBUG("SobelWindow: No Sobel image available");
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        
        // WM_DESTROY: 释放资源
        else if (msg == WM_DESTROY) {
            LOG_INFO("SobelWindow: Window destroyed, freeing resources");
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
    
    if (!RegisterClass(&wc)) {
        LOG_ERROR("CreateSobelWindow: Failed to register window class");
        return;
    }
    LOG_DEBUG("CreateSobelWindow: Window class registered");

    // 创建Sobel窗口
    int windowW = g_sobelImage.width > 0 ? g_sobelImage.width + 100 : 640;
    int windowH = g_sobelImage.height > 0 ? g_sobelImage.height + 150 : 480;
    LOG_DEBUG("CreateSobelWindow: Window size %dx%d", windowW, windowH);
    
    g_hwndSobel = CreateWindowEx(0, L"SobelWindowClass", L"Sobel Edge Detection",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT,
        windowW, windowH,
        NULL, NULL, hInstance, NULL);
    
    if (g_hwndSobel) {
        LOG_INFO("CreateSobelWindow: Window created, hwnd=%p", g_hwndSobel);
    } else {
        LOG_ERROR("CreateSobelWindow: Failed to create window");
    }
}

/**
 * ============================================================
 * 第九节: 网格图像拼接展示
 * ============================================================
 */

/**
 * ShowTrajectoryImage - 将6个步骤图像拼接成2x3网格
 * 
 * 功能说明:
 *   读取g_stepImages中的6个中间结果图像，将其缩放并排列成
 *   一个2行3列的网格图像，便于在一个窗口中查看整个处理流程。
 * 
 * 参数说明:
 *   src        - 原始图像（用于计算缩放比例）
 *   trajPixels - 轨迹点像素坐标数组
 *   trajCount  - 轨迹点数量
 * 
 * 实现步骤:
 *   1. 计算网格布局参数（单元格大小、间距等）
 *   2. 为整个网格分配内存
 *   3. 遍历6个步骤图像，逐像素缩放复制到网格中
 *   4. 更新全局g_trajImage供窗口绘制
 * 
 * 图像缩放算法:
 *   使用最邻近插值，计算源图像到目标单元格的映射关系。
 *   目标像素(x,y)对应源像素(x * srcW / cellW, y * srcH / cellH)
 */
void ShowTrajectoryImage(Image* src, Point2D* trajPixels, int trajCount) {
    // 参数检查
    if (!src) {
        LOG_ERROR("ShowTrajectoryImage: NULL source image");
        return;
    }
    
    int imgW = src->width;
    int imgH = src->height;
    LOG_INFO("ShowTrajectoryImage: Creating grid from %dx%d image, %d trajectory points", 
             imgW, imgH, trajCount);

    // ==================== 计算网格布局参数 ====================
    // 单元格宽度 = 原图宽度 / 列数
    int cellW = imgW / GRID_COLS;
    // 单元格高度 = 保持宽高比
    int cellH = (int)(cellW * (double)imgH / imgW + 0.5);
    // 每个单元格总高度 = 标签高度 + 图像高度
    int cellTotalH = LABEL_HEIGHT + cellH;
    // 整个网格宽度
    int gridW = GRID_COLS * cellW + (GRID_COLS + 1) * GRID_PADDING;
    // 整个网格高度
    int gridH = GRID_ROWS * cellTotalH + (GRID_ROWS + 1) * GRID_PADDING;
    
    LOG_DEBUG("ShowTrajectoryImage: Grid layout - gridW=%d, gridH=%d, cellW=%d, cellH=%d",
              gridW, gridH, cellW, cellH);

    // ==================== 分配网格图像内存 ====================
    int dstChannels = 1;       // 输出为灰度图像
    int dstRowSize = gridW;     // 每行字节数
    int totalSize = dstRowSize * gridH;  // 总内存大小
    
    LOG_DEBUG("ShowTrajectoryImage: Allocating %d bytes for grid image", totalSize);

    // 释放旧的网格图像
    if (g_trajImage.data) free(g_trajImage.data);
    
    // 设置网格图像属性
    g_trajImage.width = gridW;
    g_trajImage.height = gridH;
    g_trajImage.channels = dstChannels;
    g_trajImage.data = (unsigned char*)malloc(totalSize);
    
    if (!g_trajImage.data) {
        LOG_ERROR("ShowTrajectoryImage: Failed to allocate %d bytes", totalSize);
        return;
    }
    
    memset(g_trajImage.data, 0, totalSize);  // 初始化为黑色
    LOG_DEBUG("ShowTrajectoryImage: Grid image allocated successfully");

    // ==================== 逐个步骤图像复制到网格 ====================
    int imagesProcessed = 0;
    for (int s = 0; s < STEP_COUNT; s++) {
        Image* stepImg = &g_stepImages[s];
        if (!stepImg->data) {
            LOG_WARN("ShowTrajectoryImage: Step %d has no image data", s);
            continue;
        }

        // 计算该步骤图像在网格中的位置
        int col = s % GRID_COLS;
        int row = s / GRID_COLS;
        int cellX = GRID_PADDING + col * (cellW + GRID_PADDING);
        int cellY = GRID_PADDING + row * (cellTotalH + GRID_PADDING) + LABEL_HEIGHT;

        int srcW = stepImg->width;
        int srcH = stepImg->height;
        
        LOG_DEBUG("ShowTrajectoryImage: Processing step %d (%s), cell at (%d,%d)",
                  s, g_stepLabels[s], cellX, cellY);

        // ==================== 像素级缩放复制 ====================
        for (int dy = 0; dy < cellH; dy++) {
            // 计算源图像对应行（最邻近插值）
            int sy = dy * srcH / cellH;
            for (int dx = 0; dx < cellW; dx++) {
                // 计算源图像对应列
                int sx = dx * srcW / cellW;
                
                int dstX = cellX + dx;
                int dstY = cellY + dy;
                if (dstX >= gridW || dstY >= gridH) continue;

                // 提取灰度值
                unsigned char gray = 0;
                if (stepImg->channels == 1) {
                    // 灰度图像直接取值
                    gray = stepImg->data[sy * srcW + sx];
                } else {
                    // 彩色图像转灰度（加权平均法）
                    int srcOff = (sy * srcW + sx) * 3;
                    unsigned char b = stepImg->data[srcOff];
                    unsigned char g = stepImg->data[srcOff + 1];
                    unsigned char r = stepImg->data[srcOff + 2];
                    // 标准灰度转换系数
                    gray = (unsigned char)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f);
                }
                g_trajImage.data[dstY * dstRowSize + dstX] = gray;
            }
        }
        
        // 在每个单元格下方画一条分隔线
        int lineY = cellY + cellH;
        if (lineY < gridH) {
            for (int x = cellX; x < cellX + cellW && x < gridW; x++) {
                g_trajImage.data[lineY * dstRowSize + x] = 80;  // 深灰色
            }
        }
        
        imagesProcessed++;
    }
    
    LOG_INFO("ShowTrajectoryImage: Processed %d/%d step images", 
             imagesProcessed, STEP_COUNT);

    // ==================== 通知窗口重绘 ====================
    if (g_hwndTraj) {
        LOG_DEBUG("ShowTrajectoryImage: Invalidating trajectory window");
        InvalidateRect(g_hwndTraj, NULL, FALSE);
        RECT rc;
        GetClientRect(g_hwndTraj, &rc);
        SendMessage(g_hwndTraj, WM_SIZE, 0, MAKELPARAM(rc.right, rc.bottom));
    }
}

/**
 * ============================================================
 * 第十节: 主窗口消息处理过程
 * ============================================================
 * 
 * 窗口消息说明:
 *   WM_CREATE    - 窗口创建时初始化控件和资源
 *   WM_USER+1    - 自定义消息，触发自动测试流程
 *   WM_COMMAND   - 按钮点击等命令消息
 *   WM_PAINT     - 窗口重绘
 *   WM_DESTROY   - 窗口销毁
 * 
 * 按钮ID对应关系:
 *   1  - Generate Image   生成标定图像
 *   2  - Load Image       加载图像
 *   3  - Detect Circles    检测圆点
 *   4  - Calibrate         执行标定
 *   5  - Save Results      保存结果
 *   6  - Test Transform    测试变换
 *   7  - TestImage         加载测试图像
 *   8  - AutoTrajTest      自动轨迹测试
 */

/**
 * WindowProc - 主窗口的消息处理函数
 * 
 * 功能说明:
 *   处理所有发送到主窗口的消息，包括按钮点击、窗口重绘等。
 *   这是整个应用程序的核心消息分发中心。
 * 
 * 参数说明:
 *   hwnd   - 窗口句柄
 *   msg    - 消息类型
 *   wParam - 消息参数（通常为附加信息如按钮ID）
 *   lParam - 消息参数
 * 
 * 返回值:
 *   0 表示消息已处理，非0表示使用默认处理
 */
LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // 静态字体句柄，跨消息保持（避免重复创建）
    static HFONT hFontTitle, hFontBtn, hFontText, hFontMono;
    // 状态栏文本
    static char statusText[256] = "Ready - Click buttons to start";

    LOG_DEBUG("WindowProc: msg=0x%04X, wParam=0x%08X, lParam=0x%08X", 
              msg, wParam, lParam);

    switch (msg) {
    
    // =============================================================
    // WM_CREATE: 窗口创建，初始化所有控件
    // =============================================================
    case WM_CREATE:
        {
            LOG_INFO("WindowProc: WM_CREATE - Initializing main window");
            
            // ==================== 创建字体 ====================
            // 标题字体 - 大号粗体
            hFontTitle = CreateFont(24, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            // 按钮字体 - 中号粗体
            hFontBtn = CreateFont(14, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            // 文本字体 - 中号常规
            hFontText = CreateFont(14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Arial");
            // 等宽字体 - 用于显示数值和代码
            hFontMono = CreateFont(13, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Consolas");
            
            LOG_DEBUG("WindowProc: Fonts created");

            // ==================== 创建按钮 ====================
            // 按钮布局参数
            int btnW = 150, btnH = 32, btnGap = 10;
            
            // 第一行：按钮 1-4（核心标定流程）
            int btnY1 = 700, startX = 20;
            
            // 按钮1: 生成标定图像
            CreateWindow(L"BUTTON", L"1. Generate Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)1, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 1 (Generate Image) created");

            // 按钮2: 加载标定图像
            CreateWindow(L"BUTTON", L"2. Load Image",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)2, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 2 (Load Image) created");

            // 按钮3: 检测圆点
            CreateWindow(L"BUTTON", L"3. Detect Circles",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)3, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 3 (Detect Circles) created");

            // 按钮4: 执行标定
            CreateWindow(L"BUTTON", L"4. Calibrate",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY1, btnW, btnH, hwnd, (HMENU)4, NULL, NULL);
            LOG_DEBUG("WindowProc: Button 4 (Calibrate) created");

            // 第二行：按钮 5-8（辅助功能）
            int btnY2 = btnY1 + btnH + 8;
            startX = 20;
            
            // 按钮5: 保存结果
            CreateWindow(L"BUTTON", L"5. Save Results",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)5, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 5 (Save Results) created");

            // 按钮6: 测试变换
            CreateWindow(L"BUTTON", L"6. Test Transform",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)6, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 6 (Test Transform) created");

            // 按钮7: 加载测试图像
            CreateWindow(L"BUTTON", L"7. TestImage",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)7, NULL, NULL);
            startX += btnW + btnGap;
            LOG_DEBUG("WindowProc: Button 7 (TestImage) created");

            // 按钮8: 自动轨迹测试
            CreateWindow(L"BUTTON", L"8. AutoTrajTest",
                WS_TABSTOP | WS_VISIBLE | WS_CHILD | BS_PUSHBUTTON,
                startX, btnY2, btnW, btnH, hwnd, (HMENU)8, NULL, NULL);
            LOG_DEBUG("WindowProc: Button 8 (AutoTrajTest) created");

            // ==================== 创建图像显示区域 ====================
            g_hwndImage = CreateWindow(L"STATIC", L"",
                WS_CHILD | WS_VISIBLE | SS_BLACKFRAME,
                20, 70, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT, 
                hwnd, NULL, NULL, NULL);
            LOG_DEBUG("WindowProc: Image display area created (%dx%d)", 
                     CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT);

            g_hwndImage = CreateWindow(L"STATIC", L"",
                WS_CHILD | WS_VISIBLE | SS_BLACKFRAME,
                20, 70, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT, hwnd, NULL, NULL, NULL);

            // ==================== 创建状态栏 ====================
            g_hwndStatus = CreateWindow(L"STATIC", L"Ready - Click buttons to start",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                20, CALIB_IMAGE_HEIGHT + 75, 1000, 25, hwnd, NULL, NULL, NULL);
            SendMessage(g_hwndStatus, WM_SETFONT, (WPARAM)hFontText, TRUE);
            LOG_DEBUG("WindowProc: Status bar created");

            // ==================== 创建标题 ====================
            HWND hTitle = CreateWindow(L"STATIC", L"9-Point Calibration GUI",
                WS_CHILD | WS_VISIBLE,
                CALIB_IMAGE_WIDTH + 30, 20, 350, 35, hwnd, NULL, NULL, NULL);
            SendMessage(hTitle, WM_SETFONT, (WPARAM)hFontTitle, TRUE);
            LOG_DEBUG("WindowProc: Title created");

            // ==================== 创建检测方法选择框 ====================
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
            LOG_DEBUG("WindowProc: Method selector created");
            
            LOG_INFO("WindowProc: WM_CREATE completed - all controls initialized");
        }
        return 0;

    // =============================================================
    // WM_USER + 1: 自定义自动测试消息
    // =============================================================
    case WM_USER + 1:
        {
            LOG_INFO("=== Auto-test triggered ===");
            char exeDir[MAX_PATH];
            GetExeDir(exeDir, MAX_PATH);
            char filename[MAX_PATH];
            sprintf_s(filename, sizeof(filename), "%s\\test_auto.bmp", exeDir);

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

    // =============================================================
    // WM_COMMAND: 处理按钮点击等命令消息
    // =============================================================
    case WM_COMMAND:
        {
            int btnId = LOWORD(wParam);  // 从wParam低字提取按钮ID
            char msgBuf[1024 * 10];
            LOG_INFO("WindowProc: WM_COMMAND - button ID = %d", btnId);

            switch (btnId) {
            // =============================================================
            // 按钮1: 生成标定图像
            // 功能: 调用算子库生成带有9个圆点的标准标定图案
            // =============================================================
            case 1:  // Generate Image
                LOG_INFO("Button 1: Generate Image - START");
                if (g_image.data) {
                    free(g_image.data);
                    g_image.data = NULL;
                    LOG_DEBUG("Button 1: Freed old image data");
                }
                if (CreateCalibrationImage(&g_image, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT)) {
                    LOG_INFO("Button 1: Image created (%dx%d)", g_image.width, g_image.height);
                    SaveBMP("calibration_9points.bmp", &g_image);
                    LOG_INFO("Button 1: Image saved to calibration_9points.bmp");
                    strcpy_s(statusText, sizeof(statusText), "Step 1: Calibration image generated and saved");
                    g_step = 1;
                    LOG_INFO("Button 1: g_step updated to %d", g_step);
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                } else {
                    LOG_ERROR("Button 1: Failed to create calibration image");
                }
                break;

            // =============================================================
            // 按钮2: 加载图像
            // 功能: 弹出文件选择对话框，加载用户指定的标定图像
            // =============================================================
            case 2: { // Load Image
                LOG_INFO("Button 2: Load Image - START");
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
                    LOG_INFO("Button 2: File selected: %s", filename);
                    Image loadedImg = {0};
                    if (LoadBMP(filename, &loadedImg)) {
                        LOG_INFO("Button 2: Image loaded successfully");
                        if (g_image.data) free(g_image.data);
                        g_image = loadedImg;

                        sprintf_s(statusText, sizeof(statusText), "Loaded: %s (%dx%d)", 
                                 filename, g_image.width, g_image.height);
                        g_step = 2;
                        LOG_INFO("Button 2: g_step updated to %d", g_step);
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                    } else {
                        LOG_ERROR("Button 2: LoadBMP failed for: %s", filename);
                        sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s", filename);
                        MessageBoxA(hwnd, msgBuf, "Load Error", MB_OK | MB_ICONWARNING);
                        strcpy_s(statusText, sizeof(statusText), "Error: Cannot load image");
                    }
                } else {
                    LOG_INFO("Button 2: File dialog cancelled by user");
                }
                break;
            }

            // =============================================================
            // 按钮3: 检测圆点
            // 功能: 使用选定的检测方法识别图像中的9个圆点中心
            // =============================================================
            case 3: { // Detect Circles
                LOG_INFO("Button 3: Detect Circles - START");
                // 检查图像是否已加载
                if (!g_image.data) {
                    LOG_WARN("Button 3: No image loaded");
                    strcpy_s(statusText, sizeof(statusText), "Error: No image loaded");
                    MessageBoxA(hwnd, "Please complete Step 1 or Step 2 to load an image first.", 
                              "No Image", MB_OK | MB_ICONWARNING);
                } else {
                    // 获取检测方法
                    int sel = SendMessage(g_hwndMethodSelect, CB_GETCURSEL, 0, 0);
                    g_detectMethod = (DetectMethod)sel;
                    LOG_INFO("Button 3: Detection method = %d", g_detectMethod);

                    // 执行圆点检测
                    DetectCircles(&g_image, g_detectedPts, &g_detectedCount);
                    LOG_INFO("Button 3: DetectCircles returned, count = %d", g_detectedCount);

                    // 保存检测结果
                    for (int i = 0; i < g_detectedCount && i < 9; i++) {
                        g_imagePts[i] = g_detectedPts[i];
                        LOG_DEBUG("Button 3: Point %d: (%.2f, %.2f)", 
                                 i+1, g_detectedPts[i].x, g_detectedPts[i].y);
                    }
                    DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 0);

                    // 判断检测结果
                    if (g_detectedCount < 3) {
                        LOG_ERROR("Button 3: Insufficient circles (%d)", g_detectedCount);
                        sprintf_s(msgBuf, sizeof(msgBuf), 
                                 "Circle detection failed!\nDetected only %d circles.", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Error: Insufficient circles detected");
                        MessageBoxA(hwnd, msgBuf, "Detection Error", MB_OK | MB_ICONERROR);
                    } else {
                        LOG_INFO("Button 3: Successfully detected %d circles", g_detectedCount);
                        sprintf_s(msgBuf, sizeof(msgBuf), 
                                 "Detected %d circles successfully!", g_detectedCount);
                        strcpy_s(statusText, sizeof(statusText), "Step 3: Detected circles (green markers)");
                        MessageBoxA(hwnd, msgBuf, "Detection Complete", MB_OK | MB_ICONINFORMATION);
                        g_step = 3;
                        LOG_INFO("Button 3: g_step = %d", g_step);
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                    }
                }
                break;
            }

            // =============================================================
            // 按钮4: 执行标定
            // 功能: 根据检测到的圆点坐标和已知的世界坐标，计算仿射变换矩阵
            // =============================================================
            case 4: { // Calibrate
                LOG_INFO("Button 4: Calibrate - START");
                // 检查前置条件：是否已完成圆点检测
                if (g_step < 3) {
                    LOG_WARN("Button 4: Step validation failed (g_step=%d < 3)", g_step);
                    MessageBoxA(hwnd, "Please complete Step 3 (Detect Circles) before calibration.", 
                              "Step Error", MB_OK | MB_ICONWARNING);
                } else {
                    LOG_INFO("Button 4: Starting nine-point calibration...");
                    // 执行九点标定算法
                    if (CalibrateNinePoint(g_imagePts, g_worldPts, 9, &g_transform)) {
                        LOG_INFO("Button 4: Calibration succeeded!");
                        LOG_DEBUG("Button 4: Transform params: a=%.6f, b=%.6f, c=%.2f", 
                                 g_transform.a, g_transform.b, g_transform.c);
                        LOG_DEBUG("Button 4: Transform params: d=%.6f, e=%.6f, f=%.2f", 
                                 g_transform.d, g_transform.e, g_transform.f);
                        
                        // 同步变换参数到算子层（供后续使用）
                        CalibSetTransform(g_transform);
                        
                        // 计算并评估标定误差
                        CalculateError(g_imagePts, g_worldPts, 9, g_transform, &g_avgError, &g_maxError);
                        LOG_INFO("Button 4: Error - avg=%.4f mm, max=%.4f mm", g_avgError, g_maxError);
                        
                        // 格式化结果消息
                        sprintf_s(msgBuf, sizeof(msgBuf), 
                                 "Calibration Complete!\n\n"
                                 "X = %.4f*x + %.4f*y + %.2f\n"
                                 "Y = %.4f*x + %.4f*y + %.2f\n\n"
                                 "Avg Error: %.4f mm\n"
                                 "Max Error: %.4f mm",
                                 g_transform.a, g_transform.b, g_transform.c, 
                                 g_transform.d, g_transform.e, g_transform.f, 
                                 g_avgError, g_maxError);
                        DrawDetectedCircles(&g_image, g_detectedPts, g_detectedCount, 128);
                        strcpy_s(statusText, sizeof(statusText), "Step 4: Calibration complete!");
                        g_step = 4;
                        LOG_INFO("Button 4: g_step = %d", g_step);
                        InvalidateRect(g_hwndImage, NULL, FALSE);
                        MessageBoxA(hwnd, msgBuf, "Calibration Results", MB_OK | MB_ICONINFORMATION);
                    } else {
                        LOG_ERROR("Button 4: Calibration failed");
                        sprintf_s(msgBuf, sizeof(msgBuf), 
                                 "Calibration Failed! Detected: %d points", g_detectedCount);
                        MessageBoxA(hwnd, msgBuf, "Calibration Failed", MB_OK | MB_ICONERROR);
                    }
                }
                break;
            }

            // =============================================================
            // 按钮5: 保存结果
            // 功能: 将标定参数保存到文本文件，将结果图像保存为BMP
            // =============================================================
            case 5:  // Save Results
                LOG_INFO("Button 5: Save Results - START");
                // 保存结果图像
                if (g_image.data) {
                    if (SaveBMP("calibration_result.bmp", &g_image)) {
                        LOG_INFO("Button 5: Image saved to calibration_result.bmp");
                    } else {
                        LOG_WARN("Button 5: Failed to save image");
                    }
                }
                // 保存标定参数
                {
                    FILE* fp;
                    if (fopen_s(&fp, "calibration_params.txt", "w") == 0) {
                        LOG_INFO("Button 5: Saving parameters to calibration_params.txt");
                        fprintf(fp, "# 9-Point Calibration Parameters\n");
                        fprintf(fp, "# ====================================\n");
                        fprintf(fp, "# Affine Transform: X = a*x + b*y + c\n");
                        fprintf(fp, "#                    Y = d*x + e*y + f\n\n");
                        fprintf(fp, "a=%.10f\nb=%.10f\nc=%.10f\n", 
                               g_transform.a, g_transform.b, g_transform.c);
                        fprintf(fp, "d=%.10f\ne=%.10f\nf=%.10f\n", 
                               g_transform.d, g_transform.e, g_transform.f);
                        fprintf(fp, "\n# Error Statistics:\n");
                        fprintf(fp, "# Average Error: %.6f mm\n", g_avgError);
                        fprintf(fp, "# Maximum Error: %.6f mm\n", g_maxError);
                        fclose(fp);
                        LOG_INFO("Button 5: Parameters saved successfully");
                    } else {
                        LOG_ERROR("Button 5: Failed to open calibration_params.txt");
                    }
                }
                sprintf_s(msgBuf, sizeof(msgBuf), 
                         "Saved:\n- calibration_params.txt\n- calibration_result.bmp\n\n"
                         "Avg Error: %.4f mm\nMax Error: %.4f mm", 
                         g_avgError, g_maxError);
                strcpy_s(statusText, sizeof(statusText), "Results saved to files");
                MessageBoxA(hwnd, msgBuf, "Save Complete", MB_OK | MB_ICONINFORMATION);
                break;

            // =============================================================
            // 按钮6: 测试坐标变换
            // 功能: 输入测试点的像素坐标，验证仿射变换是否正确
            // =============================================================
            case 6: {  // Test Transform
                LOG_INFO("Button 6: Test Transform - START");
                if (g_step < 4) {
                    LOG_WARN("Button 6: Calibration not done (g_step=%d < 4)", g_step);
                    break;
                }
                // 测试点：图像中心 (400, 300)
                Point2D testPixel = { 400, 300 };
                Point2D testWorld = ImageToWorld(testPixel, g_transform);
                LOG_INFO("Button 6: Test pixel (%.0f, %.0f) -> World (%.2f, %.2f)", 
                        testPixel.x, testPixel.y, testWorld.x, testWorld.y);
                sprintf_s(msgBuf, sizeof(msgBuf), 
                         "Test Transform:\n\n"
                         "Input pixel: (%.0f, %.0f)\n"
                         "Output world: (%.2f, %.2f) mm",
                         testPixel.x, testPixel.y, testWorld.x, testWorld.y);
                strcpy_s(statusText, sizeof(statusText), "Transform test complete");
                MessageBoxA(hwnd, msgBuf, "Transform Test", MB_OK);
                break;
            }
            
            // =============================================================
            // 按钮7: 加载测试图像
            // 功能: 加载任意图像，如已完成标定则执行轨迹检测
            // =============================================================
            case 7: { // TestImage
                    LOG_INFO("Button 7: TestImage - START");
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
                        LOG_INFO("Button 7: File selected: %s", filename);
                        Image loadedImg = { 0 };
                        if (LoadBMP(filename, &loadedImg)) {
                            LOG_INFO("Button 7: Image loaded (%dx%d)", 
                                    loadedImg.width, loadedImg.height);
                            if (g_image.data) free(g_image.data);
                            g_image = loadedImg;

                            sprintf_s(statusText, sizeof(statusText), 
                                    "Loaded: %s (%dx%d)", filename, g_image.width, g_image.height);

                            // 如果已完成标定，自动执行轨迹检测
                            if (g_step >= 4) {
                                LOG_INFO("Button 7: Calibration available, starting trajectory detection...");
                                FreeStepImages();
                                DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount,
                                                      g_stepImages, g_trajBarId, STEP_COUNT);
                                LOG_INFO("Button 7: Trajectory detection complete, found %d points", 
                                        g_trajCount);
                                
                                // 统计各暗条的点数量
                                int barCount[20] = {0};
                                for (int i = 0; i < g_trajCount && i < 50000; i++) {
                                    if (g_trajBarId[i] >= 0 && g_trajBarId[i] < 20) {
                                        barCount[g_trajBarId[i]]++;
                                    }
                                }
                                LOG_INFO("Button 7: Points per bar:");
                                for (int i = 0; i < 16; i++) {
                                    if (barCount[i] > 0) {
                                        LOG_INFO("  Bar %d: %d points", i, barCount[i]);
                                    }
                                }
                                
                                sprintf_s(msgBuf, sizeof(msgBuf),
                                    "Detected %d trajectory points\n\n"
                                    "Start: (%.2f, %.2f)\n"
                                    "End: (%.2f, %.2f)",
                                    g_trajCount,
                                    g_trajCount > 0 ? g_trajWorld[0].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[0].y : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].x : 0,
                                    g_trajCount > 0 ? g_trajWorld[g_trajCount-1].y : 0);
                                MessageBoxA(hwnd, msgBuf, "Trajectory Detected", MB_OK);

                                ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                                CreateTrajectoryWindow((HINSTANCE)GetWindowLongPtr(hwnd, GWLP_HINSTANCE));
                                strcat_s(statusText, sizeof(statusText), " | Trajectory in new window");
                            } else {
                                LOG_INFO("Button 7: Calibration not done, skipping trajectory detection");
                            }

                            InvalidateRect(g_hwndImage, NULL, FALSE);
                        } else {
                            LOG_ERROR("Button 7: Failed to load image: %s", filename);
                            sprintf_s(msgBuf, sizeof(msgBuf), "Cannot load file:\n%s", filename);
                            MessageBoxA(hwnd, msgBuf, "Load Error", MB_OK | MB_ICONWARNING);
                            strcpy_s(statusText, sizeof(statusText), "Error: Cannot load image");
                        }
                    } else {
                        LOG_INFO("Button 7: File dialog cancelled by user");
                    }
                    break;
                }

            // =============================================================
            // 按钮8: 自动轨迹测试
            // 功能: 自动加载test_auto.bmp，执行16暗条轨迹检测，展示6步中间结果
            // =============================================================
            case 8: { // AutoTrajTest
                LOG_INFO("Button 8: AutoTrajTest - START");
                // 获取exe所在目录
                char exeDir[MAX_PATH];
                GetExeDir(exeDir, MAX_PATH);
                char trajTestPath[MAX_PATH];
                sprintf_s(trajTestPath, sizeof(trajTestPath), "%s\\test_auto.bmp", exeDir);
                LOG_DEBUG("Button 8: Looking for test image: %s", trajTestPath);
                
                Image loadedImg = { 0 };
                if (LoadBMP(trajTestPath, &loadedImg)) {
                    LOG_INFO("Button 8: Image loaded successfully");
                    LOG_DEBUG("Button 8: Image info - %dx%d, channels=%d", 
                             loadedImg.width, loadedImg.height, loadedImg.channels);
                    
                    if (g_image.data) free(g_image.data);
                    g_image = loadedImg;

                    // 释放旧步骤图像
                    FreeStepImages();
                    
                    // 执行OpenCV轨迹检测（包含6步中间结果）
                    LOG_INFO("Button 8: Starting OpenCV trajectory detection...");
                    DetectTrajectoryOpenCV(&g_image, g_trajPixels, &g_trajCount,
                                          g_stepImages, g_trajBarId, STEP_COUNT);
                    LOG_INFO("Button 8: Detection complete, found %d trajectory points", g_trajCount);

                    // 统计各暗条的点数量
                    int barCount[20] = {0};
                    for (int i = 0; i < g_trajCount && i < 50000; i++) {
                        if (g_trajBarId[i] >= 0 && g_trajBarId[i] < 20) {
                            barCount[g_trajBarId[i]]++;
                        }
                    }
                    LOG_INFO("Button 8: Detection results by bar:");
                    for (int i = 0; i < 20; i++) {
                        if (barCount[i] > 0) {
                            LOG_INFO("  Bar %d: %d points", i, barCount[i]);
                        }
                    }

                    // 在网格窗口中展示6步中间结果
                    ShowTrajectoryImage(&g_image, g_trajPixels, g_trajCount);
                    CreateTrajectoryWindow((HINSTANCE)GetWindowLongPtr(hwnd, GWLP_HINSTANCE));

                    sprintf_s(statusText, sizeof(statusText),
                        "AutoTrajTest: %s | %d traj points", trajTestPath, g_trajCount);
                    LOG_INFO("Button 8: Status updated - %s", statusText);
                    InvalidateRect(g_hwndImage, NULL, FALSE);
                } else {
                    LOG_ERROR("Button 8: Failed to load %s", trajTestPath);
                    sprintf_s(statusText, sizeof(statusText),
                        "AutoTrajTest: failed to load test_auto.bmp");
                    MessageBoxA(hwnd, 
                        "Cannot load test_auto.bmp\nPlease copy it to the exe directory.",
                        "Load Error", MB_OK | MB_ICONWARNING);
                }
                break;
            }

            }

            SetWindowTextA(g_hwndStatus, statusText);
            InvalidateRect(hwnd, NULL, FALSE);
        }
        return 0;

    // =============================================================
    // WM_PAINT: 窗口重绘消息
    // 功能: 绘制主窗口内容，包括图像和标定信息
    // =============================================================
    case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            LOG_DEBUG("WindowProc: WM_PAINT - Redrawing main window");

            // ==================== 绘制图像显示区域 ====================
            HDC hdcImg = GetDC(g_hwndImage);
            if (g_image.data) {
                LOG_DEBUG("WindowProc: Drawing image to display area");
                DrawImageToHDC(hdcImg, &g_image, 0, 0);
            } else {
                // 无图像时显示占位文字
                RECT rc; GetClientRect(g_hwndImage, &rc);
                FillRect(hdcImg, &rc, (HBRUSH)(COLOR_WINDOW + 1));
                SetBkMode(hdcImg, TRANSPARENT);
                SetTextAlign(hdcImg, TA_CENTER);
                TextOutA(hdcImg, rc.right / 2, rc.bottom / 2 - 10, "No Image", 8);
            }
            ReleaseDC(g_hwndImage, hdcImg);

            // ==================== 绘制标定信息 ====================
            SetBkMode(hdc, TRANSPARENT);
            HFONT hOld = (HFONT)SelectObject(hdc, hFontMono);

            int infoX = CALIB_IMAGE_WIDTH + 40, infoY = 80;
            char line[256];

            // 如果已完成标定，显示标定结果
            if (g_step >= 4) {
                LOG_DEBUG("WindowProc: Drawing calibration results");
                sprintf_s(line, sizeof(line), "=== Calibration Results ===");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 25;

                sprintf_s(line, sizeof(line), "Transform Parameters:");
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 20;

                sprintf_s(line, sizeof(line), "X = %.4f*x + %.4f*y + %.2f", 
                         g_transform.a, g_transform.b, g_transform.c);
                TextOutA(hdc, infoX, infoY, line, strlen(line));
                infoY += 18;

                sprintf_s(line, sizeof(line), "Y = %.4f*x + %.4f*y + %.2f", 
                         g_transform.d, g_transform.e, g_transform.f);
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

    // =============================================================
    // WM_DESTROY: 窗口销毁消息
    // 功能: 释放所有资源，关闭程序
    // =============================================================
    case WM_DESTROY:
        LOG_INFO("WindowProc: WM_DESTROY - Cleaning up and exiting");
        if (g_image.data) {
            free(g_image.data);
            g_image.data = NULL;
            LOG_DEBUG("WindowProc: Freed main image data");
        }
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProc(hwnd, msg, wParam, lParam);
}

/**
 * ============================================================
 * 第十一节: 程序入口点
 * ============================================================
 */

/**
 * WinMain - Windows应用程序入口函数
 * 
 * 功能说明:
 *   这是程序的入口点，负责初始化整个应用程序。
 *   初始化顺序：控制台 -> 日志系统 -> 窗口类 -> 主窗口 -> 消息循环
 * 
 * 参数说明:
 *   hInstance     - 当前应用程序实例句柄
 *   hPrevInstance - 早期Windows版本的遗留参数，始终为NULL
 *   lpCmdLine     - 命令行参数字符串
 *   nCmdShow      - 窗口的初始显示方式
 * 
 * 程序流程:
 *   1. 保存实例句柄
 *   2. 初始化调试控制台和日志系统
 *   3. 注册窗口类
 *   4. 创建主窗口
 *   5. 显示并更新窗口
 *   6. 发送自定义消息触发自动测试
 *   7. 进入消息循环
 *   8. 程序退出前清理控制台
 */
int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, 
                   LPSTR lpCmdLine, int nCmdShow) {
    LOG_INFO("==========================================");
    LOG_INFO("WinMain: Application starting");
    LOG_INFO("WinMain: hInstance=%p, nCmdShow=%d", hInstance, nCmdShow);
    
    g_hInstance = hInstance;

    // ==================== 第一步: 初始化控制台和日志 ====================
    LOG_INFO("WinMain: Initializing console and logging system");
    InitConsole();
    LOG_INFO("WinMain: Console initialized");

    // ==================== 第二步: 设置算子库日志回调 ====================
    // 将CalibOperator库的日志输出到本程序的控制台和文件
    CalibSetLogCallback(OperatorLogCallback);
    LOG_INFO("WinMain: Operator log callback registered");

    // ==================== 第三步: 注册窗口类 ====================
    LOG_INFO("WinMain: Registering window class");
    WNDCLASS wc = {0};
    wc.lpfnWndProc = WindowProc;           // 消息处理函数
    wc.hInstance = hInstance;                // 实例句柄
    wc.lpszClassName = L"CalibrationGUI";   // 类名
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);  // 箭头光标
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);  // 窗口背景色

    if (!RegisterClass(&wc)) {
        LOG_ERROR("WinMain: Failed to register window class");
        MessageBoxA(NULL, "Failed to register window class!", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }
    LOG_INFO("WinMain: Window class registered successfully");

    // ==================== 第四步: 创建主窗口 ====================
    LOG_INFO("WinMain: Creating main window");
    HWND hwnd = CreateWindow(
        L"CalibrationGUI",                   // 窗口类名
        L"9-Point Calibration GUI",         // 窗口标题
        WS_OVERLAPPEDWINDOW,                // 窗口样式（可调整大小、有标题栏）
        CW_USEDEFAULT, CW_USEDEFAULT,       // 位置（使用默认值）
        2800, 2300,                          // 窗口大小
        NULL, NULL, hInstance, NULL);       // 父窗口、菜单、实例、创建参数

    if (!hwnd) {
        LOG_ERROR("WinMain: Failed to create main window");
        MessageBoxA(NULL, "Failed to create main window!", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }
    LOG_INFO("WinMain: Main window created successfully, hwnd=%p", hwnd);

    // ==================== 第五步: 显示和更新窗口 ====================
    LOG_INFO("WinMain: Showing and updating main window");
    ShowWindow(hwnd, SW_SHOWNORMAL);  // 显示窗口
    UpdateWindow(hwnd);               // 立即绘制（发送WM_PAINT）

    // ==================== 第六步: 触发自动测试 ====================
    // 程序启动后自动加载test_auto.bmp并执行轨迹检测
    LOG_INFO("WinMain: Posting auto-test message (WM_USER+1)");
    PostMessage(hwnd, WM_USER + 1, 0, 0);

    // ==================== 第七步: 消息循环 ====================
    LOG_INFO("WinMain: Entering message loop");
    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);   // 转换虚拟键消息为字符消息
        DispatchMessage(&msg);    // 分发消息到窗口过程
    }
    LOG_INFO("WinMain: Exited message loop");

    // ==================== 第八步: 清理资源 ====================
    LOG_INFO("WinMain: Cleaning up resources");
    CloseConsole();
    LOG_INFO("WinMain: Application terminated normally");
    
    return (int)msg.wParam;  // 返回退出码
}
