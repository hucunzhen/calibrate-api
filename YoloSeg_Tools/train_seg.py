#!/usr/bin/env python3
"""
Ultralytics YOLO-Seg 训练入口（便于从工具界面调用）。

若 data.yaml 中含顶层键 ``augment``（几何/颜色增强），将一并传入 ``model.train()``，
与 Ultralytics 默认参数合并（后者可被覆盖）。

  python train_seg.py --data .../data.yaml --weights yolov8m-seg.pt --epochs 100 --imgsz 640 --batch 4

工作目录建议设为数据集根目录，runs 会生成在当前目录下。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path


def _load_augment_from_data_yaml(data_path: Path) -> dict:
    try:
        import yaml
    except ImportError:
        print("读取 augment 需要 PyYAML：pip install PyYAML", file=sys.stderr)
        return {}

    try:
        cfg = yaml.safe_load(data_path.read_text(encoding="utf-8")) or {}
    except Exception as e:
        print(f"解析 data.yaml 失败: {e}", file=sys.stderr)
        return {}

    aug = cfg.get("augment")
    if not isinstance(aug, dict):
        return {}

    # 仅传入 train 认识的标量；忽略 None / 错误类型
    allowed = {
        "degrees",
        "translate",
        "scale",
        "shear",
        "perspective",
        "flipud",
        "fliplr",
        "mosaic",
        "mixup",
        "copy_paste",
        "hsv_h",
        "hsv_s",
        "hsv_v",
        "erasing",
        "crop_fraction",
        "auto_augment",
    }
    out: dict = {}
    for k, v in aug.items():
        if k not in allowed or v is None:
            continue
        if isinstance(v, (int, float, bool, str)):
            out[k] = v
        else:
            continue
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="YOLO-Seg 训练")
    ap.add_argument("--data", required=True, type=Path, help="data.yaml 路径")
    ap.add_argument("--weights", type=str, default="yolov8m-seg.pt")
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--device", type=str, default="", help="0 / cpu / 空=自动")
    args = ap.parse_args()

    data_path = args.data
    if not data_path.is_file():
        print(f"data.yaml 不存在: {data_path}", file=sys.stderr)
        return 2

    try:
        from ultralytics import YOLO
    except ImportError:
        print("请先安装: pip install -r YoloSeg_Tools/requirements-yolo-seg.txt", file=sys.stderr)
        return 3

    aug_kw = _load_augment_from_data_yaml(data_path)
    if aug_kw:
        print(f"已从 data.yaml 读取 augment 参数: {sorted(aug_kw.keys())}")

    dev = args.device.strip() or None
    model = YOLO(args.weights)

    train_kw: dict = {
        "data": str(data_path.resolve()),
        "epochs": args.epochs,
        "imgsz": args.imgsz,
        "batch": args.batch,
    }
    if dev is not None:
        train_kw["device"] = dev
    train_kw.update(aug_kw)

    model.train(**train_kw)
    print("训练任务已结束（详见上方 Ultralytics 日志与 runs 目录）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
