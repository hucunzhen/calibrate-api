#!/usr/bin/env python3
# Copyright (c) Meta Platforms, Inc. (SAM export logic derived from segment-anything/scripts/export_onnx_model.py)
"""
将 SAM  checkpoint 导出为 ONNX（图像编码器 + 提示/解码器），供 ONNX Runtime 推理。

依赖: pip install torch onnx onnxruntime segment-anything

示例:
  python export_sam_onnx.py --checkpoint ..\\sam_vit_b_01ec64.pth --model-type vit_b --out-dir ..\\models\\onnx
  python export_sam_onnx.py --checkpoint ..\\models\\sam_vit_h_4b8939.pth --model-type vit_h --out-dir ..\\models\\onnx
"""

from __future__ import annotations

import argparse
import inspect
import os
import sys
import warnings

# 避免 Windows GBK 控制台在 torch.onnx 内部打印 emoji 时崩溃
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

import torch

from segment_anything import sam_model_registry
from segment_anything.utils.onnx import SamOnnxModel


def _onnx_export_kwargs() -> dict:
    """PyTorch 2.x 默认 dynamo 导出在部分环境下易触发控制台编码问题，优先走 legacy。"""
    sig = inspect.signature(torch.onnx.export)
    kw: dict = {}
    if "dynamo" in sig.parameters:
        kw["dynamo"] = False
    return kw


def export_image_encoder(sam, out_path: str, opset: int) -> None:
    sam.image_encoder.eval()
    dummy = torch.randn(1, 3, 1024, 1024, dtype=torch.float32)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)) or ".", exist_ok=True)
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", category=torch.jit.TracerWarning)
        torch.onnx.export(
            sam.image_encoder,
            dummy,
            out_path,
            export_params=True,
            opset_version=opset,
            do_constant_folding=True,
            input_names=["image"],
            output_names=["image_embeddings"],
            dynamic_axes={
                "image": {0: "batch"},
                "image_embeddings": {0: "batch"},
            },
            **_onnx_export_kwargs(),
        )
    print(f"[OK] Image encoder -> {out_path}")


def export_prompt_mask_decoder(
    sam,
    out_path: str,
    opset: int,
    return_single_mask: bool,
    gelu_approximate: bool,
    use_stability_score: bool,
    return_extra_metrics: bool,
) -> None:
    onnx_model = SamOnnxModel(
        model=sam,
        return_single_mask=return_single_mask,
        use_stability_score=use_stability_score,
        return_extra_metrics=return_extra_metrics,
    )

    if gelu_approximate:
        for _n, m in onnx_model.named_modules():
            if isinstance(m, torch.nn.GELU):
                m.approximate = "tanh"

    dynamic_axes = {
        "point_coords": {1: "num_points"},
        "point_labels": {1: "num_points"},
    }

    embed_dim = sam.prompt_encoder.embed_dim
    embed_size = sam.prompt_encoder.image_embedding_size
    mask_input_size = [4 * x for x in embed_size]
    dummy_inputs = {
        "image_embeddings": torch.randn(1, embed_dim, *embed_size, dtype=torch.float),
        "point_coords": torch.randint(low=0, high=1024, size=(1, 5, 2), dtype=torch.float),
        "point_labels": torch.randint(low=0, high=4, size=(1, 5), dtype=torch.float),
        "mask_input": torch.randn(1, 1, *mask_input_size, dtype=torch.float),
        "has_mask_input": torch.tensor([1], dtype=torch.float),
        "orig_im_size": torch.tensor([1500, 2250], dtype=torch.float),
    }

    _ = onnx_model(**dummy_inputs)

    output_names = ["masks", "iou_predictions", "low_res_masks"]
    os.makedirs(os.path.dirname(os.path.abspath(out_path)) or ".", exist_ok=True)

    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", category=torch.jit.TracerWarning)
        warnings.filterwarnings("ignore", category=UserWarning)
        with open(out_path, "wb") as f:
            torch.onnx.export(
                onnx_model,
                tuple(dummy_inputs.values()),
                f,
                export_params=True,
                verbose=False,
                opset_version=opset,
                do_constant_folding=True,
                input_names=list(dummy_inputs.keys()),
                output_names=output_names,
                dynamic_axes=dynamic_axes,
                **_onnx_export_kwargs(),
            )
    print(f"[OK] Prompt encoder + mask decoder -> {out_path}")


def try_onnxruntime(decoder_path: str) -> None:
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        print("[SKIP] onnxruntime 未安装，跳过会话校验")
        return

    sess = ort.InferenceSession(decoder_path, providers=["CPUExecutionProvider"])
    print(f"[OK] ONNX Runtime 可加载 decoder: {decoder_path}")


def main() -> int:
    p = argparse.ArgumentParser(description="Export SAM to ONNX (encoder + decoder)")
    p.add_argument("--checkpoint", required=True, help="SAM .pth 路径")
    p.add_argument(
        "--model-type",
        required=True,
        choices=["vit_b", "vit_l", "vit_h"],
        help="必须与 checkpoint 匹配（vit_b / vit_l / vit_h）",
    )
    p.add_argument("--out-dir", default=os.path.join(os.path.dirname(__file__), "..", "models", "onnx"))
    p.add_argument("--opset", type=int, default=17)
    p.add_argument(
        "--return-single-mask",
        action="store_true",
        help="导出仅返回单个最优掩码（ORT 略快）；Flow「SAM 图像分割」需要多候选时请省略此项以保留 SAM 默认的 3～4 个掩码",
    )
    p.add_argument("--gelu-approximate", action="store_true", help="GELU 用 tanh 近似（部分嵌入式运行时）")
    p.add_argument("--no-encoder", action="store_true", help="仅导出 decoder")
    p.add_argument("--no-decoder", action="store_true", help="仅导出 encoder")
    args = p.parse_args()

    ckpt = os.path.abspath(args.checkpoint)
    if not os.path.isfile(ckpt):
        print(f"[ERROR] checkpoint 不存在: {ckpt}", file=sys.stderr)
        return 1

    out_dir = os.path.abspath(args.out_dir)
    os.makedirs(out_dir, exist_ok=True)

    prefix = f"sam_{args.model_type}"
    enc_path = os.path.join(out_dir, f"{prefix}_encoder.onnx")
    dec_path = os.path.join(out_dir, f"{prefix}_decoder.onnx")

    print(f"Loading SAM {args.model_type} from {ckpt} ...")
    sam = sam_model_registry[args.model_type](checkpoint=ckpt)
    sam.eval()

    if not args.no_encoder:
        export_image_encoder(sam, enc_path, args.opset)

    if not args.no_decoder:
        export_prompt_mask_decoder(
            sam,
            dec_path,
            args.opset,
            return_single_mask=args.return_single_mask,
            gelu_approximate=args.gelu_approximate,
            use_stability_score=False,
            return_extra_metrics=False,
        )
        try_onnxruntime(dec_path)
        if args.return_single_mask:
            print(
                "[NOTE] 已启用 --return-single-mask：decoder 仅输出单掩码。"
                "若需在 Flow 中使用 Mask/Mask2/Mask3 等多候选，请不加该参数重新导出 decoder。"
            )

    print(f"完成。输出目录: {out_dir}")
    print("推理顺序: 1) encoder(image)->embeddings  2) decoder(embeddings, points, ...)->masks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
