# HALCON 形状模板匹配 Flow 示例

文件：`halcon_shape_model_match_example.flow.json`

## 流程说明

```
加载图像 → 灰度 → Emphasize → 二值阈值 → 圆形态学开运算
    → 二值转 XLD → 创建形状模板 (CreateShapeModel)
    → 在同一灰度图上查找 (FindShapeModel) → 匹配中心转点列 → 显示
```

- **主显示窗口**：`halcon_display_shape_match` — 多色变换轮廓 + 十字中心 + 得分
- **副显示窗口**：强调后灰度 + 橘色模板轮廓 XLD（创建阶段）
- **查找节点**摘要栏显示每个匹配的 Row / Col / Angle / Score

## 使用步骤

1. 打开 **流程** 页，加载 `flows/halcon/halcon_shape_model_match_example.flow.json`
2. 需 HALCON 构建（已安装 HalconDotNet）
3. **加载图像** 的 `filePath` 留空则运行时会弹出选择；也可填绝对路径
4. 运行流程；阈值不合适时调整 `halcon_threshold_bin` 的 `minGray` / `maxGray` 或 `minContourPoints`

## 规则阵列过滤（剔除误检）

若目标呈 **M 行 × N 列** 等距排布，推荐拆成两步（`main2.flow.json` 已按此连线）：

```
FindShapeModel → halcon_pick_shape_match_lattice（阵列格点筛选，输出 16 模板）→ halcon_fit_shape_match_lattice（阵列聚类拟合）
              → halcon_filter_shape_match_grid（阵列过滤匹配）
              → halcon_display_shape_match
```

- **`halcon_pick_shape_match_lattice`**：链向定向 + 2 列×8 行落格，每格最高分 → 直接输出 **16** 个 `Row/Column/Angle/Score` 及 `GridRow/GridCol`、链向角。
- **`halcon_chain_strip_*`（见 `main2.flow.json`）**：
  - **快速路径**：**`halcon_chain_strip_bootstrap`**（输出格网 u 轴 θ≈0° 与 `BootstrapPickIndices`）→ **`halcon_chain_strip_pick_uv_grid`**（沿用 θ 投影 u/v，每格最高分 → 16 点）
  - **条带路径**：引导 → 定向落格 → 列0+条带输出（`halcon_chain_strip_pick_fill`）
- **`halcon_estimate_shape_match_chain`**：一步完成引导+定向+列0+条带（兼容旧流程）。
- **`halcon_fit_shape_match_lattice`**：可接上游链向；u/v 聚类得列/行中心线与理论格心（`ColCenterU` / `RowCenterV` 等）。
- **`halcon_filter_shape_match_grid`**：在聚类结果上落格、每格选优、邻格/得分过滤；可连接上游聚类端口，也可单独使用（内嵌聚类，兼容旧流程）。

| 参数 | 含义 |
|------|------|
| gridRows / gridCols | 已知行数、列数 |
| pitchRow / pitchCol | 行/列间距（像素）；**0 = 自动估计** |
| gridAngleDeg | **auto**：用 FindShapeModel 匹配角 + PCA，并自动尝试 ±90° 与行列轴对调（适配整板旋转） |
| maxAngleDeviationDeg | 相对匹配角的偏差上限；**整板同向旋转**可设 10~15°；对称模板或角度不稳定时请 **0** |
| snapTolerancePx | 点到格心最大偏差；0 ≈ 0.35×min间距 |
| minNeighborVotes | ≥1 时要求与邻格间距一致，抑制孤立误检 |

原理简述：先把 (Row,Col) 投影到估计的阵列 u/v 轴，再 1D 聚类得格心，**每格只保留得分最高**且落在容差内的匹配。节点摘要会显示 `θ≈…°`，若出现「行列轴已对调」表示自动把行/列参数与数据长边对齐。

示例 flow 中默认为 3×3，请按实际产品修改行列数。

## 参数建议

| 节点 | 参数 | 说明 |
|------|------|------|
| halcon_create_shape_model | metric | 阈值/XLD 轮廓用 `ignore_local_polarity` |
| halcon_find_shape_model | numMatches | `0` = 全部匹配 |
| halcon_find_shape_model | minScore | 漏检降低、误检提高 |
| halcon_find_shape_model | angleStart/Extent | 覆盖目标实际旋转范围 |

## 使用已导出的 .shm（可选）

形状模板页导出 `.shm` / `.dfm` 时，**默认文件名编码创建参数**（`xv_` 前缀），便于在目录中区分不同模型，例如：

`xv_shape_ThXld_n4_am30x60_ilp_mc10_g20-255.shm`

（负号写作 `m`，小数点写作 `p`；导入时若文件名符合该格式会自动恢复界面参数。）

形状模板页导出模型后：

1. 断开 `halcon_create_shape_model` → `halcon_find_shape_model` 的 ModelId 连线
2. 将 **HALCON 加载形状模板** 的 ModelId 连到查找节点
3. 把 `shape_model.shm` 放在与本 flow 同目录，或修改 `filePath`

未使用的加载节点可删除。

## 双图（模板图 + 检测图）

增加一条 `load_image` → `grayscale` 作为检测支路，仅将 `halcon_find_shape_model.In` 接到检测灰度；创建模板仍用原支路。
