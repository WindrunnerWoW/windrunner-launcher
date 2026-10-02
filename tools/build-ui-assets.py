#!/usr/bin/env python3
"""Assemble launcher raster assets from generated sources + constructed metal."""
from __future__ import annotations

import argparse
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance, ImageChops

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "artifacts" / "assets"
EXISTING = ROOT / "src/WindrunnerLauncher.App/Assets"
OUT = EXISTING

GREEN = (0, 255, 0)


def ensure_dirs() -> None:
    for name in (
        "Backgrounds",
        "Branding",
        "Frames",
        "Buttons",
        "Controls",
        "Icons",
        "Chrome",
        "Textures",
        "News",
    ):
        (OUT / name).mkdir(parents=True, exist_ok=True)


def load(name: str) -> Image.Image:
    path = SRC / name
    if not path.exists():
        raise FileNotFoundError(path)
    return Image.open(path).convert("RGBA")


def chroma_key(im: Image.Image, g_min: int = 150, dominance: int = 36, feather: int = 2) -> Image.Image:
    arr = np.array(im.convert("RGBA"))
    r = arr[:, :, 0].astype(np.int16)
    g = arr[:, :, 1].astype(np.int16)
    b = arr[:, :, 2].astype(np.int16)
    a = arr[:, :, 3].astype(np.int16)
    green = (g >= g_min) & (g >= r + dominance) & (g >= b + dominance)
    alpha = np.where(green, 0, a)
    arr[:, :, 3] = np.clip(alpha, 0, 255).astype(np.uint8)
    out = Image.fromarray(arr, "RGBA")
    if feather:
        mask = out.split()[-1].filter(ImageFilter.GaussianBlur(feather))
        out.putalpha(mask)
        # Re-kill strong green
        arr = np.array(out)
        r, g, b, a = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2], arr[:, :, 3]
        kill = (g.astype(np.int16) >= g_min) & (g.astype(np.int16) >= r.astype(np.int16) + dominance)
        arr[:, :, 3] = np.where(kill, 0, a)
        out = Image.fromarray(arr, "RGBA")
    return out


def tight_crop(im: Image.Image, pad: int = 4, threshold: int = 12) -> Image.Image:
    arr = np.array(im.split()[-1])
    ys, xs = np.where(arr > threshold)
    if len(xs) == 0:
        return im
    x0, x1 = max(0, xs.min() - pad), min(im.width, xs.max() + pad + 1)
    y0, y1 = max(0, ys.min() - pad), min(im.height, ys.max() + pad + 1)
    return im.crop((x0, y0, x1, y1))


def value_noise(h: int, w: int, cell: int, seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    gy = h // cell + 3
    gx = w // cell + 3
    grid = rng.random((gy, gx)).astype(np.float32)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    y = yy / cell
    x = xx / cell
    y0 = np.floor(y).astype(np.int32)
    x0 = np.floor(x).astype(np.int32)
    fy = y - y0
    fx = x - x0
    # smoothstep
    sy = fy * fy * (3 - 2 * fy)
    sx = fx * fx * (3 - 2 * fx)
    n00 = grid[y0, x0]
    n10 = grid[y0, x0 + 1]
    n01 = grid[y0 + 1, x0]
    n11 = grid[y0 + 1, x0 + 1]
    n0 = n00 * (1 - sx) + n10 * sx
    n1 = n01 * (1 - sx) + n11 * sx
    return n0 * (1 - sy) + n1 * sy


def hammered(h: int, w: int, seed: int = 1) -> tuple[np.ndarray, np.ndarray]:
    height = (
        value_noise(h, w, 28, seed) * 0.50
        + value_noise(h, w, 11, seed + 7) * 0.32
        + value_noise(h, w, 4, seed + 13) * 0.18
    )
    dy, dx = np.gradient(height)
    light = np.clip(0.42 + (-dy) * 2.8 + (-dx) * 0.55, 0.08, 1.35)
    return height.astype(np.float32), light.astype(np.float32)


def colorize_metal(light: np.ndarray, height: np.ndarray, kind: str = "iron") -> np.ndarray:
    if kind == "gold":
        lo = np.array([62, 40, 14], dtype=np.float32)
        hi = np.array([232, 188, 92], dtype=np.float32)
        mid = np.array([168, 122, 48], dtype=np.float32)
    elif kind == "bronze":
        lo = np.array([38, 24, 12], dtype=np.float32)
        hi = np.array([186, 138, 64], dtype=np.float32)
        mid = np.array([110, 78, 36], dtype=np.float32)
    elif kind == "crimson":
        lo = np.array([42, 8, 8], dtype=np.float32)
        hi = np.array([196, 48, 32], dtype=np.float32)
        mid = np.array([122, 22, 18], dtype=np.float32)
    else:
        lo = np.array([10, 8, 7], dtype=np.float32)
        hi = np.array([78, 66, 52], dtype=np.float32)
        mid = np.array([28, 22, 18], dtype=np.float32)
    t = np.clip(light, 0, 1.2)
    rgb = np.empty(light.shape + (3,), dtype=np.float32)
    for i in range(3):
        rgb[:, :, i] = np.where(
            t < 0.55,
            lo[i] + (mid[i] - lo[i]) * (t / 0.55),
            mid[i] + (hi[i] - mid[i]) * np.clip((t - 0.55) / 0.65, 0, 1),
        )
        rgb[:, :, i] += (height - 0.5) * 18
    return np.clip(rgb, 0, 255).astype(np.uint8)


def to_image(rgb: np.ndarray, alpha: np.ndarray | None = None) -> Image.Image:
    if alpha is None:
        return Image.fromarray(rgb, "RGB").convert("RGBA")
    rgba = np.dstack([rgb, np.clip(alpha, 0, 255).astype(np.uint8)])
    return Image.fromarray(rgba, "RGBA")


def rounded_mask(w: int, h: int, radius: int) -> Image.Image:
    m = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)
    return m


def frame_mask(w: int, h: int, thickness: int, radius: int = 6) -> Image.Image:
    outer = rounded_mask(w, h, radius)
    inner = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(inner)
    t = thickness
    r = max(1, radius - 2)
    d.rounded_rectangle((t, t, w - 1 - t, h - 1 - t), radius=r, fill=255)
    return ImageChops.subtract(outer, inner)


def gold_edge(draw: ImageDraw.ImageDraw, box, width: int = 1) -> None:
    x0, y0, x1, y1 = box
    draw.rectangle(box, outline=(210, 168, 78, 230), width=width)


def draw_rivet(canvas: Image.Image, cx: int, cy: int, r: int = 5) -> None:
    d = ImageDraw.Draw(canvas)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(28, 22, 16, 255))
    d.ellipse((cx - r + 1, cy - r + 1, cx + r - 2, cy + r - 2), fill=(168, 124, 52, 255))
    d.ellipse((cx - r + 2, cy - r + 1, cx, cy), fill=(236, 210, 130, 220))


def kill_green(im: Image.Image) -> Image.Image:
    arr = np.array(im)
    r = arr[:, :, 0].astype(np.int16)
    g = arr[:, :, 1].astype(np.int16)
    b = arr[:, :, 2].astype(np.int16)
    a = arr[:, :, 3]
    greenish = (a > 8) & (g > r + 12) & (g > b + 8)
    # fold leftover chroma into bronze
    arr[greenish, 1] = np.clip((r[greenish] * 0.62 + b[greenish] * 0.25).astype(np.int16), 0, 255).astype(np.uint8)
    arr[greenish, 2] = np.clip((b[greenish] * 0.55).astype(np.int16), 0, 200).astype(np.uint8)
    return Image.fromarray(arr, "RGBA")


def build_window_frame(size: int = 768, slice_px: int = 64) -> Image.Image:
    h = w = size
    height, light = hammered(h, w, seed=21)
    iron = colorize_metal(light, height, "iron")
    bronze = colorize_metal(light * 1.05, height, "bronze")
    gold = colorize_metal(np.clip(light * 1.15, 0, 1.4), height, "gold")

    # Mix: iron body, bronze ridges
    body = iron.copy()
    mix = (height > 0.62).astype(np.float32) * 0.35
    body = (body * (1 - mix[..., None]) + bronze * mix[..., None]).astype(np.uint8)

    alpha = np.array(frame_mask(w, h, slice_px, radius=10), dtype=np.float32)

    # Inward gothic corners: extra opaque spikes
    overlay = Image.new("L", (w, h), 0)
    od = ImageDraw.Draw(overlay)
    spike = slice_px
    for ox, oy, sx, sy in (
        (0, 0, 1, 1),
        (w, 0, -1, 1),
        (0, h, 1, -1),
        (w, h, -1, -1),
    ):
        pts = [
            (ox, oy),
            (ox + sx * spike, oy),
            (ox + sx * int(spike * 0.55), oy + sy * int(spike * 0.38)),
            (ox + sx * int(spike * 0.22), oy + sy * int(spike * 0.22)),
            (ox + sx * int(spike * 0.38), oy + sy * int(spike * 0.55)),
            (ox, oy + sy * spike),
        ]
        od.polygon(pts, fill=255)
        # inner talon
        talon = [
            (ox + sx * int(slice_px * 0.18), oy + sy * int(slice_px * 0.18)),
            (ox + sx * int(slice_px * 0.72), oy + sy * int(slice_px * 0.12)),
            (ox + sx * int(slice_px * 0.50), oy + sy * int(slice_px * 0.50)),
            (ox + sx * int(slice_px * 0.12), oy + sy * int(slice_px * 0.72)),
        ]
        od.polygon(talon, fill=255)

    alpha = np.maximum(alpha, np.array(overlay, dtype=np.float32))
    # Soften inner hole so center is fully transparent
    hole = Image.new("L", (w, h), 0)
    hd = ImageDraw.Draw(hole)
    inset = slice_px
    hd.rounded_rectangle(
        (inset, inset, w - 1 - inset, h - 1 - inset),
        radius=4,
        fill=255,
    )
    # Subtract hole except where corner overlay lives
    hole_arr = np.array(hole, dtype=np.float32)
    corner_only = np.array(overlay, dtype=np.float32)
    alpha = np.where((hole_arr > 0) & (corner_only < 40), 0, alpha)

    rgba = np.dstack([body, np.clip(alpha, 0, 255).astype(np.uint8)])
    im = Image.fromarray(rgba, "RGBA")

    # Bevel / gold trims
    d = ImageDraw.Draw(im)
    # outer gold hairline
    d.rectangle((2, 2, w - 3, h - 3), outline=(214, 174, 86, 210), width=2)
    d.rectangle((5, 5, w - 6, h - 6), outline=(48, 34, 18, 180), width=2)
    # inner gold around the opening
    m = inset - 3
    d.rectangle((m, m, w - 1 - m, h - 1 - m), outline=(196, 150, 64, 230), width=2)
    d.rectangle((m + 3, m + 3, w - 4 - m, h - 4 - m), outline=(18, 12, 8, 200), width=2)

    # Top highlight strip
    highlight = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    hd = ImageDraw.Draw(highlight)
    hd.rectangle((8, 6, w - 9, 11), fill=(255, 230, 170, 70))
    im = Image.alpha_composite(im, highlight)

    # Composite AI frame ornaments if available
    try:
        ai = tight_crop(chroma_key(load("window_frame_ref.png"), g_min=120, dominance=28, feather=1), pad=0)
        ai = ai.resize((w, h), Image.Resampling.LANCZOS)
        # Keep AI mostly in corners
        corner = Image.new("L", (w, h), 0)
        cd = ImageDraw.Draw(corner)
        c = slice_px + 8
        cd.rectangle((0, 0, c, c), fill=255)
        cd.rectangle((w - c, 0, w, c), fill=255)
        cd.rectangle((0, h - c, c, h), fill=255)
        cd.rectangle((w - c, h - c, w, h), fill=255)
        corner = corner.filter(ImageFilter.GaussianBlur(10))
        ai_a = ai.split()[-1]
        mixed = ImageChops.multiply(ai_a, corner)
        ai.putalpha(mixed)
        im = Image.alpha_composite(im, ai)
        im = kill_green(im)
    except FileNotFoundError:
        pass

    # Rivets along inner edge
    rpos = inset - 10
    for x, y in (
        (rpos, rpos),
        (w - rpos, rpos),
        (rpos, h - rpos),
        (w - rpos, h - rpos),
        (w // 2, 18),
        (w // 2, h - 18),
        (18, h // 2),
        (w - 18, h // 2),
    ):
        draw_rivet(im, x, y, 6)

    return im


def build_parchment_frame(w: int = 512, h: int = 768, t: int = 28) -> Image.Image:
    height, light = hammered(h, w, seed=9)
    iron = colorize_metal(light, height, "iron")
    alpha = np.array(frame_mask(w, h, t, radius=3), dtype=np.float32)
    im = to_image(iron, alpha)
    d = ImageDraw.Draw(im)
    d.rectangle((1, 1, w - 2, h - 2), outline=(168, 124, 52, 210), width=1)
    d.rectangle((t - 3, t - 3, w - t + 2, h - t + 2), outline=(196, 150, 64, 200), width=1)
    for x, y in (
        (10, 10),
        (w - 11, 10),
        (10, h - 11),
        (w - 11, h - 11),
    ):
        draw_rivet(im, x, y, 5)
    return im


def build_separator_h(w: int = 512, h: int = 8) -> Image.Image:
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle((0, 2, w, 3), fill=(12, 8, 6, 255))
    d.rectangle((0, 3, w, 4), fill=(196, 150, 64, 220))
    d.rectangle((0, 4, w, 5), fill=(72, 50, 22, 180))
    return im


def build_separator_v(w: int = 8, h: int = 512) -> Image.Image:
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle((2, 0, 3, h), fill=(12, 8, 6, 255))
    d.rectangle((3, 0, 4, h), fill=(196, 150, 64, 200))
    d.rectangle((4, 0, 5, h), fill=(72, 50, 22, 160))
    return im


def build_nav_glow(w: int = 256, h: int = 28) -> Image.Image:
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    arr = np.zeros((h, w, 4), dtype=np.uint8)
    cy = h - 6
    for y in range(h):
        for x in range(w):
            dx = (x - w / 2) / (w * 0.42)
            dy = (y - cy) / 7.0
            g = math.exp(-(dx * dx + dy * dy))
            arr[y, x, 0] = 232
            arr[y, x, 1] = 168
            arr[y, x, 2] = 64
            arr[y, x, 3] = int(min(220, g * 210))
    im = Image.fromarray(arr, "RGBA")
    d = ImageDraw.Draw(im)
    d.rectangle((18, h - 5, w - 19, h - 3), fill=(255, 210, 110, 255))
    return im


def sample_red_fill(src: Image.Image, tw: int, th: int) -> Image.Image:
    arr = np.array(src.convert("RGBA"))
    r, g, b, a = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2], arr[:, :, 3]
    red = (a > 80) & (r.astype(np.int16) > g.astype(np.int16) + 25) & (r.astype(np.int16) > b.astype(np.int16) + 20)
    ys, xs = np.where(red)
    if len(xs) < 50:
        fill = Image.new("RGB", (tw, th), (122, 22, 18))
        return fill.convert("RGBA")
    crop = src.crop((xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)).convert("RGB")
    return crop.resize((tw, th), Image.Resampling.LANCZOS).convert("RGBA")


def build_play(state: str, w: int = 560, h: int = 190) -> Image.Image:
    # Silhouette: rectangle with 10px chamfer, very slight bottom taper via mask
    mask = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(mask)
    chamfer = 14
    pts = [
        (chamfer, 0),
        (w - 1 - chamfer, 0),
        (w - 1, chamfer),
        (w - 1, h - 1 - chamfer),
        (w - 1 - chamfer, h - 1),
        (chamfer, h - 1),
        (0, h - 1 - chamfer),
        (0, chamfer),
    ]
    d.polygon(pts, fill=255)

    height, light = hammered(h, w, seed=4)
    iron = colorize_metal(light * 0.9, height, "iron")
    gold = colorize_metal(np.clip(light * 1.2, 0, 1.45), height, "gold")
    bronze = colorize_metal(light, height, "bronze")

    # Outer metal ring 16px, gold 12px, inner crimson
    yy, xx = np.mgrid[0:h, 0:w]
    # distance to edge of polygon approximated by min dist to border of mask
    m = np.array(mask, dtype=np.float32) / 255.0
    # erode via min filter approximations using PIL
    inner_gold = mask.point(lambda v: 255 if v > 0 else 0).filter(ImageFilter.MinFilter(33))
    inner_red = mask.point(lambda v: 255 if v > 0 else 0).filter(ImageFilter.MinFilter(55))
    gmask = np.array(inner_gold, dtype=np.float32) / 255.0
    rmask = np.array(inner_red, dtype=np.float32) / 255.0

    rgb = iron.copy()
    gold_band = (m > 0.5) & (gmask < 0.5)
    rgb[gold_band] = gold[gold_band]
    bronze_band = (m > 0.5) & (gmask >= 0.5) & (rmask < 0.5)
    rgb[bronze_band] = bronze[bronze_band]

    # Crimson fill from AI sample when possible
    try:
        ai_name = {
            "normal": "play_rect_normal.png",
            "hover": "play_rect_hover.png",
            "pressed": "play_rect_pressed.png",
            "disabled": "play_rect_disabled.png",
        }[state]
        if not (SRC / ai_name).exists():
            ai_name = "play_normal.png"
        red_src = sample_red_fill(chroma_key(load(ai_name)), w, h)
        red_arr = np.array(red_src.convert("RGB"))
    except Exception:
        red_arr = colorize_metal(light, height, "crimson")

    if state == "hover":
        red_arr = np.clip(red_arr.astype(np.int16) + 28, 0, 255).astype(np.uint8)
        gold = np.clip(gold.astype(np.int16) + 20, 0, 255).astype(np.uint8)
        rgb[gold_band] = gold[gold_band]
    elif state == "pressed":
        red_arr = np.clip(red_arr.astype(np.int16) - 36, 0, 255).astype(np.uint8)
        # inner shadow
        shadow = np.linspace(0.55, 1.0, h, dtype=np.float32)[:, None, None]
        red_arr = (red_arr.astype(np.float32) * shadow).astype(np.uint8)
    elif state == "disabled":
        gray = red_arr.mean(axis=2, keepdims=True)
        red_arr = np.clip(gray * 0.45 + np.array([[[70, 42, 28]]]) * 0.55, 0, 255).astype(np.uint8)
        gold_desat = gold.mean(axis=2, keepdims=True)
        gold = np.clip(gold_desat * 0.5 + np.array([[[90, 70, 40]]]) * 0.5, 0, 255).astype(np.uint8)
        rgb[gold_band] = gold[gold_band]

    red_sel = rmask > 0.5
    rgb[red_sel] = red_arr[red_sel]

    alpha = np.array(mask, dtype=np.uint8)
    im = to_image(rgb, alpha)

    # Inner illumination
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    if state != "disabled":
        a = 90 if state == "hover" else (30 if state == "pressed" else 55)
        gd.ellipse((w * 0.18, 28, w * 0.82, h * 0.55), fill=(255, 140, 50, a))
        glow = glow.filter(ImageFilter.GaussianBlur(18))
        # clip glow to red
        garr = np.array(glow)
        garr[:, :, 3] = (garr[:, :, 3].astype(np.float32) * rmask).astype(np.uint8)
        glow = Image.fromarray(garr, "RGBA")
        im = Image.alpha_composite(im, glow)

    d = ImageDraw.Draw(im)
    # inner gold hairline around red
    inset = 28
    d.rounded_rectangle(
        (inset, inset - 2, w - inset, h - inset + 2),
        radius=4,
        outline=(232, 196, 96, 210 if state != "disabled" else 90),
        width=2,
    )
    # outer dark rim
    d.polygon(pts, outline=(8, 6, 4, 255))

    # Corner bosses
    for cx, cy in ((22, 22), (w - 23, 22), (22, h - 23), (w - 23, h - 23)):
        draw_rivet(im, cx, cy, 6)

    if state == "hover":
        im = ImageEnhance.Brightness(im).enhance(1.06)
        im = ImageEnhance.Contrast(im).enhance(1.08)
    if state == "pressed":
        im = ImageEnhance.Brightness(im).enhance(0.86)

    return im


def build_dropdown_bg(w: int = 512, h: int = 48) -> Image.Image:
    height, light = hammered(h, w, seed=3)
    # inset: invert lighting a bit
    light = np.clip(0.75 - light * 0.35 + 0.15, 0.1, 1)
    iron = colorize_metal(light, height, "iron")
    mask = rounded_mask(w, h, 3)
    im = to_image(iron, np.array(mask))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((1, 1, w - 2, h - 2), radius=3, outline=(186, 142, 58, 230), width=1)
    d.line((3, 3, w - 4, 3), fill=(8, 6, 5, 160), width=2)
    return im


def build_arrow(size: int = 32) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.polygon([(6, 10), (26, 10), (16, 23)], fill=(214, 170, 72, 255), outline=(90, 62, 22, 255))
    d.line([(8, 11), (16, 20)], fill=(255, 230, 150, 180), width=1)
    return im


def build_gear(size: int = 32) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = cy = size / 2
    r_outer = 12
    r_inner = 7
    teeth = 8
    pts = []
    for i in range(teeth * 2):
        ang = math.pi * i / teeth - math.pi / 2
        r = r_outer if i % 2 == 0 else r_inner + 1.5
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    d.polygon(pts, fill=(198, 150, 58, 255), outline=(70, 48, 18, 255))
    d.ellipse((cx - 4, cy - 4, cx + 4, cy + 4), fill=(28, 20, 12, 255), outline=(214, 174, 86, 255))
    return im


def build_metal_button(w: int = 256, h: int = 64) -> Image.Image:
    height, light = hammered(h, w, seed=12)
    bronze = colorize_metal(light, height, "bronze")
    iron = colorize_metal(light * 0.85, height, "iron")
    rgb = iron.copy()
    # gold rim
    mask = rounded_mask(w, h, 3)
    inner = mask.filter(ImageFilter.MinFilter(9))
    m = np.array(mask)
    inn = np.array(inner)
    rgb[inn > 0] = iron[inn > 0]
    band = (m > 0) & (inn == 0)
    gold = colorize_metal(light * 1.1, height, "gold")
    rgb[band] = gold[band]
    im = to_image(rgb, m)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((1, 1, w - 2, h - 2), radius=3, outline=(210, 168, 78, 200), width=1)
    return im


def build_chrome(kind: str, w: int = 44, h: int = 36) -> Image.Image:
    height, light = hammered(h, w, seed=8 + len(kind))
    iron = colorize_metal(light * 0.75, height, "iron")
    if kind == "close":
        # dark red well
        red = colorize_metal(light * 0.7, height, "crimson")
        iron = (iron.astype(np.float32) * 0.35 + red.astype(np.float32) * 0.65).astype(np.uint8)
    mask = rounded_mask(w, h, 2)
    im = to_image(iron, np.array(mask))
    d = ImageDraw.Draw(im)
    outline = (186, 140, 56, 210) if kind != "close" else (160, 70, 50, 220)
    d.rounded_rectangle((1, 1, w - 2, h - 2), radius=2, outline=outline, width=1)
    gold = (226, 190, 96, 255)
    if kind == "min":
        d.rectangle((12, 16, 31, 19), fill=gold)
    elif kind == "max":
        d.rectangle((12, 10, 31, 26), outline=gold, width=2)
    elif kind == "close":
        d.line((13, 10, 30, 25), fill=(255, 210, 180, 255), width=2)
        d.line((30, 10, 13, 25), fill=(255, 210, 180, 255), width=2)
    elif kind == "gear":
        g = build_gear(22).resize((20, 20), Image.Resampling.LANCZOS)
        im.alpha_composite(g, (12, 8))
    return im


def slice_icon_sheet() -> dict[str, Image.Image]:
    sheet = chroma_key(load("status_icons_sheet.png"), g_min=130, dominance=30, feather=1)
    w, h = sheet.size
    cells = {
        "server": (0, 0, w // 2, h // 2),
        "client": (w // 2, 0, w, h // 2),
        "mods": (0, h // 2, w // 2, h),
        "update": (w // 2, h // 2, w, h),
    }
    out = {}
    for name, box in cells.items():
        icon = tight_crop(sheet.crop(box), pad=4)
        icon.thumbnail((64, 64), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
        canvas.paste(icon, ((64 - icon.width) // 2, (64 - icon.height) // 2), icon)
        out[name] = canvas
    return out


def fit_logo() -> Image.Image:
    logo = tight_crop(chroma_key(load("logo_windrunner.png"), g_min=140, dominance=32, feather=1), pad=6)
    # scale to a useful header width
    target_h = 140
    scale = target_h / logo.height
    nw, nh = int(logo.width * scale), int(logo.height * scale)
    return logo.resize((nw, nh), Image.Resampling.LANCZOS)


def process_news(name: str, dest: Path) -> None:
    im = Image.open(SRC / name).convert("RGB")
    im = im.resize((320, 240), Image.Resampling.LANCZOS)
    im.save(dest, "PNG", optimize=True)


def process_watermark() -> Image.Image:
    try:
        im = tight_crop(chroma_key(load("lion_watermark.png"), g_min=130, dominance=28, feather=1), pad=2)
        im.thumbnail((280, 280), Image.Resampling.LANCZOS)
        # force to gray-brown
        arr = np.array(im)
        lum = arr[:, :, 0:3].mean(axis=2)
        arr[:, :, 0] = np.clip(lum * 0.55 + 40, 0, 255)
        arr[:, :, 1] = np.clip(lum * 0.45 + 30, 0, 255)
        arr[:, :, 2] = np.clip(lum * 0.35 + 20, 0, 255)
        arr[:, :, 3] = (arr[:, :, 3].astype(np.float32) * 0.55).astype(np.uint8)
        return Image.fromarray(arr, "RGBA")
    except FileNotFoundError:
        return Image.new("RGBA", (8, 8), (0, 0, 0, 0))


def save(im: Image.Image, rel: str) -> None:
    path = OUT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    im.save(path, "PNG")
    print(f"wrote {path.relative_to(ROOT)} {im.size} {im.mode}")


def main() -> None:
    ensure_dirs()

    home = Image.open(SRC / "home_realm.png").convert("RGB")
    save(home.convert("RGBA"), "Backgrounds/home_realm.png")

    parch = Image.open(SRC / "parchment.png").convert("RGB")
    save(parch.convert("RGBA"), "Textures/parchment.png")
    metal = Image.open(SRC / "dark_metal.png").convert("RGB")
    save(metal.convert("RGBA"), "Textures/dark_metal.png")
    save(process_watermark(), "Textures/lion_watermark.png")

    save(fit_logo(), "Branding/logo.png")

    save(build_window_frame(), "Frames/window_frame.png")
    save(build_parchment_frame(), "Frames/parchment_frame.png")
    save(build_separator_h(), "Frames/separator_horizontal.png")
    save(build_separator_v(), "Frames/separator_vertical.png")

    for state in ("normal", "hover", "pressed", "disabled"):
        save(build_play(state), f"Buttons/play_{state}.png")
    save(build_metal_button(), "Buttons/metal_button.png")

    # Prefer constructed dropdown (even 9-slice) over the AI plate
    save(build_dropdown_bg(), "Controls/dropdown_background.png")
    try:
        arrow = tight_crop(chroma_key(load("dropdown_arrow.png")), pad=2)
        arrow.thumbnail((32, 32), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (32, 32), (0, 0, 0, 0))
        canvas.paste(arrow, ((32 - arrow.width) // 2, (32 - arrow.height) // 2), arrow)
        save(canvas, "Controls/dropdown_arrow.png")
    except Exception:
        save(build_arrow(), "Controls/dropdown_arrow.png")
    save(build_gear(), "Controls/dropdown_gear.png")
    save(build_nav_glow(), "Controls/nav_glow.png")

    icons = slice_icon_sheet()
    for name, im in icons.items():
        save(im, f"Icons/{name}.png")

    for kind in ("gear", "min", "max", "close"):
        save(build_chrome(kind), f"Chrome/btn_{kind}.png")

    process_news("news_thumb_1.png", OUT / "News/news_1.png")
    process_news("news_thumb_2.png", OUT / "News/news_2.png")
    process_news("news_thumb_3.png", OUT / "News/news_3.png")
    print("done")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=SRC, help="Directory containing the source PNG assets.")
    SRC = parser.parse_args().source
    if not SRC.is_dir():
        parser.error(f"Source directory does not exist: {SRC}")
    main()
