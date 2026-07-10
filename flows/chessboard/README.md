# 棋盘格标定与图像矫正

本目录示例 flow 与说明。**产线完整部署顺序**（九点标定、模板、主流程）见 **[../README_workflow_guide.md](../README_workflow_guide.md)**。

本页路径均相对 **当前 `.flow.json` 所在目录**。

## 1. 棋盘格标定（去畸变 + 透视矫正）

与 [产线部署指南](../README_workflow_guide.md#1-棋盘格标定去畸变--透视矫正) 一致：

1. 运行 `chessboard_intrinsics_from_dir.flow.json` 得到 `CalibrationJson`。
2. **选一张较正的标定图** 作为透视展开的 **`viewIndex`**。
3. 串联 `calibration_correct_image` 或分步 undistort + warp。

## 算子一览

| 算子 | 作用 |
|------|------|
| `camera_calib_capture` | 摄像头预览采集标定图（确定/完成）→ `ImagePaths` / `ImageDirectory` |
| `chessboard_find_corners` | 单张图检测棋盘角点 |
| `chessboard_calibrate_intrinsics` | 多视图 OpenCV 标定 → `Intrinsics` / `CalibrationJson` |
| `intrinsics_undistort_image` | 内参去畸变（`cv::undistort`） |
| `chessboard_perspective_warp_image` | 棋盘平面透视展开（鸟瞰） |
| `calibration_correct_image` | **组合**：去畸变 + 透视（原 `load_image` 内置选项已拆出） |
| `chessboard_pixels_to_world` | 像素轨迹 → 棋盘平面 mm 坐标 |

取图算子 `load_image` / `load_image_dir` / `camera_snap` **不再**内置矫正，须串联上述矫正算子。

---

## 推荐流程拓扑

### A. 标定 + 去畸变 + 透视（分步，便于调试）

```
chessboard_calibrate_intrinsics ── CalibrationJson ──┐
load_image ── Image ── intrinsics_undistort_image ──┼── chessboard_perspective_warp_image ── Out
                                                      │
                      CalibrationJson ────────────────┘
```

### B. 标定 + 一步矫正（产线常用）

```
chessboard_calibrate_intrinsics ── CalibrationJson ──┐
load_image / camera_snap ── Image ── calibration_correct_image ── Out ── 下游
                                                      │
                      CalibrationJson ────────────────┘
```

`calibration_correct_image` 参数：

- **内参畸变矫正** `enableUndistort`：需 `CalibrationJson`（含 intrinsics）
- **透视展开** `enablePerspective`：需完整 `CalibrationJson`（含 `extrinsicsPerView`）
- 两项可单独开、可同时开；都关则透传原图

### C. 仅去畸变

见 `chessboard_undistort_example.flow.json`。

### D. 像素 → 世界坐标

见 `chessboard_trajectory_to_world.flow.json`；`viewIndex` 须与当前图像位姿对应的标定视图一致。

---

## 透视展开：输出尺寸与角度

算子 **`chessboard_perspective_warp_image`** 与 **`calibration_correct_image`**（启用透视时）共用参数 **透视输出范围**、**输出尺度**。

### 透视输出范围 `perspectiveOutputFrame`

| 值 | 含义 | 输出尺寸 |
|----|------|----------|
| `plane`（产线默认） | 整图按共面单应展开 | 与原图相同 |
| `board` | 裁剪到标定板区域 | 见下方「输出尺度」 |
| `local` | 原图尺寸，仅板内有效 | 与原图相同 |

### 输出尺度 `perspectiveOutputScale`

| 值 | 输出宽高 | 多视角 / 不同角度 |
|----|----------|-------------------|
| **`board_pixels`**（产线默认） | 按**外参投影**的棋盘边长定尺寸 | 与 plane 联用输出仍为原图尺寸 |
| `metric` | 固定 `(cols-1)×squareSizeMm×pxPerMm` × `(rows-1)×…` | **各 viewIndex 一致** |

示例：11×8 棋盘、`squareSizeMm=5`、`pxPerMm=32`（默认）→ `metric` 固定 **1600×1120** 像素。

**若多视角批处理需要相同画布尺寸**，请使用 `metric`（默认），不要用 `board_pixels`。

### viewIndex

- 对应 `CalibrationJson.extrinsicsPerView[i]`，顺序与标定**成功**图像一致（从 0 起）
- 处理某张图时，应使用与该图成像位姿最接近的视图序号
- 透视展开依赖外参；仅去畸变只需 intrinsics

### 其他透视参数

| 参数 | 说明 |
|------|------|
| `cols` / `rows` | 内侧角点列/行数，须与标定一致 |
| `squareSizeMm` | 方格物理边长 (mm) |
| `pxPerMm` | 输出缩放（像素/毫米），默认 **32** |
| `assumeUndistorted` | 默认 **true**；透射前须已去畸变 |

---

## 示例 flow 索引

| 文件 | 说明 |
|------|------|
| `chessboard_example.flow.json` | 角点检测 + 显示 |
| `chessboard_intrinsics_example.flow.json` | 目录批量内参标定 |
| `chessboard_intrinsics_from_dir.flow.json` | **摄像头交互采集** → 内参标定、质检报告、角点逐张检查、去畸变对比并保存 JSON |
| `chessboard_intrinsics_with_qc.flow.json` | 与上一文件相同（带完整质检支路） |
| `chessboard_undistort_example.flow.json` | 去畸变对比显示 |
| `chessboard_perspective_warp_example.flow.json` | 去畸变 → 透视展开 → 存图 |
| `chessboard_intrinsics_extrinsics_usage.flow.json` | 标定结果导出与显示 |
| `chessboard_trajectory_to_world.flow.json` | 轨迹投影到棋盘平面 |
| `chessboard_corners_overlay_example.flow.json` | 角点叠加可视化 |

---

## 标定质检（自动判级 + 目视支路）

`chessboard_intrinsics_from_dir.flow.json` / `chessboard_intrinsics_with_qc.flow.json` 含三条支路：

1. **显示标定结果**：运行后**弹出质检报告窗口**（非日志），含 **[1] 标定好坏**、**[2] 补拍位置**、**[3] 坏图清单**；日志仅一行摘要。
2. **角点逐张检查**：`加载图像目录(each)` → `棋盘格角点` → `显示图像`（运行流程时逐张刷新，无角点即需重拍）。
3. **去畸变对比**：`加载图像目录(single)` → `内参畸变矫正` → `显示图像`（Img=原图，Image=矫正后）。

**CalibrationJson 新增字段**（标定算子输出）：

| 字段 | 说明 |
|------|------|
| `calibrationStats.attemptedCount` | 目录内参与扫描的张数 |
| `calibrationStats.successfulCount` | 角点成功、参与标定的张数 |
| `calibrationStats.failedCount` | 读图失败或角点失败的张数 |
| `failedImagePaths[]` | 失败图像绝对路径列表 |
| `extrinsicsPerView[].reprojRms` | 该视图单张重投影 RMS（像素） |

**RMS 参考**：&lt; 0.10 优秀；0.10~0.30 良好；0.30~0.50 可用；&gt; 0.50 建议重拍。产线建议 15~25 张成功图，覆盖画面四角与中心，并保留一张较正视图供 `viewIndex`。

---

标定节点输出 JSON 含：

- `intrinsics`：`fx, fy, cx, cy` 与畸变 `k1..k3, p1, p2`
- `extrinsicsPerView[]`：每视图 `rvec, tvec`（棋盘系 → 相机）
- `cols, rows, squareSizeMm` 等元数据

保存：接 `save_text` 到 `CalibrationJson` 端口；加载：下一流程用 `load_calibration_result` 或 `calibrationJsonFile` 参数。

---

## 常见问题

**Q: 填了 67×50（或更大）检不出角点？**  
A: `cols` / `rows` 是 **内侧角点个数**（您说的 67×50 点数填法是对的），不是毫米尺寸。

更常见的原因是 **方格在图像里太小**（例如 0.5 mm 方格只有 4～5 像素/格）：

| 指标 | 建议 | 您当前约 |
|------|------|----------|
| 方格边长（像素） | **≥ 15 px**（标定稳定）；最低不宜 **< 8 px** | 4～5 px |
| 成像 mm/px | ≤ 0.05 mm/px（0.1 mm 精度） | 0.5÷4.5 ≈ **0.11 mm/px** |
| 角点总数 | 内参标定 **11×8** 足够；67×50=3350 点非必需 | 3350 |

**怎么办（按优先级）：**

1. **拉近相机 / 长焦 / ROI 裁切**，使 0.5 mm 方格在图上 **≥ 15 像素**（约需比现在还大 **3～4 倍**）。
2. 专用于标定的棋盘改用 **2～5 mm 方格**（产线默认 5 mm）；密点棋盘可留给别的用途。
3. 流程里 `cols=67`、`rows=50`、`squareSizeMm=0.5` 保持一致；**快速检测** 先设 **false**。
4. 程序已对过小方格 **自动 2×/4× 放大再检测**，并缩小亚像素窗口（须 **重新编译 CalibOperator** 后生效）；放大只能缓解检出，**不能替代光学放大**，0.1 mm 验收仍须提高像素尺度。

**Q: 换 viewIndex 后透视图大小变了？**  
A: 检查 `perspectiveOutputScale` 是否为 `metric`；`board_pixels` 会随图中棋盘大小变化。未检测到角点时，程序会回退 `metric`，避免外参投影导致尺寸乱跳。

**Q: 透视图拉伸 / 黑边多？**  
A: 先去畸变再透视；`undistortAlpha` 可试 0~1 裁黑边。透视用 `viewIndex` 与当前图位姿不匹配时会偏。

**Q: 旧 flow 里 load_image 开了去畸变？**  
A: 删除取图节点上的矫正参数，改为：`load_image` → `calibration_correct_image`（或分步 undistort + warp）。

**Q: 路径怎么写？**  
A: 相对当前 flow 文件，如 `../../test_images/foo.bmp`；浏览选文件后会自动转为相对路径。

---

## 相关文档

- **产线部署总览**：`../README_workflow_guide.md`
- HALCON 形状匹配：`../halcon/README_shape_model_match.md`
- 粗精匹配：`../halcon/README_coarse_fine_shape_match.md`
- 流程总索引：`../README.md`
