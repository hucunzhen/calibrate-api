"""
Open-vocabulary text -> axis-aligned box (xyxy in original image pixels) via OWLv2.

Production Flow uses C# (Owlv2OnnxTextToBox + ONNX); this script remains for manual
debugging or tooling — not invoked by the app at runtime.

  pip install -r SAM_Inference/requirements-grounded.txt

Example:
  python grounded_text_to_box.py image.bmp "dart board" --threshold 0.25 --out-json out.json
"""

from __future__ import annotations

import argparse
import json
import sys
import traceback
from pathlib import Path


def fail(out_json: Path, msg: str, code: int = 1) -> None:
    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps({"ok": False, "error": msg}, ensure_ascii=False), encoding="utf-8")
    sys.exit(code)


def main() -> None:
    ap = argparse.ArgumentParser(description="OWLv2 text grounding -> boxes JSON")
    ap.add_argument("image_path", help="RGB/BMP etc. path readable by PIL")
    ap.add_argument("text", help="Object description (Chinese or English)")
    ap.add_argument("--threshold", type=float, default=0.25, help="score threshold for detections")
    ap.add_argument("--out-json", required=True, help="output JSON path")
    ap.add_argument(
        "--model",
        default="google/owlv2-base-patch16-ensemble",
        help="Hugging Face model id for Owlv2Processor / Owlv2ForObjectDetection",
    )
    ap.add_argument(
        "--raw-query",
        action="store_true",
        help="use text as-is; default wraps as 'a photo of {text}'",
    )
    args = ap.parse_args()
    out_json = Path(args.out_json)

    try:
        _run_grounding(args, out_json)
    except SystemExit:
        raise
    except Exception:
        fail(out_json, traceback.format_exc())


def _run_grounding(args: argparse.Namespace, out_json: Path) -> None:
    try:
        import torch
        from PIL import Image, ImageFile
        from transformers import Owlv2ForObjectDetection, Owlv2Processor

        # 轻微损坏的 JPEG/部分解码场景；对 OWLv2 输入通常仍可接受
        ImageFile.LOAD_TRUNCATED_IMAGES = True
    except ImportError as e:
        fail(out_json, f"缺少依赖: {e}。请执行: pip install -r SAM_Inference/requirements-grounded.txt")

    img_path = Path(args.image_path)
    if not img_path.is_file():
        fail(out_json, f"图像不存在: {img_path}")

    try:
        image = Image.open(img_path).convert("RGB")
    except Exception as e:
        fail(out_json, f"无法读取图像: {e}")

    query = (args.text or "").strip()
    if not query:
        fail(out_json, "text 为空")

    texts = [[query]] if args.raw_query else [[f"a photo of {query}"]]

    try:
        processor = Owlv2Processor.from_pretrained(args.model)
        model = Owlv2ForObjectDetection.from_pretrained(args.model)
    except Exception as e:
        fail(out_json, f"加载 OWLv2 失败（首次运行需下载权重）: {e}")

    # OWLv2 文本编码器 query 长度上限通常为 16 token；中文或未截断会超过，触发 position embedding 维度错误。
    max_len = min(getattr(processor.tokenizer, "model_max_length", 16) or 16, 16)
    inputs = processor(
        text=texts,
        images=image,
        return_tensors="pt",
        truncation=True,
        max_length=max_len,
        padding="max_length",
    )

    with torch.no_grad():
        outputs = model(**inputs)

    target_sizes = torch.tensor([image.size[::-1]])
    # transformers>=5.x：Owlv2 使用 post_process_grounded_object_detection（需 text_labels）；旧版仅有 post_process_object_detection
    if hasattr(processor, "post_process_grounded_object_detection"):
        results = processor.post_process_grounded_object_detection(
            outputs=outputs,
            threshold=args.threshold,
            target_sizes=target_sizes,
            text_labels=texts,
        )[0]
    else:
        results = processor.post_process_object_detection(
            outputs=outputs, target_sizes=target_sizes, threshold=args.threshold
        )[0]

    boxes_out: list[dict[str, float]] = []
    scores = results["scores"]
    boxes = results["boxes"]
    for i in range(len(scores)):
        x1, y1, x2, y2 = boxes[i].tolist()
        boxes_out.append(
            {
                "x1": float(x1),
                "y1": float(y1),
                "x2": float(x2),
                "y2": float(y2),
                "score": float(scores[i].item()),
            }
        )

    boxes_out.sort(key=lambda b: b["score"], reverse=True)

    payload = {
        "ok": True,
        "boxes": boxes_out,
        "query": query,
        "raw_query": bool(args.raw_query),
        "threshold": float(args.threshold),
        "text_max_tokens": max_len,
    }
    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
