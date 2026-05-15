# 独立函数算子框架说明

## 概述

本框架将原来基于上下文的分步API调用模式，简化为**独立函数（Standalone Functions）**模式。无需创建/释放上下文，每次调用均为完整处理。

## 框架对比

### 旧模式（分步API）
```csharp
// 1. 创建上下文
var ctx = NativeAPI.CALIB_TrajStep_Create();

// 2. 分步调用（繁琐）
NativeAPI.CALIB_TrajStep_1_ConvertToGrayscale(ctx, srcImg);
NativeAPI.CALIB_TrajStep_2_PreprocessAndFindContours(ctx, ...);
NativeAPI.CALIB_TrajStep_3_CreateWorkpieceMask(ctx, ...);
NativeAPI.CALIB_TrajStep_4_DetectDarkBars(ctx, ...);
NativeAPI.CALIB_TrajStep_5_MorphologyCleanup(ctx, ...);
NativeAPI.CALIB_TrajStep_6_FindAndSortDarkContours(ctx, ...);
NativeAPI.CALIB_TrajStep_7_SampleContours(ctx, 16, 3.0);
NativeAPI.CALIB_TrajStep_7_5_FitShape(ctx, 0, 0);
NativeAPI.CALIB_TrajStep_8_VerifyByMask(ctx);
NativeAPI.CALIB_TrajStep_9_DeduplicateAndSort(ctx);
NativeAPI.CALIB_TrajStep_10_ConvertToOutput(ctx, ...);

// 3. 释放上下文
NativeAPI.CALIB_TrajStep_Free(ctx);
```

### 新模式（独立函数）
```csharp
// 一行调用完成所有处理
var points = CalibOperator.DetectTrajectory(grayData, 2448, 2048);
```

## 新增的独立函数

### C API（CalibOperatorExport.h）

#### 轨迹检测

| 函数 | 说明 |
|------|------|
| `CALIB_DetectTrajectoryStandalone` | 完整版，支持所有参数 |
| `CALIB_DetectTrajectorySimple` | 简化版，使用默认参数 |
| `CALIB_DetectTrajectoryWithSteps` | 带中间结果输出 |
| `CALIB_DetectTrajectoryFromBinary` | 从二值图像检测 |
| `CALIB_DetectHollowTrajectoryStandalone` | 空洞检测完整版 |
| `CALIB_DetectHollowTrajectorySimple` | 空洞检测简化版 |

#### 图像处理

| 函数 | 说明 |
|------|------|
| `CALIB_SampleContoursStandalone` | 轮廓采样 |
| `CALIB_PreprocessAndFindContours` | 预处理+找轮廓 |
| `CALIB_CreateWorkpieceMask` | 生成工件掩码 |
| `CALIB_DetectDarkBarsBinary` | 检测暗条生成二值图 |
| `CALIB_FreeStepResult` | 释放步骤结果 |

### C# API（CalibOperatorSimple.cs）

```csharp
// 简单调用
var points = CalibOperator.DetectTrajectory(grayData, width, height);

// 带参数调用
var points = CalibOperator.DetectTrajectory(grayData, width, height,
    targetBars: 16,
    samplingSpacing: 3.0,
    blurKsize: 7,
    morphKernelSize: 5,
    darkThreshold: 50);

// 完整版（返回中间图像）
var result = CalibOperator.DetectTrajectoryFull(grayData, width, height, options);
if (result.Success) {
    var points = result.Points;
    var barIds = result.BarIds;
    var stepImages = result.StepImages; // 中间结果图
}

// 从二值图像检测
var points = CalibOperator.DetectTrajectoryFromBinary(binaryData, width, height);

// 空洞检测
var points = CalibOperator.DetectHollowTrajectory(grayData, width, height);
```

## 文件变更

| 文件 | 变更 |
|------|------|
| `CalibOperatorExport.h` | 新增独立函数声明 |
| `CalibOperatorExport.cpp` | 新增独立函数实现 |
| `CalibOperatorSimple.cs` | **新增** C#简化调用封装 |

## 使用示例

### 示例1：基本轨迹检测

```csharp
using CalibOperatorPInvoke;

// 加载图像
var bmp = new Bitmap("test.bmp");

// 转换为灰度数据
byte[] grayData = CalibOperator.BitmapToGrayArray(bmp);

// 检测轨迹
var points = CalibOperator.DetectTrajectory(grayData, bmp.Width, bmp.Height);

// 绘制结果
foreach (var pt in points)
{
    Console.WriteLine($"({pt.X:F1}, {pt.Y:F1})");
}
```

### 示例2：自定义参数

```csharp
var options = new TrajectoryOptions
{
    TargetBars = 20,           // 目标暗条数量
    SamplingSpacing = 2.0,      // 采样间距
    BlurKsize = 9,            // 模糊核大小
    MorphKernelSize = 7,       // 形态学核大小
    DarkThreshold = 60,        // 暗条阈值
    FitMode = 0                // 0=stadium, 1=曲率去噪
};

var result = CalibOperator.DetectTrajectoryFull(grayData, width, height, options);
```

### 示例3：获取中间结果

```csharp
var result = CalibOperator.DetectTrajectoryFull(grayData, width, height);

// 显示中间步骤图像
for (int i = 0; i < result.StepImages.Count; i++)
{
    var img = result.StepImages[i];
    var bmp = img.ToBitmap();
    bmp.Save($"step_{i}.bmp");
}
```

### 示例4：分步处理（保留灵活性）

```csharp
// 步骤1：预处理找轮廓
var contours = CalibOperator.PreprocessAndFindContours(grayData, width, height);

// 步骤2：生成工件掩码
var maskData = CalibOperator.CreateWorkpieceMask(contours, width, height);

// 步骤3：检测暗条生成二值图
var binaryData = CalibOperator.DetectDarkBarsBinary(grayData, maskData, width, height);

// 步骤4：从二值图采样轨迹
var points = CalibOperator.DetectTrajectoryFromBinary(binaryData, width, height);
```

## 参数说明

### TrajectoryOptions

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `BlurKsize` | 7 | 高斯模糊核大小（奇数） |
| `MorphKernelSize` | 5 | 形态学核大小（奇数） |
| `EnableWatershed` | false | 启用分水岭预分割 |
| `MinContourArea` | 0 | 最小轮廓面积阈值 |
| `DarkThreshold` | 50 | 暗条上限阈值 |
| `DarkMinThreshold` | 5 | 暗条下限阈值 |
| `OuterThreshold` | 0 | 外围区域阈值（0=自适应） |
| `TargetBars` | 16 | 目标暗条数量 |
| `SamplingSpacing` | 3.0 | 等弧长采样间距（像素） |
| `FitMode` | 0 | 拟合模式：0=stadium, 1=曲率去噪 |

## 错误码

| 错误码 | 含义 |
|--------|------|
| -1 | 参数无效 |
| -2 | 图像数据为空 |
| -3 | 未找到工件轮廓 |
| -4 | 内存分配失败 |
| >= 0 | 成功，返回检测到的点数 |

## 向后兼容

旧的分步API仍然保留在新代码中，可继续使用：

```csharp
// 旧API仍然可用（但不推荐）
var ctx = NativeAPI.CALIB_TrajStep_Create();
// ... 分步调用 ...
NativeAPI.CALIB_TrajStep_Free(ctx);
```
