#!/usr/bin/env python3
"""
使用 Ultralytics YOLO 分割预训练权重，对 images/train 中的图片生成 YOLO-Seg 格式标签（归一化多边形）。

用法:
  pip install -r YoloSeg_Tools/requirements-yolo-seg.txt
  python auto_label_seg.py --images-dir .../images/train --labels-dir .../labels/train --weights yolov8m-seg.pt

--single-class N: 将所有实例类别重写为 N（工业单类常用）；默认 -1 保留预训练模型的类别 id（需与 data.yaml 中 nc/names 一致）。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

IMG_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"}


def main() -> int:
    ap = argparse.ArgumentParser(description="YOLO-Seg 自动标注（Ultralytics）")
    ap.add_argument("--images-dir", required=True, type=Path, help="训练图像目录（通常为 dataset/images/train）")
    ap.add_argument("--labels-dir", required=True, type=Path, help="输出标签目录（通常为 dataset/labels/train）")
    ap.add_argument("--weights", type=str, default="yolov8m-seg.pt", help="分割权重路径或名称（如 yolov8s-seg.pt、yolov8l-seg.pt）")
    ap.add_argument("--conf", type=float, default=0.25, help="置信度阈值")
    ap.add_argument("--device", type=str, default="", help="cuda:0 / cpu / 空=自动")
    ap.add_argument("--single-class", type=int, default=-1, dest="single_class", help=">=0 时所有实例改为该类别 id；-1 保留模型原始类别")
    args = ap.parse_args()

    images_dir: Path = args.images_dir
    labels_dir: Path = args.labels_dir
    if not images_dir.is_dir():
        print(f"图像目录不存在: {images_dir}", file=sys.stderr)
        return 2
    labels_dir.mkdir(parents=True, exist_ok=True)

    try:
        from ultralytics import YOLO
    except ImportError:
        print("请先安装: pip install -r YoloSeg_Tools/requirements-yolo-seg.txt", file=sys.stderr)
        return 3

    model = YOLO(args.weights)
    device = args.device.strip() or None

    files = sorted(
        p for p in images_dir.iterdir() if p.is_file() and p.suffix.lower() in IMG_EXT
    )
    if not files:
        print(f"未找到支持的图像: {images_dir}", file=sys.stderr)
        return 4

    n_ok = 0
    n_empty = 0
    for img_path in files:
        stem = img_path.stem
        label_path = labels_dir / f"{stem}.txt"

        results = model.predict(
            source=str(img_path),
            conf=args.conf,
            device=device,
            verbose=False,
        )
        r = results[0]
        lines: list[str] = []

        if r.masks is None or len(r.masks) == 0:
            label_path.write_text("", encoding="utf-8")
            n_empty += 1
            continue

        polys = r.masks.xyn
        clss = r.boxes.cls.cpu().numpy().astype(int)
        for c, poly_norm in zip(clss, polys):
            cid = int(args.single_class) if args.single_class >= 0 else int(c)
            flat = " ".join(f"{float(x):.6f} {float(y):.6f}" for x, y in poly_norm)
            lines.append(f"{cid} {flat}")

        label_path.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
        n_ok += 1

    print(f"完成: 有标注 {n_ok} 张, 空标签 {n_empty} 张, 共扫描 {len(files)} 张")
    return 0


if __name__ == "__main__":
    sys.exit(main())
