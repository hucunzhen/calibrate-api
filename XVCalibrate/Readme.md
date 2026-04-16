# XVCalibrate - 九点标定示例

## 项目说明

基于 `算法API` 的九点标定示例程序，用于建立图像坐标到物理坐标的仿射变换映射。

## 项目结构

```
算法API/
├── XVCalibrate/               ← VS工程目录
│   ├── XVCalibrate.sln        - 解决方案
│   ├── XVCalibrate.vcxproj    - 项目文件
│   └── Readme.md
├── include头文件/             - 算法库头文件
├── release/                   - 算法库LIB和DLL
│   ├── lib/                   - LIB文件
│   └── *.dll                  - DLL文件
├── 第三方库_调用算法需要/     - 第三方DLL
└── calibrate_example.cpp       - 主程序源码
```

## 编译配置

| 配置项 | 值 |
|--------|-----|
| 平台 | x64 / Win32 |
| 配置 | Debug / Release |
| 字符集 | Unicode |
| SDK | Windows 10 |

## 依赖库

```
BlobAnalysisPro.lib   - 斑点分析
ContourPos.lib       - 轮廓定位
FitShape.lib         - 几何形状拟合
Measure.lib          - 测量函数
Path.lib             - 路径处理
Preprocess.lib        - 图像预处理
```

## 使用方法

### 1. 打开工程

```
双击 XVCalibrate.sln
```

### 2. 选择配置

- **配置**: `Debug` 或 `Release`
- **平台**: `x64` (推荐) 或 `Win32`

### 3. 编译运行

```
菜单: 生成 → 重新生成解决方案
菜单: 调试 → 开始执行(不调试) 或 F5
```

### 4. 查看输出

程序将输出：
- 仿射变换参数
- 重投影误差分析
- 标定参数保存文件 `calibration_params.txt`

## 标定流程

```
1. 采集标定板图像 (9个黑色圆, 圆心有白色十字)
       ↓
2. 提取9个圆心的像素坐标 (使用算法库)
       ↓
3. 输入已知的物理坐标 (根据标定板规格)
       ↓
4. 求解仿射变换参数 (最小二乘法)
       ↓
5. 验证并保存参数
       ↓
6. 后续用于坐标转换
```

## 仿射变换模型

$$
X = a_x \cdot x + b_x \cdot y + c_x \\
Y = a_y \cdot x + b_y \cdot y + c_y
$$

| 参数 | 含义 |
|------|------|
| `(x, y)` | 图像像素坐标 |
| `(X, Y)` | 物理坐标(毫米) |

## 程序输出示例

```
========================================
      九点标定示例程序
========================================

========== 九点标定开始 ==========

输入像素坐标:
序号      X(像素)      Y(像素)
  1      500.00       300.00
  2      600.00       300.00
  ...

输入物理坐标:
序号        X(mm)        Y(mm)
  1       0.000       0.000
  2      10.000       0.000
  ...

======================================
        仿射变换参数
======================================
X = 0.100000 * x + 0.000000 * y + -50.000000
Y = 0.000000 * x + 0.100000 * y + -30.000000
======================================

平均重投影误差: 0.0023 mm
最大重投影误差: 0.0041 mm

========== 九点标定完成 ==========
```

## 实际使用

```cpp
// 1. 提取圆心像素坐标
vector<XVPoint2D> imagePts = extractCircleCenters(&image);

// 2. 输入物理坐标(根据标定板规格)
vector<XVPoint2D> worldPts = {
    {0, 0}, {10, 0}, {20, 0},
    {0, 10}, {10, 10}, {20, 10},
    {0, 20}, {10, 20}, {20, 20}
};

// 3. 标定
AffineTransform transform = calibrateNinePoint(imagePts, worldPts);

// 4. 保存
saveCalibration(transform, "calibration_params.txt");

// 5. 坐标转换
XVPoint2D pixel = detectObject(&image);
XVPoint2D world = imageToWorld(pixel, transform);
printf("物理位置: (%.3f, %.3f) mm\n", world.x, world.y);
```
