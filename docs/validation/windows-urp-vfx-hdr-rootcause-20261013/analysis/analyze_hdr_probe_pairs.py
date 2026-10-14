#!/usr/bin/env python3
"""Read-only derived analysis for the frozen Phase B and Phase C probe artifacts."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


WIDTH = 480
HEIGHT = 360
ROI_BOTTOM_ORIGIN = {"x_ranges": [[145, 185], [295, 335]], "y_range": [300, 345]}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def read_float_rgba(path: Path) -> np.ndarray:
    values = np.fromfile(path, dtype="<f4")
    expected = WIDTH * HEIGHT * 4
    if values.size != expected:
        raise ValueError(f"Unexpected float payload length for {path}: {values.size} != {expected}")
    image = values.reshape((HEIGHT, WIDTH, 4))
    if not np.isfinite(image).all():
        raise ValueError(f"Non-finite float value in {path}")
    return image


def srgb_to_linear(rgb: np.ndarray) -> np.ndarray:
    values = np.asarray(rgb, dtype=np.float64)
    return np.where(values <= 0.04045, values / 12.92, np.power((values + 0.055) / 1.055, 2.4))


def rgb_stats(rgb: np.ndarray) -> dict:
    values = np.asarray(rgb, dtype=np.float64)
    flat = values.reshape((-1, 3))
    green = (values[..., 1] > 5.0) & (values[..., 1] > 3.0 * values[..., 0]) & (values[..., 2] < 0.5)
    return {
        "mean_all_rgb_samples": float(flat.mean()),
        "p99_all_rgb_samples": float(np.quantile(flat, 0.99)),
        "mean_rgb": [float(v) for v in flat.mean(axis=0)],
        "p99_rgb": [float(v) for v in np.quantile(flat, 0.99, axis=0)],
        "max_rgb": [float(v) for v in flat.max(axis=0)],
        "green_dominant_pixel_count_g_gt_5_g_gt_3r_b_lt_0_5": int(green.sum()),
    }


def pair_stats(normal: np.ndarray, quilt_encoded: np.ndarray, decode_quilt: bool = True) -> dict:
    normal_rgb = np.asarray(normal[..., :3], dtype=np.float64)
    quilt_raw = np.asarray(quilt_encoded[..., :3], dtype=np.float64)
    quilt_linear = srgb_to_linear(quilt_raw) if decode_quilt else quilt_raw
    errors = np.abs(normal_rgb - quilt_linear).reshape((-1, 3))
    return {
        "normal": rgb_stats(normal_rgb),
        "quilt_raw_rgb": rgb_stats(quilt_raw),
        "quilt_srgb_decoded_linear": rgb_stats(quilt_linear),
        "normal_vs_decoded_quilt_abs_error": {
            "mean_all_rgb_samples": float(errors.mean()),
            "p99_all_rgb_samples": float(np.quantile(errors, 0.99)),
            "max_all_rgb_samples": float(errors.max()),
        },
    }


def read_png_rgb(path: Path) -> np.ndarray:
    image = Image.open(path).convert("RGB")
    if image.size != (WIDTH, HEIGHT):
        raise ValueError(f"Unexpected PNG size for {path}: {image.size}")
    return np.asarray(image, dtype=np.float64) / 255.0


def roi_pixels(image: np.ndarray, bottom_up: bool = False) -> np.ndarray:
    y0, y1 = ROI_BOTTOM_ORIGIN["y_range"]
    y_slice = image[y0:y1] if bottom_up else image[HEIGHT - y1 : HEIGHT - y0]
    pieces = [y_slice[:, x0:x1] for x0, x1 in ROI_BOTTOM_ORIGIN["x_ranges"]]
    combined = np.concatenate(pieces, axis=1)
    if combined.shape[0] * combined.shape[1] != 3600:
        raise ValueError(f"Coin halo ROI has unexpected pixel count: {combined.shape}")
    return combined.reshape((-1, 3))


def mean_rgb(values: np.ndarray) -> list[float]:
    return [float(v) for v in np.asarray(values, dtype=np.float64).reshape((-1, 3)).mean(axis=0)]


def roi_pair(normal: np.ndarray, quilt: np.ndarray, quilt_is_srgb: bool, binary_bottom_up: bool = False) -> dict:
    n = roi_pixels(normal[..., :3], bottom_up=binary_bottom_up)
    q_encoded = roi_pixels(quilt[..., :3], bottom_up=binary_bottom_up)
    q = srgb_to_linear(q_encoded) if quilt_is_srgb else q_encoded
    mae = np.abs(n - q).reshape(-1)
    return {
        "pixel_count": int(n.shape[0]),
        "normal_mean_all_rgb_samples": float(n.mean()),
        "normal_mean_rgb": mean_rgb(n),
        "quilt_encoded_mean_all_rgb_samples": float(q_encoded.mean()),
        "quilt_encoded_mean_rgb": mean_rgb(q_encoded),
        "quilt_srgb_decoded_linear_mean_all_rgb_samples": float(q.mean()) if quilt_is_srgb else None,
        "quilt_srgb_decoded_linear_mean_rgb": mean_rgb(q) if quilt_is_srgb else None,
        "normal_vs_quilt_linearized_mean_absolute_error": float(mae.mean()),
        "normal_vs_quilt_linearized_p99_absolute_error": float(np.quantile(mae, 0.99)),
    }


def input_file(path: Path, root: Path, registry: dict[str, dict]) -> Path:
    resolved = path.resolve()
    try:
        rel = resolved.relative_to(root.resolve()).as_posix()
    except ValueError as exc:
        raise ValueError(f"Input escaped repository root: {path}") from exc
    if rel not in registry:
        registry[rel] = {"path": rel, "bytes": resolved.stat().st_size, "sha256": sha256(resolved)}
    return resolved


def inputs_for_pair(base: Path, phase: str, kind: str) -> tuple[Path, Path]:
    if phase == "phase-b":
        folder = base / "docs/validation/windows-urp-vfx-rendering-incident-20261013/probes/hdr-target/phase-b" / kind / "evidence"
        prefix = "green-lights" if kind == "green" else "coin-min"
        if kind == "green":
            return folder / f"{prefix}-first-alive-normal.bin", folder / f"{prefix}-first-alive-quilt.bin"
    else:
        folder = base / "docs/validation/windows-urp-vfx-hdr-rootcause-20261013/phase-c/runs" / kind / "evidence"
        prefix = "green-lights" if kind == "green" else "coin-min"
    if kind == "coin":
        return folder / f"{prefix}-normal-float-peak.bin", folder / f"{prefix}-quilt-paired-float-peak.bin"
    raise ValueError(f"Unsupported paired input: {phase}/{kind}")


def analyze_green(base: Path, phase: str, registry: dict[str, dict]) -> dict:
    folder = (base / "docs/validation/windows-urp-vfx-rendering-incident-20261013/probes/hdr-target/phase-b" / "green/evidence") if phase == "phase-b" else (base / "docs/validation/windows-urp-vfx-hdr-rootcause-20261013/phase-c/runs/green/evidence")
    pair_names = ["green-lights-first-alive", "green-lights-peak-alive"]
    pairs = {}
    for name in pair_names:
        normal_path = input_file(folder / f"{name}-normal.bin", base, registry)
        quilt_path = input_file(folder / f"{name}-quilt.bin", base, registry)
        normal = read_float_rgba(normal_path)
        quilt = read_float_rgba(quilt_path)
        pairs[name] = pair_stats(normal, quilt)
    metadata_path = input_file(folder / "green-lights-float-metadata.txt", base, registry)
    csv_path = input_file(folder / "frames.csv", base, registry)
    with csv_path.open("r", encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    native = [int(row["greenLightsAlive"]) > 0 for row in rows]
    normal_visual = [int(row["normalGreenPixels"]) > 0 for row in rows]
    quilt_visual = [int(row["quiltGreenPixels"]) > 0 for row in rows]
    return {
        "paired_float_samples": pairs,
        "native_and_visual_frame_counts": {
            "csv_rows": len(rows),
            "native_green_lights_alive_positive_rows": sum(native),
            "normal_green_pixel_positive_rows": sum(normal_visual),
            "quilt_green_pixel_positive_rows": sum(quilt_visual),
            "normal_quilt_visual_presence_identical_rows": sum(n == q for n, q in zip(normal_visual, quilt_visual)),
            "both_visual_positive_rows": sum(n and q for n, q in zip(normal_visual, quilt_visual)),
            "both_visual_zero_rows": sum(not n and not q for n, q in zip(normal_visual, quilt_visual)),
            "native_positive_and_both_visual_positive_rows": sum(n and v and q for n, v, q in zip(native, normal_visual, quilt_visual)),
            "native_positive_and_both_visual_zero_rows": sum(n and not v and not q for n, v, q in zip(native, normal_visual, quilt_visual)),
        },
        "native_pair_metadata_path": metadata_path.relative_to(base).as_posix(),
    }


def analyze_coin(base: Path, phase: str, registry: dict[str, dict]) -> dict:
    folder = (base / "docs/validation/windows-urp-vfx-rendering-incident-20261013/probes/hdr-target/phase-b" / "coin/evidence") if phase == "phase-b" else (base / "docs/validation/windows-urp-vfx-hdr-rootcause-20261013/phase-c/runs/coin/evidence")
    normal_path = input_file(folder / "hdr-float-normal.bin", base, registry)
    quilt_path = input_file(folder / "hdr-float-quilt.bin", base, registry)
    normal_float = read_float_rgba(normal_path)
    quilt_float = read_float_rgba(quilt_path)
    float_pair = pair_stats(normal_float, quilt_float)
    float_meta = input_file(folder / "hdr-float-metadata.txt", base, registry)
    min_normal_path = input_file(folder / "coin-min-normal-float-peak.bin", base, registry)
    min_quilt_path = input_file(folder / "coin-min-quilt-paired-float-peak.bin", base, registry)
    min_pair = pair_stats(read_float_rgba(min_normal_path), read_float_rgba(min_quilt_path))
    normal_png_kind = "normal-luma-min-normal.png"
    quilt_png_kind = "normal-luma-min-quilt.png"
    normal_png_min = input_file(folder / normal_png_kind, base, registry)
    quilt_png_min = input_file(folder / quilt_png_kind, base, registry)
    normal_png_max = input_file(folder / "normal-luma-max-normal.png", base, registry)
    quilt_png_max = input_file(folder / "normal-luma-max-quilt.png", base, registry)
    png_min_roi = roi_pair(read_png_rgb(normal_png_min), read_png_rgb(quilt_png_min), quilt_is_srgb=True)
    png_max_roi = roi_pair(read_png_rgb(normal_png_max), read_png_rgb(quilt_png_max), quilt_is_srgb=True)
    raw_float_roi = roi_pair(normal_float, quilt_float, quilt_is_srgb=True, binary_bottom_up=True)
    return {
        "raw_float_observation_zero_pair": float_pair,
        "raw_float_observation_zero_coin_halo_roi": raw_float_roi,
        "raw_float_metadata_path": float_meta.relative_to(base).as_posix(),
        "coin_minimum_float_pair": min_pair,
        "paired_png_coin_halo_roi": {
            "normal_luma_min": png_min_roi,
            "normal_luma_max": png_max_roi,
            "normal_png_interpretation": "raw RGB8 values divided by 255; no additional transform",
            "quilt_png_interpretation": "RGB8 values divided by 255 and piecewise inverse-sRGB transformed",
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[4])
    args = parser.parse_args()
    root = args.root.resolve()
    output_dir = Path(__file__).resolve().parent
    input_registry: dict[str, dict] = {}
    result = {
        "schemaVersion": 1,
        "scope": "Read-only paired-output derivations from frozen Phase B and Phase C artifacts.",
        "environment": {"platform": "Windows", "graphicsApi": "D3D12", "mode": "Unity Play Mode", "views": 1, "aa": "Off", "postProcessing": "On", "hardwareDevice": False, "sdkRuntimeModified": False},
        "transformAssumptions": {
            "normalFloat": "R16G16B16A16_SFloat linear target, read back as little-endian float32 RGBA.",
            "quiltFloat": "R8G8B8A8_SRGB target read back to float32 RGBA grid; values are interpreted as normalized sRGB code values then piecewise inverse-sRGB transformed for derived comparisons.",
            "coordinateMapping": "Unity raw float RGBA payloads are treated as bottom-row-first for the bottom-origin ROI. PNG inputs are top-row-first and are vertically mapped to that same bottom-origin ROI.",
            "inverseSrgb": "Per RGB channel: v/12.92 if v<=0.04045; otherwise ((v+0.055)/1.055)^2.4. No clamping or normalization beyond existing RGB8 range.",
            "corroboration": "Probe recorded sRGB quilt targets and controlled GL.sRGBWrite for the candidate blit. This encoding transform is an analysis assumption, not an independent colorimeter calibration.",
            "greenMask": "G>5 and G>3*R and B<0.5 on linear float values; descriptive pixel mask only.",
            "coinHaloRoi": "Bottom-origin 3600 pixels: x=[145,185) or [295,335), y=[300,345).",
        },
        "phaseB": {"green": analyze_green(root, "phase-b", input_registry), "coin": analyze_coin(root, "phase-b", input_registry)},
        "phaseC": {"green": analyze_green(root, "phase-c", input_registry), "coin": analyze_coin(root, "phase-c", input_registry)},
    }
    result["inputs"] = sorted(input_registry.values(), key=lambda item: item["path"])
    output_path = output_dir / "analysis.json"
    output_path.write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n", encoding="ascii", newline="\n")
    print(json.dumps({"analysisPath": str(output_path), "inputCount": len(result["inputs"]), "phaseBGreenRows": result["phaseB"]["green"]["native_and_visual_frame_counts"], "phaseCGreenRows": result["phaseC"]["green"]["native_and_visual_frame_counts"], "phaseBCoinHalo": result["phaseB"]["coin"]["raw_float_observation_zero_coin_halo_roi"], "phaseCCoinHalo": result["phaseC"]["coin"]["raw_float_observation_zero_coin_halo_roi"]}, indent=2, ensure_ascii=True))


if __name__ == "__main__":
    main()
