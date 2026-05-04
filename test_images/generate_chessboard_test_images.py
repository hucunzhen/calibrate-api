#!/usr/bin/env python3
"""
生成三张棋盘格测试图（内侧角点 9x6，与 Flow 示例 chessboard_find_corners / calibrate 一致）。
依赖: pip install opencv-python numpy

输出（与本目录相对）:
  chessboard_view1.png / view2 / view3 — 多视图标定用
  chessboard_sample.png — 与 view1 相同，供单图角点流程使用
"""

from __future__ import annotations

import os

try:
    import cv2
    import numpy as np
except ImportError as e:
    raise SystemExit(
        "请先安装: pip install opencv-python numpy\n" + str(e)
    ) from e

# OpenCV findChessboardCorners 的 patternSize = 内侧角点列数 x 行数
INNER_COLS = 9
INNER_ROWS = 6
# 方格像素边长（仅合成图，与 squareSizeMm 无关）
SQUARE_PX = 48
MARGIN_PX = 80


def make_checkerboard_gray(
    inner_cols: int,
    inner_rows: int,
    square_px: int,
    margin_px: int,
) -> np.ndarray:
    """灰度图：白底黑格，内侧角点数为 inner_cols x inner_rows。"""
    n_squares_x = inner_cols + 1
    n_squares_y = inner_rows + 1
    h = n_squares_y * square_px + 2 * margin_px
    w = n_squares_x * square_px + 2 * margin_px
    img = np.full((h, w), 255, dtype=np.uint8)
    for i in range(n_squares_y):
        for j in range(n_squares_x):
            if (i + j) % 2 == 0:
                y0 = margin_px + i * square_px
                x0 = margin_px + j * square_px
                img[y0 : y0 + square_px, x0 : x0 + square_px] = 0
    return img


def to_bgr(img_gray: np.ndarray) -> np.ndarray:
    return cv2.cvtColor(img_gray, cv2.COLOR_GRAY2BGR)


def mild_perspective(img_gray: np.ndarray, idx: int) -> np.ndarray:
    """轻微透视/旋转，模拟不同拍摄视角（仍应能稳定检出棋盘）。"""
    h, w = img_gray.shape[:2]
    src = np.float32([[0, 0], [w, 0], [w, h], [0, h]])
    # 小幅扰动四角
    rng = np.random.default_rng(42 + idx)
    jitter = 25 + idx * 8
    dst = src + rng.uniform(-jitter, jitter, src.shape).astype(np.float32)
    M = cv2.getPerspectiveTransform(src, dst)
    warped = cv2.warpPerspective(
        img_gray, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=255
    )
    # 再叠一小角度平面旋转，便于三张图互不相同
    angle = -6.0 + idx * 7.0
    center = (w / 2.0, h / 2.0)
    R = cv2.getRotationMatrix2D(center, angle, 1.0)
    rotated = cv2.warpAffine(
        warped, R, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=255
    )
    return rotated


def verify_corners(path: str) -> None:
    img = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if img is None:
        print(f"[WARN] 无法读取: {path}")
        return
    pattern = (INNER_COLS, INNER_ROWS)
    ok, corners = cv2.findChessboardCorners(
        img,
        pattern,
        cv2.CALIB_CB_ADAPTIVE_THRESH | cv2.CALIB_CB_NORMALIZE_IMAGE,
    )
    if ok:
        print(f"[OK] {os.path.basename(path)}: 检出棋盘角点 {len(corners)} 个")
    else:
        print(f"[FAIL] {os.path.basename(path)}: 未检出 {INNER_COLS}x{INNER_ROWS} 棋盘，请调参或增大边距")


def main() -> None:
    out_dir = os.path.dirname(os.path.abspath(__file__))
    base = make_checkerboard_gray(INNER_COLS, INNER_ROWS, SQUARE_PX, MARGIN_PX)

    outputs = [
        ("chessboard_view1.png", to_bgr(base)),
        ("chessboard_view2.png", to_bgr(mild_perspective(base, 0))),
        ("chessboard_view3.png", to_bgr(mild_perspective(base, 1))),
    ]

    for name, bgr in outputs:
        path = os.path.join(out_dir, name)
        cv2.imwrite(path, bgr)
        print(f"写入 {path}")

    sample_path = os.path.join(out_dir, "chessboard_sample.png")
    cv2.imwrite(sample_path, outputs[0][1])
    print(f"写入 {sample_path} (与 view1 相同)")

    print("\n自检 findChessboardCorners:")
    for name, _ in outputs:
        verify_corners(os.path.join(out_dir, name))
    verify_corners(sample_path)


if __name__ == "__main__":
    main()
