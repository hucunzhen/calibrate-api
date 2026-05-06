# SAM ONNX 模型（本目录）

由仓库内 checkpoint 导出，供 ONNX Runtime（C#/C++/Python）推理。

## 导出命令

在仓库根目录执行（需已安装 `torch`、`onnx`、`onnxruntime`、`segment-anything`）：

```powershell
cd D:\work\calibrate-api\SAM_Inference

# ViT-B（约 375MB checkpoint，推荐开发调试）
# 多候选掩码（Flow 算子 Mask / Mask2 / Mask3 / Mask4）：不要加 --return-single-mask
python export_sam_onnx.py --checkpoint ..\sam_vit_b_01ec64.pth --model-type vit_b --out-dir ..\models\onnx

# ViT-H（约 2.4GB checkpoint，体积与导出时间较长）
python export_sam_onnx.py --checkpoint ..\models\sam_vit_h_4b8939.pth --model-type vit_h --out-dir ..\models\onnx

# 若仅需单个最优掩码、略减 decoder 计算，可显式加上（此时 C# 侧只有 Mask 有意义）：
# python export_sam_onnx.py ... --return-single-mask
```

生成文件：

| 文件 | 说明 |
|------|------|
| `sam_vit_b_encoder.onnx` | 输入 `image` [1,3,1024,1024]，输出 `image_embeddings` |
| `sam_vit_b_decoder.onnx` | 输入 embeddings + 点提示等，输出 `masks`, `iou_predictions`, `low_res_masks` |

`vit_h` / `vit_l` 同理，前缀为 `sam_vit_h_`、`sam_vit_l_`。

## 与官方脚本的关系

Decoder 导出逻辑与 [segment-anything/scripts/export_onnx_model.py](https://github.com/facebookresearch/segment-anything/blob/main/scripts/export_onnx_model.py) 一致（`SamOnnxModel`）。Encoder 为单独的 `image_encoder` ONNX。

完整 ONNX 推理流程参见官方 notebook：`notebooks/onnx_model_example.ipynb`。

## 动态权重量化（减小体积与 CPU 计算）

使用 ONNX Runtime **动态量化**（权重 INT8、激活仍为 FP32），无需校准数据：

```powershell
cd D:\work\calibrate-api\SAM_Inference

# C# / Microsoft.ML.OnnxRuntime CPU：请加 --matmul-only，生成 *_int8_mm.onnx（避免 ConvInteger 未实现）
python quantize_sam_onnx.py --in-dir ..\models\onnx --prefix sam_vit_b --matmul-only

# Python 等环境若 ORT 已实现 ConvInteger，可用默认（同时量化 Conv）
python quantize_sam_onnx.py --in-dir ..\models\onnx --prefix sam_vit_b
```

**ConvInteger**：默认量化含 ViT `patch_embed` 的 Conv 时会产生 `ConvInteger`，标准 ORT CPU 常报错 `NotImplemented`。工程内 SAM Flow 算子默认使用 **FP32**；若要量化版请使用 `--matmul-only` 生成的 `sam_*_encoder_int8_mm.onnx` / `sam_*_decoder_int8_mm.onnx`。

可选：`--per-channel`、或对单个文件指定 `--encoder` / `--decoder`。

控制台可能出现 `Slice` / `unsupported type to quantize` 等 WARNING：动态量化只处理部分算子权重，其余保持 FP32，属正常现象。

若需 **静态量化**（激活也量化，体积/延迟更优），需要代表性样本做校准，可在此基础上自行接入 `onnxruntime.quantization.quantize_static` 与 `CalibrationDataReader`。

## OWLv2（文本 grounding → SAM 框提示）

SAM Flow 算子在填写 **textPrompt** 时，会在 **C#** 内用 ONNX Runtime 跑 OWLv2，不再调用 Python。

### 所需文件

| 路径（默认） | 说明 |
|--------------|------|
| `models/onnx/owlv2_base_patch16_ensemble.onnx`（可调） | 使用 Hugging Face Optimum 等工具导出的 OWLv2 **零样本检测** ONNX（输入含 `pixel_values`、`input_ids`、`attention_mask`） |
| `models/onnx/owlv2_tokenizer/tokenizer.json`（可调） | 与 OWLv2 文本塔一致的 **CLIP** tokenizer（可与 `openai/clip-vit-base-patch32` 的 `tokenizer.json` 相同家族）；工程通过 **Tokenizers.DotNet**（Rust `hf_tokenizers`）加载 |

### 导出示例（开发机一次性操作）

需 Python 环境仅用于 **离线导出**，运行时不需要 `python.exe`：

```powershell
pip install optimum[exporters] transformers onnx onnxruntime

# 示例：导出 base-patch16（名称与算子默认路径对齐时请自行重命名或改 Flow 参数）
optimum-cli export onnx --model google/owlv2-base-patch16-ensemble --task zero-shot-object-detection .\models\onnx\owlv2_export
```

将生成的 ONNX 复制为 Flow 默认路径下的 `owlv2_base_patch16_ensemble.onnx`（或在工作流里填写 **OWLv2 ONNX** 参数的完整路径）。从 Hugging Face 仓库下载 `tokenizer.json`（及可选 `vocab.json` / `merges.txt`）到 `models/onnx/owlv2_tokenizer/`。

**Windows**：NuGet 包 `Tokenizers.DotNet.runtime.win-x64` 会把 `hf_tokenizers.dll` 拷到输出目录；其他 RID 需对应 runtime 包。

图像预处理：填黑 pad 成正方形 → **960×960** → CLIP mean/std → NCHW `float32`。文本：**最长 16 token**，padding id **0**，与导出模型保持一致。

## 工程引用

- ONNX 文件体积较大，默认不强制提交到 Git；可按需在 CI/发布步骤复制 `models\onnx\*.onnx` 到应用程序目录。
- C# 示例：引用 `Microsoft.ML.OnnxRuntime`，加载上述两个模型并按 Meta 示例拼装预处理（resize/pad 到 1024）与后处理。
