#!/usr/bin/env python3
"""
Ultralytics YOLO-Seg 单图推理：输出可视化 BMP + 检测 JSON（含归一化多边形）。

  python predict_seg.py --input in.bmp --weights path/to/best.pt --output-vis vis.bmp --output-json out.json
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser(description="YOLO-Seg predict (Ultralytics)")
    ap.add_argument("--input", required=True, type=Path)
    ap.add_argument("--weights", required=True, type=str)
    ap.add_argument("--output-vis", required=True, type=Path, dest="output_vis")
    ap.add_argument("--output-json", required=True, type=Path, dest="output_json")
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--device", type=str, default="", help="cuda:0 / cpu / 空为自动")
    ap.add_argument("--imgsz", type=int, default=0, help="0 表示使用模型默认 stride")
    args = ap.parse_args()

    if not args.input.is_file():
        print(f"输入不存在: {args.input}", file=sys.stderr)
        return 2

    try:
        import cv2
        from ultralytics import YOLO
    except ImportError as e:
        print(f"需要 ultralytics 与 opencv：{e}", file=sys.stderr)
        return 3

    model = YOLO(args.weights)
    kw: dict = {"source": str(args.input), "conf": args.conf, "verbose": False, "save": False}
    if args.device.strip():
        kw["device"] = args.device.strip()
    if args.imgsz > 0:
        kw["imgsz"] = args.imgsz

    results = model.predict(**kw)
    r = results[0]
    h, w = int(r.orig_shape[0]), int(r.orig_shape[1])

    detections: list[dict] = []
    boxes = r.boxes
    masks_xy = r.masks.xy if r.masks is not None else None

    if boxes is not None and len(boxes) > 0:
        for i in range(len(boxes)):
            cls_id = int(boxes.cls[i].item())
            score = float(boxes.conf[i].item())
            xyxy = [float(v) for v in boxes.xyxy[i].tolist()]
            poly_norm = None
            if masks_xy is not None and i < len(masks_xy):
                poly = masks_xy[i]
                poly_norm = [[float(px) / w, float(py) / h] for px, py in poly]
            detections.append(
                {
                    "class_id": cls_id,
                    "score": score,
                    "xyxy": xyxy,
                    "polygon_norm": poly_norm,
                }
            )

    payload = {
        "weights": args.weights,
        "orig_shape": [h, w],
        "detection_count": len(detections),
        "detections": detections,
    }
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    plot_bgr = r.plot()
    args.output_vis.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(args.output_vis), plot_bgr)

    print(f"detections={len(detections)} shape={h}x{w}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
