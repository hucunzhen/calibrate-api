# v4 产线流程包

**文档**：[../产线视觉标定与主流程.md](../产线视觉标定与主流程.md)（[PDF](../产线视觉标定与主流程.pdf)）— 日常跑 **`main.flow.json`**（§1）；换产品再做标定 flow。  
**专章 PDF**：[九点标定_圆点检测.pdf](九点标定_圆点检测.pdf)  
**PLC（换产品标定时）**：先 **手动模式**，再 **标定模式**（见文档 §PLC 页面）。

## 流程页快捷按钮（从左到右）

| 按钮 | 文件 | 用途 |
|------|------|------|
| **主流程** | `main.flow.json` | 量产：粗/精匹配 + img_to_world + PLC |
| **棋盘格标定** | `chessboard_intrinsics_from_dir.flow.json` | 棋盘内参 → `chessboard/calibration_result.json` |
| **九点打点** | `calibSendContour.flow.json` | 机台 **中心 + stepX/Y** → **weld_trajectory_world** → `models/world_pos.txt`（给九点标定用，不是量产焊） |
| **九点标定** | `caliNinePoint.flow.json` | 圆点检测 + 仿射求解 → `models/calibration_nine_point.json` |
| **微调** | `adjustAffineCalibration.flow.json` | 读已保存九点 JSON，对话框平移/缩放后覆盖写回 |

## 其它文件

| 文件 | 用途 |
|------|------|
| `halcon_coarse_shape_match.flow.json` | 粗匹配单步调试（`main` 组合子流程） |
| `halcon_fine_shape_match.flow.json` | 精匹配单步调试 |
| `chessboard_undistort_example.flow.json` | 去畸变对比示例 |

## 目录约定（相对本文件夹）

| 路径 | 内容 |
|------|------|
| `models/` | 九点标定 JSON、标定图、world_pos.txt |
| `chessboard/` | 棋盘标定 JSON（阶段 1 输出） |
| `captured_chessboard/` | 棋盘采图目录（阶段 1 默认） |
| `roi/` | 粗/精/外框 ROI（`.xvroi.json`，mm 坐标；操作见 [../halcon/README_shape_model_roi.md](../halcon/README_shape_model_roi.md)） |

## 加载方式

流程页选配方 **v4** 后点上表快捷按钮；也可 **加载** 选择 `.flow.json`（路径以 **v4 目录** 为基准）。

**工艺卡**：`product_card.json`（换型阶段 0–6、是否允许量产）；操作员在 **量产** 页查看，工艺/视觉在 **换型向导** 中维护。
