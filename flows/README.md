# Flow 示例与帮助文档

工业视觉工具 **流程** 页示例 `.flow.json` 与说明。工具栏 **帮助** 会打开当前目录或下方的产线部署指南。

## 产线部署（必读）

- **[产线视觉标定与主流程.md](产线视觉标定与主流程.md)** — **§1 量产日常运行** + 换产品时标定部署（阶段 0–6）  
  PDF：[产线视觉标定与主流程.pdf](产线视觉标定与主流程.pdf)（更新 Markdown 后运行 `python flows/_md_to_pdf.py` 重新生成；依赖 `markdown` 与系统自带 Edge 浏览器）
- **[README_workflow_guide.md](README_workflow_guide.md)** — 技术部署指南（棋盘格标定 → 九点标定 → 形状模板 → 主流程）

---

## 目录

| 目录 | 文档 | 内容 |
|------|------|------|
| **flows/** | [README_workflow_guide.md](README_workflow_guide.md) | 产线标定、模板、主流程部署顺序 |
| [chessboard/](chessboard/) | [README.md](chessboard/README.md) | 棋盘格标定、去畸变、透视展开、`calibration_correct_image` |
| [halcon/](halcon/) | [README_shape_model_match.md](halcon/README_shape_model_match.md) | HALCON 形状模板创建 / 查找 / 阵列过滤 |
| [halcon/](halcon/) | [README_coarse_fine_shape_match.md](halcon/README_coarse_fine_shape_match.md) | 粗定位 + 可变形精匹配 + Mask 循环 |
| [sam/](sam/) | — | SAM / 文本检测分割示例 flow |
| [xingzu/](xingzu/) | — | 项目定制流程示例 |

## 路径约定

- 算子中的文件路径默认 **相对当前 `.flow.json` 所在目录**
- 嵌套 **组合算子** 内子流程相对 **子 flow 文件** 目录
- 浏览对话框选中的文件会尽量存为相对路径

## 运行方式

1. 流程页 → **加载** 或 **新标签** 打开 `.flow.json`
2. **运行**（F5）：托管引擎，便于查看各节点端口数据
3. **Native**：C++ NativeFlowEngine（部分算子仅 Native 或仅托管，失败时会回退）

## 示例入口

**标定与主流程（v4 产线包）**

```
产线视觉标定与主流程.md                           # 产线全流程
v4/README.md                                      # v4 流程索引
v4/chessboard_intrinsics_from_dir.flow.json
v4/calibSendContour.flow.json
v4/caliNinePoint.flow.json
v4/main.flow.json
```

**棋盘 / HALCON 专题**

```
chessboard/chessboard_perspective_warp_example.flow.json
chessboard/chessboard_undistort_example.flow.json
halcon/halcon_shape_model_match_example.flow.json
halcon/halcon_coarse_fine_shape_match_example.flow.json
```
