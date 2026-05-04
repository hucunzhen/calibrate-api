#!/usr/bin/env python3
"""
对 SAM FP32 ONNX（encoder / decoder）做 ONNX Runtime 动态权重量化。

动态量化将 MatMul / Conv / Gemm 等算子的权重压成 INT8，推理时激活仍为 FP32；
可显著减小权重体积、在支持 VNNI 等指令的 CPU 上常能加速，精度损失通常较小。

注意：默认同时量化 Conv 时，ViT 编码器会出现 ConvInteger；Microsoft.ML.OnnxRuntime 的 CPU EP
常报 NotImplemented。在 C# / 标准 ORT CPU 下请使用 --matmul-only（仅 MatMul/Gemm），输出建议后缀 _int8_mm；
或直接推理 FP32 ONNX。

无需校准数据；若某子图不支持量化，ORT 会保留 FP32 路径。

示例:
  python quantize_sam_onnx.py --in-dir ..\\models\\onnx --prefix sam_vit_b
  python quantize_sam_onnx.py --in-dir ..\\models\\onnx --prefix sam_vit_b --matmul-only --suffix _int8_mm
  python quantize_sam_onnx.py --encoder ..\\models\\onnx\\sam_vit_b_encoder.onnx --decoder ..\\models\\onnx\\sam_vit_b_decoder.onnx
"""

from __future__ import annotations

import argparse
import os
import sys

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


def _output_path(fp32_path: str, suffix: str) -> str:
    root, ext = os.path.splitext(os.path.abspath(fp32_path))
    if ext.lower() != ".onnx":
        raise ValueError(f"期望 .onnx 文件: {fp32_path}")
    return f"{root}{suffix}{ext}"


def _quantize_one(
    fp32_path: str,
    out_path: str,
    *,
    per_channel: bool,
    reduce_range: bool,
    matmul_only: bool,
) -> None:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    os.makedirs(os.path.dirname(os.path.abspath(out_path)) or ".", exist_ok=True)
    q_kw = dict(
        model_input=fp32_path,
        model_output=out_path,
        weight_type=QuantType.QInt8,
        per_channel=per_channel,
        reduce_range=reduce_range,
    )
    if matmul_only:
        # 不量化 Conv，避免出现 ConvInteger（ORT CPU 常未实现）
        q_kw["op_types_to_quantize"] = ["MatMul", "Gemm"]
    quantize_dynamic(**q_kw)
    sz_in = os.path.getsize(fp32_path)
    sz_out = os.path.getsize(out_path)
    ratio = sz_in / max(sz_out, 1)
    print(f"[OK] {fp32_path}")
    print(
        f"     -> {out_path}  ({sz_in / 1_048_576:.1f} MiB -> {sz_out / 1_048_576:.1f} MiB, ~{ratio:.2f}x)"
    )


def _try_load(path: str, *, label: str) -> None:
    try:
        import onnxruntime as ort
    except ImportError:
        print(f"[SKIP] onnxruntime 未安装，跳过 {label} 会话校验")
        return
    ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    print(f"[OK] ONNX Runtime 可加载 {label}: {path}")


def main() -> int:
    p = argparse.ArgumentParser(description="SAM ONNX 动态权重量化 (ORT)")
    p.add_argument("--in-dir", default=None, help="含 sam_<type>_encoder/decoder.onnx 的目录")
    p.add_argument(
        "--prefix",
        default="sam_vit_b",
        help="与导出文件名前缀一致，如 sam_vit_b / sam_vit_h（配合 --in-dir）",
    )
    p.add_argument("--encoder", default=None, help="FP32 encoder.onnx 路径（优先于 --in-dir）")
    p.add_argument("--decoder", default=None, help="FP32 decoder.onnx 路径（优先于 --in-dir）")
    p.add_argument(
        "--suffix",
        default="_int8",
        help="输出文件名后缀，如 encoder.onnx -> encoder_int8.onnx",
    )
    p.add_argument(
        "--per-channel",
        action="store_true",
        help="按通道量化权重（体积略大、精度常更好，量化稍慢）",
    )
    p.add_argument(
        "--reduce-range",
        action="store_true",
        help="兼容旧版 ORT/某些 CPU；通常不必开",
    )
    p.add_argument(
        "--matmul-only",
        action="store_true",
        help="仅量化 MatMul/Gemm，跳过 Conv（推荐配合 C#/OnnxRuntime CPU，避免 ConvInteger NotImplemented）",
    )
    args = p.parse_args()

    suffix = args.suffix
    if args.matmul_only and suffix == "_int8":
        suffix = "_int8_mm"
        print("[INFO] --matmul-only：输出后缀改为 _int8_mm（可用 --suffix 覆盖）")

    enc = args.encoder
    dec = args.decoder
    if enc is None and dec is None:
        if not args.in_dir:
            print("[ERROR] 请指定 --encoder/--decoder，或同时指定 --in-dir（可用 --prefix）", file=sys.stderr)
            return 1
        d = os.path.abspath(args.in_dir)
        enc = os.path.join(d, f"{args.prefix}_encoder.onnx")
        dec = os.path.join(d, f"{args.prefix}_decoder.onnx")

    todo: list[tuple[str, str]] = []
    if enc:
        if not os.path.isfile(enc):
            print(f"[ERROR] encoder 不存在: {enc}", file=sys.stderr)
            return 1
        todo.append(("encoder", enc))
    if dec:
        if not os.path.isfile(dec):
            print(f"[ERROR] decoder 不存在: {dec}", file=sys.stderr)
            return 1
        todo.append(("decoder", dec))
    if not todo:
        print("[ERROR] 没有可量化的模型", file=sys.stderr)
        return 1

    for label, fp32 in todo:
        out = _output_path(fp32, suffix)
        if os.path.abspath(out) == os.path.abspath(fp32):
            print(f"[ERROR] 输出路径与输入相同，请修改 --suffix: {fp32}", file=sys.stderr)
            return 1
        _quantize_one(
            fp32,
            out,
            per_channel=args.per_channel,
            reduce_range=args.reduce_range,
            matmul_only=args.matmul_only,
        )
        _try_load(out, label=label)

    print("完成。推理 API 与 FP32 一致；C#/ORT CPU 建议 --matmul-only 生成 *_int8_mm.onnx，或直接用 FP32。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
