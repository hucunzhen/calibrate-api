#!/usr/bin/env python3
# Copyright (c) Meta (SAM architecture); training loop for domain adaptation.
"""
在自定义「图像 + 二值掩膜」数据上微调 SAM（默认冻结 image encoder，训练 mask_decoder + prompt_encoder）。

数据目录（默认同级 images / masks；YOLO 常用 images/train）:
  <data_root>/images[/子目录]/*.jpg
  <data_root>/masks[/子目录]/*.png
  与图像同主文件名，单通道 0/255 掩膜

  YOLO 示例: --data-root .../yolo_data --images-subdir train --masks-subdir （留空，掩膜在 masks/ 根下）

产物为完整 state_dict .pth，可与 export_sam_onnx.py 相同方式导出 ONNX。

依赖: pip install -r SAM_Tools/requirements-sam-train.txt 且已安装 segment-anything、torchvision。

数据增强（默认开启）: 图像与掩膜同步水平/垂直翻转与平面旋转；颜色抖动仅作用 RGB。
  关闭: --no-augment；细调见 --aug-* 参数。

示例:
  python train_sam_finetune.py --data-root D:/data/sam_ft --checkpoint D:/w/sam_vit_b_01ec64.pth --model-type vit_b --epochs 5 --out D:/out/sam_ft_best.pth
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import random
import sys
from pathlib import Path

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image
from torch import nn
from tqdm import tqdm

try:
    from torchvision.transforms import InterpolationMode
    from torchvision.transforms import functional as TF_vis
except ImportError:
    TF_vis = None  # type: ignore[misc, assignment]
    InterpolationMode = None  # type: ignore[misc, assignment]

try:
    from segment_anything import sam_model_registry
    from segment_anything.utils.transforms import ResizeLongestSide
except ImportError:
    print("请先安装 segment-anything，例如: pip install git+https://github.com/facebookresearch/segment-anything.git", file=sys.stderr)
    raise


IMG_EXT = {".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"}


def _subdir_part(raw: str) -> str | None:
    s = (raw or "").strip().replace("\\", "/").strip("/")
    return s if s else None


def resolve_image_mask_dirs(data_root: Path, images_subdir: str, masks_subdir: str) -> tuple[Path, Path]:
    rel_i = _subdir_part(images_subdir)
    rel_m = _subdir_part(masks_subdir)
    img_dir = (data_root / "images" / Path(rel_i)).resolve() if rel_i else (data_root / "images").resolve()
    m_dir = (data_root / "masks" / Path(rel_m)).resolve() if rel_m else (data_root / "masks").resolve()
    return img_dir, m_dir


def list_pairs(data_root: Path, images_subdir: str = "", masks_subdir: str = "") -> list[tuple[Path, Path]]:
    img_dir, m_dir = resolve_image_mask_dirs(data_root, images_subdir, masks_subdir)
    if not img_dir.is_dir():
        raise SystemExit(
            f"图像目录不存在: {img_dir}\n"
            f"若图为 YOLO 的 images/train，请加参数: --images-subdir train"
        )
    if not m_dir.is_dir():
        raise SystemExit(
            f"掩膜目录不存在: {m_dir}\n"
            f"请确认已生成 masks（可与图像同级或再加子目录，用 --masks-subdir 指定）"
        )
    pairs: list[tuple[Path, Path]] = []
    for p in sorted(img_dir.iterdir()):
        if p.suffix.lower() not in IMG_EXT:
            continue
        m = m_dir / (p.stem + ".png")
        if not m.is_file():
            for alt in (m_dir / (p.stem + ".jpg"), m_dir / (p.stem + ".bmp")):
                if alt.is_file():
                    m = alt
                    break
            else:
                print(f"[WARN] 跳过（无掩膜）: {p.name}", file=sys.stderr)
                continue
        pairs.append((p, m))
    if not pairs:
        raise SystemExit(
            f"未找到可用的图像与掩膜配对（扫描目录: {img_dir} ↔ {m_dir}）。\n"
            "请确认文件名（主名）一致且掩膜为 .png/.jpg/.bmp；无标签的图像会被跳过。"
        )
    return pairs


def load_binary_mask(path: Path) -> np.ndarray:
    g = np.array(Image.open(path).convert("L"))
    return (g > 127).astype(np.float32)


@dataclass
class AugmentConfig:
    """训练阶段对「图像 + 掩膜」同步几何变换，颜色仅作用图像。"""

    enabled: bool = True
    p_hflip: float = 0.5
    p_vflip: float = 0.0
    rotate_deg_max: float = 12.0
    p_rotate: float = 0.85
    p_color: float = 0.6
    brightness_delta: float = 0.22
    contrast_delta: float = 0.22
    saturation_delta: float = 0.22
    hue_delta: float = 0.04


def _color_jitter_pil(pil_rgb: Image.Image, rng: random.Random, cfg: AugmentConfig) -> Image.Image:
    img = pil_rgb
    if cfg.brightness_delta > 0:
        f = 1.0 + rng.uniform(-cfg.brightness_delta, cfg.brightness_delta)
        img = TF_vis.adjust_brightness(img, max(0.01, f))
    if cfg.contrast_delta > 0:
        f = 1.0 + rng.uniform(-cfg.contrast_delta, cfg.contrast_delta)
        img = TF_vis.adjust_contrast(img, max(0.01, f))
    if cfg.saturation_delta > 0:
        f = 1.0 + rng.uniform(-cfg.saturation_delta, cfg.saturation_delta)
        img = TF_vis.adjust_saturation(img, max(0.01, f))
    if cfg.hue_delta > 0:
        hf = rng.uniform(-cfg.hue_delta, cfg.hue_delta)
        hf = max(-0.45, min(0.45, hf))
        img = TF_vis.adjust_hue(img, hf)
    return img


def augment_training_pair(
    image_rgb: np.ndarray,
    mask_bin: np.ndarray,
    rng: random.Random,
    cfg: AugmentConfig,
) -> tuple[np.ndarray, np.ndarray]:
    """返回增强后的 RGB uint8 与 float32 二值掩膜 [0,1]。"""
    if not cfg.enabled or TF_vis is None:
        return image_rgb, mask_bin

    pil_im = Image.fromarray(image_rgb.astype(np.uint8), mode="RGB")
    pil_m = Image.fromarray((mask_bin * 255.0).clip(0, 255).astype(np.uint8), mode="L")

    if cfg.p_hflip > 0 and rng.random() < cfg.p_hflip:
        pil_im = TF_vis.hflip(pil_im)
        pil_m = TF_vis.hflip(pil_m)
    if cfg.p_vflip > 0 and rng.random() < cfg.p_vflip:
        pil_im = TF_vis.vflip(pil_im)
        pil_m = TF_vis.vflip(pil_m)

    if cfg.rotate_deg_max > 0 and cfg.p_rotate > 0 and rng.random() < cfg.p_rotate:
        angle = rng.uniform(-cfg.rotate_deg_max, cfg.rotate_deg_max)
        pil_im = TF_vis.rotate(
            pil_im,
            angle,
            interpolation=InterpolationMode.BILINEAR,
            expand=False,
            fill=[0, 0, 0],
        )
        pil_m = TF_vis.rotate(
            pil_m,
            angle,
            interpolation=InterpolationMode.NEAREST,
            expand=False,
            fill=[0],
        )

    if cfg.p_color > 0 and rng.random() < cfg.p_color:
        pil_im = _color_jitter_pil(pil_im, rng, cfg)

    out_im = np.asarray(pil_im, dtype=np.uint8)
    out_m = (np.asarray(pil_m) > 127).astype(np.float32)
    return out_im, out_m


def mask_to_gt_low(mask_bin: np.ndarray, transform: ResizeLongestSide, device: torch.device, img_size: int) -> torch.Tensor:
    """二值掩膜 → 与 decoder 一致的低分辨率监督 [1,1,256,256]。"""
    m3 = np.stack([mask_bin, mask_bin, mask_bin], axis=-1)
    m_resized = transform.apply_image((m3 * 255).astype(np.uint8))[:, :, 0].astype(np.float32) / 255.0
    t = torch.from_numpy(m_resized).float().unsqueeze(0).unsqueeze(0).to(device)
    _, mh, mw = t.shape[-3:]
    padh = img_size - mh
    padw = img_size - mw
    if padh < 0 or padw < 0:
        raise ValueError("掩膜 padding 计算异常")
    t = F.pad(t, (0, padw, 0, padh), value=0.0)
    return F.interpolate(t, size=(256, 256), mode="nearest")


def sample_points(mask_bin: np.ndarray, rng: random.Random) -> tuple[np.ndarray, np.ndarray] | None:
    fg = np.where(mask_bin > 0.5)
    bg = np.where(mask_bin <= 0.5)
    if fg[0].size == 0:
        return None
    i = rng.randrange(fg[0].size)
    yf, xf = int(fg[0][i]), int(fg[1][i])
    fg_coord = np.array([[xf, yf]], dtype=np.float32)
    if bg[0].size == 0:
        return None
    j = rng.randrange(bg[0].size)
    yb, xb = int(bg[0][j]), int(bg[1][j])
    bg_coord = np.array([[xb, yb]], dtype=np.float32)
    coords = np.concatenate([fg_coord, bg_coord], axis=0)
    labels = np.array([1, 0], dtype=np.int64)
    return coords, labels


def dice_loss_with_logits(logits: torch.Tensor, targets: torch.Tensor, eps: float = 1e-6) -> torch.Tensor:
    probs = torch.sigmoid(logits)
    t = targets.float()
    inter = (probs * t).sum(dim=(1, 2, 3))
    union = probs.pow(2).sum(dim=(1, 2, 3)) + t.pow(2).sum(dim=(1, 2, 3))
    dice = (2 * inter + eps) / (union + eps)
    return 1.0 - dice.mean()


def set_trainable(sam: nn.Module, train_image_encoder: bool) -> None:
    for p in sam.image_encoder.parameters():
        p.requires_grad = train_image_encoder
    for p in sam.prompt_encoder.parameters():
        p.requires_grad = True
    for p in sam.mask_decoder.parameters():
        p.requires_grad = True
    if train_image_encoder:
        sam.image_encoder.train()
    else:
        sam.image_encoder.eval()
    sam.prompt_encoder.train()
    sam.mask_decoder.train()


def train_one_epoch(
    sam: nn.Module,
    transform: ResizeLongestSide,
    pairs: list[tuple[Path, Path]],
    device: torch.device,
    optimizer: torch.optim.Optimizer,
    scaler: torch.cuda.amp.GradScaler | None,
    bce: nn.Module,
    epoch: int,
    seed: int,
    use_amp: bool,
    augment: AugmentConfig,
) -> float:
    rng = random.Random(seed + epoch)
    rng.shuffle(pairs)
    total_loss = 0.0
    n_ok = 0
    img_size = sam.image_encoder.img_size

    for img_path, mask_path in tqdm(pairs, desc=f"epoch {epoch + 1}", leave=False):
        image = np.array(Image.open(img_path).convert("RGB"))
        mask_bin = load_binary_mask(mask_path)
        if image.shape[0] != mask_bin.shape[0] or image.shape[1] != mask_bin.shape[1]:
            print(f"[WARN] 尺寸不一致，跳过: {img_path.name}", file=sys.stderr)
            continue

        image, mask_bin = augment_training_pair(image, mask_bin, rng, augment)

        sp = sample_points(mask_bin, rng)
        if sp is None:
            continue
        coords_np, labels_np = sp
        orig_hw = (image.shape[0], image.shape[1])
        image_resized = transform.apply_image(image)
        input_torch = torch.from_numpy(image_resized).permute(2, 0, 1).float().to(device)

        coords_t = torch.from_numpy(transform.apply_coords(coords_np, orig_hw)).float().to(device).unsqueeze(0)
        labels_t = torch.from_numpy(labels_np).long().to(device).unsqueeze(0)

        gt_low = mask_to_gt_low(mask_bin, transform, device, img_size)

        optimizer.zero_grad(set_to_none=True)

        try:
            if use_amp and scaler is not None:
                with torch.cuda.amp.autocast():
                    loss = _forward_loss(sam, input_torch, coords_t, labels_t, gt_low, bce)
                scaler.scale(loss).backward()
                scaler.step(optimizer)
                scaler.update()
            else:
                loss = _forward_loss(sam, input_torch, coords_t, labels_t, gt_low, bce)
                loss.backward()
                optimizer.step()
        except RuntimeError as ex:
            print(f"[WARN] 反向失败 {img_path.name}: {ex}", file=sys.stderr)
            optimizer.zero_grad(set_to_none=True)
            continue

        total_loss += float(loss.detach().cpu())
        n_ok += 1

    if n_ok == 0:
        print("[WARN] 本 epoch 无有效步（请检查掩膜/尺寸/显存）", file=sys.stderr)
        return float("inf")
    return total_loss / n_ok


def _forward_loss(
    sam: nn.Module,
    input_torch: torch.Tensor,
    coords_t: torch.Tensor,
    labels_t: torch.Tensor,
    gt_low: torch.Tensor,
    bce: nn.Module,
) -> torch.Tensor:
    x_in = sam.preprocess(input_torch.unsqueeze(0))
    train_enc = any(p.requires_grad for p in sam.image_encoder.parameters())
    if train_enc:
        emb = sam.image_encoder(x_in)
    else:
        with torch.no_grad():
            emb = sam.image_encoder(x_in)

    sparse_emb, dense_emb = sam.prompt_encoder(points=(coords_t, labels_t), boxes=None, masks=None)
    low_res_masks, _iou = sam.mask_decoder(
        image_embeddings=emb,
        image_pe=sam.prompt_encoder.get_dense_pe(),
        sparse_prompt_embeddings=sparse_emb,
        dense_prompt_embeddings=dense_emb,
        multimask_output=False,
    )

    loss_bce = bce(low_res_masks, gt_low)
    loss_dice = dice_loss_with_logits(low_res_masks, gt_low)
    return loss_bce + loss_dice


def main() -> int:
    ap = argparse.ArgumentParser(description="SAM 掩膜监督微调（decoder + prompt encoder；encoder 默认冻结）")
    ap.add_argument("--data-root", type=Path, required=True, help="数据集根目录（其下含 images、masks）")
    ap.add_argument(
        "--images-subdir",
        type=str,
        default="",
        help="图像位于 images/<此项>；留空则使用 images/ 根。YOLO 常用: train",
    )
    ap.add_argument(
        "--masks-subdir",
        type=str,
        default="",
        help="掩膜位于 masks/<此项>；留空则使用 masks/ 根",
    )
    ap.add_argument("--checkpoint", type=Path, required=True, help="预训练 sam_vit_*.pth")
    ap.add_argument("--model-type", choices=["vit_b", "vit_l", "vit_h"], required=True)
    ap.add_argument("--epochs", type=int, default=10)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--weight-decay", type=float, default=0.01)
    ap.add_argument("--device", type=str, default="", help="cuda:0 / cpu；空则自动")
    ap.add_argument("--out", type=Path, default=None, help="保存最佳权重；默认 <data-root>/sam_finetune_best.pth")
    ap.add_argument("--train-image-encoder", action="store_true", help="同时微调 image encoder（显存大、慢）")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--no-amp", action="store_true", help="禁用 CUDA 混合精度")
    ap.add_argument("--no-augment", action="store_true", help="关闭数据增强")
    ap.add_argument("--aug-hflip", type=float, default=0.5, help="水平翻转概率")
    ap.add_argument("--aug-vflip", type=float, default=0.0, help="垂直翻转概率（对称物体可酌情调高）")
    ap.add_argument("--aug-rotate-max", type=float, default=12.0, help="随机旋转最大角度（度）；0 关闭")
    ap.add_argument("--aug-rotate-p", type=float, default=0.85, help="应用旋转的概率")
    ap.add_argument("--aug-color-p", type=float, default=0.6, help="颜色抖动概率（仅图像）")
    ap.add_argument("--aug-brightness", type=float, default=0.22, help="亮度相对抖动幅度")
    ap.add_argument("--aug-contrast", type=float, default=0.22, help="对比度相对抖动幅度")
    ap.add_argument("--aug-saturation", type=float, default=0.22, help="饱和度相对抖动幅度")
    ap.add_argument("--aug-hue", type=float, default=0.04, help="色调抖动幅度（内部已裁剪）")
    args = ap.parse_args()

    data_root = args.data_root.resolve()
    ckpt = args.checkpoint.resolve()
    if not ckpt.is_file():
        print(f"[ERROR] checkpoint 不存在: {ckpt}", file=sys.stderr)
        return 1

    out_path = args.out
    if out_path is None:
        out_path = data_root / "sam_finetune_best.pth"
    else:
        out_path = out_path.resolve()

    device_str = args.device.strip()
    if not device_str:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    else:
        device = torch.device(device_str)

    use_amp = device.type == "cuda" and not args.no_amp
    scaler = torch.cuda.amp.GradScaler() if use_amp else None

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    if device.type == "cuda":
        torch.cuda.manual_seed_all(args.seed)

    pairs = list_pairs(data_root, args.images_subdir, args.masks_subdir)
    img_d, m_d = resolve_image_mask_dirs(data_root, args.images_subdir, args.masks_subdir)
    aug = AugmentConfig(
        enabled=not args.no_augment and TF_vis is not None,
        p_hflip=max(0.0, min(1.0, args.aug_hflip)),
        p_vflip=max(0.0, min(1.0, args.aug_vflip)),
        rotate_deg_max=max(0.0, args.aug_rotate_max),
        p_rotate=max(0.0, min(1.0, args.aug_rotate_p)),
        p_color=max(0.0, min(1.0, args.aug_color_p)),
        brightness_delta=max(0.0, args.aug_brightness),
        contrast_delta=max(0.0, args.aug_contrast),
        saturation_delta=max(0.0, args.aug_saturation),
        hue_delta=max(0.0, args.aug_hue),
    )
    if TF_vis is None and not args.no_augment:
        print("[WARN] 未安装 torchvision，数据增强已跳过；请 pip install torchvision", file=sys.stderr)

    print(f"[INFO] 图像目录: {img_d}")
    print(f"[INFO] 掩膜目录: {m_d}")
    print(f"[INFO] 配对样本数: {len(pairs)}，设备: {device}，AMP: {use_amp}，微调 encoder: {args.train_image_encoder}")
    print(f"[INFO] 数据增强: {'开' if aug.enabled else '关'} (hflip={aug.p_hflip}, vflip={aug.p_vflip}, rot±{aug.rotate_deg_max}°, color_p={aug.p_color})")

    sam = sam_model_registry[args.model_type](checkpoint=str(ckpt))
    sam.to(device)
    set_trainable(sam, args.train_image_encoder)

    transform = ResizeLongestSide(sam.image_encoder.img_size)
    params = [p for p in sam.parameters() if p.requires_grad]
    optimizer = torch.optim.AdamW(params, lr=args.lr, weight_decay=args.weight_decay)
    bce = nn.BCEWithLogitsLoss()

    best_loss = float("inf")
    for epoch in range(max(1, args.epochs)):
        avg = train_one_epoch(
            sam,
            transform,
            pairs,
            device,
            optimizer,
            scaler,
            bce,
            epoch,
            args.seed,
            use_amp,
            aug,
        )
        print(f"[epoch {epoch + 1}/{args.epochs}] loss={avg:.4f}")
        if avg < best_loss:
            best_loss = avg
            out_path.parent.mkdir(parents=True, exist_ok=True)
            torch.save(sam.state_dict(), str(out_path))
            print(f"  -> 已保存最佳: {out_path} (loss={best_loss:.4f})")

    print(f"完成。最佳 loss={best_loss:.4f}，权重: {out_path}")
    print("下一步: 使用 export_sam_onnx.py --checkpoint <此文件> --model-type <同上> 导出 ONNX。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
