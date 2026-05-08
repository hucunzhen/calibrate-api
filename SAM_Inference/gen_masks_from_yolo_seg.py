#!/usr/bin/env python3
"""
从 YOLO-Seg 标签（归一化多边形）生成 SAM 微调用的单通道二值掩膜 PNG。

每行格式: <class_id> <x1> <y1> <x2> <y2> ... （坐标为相对图像宽高的 0~1 浮点）

示例（与 YOLO 分割训练页导出的 labels/train 一致）:
  python gen_masks_from_yolo_seg.py ^
    --images-dir D:/data/images/train ^
    --labels-dir D:/data/labels/train ^
    --out-masks-dir D:/data/masks

SAM 微调目录建议:
  <root>/images/   ← 可将 train 内图像复制或链接到此
  <root>/masks/    ← 本脚本输出与此处

依赖: pip install Pillow numpy（SAM_Tools/requirements-sam-train.txt 已包含）
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

IMG_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"}


def parse_class_filter(s: str) -> set[int] | None:
    s = s.strip()
    if not s:
        return None
    out: set[int] = set()
    for part in s.replace("，", ",").split(","):
        part = part.strip()
        if not part:
            continue
        out.add(int(part))
    return out


def parse_yolo_seg_lines(text: str, class_filter: set[int] | None) -> list[list[tuple[float, float]]]:
    polys: list[list[tuple[float, float]]] = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        if len(parts) < 7:
            continue
        try:
            cid = int(parts[0])
        except ValueError:
            continue
        if class_filter is not None and cid not in class_filter:
            continue
        nums = parts[1:]
        if len(nums) % 2 != 0:
            continue
        coords: list[tuple[float, float]] = []
        for i in range(0, len(nums), 2):
            coords.append((float(nums[i]), float(nums[i + 1])))
        if len(coords) >= 3:
            polys.append(coords)
    return polys


def rasterize(w: int, h: int, polys_norm: list[list[tuple[float, float]]], fg: int, bg: int) -> Image.Image:
    mask = Image.new("L", (w, h), bg)
    draw = ImageDraw.Draw(mask)
    for poly in polys_norm:
        xy = []
        for xn, yn in poly:
            x = int(round(max(0.0, min(1.0, xn)) * (w - 1)))
            y = int(round(max(0.0, min(1.0, yn)) * (h - 1)))
            xy.extend((x, y))
        if len(xy) >= 6:
            draw.polygon(xy, outline=fg, fill=fg)
    return mask


def main() -> int:
    ap = argparse.ArgumentParser(description="YOLO-Seg 多边形标签 → 二值掩膜 PNG")
    ap.add_argument("--images-dir", type=Path, required=True, help="图像目录（如 dataset/images/train）")
    ap.add_argument("--labels-dir", type=Path, required=True, help="YOLO-Seg .txt 标签目录（如 dataset/labels/train）")
    ap.add_argument("--out-masks-dir", type=Path, required=True, help="输出掩膜目录（将创建）")
    ap.add_argument(
        "--class-filter",
        type=str,
        default="",
        help="仅栅格化这些类别 id，英文逗号分隔，如 0 或 0,1；留空表示合并全部类别",
    )
    ap.add_argument("--fg", type=int, default=255, help="前景灰度（默认 255）")
    ap.add_argument("--bg", type=int, default=0, help="背景灰度（默认 0）")
    args = ap.parse_args()

    images_dir = args.images_dir.resolve()
    labels_dir = args.labels_dir.resolve()
    out_dir = args.out_masks_dir.resolve()
    if not images_dir.is_dir():
        print(f"[ERROR] 图像目录不存在: {images_dir}", file=sys.stderr)
        return 1
    if not labels_dir.is_dir():
        print(f"[ERROR] 标签目录不存在: {labels_dir}", file=sys.stderr)
        return 1

    cf = parse_class_filter(args.class_filter)
    out_dir.mkdir(parents=True, exist_ok=True)

    files = sorted(p for p in images_dir.iterdir() if p.is_file() and p.suffix.lower() in IMG_EXT)
    if not files:
        print(f"[ERROR] 未找到图像: {images_dir}", file=sys.stderr)
        return 2

    n_ok = 0
    n_empty = 0
    for img_path in files:
        stem = img_path.stem
        lbl_path = labels_dir / f"{stem}.txt"
        try:
            im = Image.open(img_path).convert("RGB")
            w, h = im.size
        except Exception as ex:
            print(f"[WARN] 跳过（无法读图）{img_path.name}: {ex}", file=sys.stderr)
            continue

        polys: list[list[tuple[float, float]]] = []
        if lbl_path.is_file():
            text = lbl_path.read_text(encoding="utf-8", errors="replace")
            polys = parse_yolo_seg_lines(text, cf)

        if not polys:
            mask = Image.new("L", (w, h), args.bg)
            n_empty += 1
        else:
            mask = rasterize(w, h, polys, args.fg, args.bg)
            n_ok += 1

        out_path = out_dir / f"{stem}.png"
        mask.save(out_path, format="PNG")

    print(
        f"完成: 写入 {len(files)} 张掩膜至 {out_dir} "
        f"（有多边形栅格 {n_ok} 张, 空标签全背景 {n_empty} 张）"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
