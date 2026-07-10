# 产线标定、模板与主流程部署指南

本文说明从相机标定、PLC 坐标映射、HALCON 模板制作到主流程运行的推荐顺序。相关算子细节见 [chessboard/README.md](chessboard/README.md)、[halcon/README_coarse_fine_shape_match.md](halcon/README_coarse_fine_shape_match.md)。

---

## 部署顺序概览

```mermaid
flowchart LR
  A[1 棋盘格标定] --> B[2 九点标定]
  B --> C[3 制作模板]
  C --> D[4 主流程 main.flow.json]
```

| 步骤 | 目的 |
|------|------|
| 1. 棋盘格标定 | 内参去畸变、透视展开；**选定一张正视角**作为后续 `viewIndex` |
| 2. 九点标定 | 像素坐标系 → PLC / 世界坐标系（仿射变换） |
| 3. 制作模板 | 外框 / 粗匹配 / 精匹配 / 标定板 / 焊点 等 `.shm` / `.dfm` |
| 4. 流程文件 | 加载标定结果，跑检测、匹配、轨迹下发 |

---

## 1. 棋盘格标定（去畸变 + 透视矫正）

**目标**：求相机内参、畸变系数及多视图外参，供后续 `intrinsics_undistort_image`、`chessboard_perspective_warp_image` 或 `calibration_correct_image` 使用。

**关键操作**：

1. 运行 [`chessboard/chessboard_intrinsics_from_dir.flow.json`](chessboard/chessboard_intrinsics_from_dir.flow.json)（见第 4 节），得到 `CalibrationJson`。
2. 在标定成功的多张图中，**选一张棋盘放得较正、完整的图**，记下其在 `extrinsicsPerView` 中的 **`viewIndex`**（从 0 起，与标定成功图像顺序一致）。
3. 后续透视展开、组合矫正算子中 **`viewIndex` 固定使用该视角**；去畸变仅依赖内参，与 `viewIndex` 无关。

**采图要求**（目录内标定图）：

- 棋盘格**完整入画**，尽量在同一平面；
- 多张图**分布均匀、姿态多样**（俯仰、偏航变化）；
- **至少一张较正**，专门作为透视展开的参考视角。

**输出尺度**：产线推荐 **`perspectiveOutputFrame=plane`** + **`perspectiveOutputScale=board_pixels`**（默认）。多视角批处理若需固定物理尺寸小图，可改 `metric`。详见 [chessboard/README.md](chessboard/README.md)。

---

## 2. 九点标定（像素 → PLC 坐标系）

**目标**：建立图像像素 `(x, y)` 与 PLC / 机台世界坐标 `(X, Y)` 的仿射关系，供 `img_to_world`、`send_plc` 等使用。

**推荐顺序**：

1. 用激光焊打九点（或已知九点的物理位置）→ 运行 [`halcon/caliSendContour.flow.json`](halcon/caliSendContour.flow.json) 得到 PLC 侧世界坐标与轨迹；
2. 拍一张九点可见的标定图，用 [`halcon/caliNinePoint.flow.json`](halcon/caliNinePoint.flow.json) 求变换并保存 `calibration_nine_point.json`。

**注意**：

- `caliNinePoint` 里 **九点标定** 算子的 **`worldPoints`** 应与 `caliSendContour` 生成的世界坐标一致；
- **图上像素点顺序** 必须与 **世界坐标点顺序一一对应**（通常 3×3 从左到右、从上到下）；
- 运行 `caliNinePoint` 时若开启 `confirmCorrespondence`，可在对话框中核对像素点与世界点配对。

---

## 3. 制作模板（高级功能 → 形状模板）

菜单：**高级功能 ▾ → 形状模板**（`HalconShapeModelPage`）。

按用途制作多类模板，导出 `.shm`（刚性）或 `.dfm`（可变形），供流程中 `halcon_load_shape_model` / `halcon_load_deformable_model` 加载。

### 3.1 工件外框模板（方向粗定位）

| 项 | 说明 |
|----|------|
| **用途** | 确定工件**大致方向**，缩小后续匹配的角度搜索范围，减少误匹配、加快速度 |
| **轮廓** | 工件**外轮廓**或外框特征 |
| **角度** | 创建与匹配时 **角度范围要大**，须覆盖现场可能出现的**全部旋转** |
| **流程中** | 通常 angleStart / angleExtent 设得较宽；结果 `Angle` 供下游粗匹配参考 |

### 3.2 工件粗匹配模板（位置粗定位）

| 项 | 说明 |
|----|------|
| **用途** | 确定工件**位置**；精匹配 ROI 小、搜索可更「大胆」，若不在此步缩小角度/位置，精匹配误差会很大 |
| **轮廓** | 工件上**关键、稳定**的轮廓（重复性好、不易受光照影响） |
| **角度** | 相对外框模板已收窄，但仍需覆盖合理偏差 |
| **流程中** | 粗匹配 `Row/Col/Angle` → 精匹配 `CoarseRow/CoarseColumn/CoarseAngle` |

### 3.3 工件精匹配模板（轮廓生成）

| 项 | 说明 |
|----|------|
| **用途** | 生成贴合工件的**最终轮廓**（采样、轨迹、显示） |
| **轮廓** | **找准边缘**，阈值 / XLD 提取要稳定；轮廓质量直接决定轨迹是否贴合物体 |
| **类型** | 常为 **可变形模型 `.dfm`**，配合 `halcon_fine_deformable_match` |
| **详见** | [halcon/README_coarse_fine_shape_match.md](halcon/README_coarse_fine_shape_match.md) |

### 3.4 标定板模板（九点标定）

| 项 | 说明 |
|----|------|
| **用途** | 在标定图上定位九点标定板的圆心或特征点 |
| **示例** | `caliNinePoint` 子流程 [`findCircle.flow.json`](halcon/findCircle.flow.json) 加载 `shape_model_circle.shm`，网格排序后输出 9 个像素点 |
| **制作** | 在形状模板页用标定板圆点 / 十字制作模板，匹配参数与现场成像一致 |

### 3.5 焊点模板（九点标定）

| 项 | 说明 |
|----|------|
| **用途** | 定位激光焊接九点（或焊点阵列）在图像中的位置 |
| **与九点标定关系** | 像素点经九点标定变换后写入 PLC；与 3.4 类似，轮廓应对准**实际焊点成像** |

**模板文件位置**：建议放在与主 flow 同目录（如 `flows/halcon/shape_model.shm`），流程中用相对路径加载。

---

## 4. 流程文件说明

### 4.1 `chessboard/chessboard_intrinsics_from_dir.flow.json`

| 项 | 说明 |
|----|------|
| **输入** | 参数 `imageDirectory`：标定图目录（可配合 `namePrefix`、`extensions` 过滤） |
| **输出** | `CalibrationJson`（含质检字段）→ `display_calibration` / `save_calibration_result` |
| **质检** | 同文件含角点逐张检查、去畸变对比支路；详见 [chessboard/README.md](chessboard/README.md#标定质检自动判级--目视支路) |
| **采图** | 棋盘完整、共面、姿态多样；**其中一张要较正**，记下其 `viewIndex` 供透视展开 |
| **算子参数** | `cols` / `rows` / `squareSizeMm` 须与物理棋盘一致 |

### 4.2 `halcon/caliSendContour.flow.json`

| 项 | 说明 |
|----|------|
| **用途** | 激光焊**打九点**，经 PLC 下发焊接轨迹，得到九点**世界坐标**（供下一步九点标定） |
| **使用前** | 修改组合子流程内的 **焊接轨迹** 算子（`weld_trajectory_world`）：**中心点**（`centerX` / `centerY` / `centerZ`）、**步距**（`stepXmm` / `stepYmm`）与现场九点布局一致 |
| **子流程** | 参数 `innerFlowPath` 指向实际轨迹生成 flow；部署时改为本机相对路径 |
| **输出** | `send_plc` 写入 GVAR；PLC 执行后世界坐标用于填写 `caliNinePoint` 的 `worldPoints` |

> 文件名：`caliSendContour.flow.json`（非 calibSendContour）。

### 4.3 `halcon/caliNinePoint.flow.json`

| 项 | 说明 |
|----|------|
| **用途** | 计算**九点标定**仿射参数 |
| **输入** | 标定图 + 子流程 [`findCircle.flow.json`](halcon/findCircle.flow.json) 输出的 9 个像素点 |
| **worldPoints** | 由 **`caliSendContour.flow.json`** 对应 PLC 坐标填写；**顺序与像素点一一对应** |
| **输出** | `save_calibration_result` → 如 `calibration_nine_point.json`（含 `systemError` 系统整体误差，需填写 `calibrationJsonFile` 或连接 `CalibrationJson`） |
| **核对** | `confirmCorrespondence=true` 时可交互确认像素↔世界配对 |

### 4.4 `halcon/main.flow.json`

| 项 | 说明 |
|----|------|
| **用途** | **主流程**：取图 → 预处理 → 粗/精匹配 → 轮廓采样 → 像素转世界 → PLC 下发 |
| **标定加载** | `load_calibration_result` 加载九点标定 JSON；棋盘 `CalibrationJson` 在矫正链中单独加载 |
| **子流程** | `grayPreprocess`、`binPreprocess`、`contourPreprocess`、`genMask` 等组合算子，按产线调整 |
| **PLC** | `plc_connect` → `send_plc`（`splitByBar=separate_batch`）→ 使能 / 等待焊完 |

更细的 HALCON 匹配说明：[README_shape_model_match.md](halcon/README_shape_model_match.md)、[README_coarse_fine_shape_match.md](halcon/README_coarse_fine_shape_match.md)。

---

## 5. 推荐串联关系（简图）

```
chessboard_intrinsics_from_dir  →  CalibrationJson  →  取图 → calibration_correct_image（viewIndex=正视角）
load_calibration_result         →  Transform         →  img_to_world → send_plc

形状模板页导出 .shm / .dfm  →  main.flow.json 内 halcon_load_* → 粗找 → 精找 → 轨迹
```

---

## 6. 常见问题

**Q: 透视图大小随 viewIndex 变化？**  
A: 使用 `perspectiveOutputScale=metric`；并确保 `viewIndex` 对应「较正」那张标定图。

**Q: 九点标定重投影误差大？**  
A: 检查 `worldPoints` 是否与 `caliSendContour` 一致；像素 9 点顺序是否与 `findCircle` 网格排序一致。

**Q: 如何查看棋盘格 + 九点的系统整体误差？**  
A: 九点标定算子填写 `calibrationJsonFile`（或连接棋盘 `CalibrationJson`），运行后弹窗末尾有 **[系统整体误差]** 段；`SystemErrorJson` 接 `save_calibration_result` 会写入 `systemError` 字段（合成 avg/max，单位 mm）。

**Q: 精匹配偏差大？**  
A: 确认粗匹配已缩小位置/角度；精模板边缘是否贴合物体；Mask / `CoarseAngle` 是否接入。

**Q: caliSendContour 子流程路径无效？**  
A: 将 `innerFlowPath` 改为相对 `caliSendContour.flow.json` 的路径，并配置其中的 `weld_trajectory_world`（`centerX/Y`、`stepXmm/stepYmm`）。
