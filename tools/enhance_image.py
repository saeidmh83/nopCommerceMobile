#!/usr/bin/env python3
import argparse
import math
import os
import sys
import pathlib
import shutil
import urllib.request
from typing import Optional, Tuple

import cv2
import numpy as np


# -----------------------------
# Utility helpers
# -----------------------------

def ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def read_image_unicode(path: str) -> np.ndarray:
    # Robust imread that works with unicode paths
    data = np.fromfile(path, dtype=np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError(f"Failed to read image: {path}")
    return img


def write_image_unicode(path: str, image: np.ndarray) -> None:
    ext = os.path.splitext(path)[1]
    success, encoded = cv2.imencode(ext, image)
    if not success:
        raise ValueError(f"Failed to encode image for saving: {path}")
    encoded.tofile(path)


# -----------------------------
# Document detection and geometry
# -----------------------------

def order_points_clockwise(pts: np.ndarray) -> np.ndarray:
    # pts: shape (4, 2)
    rect = np.zeros((4, 2), dtype="float32")
    s = pts.sum(axis=1)
    diff = np.diff(pts, axis=1)
    rect[0] = pts[np.argmin(s)]  # top-left
    rect[2] = pts[np.argmax(s)]  # bottom-right
    rect[1] = pts[np.argmin(diff)]  # top-right
    rect[3] = pts[np.argmax(diff)]  # bottom-left
    return rect


def four_point_transform(image: np.ndarray, pts: np.ndarray) -> np.ndarray:
    rect = order_points_clockwise(pts)
    (tl, tr, br, bl) = rect

    widthA = np.linalg.norm(br - bl)
    widthB = np.linalg.norm(tr - tl)
    maxWidth = int(round(max(widthA, widthB)))

    heightA = np.linalg.norm(tr - br)
    heightB = np.linalg.norm(tl - bl)
    maxHeight = int(round(max(heightA, heightB)))

    dst = np.array(
        [
            [0, 0],
            [maxWidth - 1, 0],
            [maxWidth - 1, maxHeight - 1],
            [0, maxHeight - 1],
        ],
        dtype="float32",
    )

    M = cv2.getPerspectiveTransform(rect, dst)
    warped = cv2.warpPerspective(image, M, (maxWidth, maxHeight), flags=cv2.INTER_CUBIC)
    return warped


def detect_document_corners(image: np.ndarray) -> Optional[np.ndarray]:
    # Downscale for speed but keep aspect
    h, w = image.shape[:2]
    scale = 1000.0 / max(h, w)
    scaled = image
    r = 1.0
    if scale < 1.0:
        r = scale
        scaled = cv2.resize(image, (int(w * r), int(h * r)), interpolation=cv2.INTER_AREA)

    gray = cv2.cvtColor(scaled, cv2.COLOR_BGR2GRAY)
    gray = cv2.GaussianBlur(gray, (5, 5), 0)
    edges = cv2.Canny(gray, 60, 180)

    # close gaps
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
    closed = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel, iterations=2)

    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None

    contours = sorted(contours, key=cv2.contourArea, reverse=True)

    for cnt in contours[:5]:
        peri = cv2.arcLength(cnt, True)
        approx = cv2.approxPolyDP(cnt, 0.02 * peri, True)
        if len(approx) == 4:
            pts = approx.reshape(4, 2).astype(np.float32)
            pts = pts / r  # map back to original scale
            return pts

    return None


# -----------------------------
# Image enhancement steps
# -----------------------------

def try_simple_white_balance(image: np.ndarray) -> np.ndarray:
    # Prefer xphoto if available (opencv-contrib)
    try:
        wb = cv2.xphoto.createSimpleWB()
        wb.setP(0.5)
        return wb.balanceWhite(image)
    except Exception:
        # Gray-world fallback
        result = image.astype(np.float32)
        avg_bgr = result.reshape(-1, 3).mean(axis=0)
        gray_value = float(avg_bgr.mean())
        scale = gray_value / (avg_bgr + 1e-6)
        result = np.clip(result * scale, 0, 255).astype(np.uint8)
        return result


def apply_clahe_lab(image: np.ndarray, clip_limit: float = 2.0, tile_grid_size: int = 8) -> np.ndarray:
    lab = cv2.cvtColor(image, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=(tile_grid_size, tile_grid_size))
    l2 = clahe.apply(l)
    lab2 = cv2.merge([l2, a, b])
    return cv2.cvtColor(lab2, cv2.COLOR_LAB2BGR)


def denoise_colored(image: np.ndarray, strength: int = 5) -> np.ndarray:
    # strength [3..10] typical
    return cv2.fastNlMeansDenoisingColored(image, None, strength, strength, 7, 21)


def unsharp_mask(image: np.ndarray, radius: float = 1.5, amount: float = 1.2, threshold: int = 0) -> np.ndarray:
    # Create blurred image
    ksize = max(3, int(round(radius * 2)) | 1)
    blurred = cv2.GaussianBlur(image, (ksize, ksize), radius)
    # Unsharp mask formula
    sharpened = cv2.addWeighted(image, 1 + amount, blurred, -amount, 0)
    if threshold <= 0:
        return sharpened
    # Apply threshold to avoid amplifying noise in flat areas
    low_contrast_mask = np.max(
        [
            cv2.absdiff(image[:, :, c], blurred[:, :, c])
            for c in range(3)
        ],
        axis=0,
    ) < threshold
    result = image.copy()
    result[~low_contrast_mask] = sharpened[~low_contrast_mask]
    return result


def adaptive_gamma(image: np.ndarray) -> np.ndarray:
    # Estimate gamma from mean luminance
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    mean = max(1e-6, float(np.mean(gray)) / 255.0)
    gamma = 0.7 if mean < 0.45 else 1.0
    if abs(gamma - 1.0) < 1e-3:
        return image
    inv = 1.0 / gamma
    table = np.array([(i / 255.0) ** inv * 255 for i in range(256)]).astype("uint8")
    return cv2.LUT(image, table)


# -----------------------------
# Super-resolution (OpenCV dnn_superres)
# -----------------------------
MODEL_URLS = {
    ("edsr", 2): "https://github.com/Saafke/EDSR_Tensorflow/releases/download/1.0/EDSR_x2.pb",
    ("edsr", 3): "https://github.com/Saafke/EDSR_Tensorflow/releases/download/1.0/EDSR_x3.pb",
    ("edsr", 4): "https://github.com/Saafke/EDSR_Tensorflow/releases/download/1.0/EDSR_x4.pb",
    ("espcn", 2): "https://github.com/fannymonori/TF-ESPCN/releases/download/1.0/ESPCN_x2.pb",
    ("espcn", 3): "https://github.com/fannymonori/TF-ESPCN/releases/download/1.0/ESPCN_x3.pb",
    ("espcn", 4): "https://github.com/fannymonori/TF-ESPCN/releases/download/1.0/ESPCN_x4.pb",
    ("fsrcnn", 2): "https://github.com/Saafke/FSRCNN_Tensorflow/releases/download/1.0/FSRCNN_x2.pb",
    ("fsrcnn", 3): "https://github.com/Saafke/FSRCNN_Tensorflow/releases/download/1.0/FSRCNN_x3.pb",
    ("fsrcnn", 4): "https://github.com/Saafke/FSRCNN_Tensorflow/releases/download/1.0/FSRCNN_x4.pb",
    ("lapsrn", 8): "https://github.com/Saafke/LapSRN_Tensorflow/releases/download/1.0/LapSRN_x8.pb",
}


def download_model_if_needed(model_dir: str, model: str, scale: int) -> str:
    ensure_dir(model_dir)
    filename = f"{model}_x{scale}.pb"
    local_path = os.path.join(model_dir, filename)
    if os.path.exists(local_path) and os.path.getsize(local_path) > 1000:
        return local_path
    url = MODEL_URLS.get((model, scale))
    if url is None:
        raise ValueError(f"No URL known for model={model}, scale={scale}")
    print(f"Downloading super-resolution model: {url}")
    with urllib.request.urlopen(url) as response:
        data = response.read()
    with open(local_path, "wb") as f:
        f.write(data)
    return local_path


def apply_superres(image: np.ndarray, model: str = "edsr", scale: int = 2, model_dir: str = "models") -> np.ndarray:
    # Ensure contrib module present
    if not hasattr(cv2, "dnn_superres"):
        print("OpenCV dnn_superres not available; skipping super-resolution.")
        return image
    model_path = download_model_if_needed(model_dir, model, scale)
    sr = cv2.dnn_superres.DnnSuperResImpl_create()
    sr.readModel(model_path)
    sr.setModel(model, scale)
    # Using DNN backend for speed/quality
    try:
        sr.setPreferableBackend(cv2.dnn.DNN_BACKEND_OPENCV)
        sr.setPreferableTarget(cv2.dnn.DNN_TARGET_CPU)
    except Exception:
        pass
    upscaled = sr.upsample(image)
    return upscaled


# -----------------------------
# Pipeline
# -----------------------------

def enhance_document(
    image: np.ndarray,
    *,
    do_warp: bool = True,
    do_superres: bool = True,
    superres_model: str = "edsr",
    superres_scale: int = 2,
    preset: str = "quality",
) -> np.ndarray:
    work = image.copy()

    # 1) Find document borders and rectify perspective
    if do_warp:
        corners = detect_document_corners(work)
        if corners is not None:
            work = four_point_transform(work, corners)

    # 2) White balance
    work = try_simple_white_balance(work)

    # 3) Denoise (light)
    dn_strength = 6 if preset == "max" else 5 if preset == "quality" else 4
    work = denoise_colored(work, strength=dn_strength)

    # 4) Local contrast enhancement
    clahe_clip = 3.0 if preset in ("quality", "max") else 2.0
    work = apply_clahe_lab(work, clip_limit=clahe_clip, tile_grid_size=8)

    # 5) Gamma adjustment for documents shot under tungsten/phone cameras
    work = adaptive_gamma(work)

    # 6) Sharpen
    if preset == "max":
        work = unsharp_mask(work, radius=1.8, amount=1.6, threshold=2)
    elif preset == "quality":
        work = unsharp_mask(work, radius=1.6, amount=1.4, threshold=2)
    else:
        work = unsharp_mask(work, radius=1.4, amount=1.2, threshold=1)

    # 7) Super-resolution
    if do_superres:
        # Only upscale if target side is < 2500 px to avoid excessive sizes
        h, w = work.shape[:2]
        if max(h, w) < 2500:
            work = apply_superres(work, model=superres_model, scale=superres_scale)

    return work


# -----------------------------
# CLI
# -----------------------------

def build_argparser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Enhance a scanned/photographed document image: rectify, denoise, contrast, sharpen, super-res",
    )
    p.add_argument("input", help="Path to input image")
    p.add_argument("-o", "--output", help="Path to output image (default: <input>_enhanced.png)")
    p.add_argument(
        "--preset",
        default="quality",
        choices=["fast", "quality", "max"],
        help="Quality/speed preset",
    )
    p.add_argument("--no-warp", action="store_true", help="Disable perspective rectification")
    p.add_argument("--no-superres", action="store_true", help="Disable super-resolution step")
    p.add_argument(
        "--superres-model",
        default="edsr",
        choices=["edsr", "espcn", "fsrcnn", "lapsrn"],
        help="Super-resolution model (opencv dnn_superres)",
    )
    p.add_argument(
        "--superres-scale",
        type=int,
        default=2,
        choices=[2, 3, 4, 8],
        help="Upscale factor for super-resolution (depends on model)",
    )
    p.add_argument(
        "--model-dir",
        default=os.path.join(os.path.dirname(__file__), "models"),
        help="Directory to cache super-resolution model files",
    )
    return p


def main(argv=None) -> int:
    args = build_argparser().parse_args(argv)

    inp = args.input
    out = args.output
    if out is None:
        stem = os.path.splitext(inp)[0]
        out = f"{stem}_enhanced.png"

    ensure_dir(os.path.dirname(out) or ".")

    image = read_image_unicode(inp)

    preset = args.preset
    # For max preset, default to x4 for stronger readability
    superres_scale = args.superres_scale
    if not args.no_superres and preset == "max" and args.superres_model != "lapsrn":
        superres_scale = 4 if args.superres_scale in (2, 3) else args.superres_scale

    enhanced = enhance_document(
        image,
        do_warp=not args.no_warp,
        do_superres=not args.no_superres,
        superres_model=args.superres_model,
        superres_scale=superres_scale,
        preset=preset,
    )

    write_image_unicode(out, enhanced)
    print(f"Saved enhanced image to: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
