#!/usr/bin/env python3
"""
生成程序图标（纯标准库，不依赖 Pillow）。

图形：深色圆角方块 + 一簇火苗 —— 呼应「燧人氏，带来火种的人」。
输出：多尺寸 .ico（16~256），供 ApplicationIcon 与窗口图标共用。

用法：python scripts/make_icon.py [输出路径]
"""

import math
import os
import struct
import sys

SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # 超采样倍数，抗锯齿

BG_TOP = (27, 29, 34)
BG_BOTTOM = (15, 16, 19)
EMBER_GLOW = (226, 87, 30)
FLAME_CORE = (255, 214, 130)
FLAME_MID = (247, 126, 42)
FLAME_EDGE = (198, 52, 10)

A_POW = 0.46
B_POW = 0.85
V_PEAK = A_POW / (A_POW + B_POW)
F_PEAK = V_PEAK ** A_POW * (1 - V_PEAK) ** B_POW  # f(v) 的最大值

BASE_Y = 0.10
FLAME_H = 0.80


def _rounded_rect_inside(x: float, y: float, radius: float = 0.22) -> bool:
    dx = max(abs(x - 0.5) - (0.5 - radius), 0.0)
    dy = max(abs(y - 0.5) - (0.5 - radius), 0.0)
    return dx * dx + dy * dy <= radius * radius


def _flame(x: float, y: float):
    """返回 (是否在火苗内, 归一化横向位置 d)。"""
    v = (y - BASE_Y) / FLAME_H
    if v < 0.0 or v > 1.0:
        return False, 0.0

    vv = max(v, 0.0)
    f = max(vv, 1e-4) ** A_POW * max(1.0 - vv, 1e-4) ** B_POW
    hw = 0.245 * f / F_PEAK

    # 底部收成圆弧（而不是一刀切平）
    cap = 0.19
    if v < cap:
        t = (cap - v) / cap
        hw *= math.sqrt(max(0.0, 1.0 - t * t))

    # 极轻微的边缘起伏，避免死板
    hw *= 1.0 + 0.05 * math.sin(v * 9.0 + 1.2)
    if hw <= 1e-6:
        return False, 0.0

    xs = 0.5 + 0.05 * math.sin(vv * 2.4 - 0.5) * (0.35 + 0.65 * vv)
    dx = abs(x - xs)
    if dx > hw:
        return False, 0.0
    return True, dx / hw


def _mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def _shade(color, factor):
    return tuple(min(255.0, max(0.0, c * factor)) for c in color)


def render(size: int):
    """返回 size*size 的 RGBA 像素（自上而下逐行）。"""
    n = size * SS
    acc = [[[0.0, 0.0, 0.0, 0.0] for _ in range(size)] for _ in range(size)]

    for py in range(n):
        y = 1.0 - (py + 0.5) / n  # 归一化，y 向上
        row = py // SS
        for px in range(n):
            x = (px + 0.5) / n

            if not _rounded_rect_inside(x, y):
                continue

            # 背景竖向渐变
            bg = _mix(BG_BOTTOM, BG_TOP, y)
            # 底部余烬微光
            gd = math.hypot(x - 0.5, y - 0.32) / 0.6
            if gd < 1.0:
                bg = _mix(bg, EMBER_GLOW, (1.0 - gd) ** 2 * 0.22)

            inside, d = _flame(x, y)
            if inside:
                if d < 0.45:
                    c = _mix(FLAME_CORE, FLAME_MID, d / 0.45)
                else:
                    c = _mix(FLAME_MID, FLAME_EDGE, (d - 0.45) / 0.55)
                # 越靠近火苗根部越亮
                c = _shade(c, 1.08 - 0.30 * max(0.0, (y - BASE_Y) / FLAME_H))
                bg = c

            cell = acc[row][px // SS]
            cell[0] += bg[0]
            cell[1] += bg[1]
            cell[2] += bg[2]
            cell[3] += 255.0

    inv = 1.0 / (SS * SS)
    out = bytearray()
    for row in range(size):
        for col in range(size):
            r, g, b, a = acc[row][col]
            coverage = a * inv  # 0~255
            if coverage <= 0.0:
                out += b"\x00\x00\x00\x00"
                continue
            # 颜色按「有覆盖的采样点」求平均，避免除以覆盖率后发暗
            k = 255.0 / a
            out += bytes((
                int(min(255.0, r * k) + 0.5),
                int(min(255.0, g * k) + 0.5),
                int(min(255.0, b * k) + 0.5),
                int(min(255.0, coverage) + 0.5),
            ))
    return bytes(out)


def bmp_for_ico(size: int, rgba_top_down: bytes) -> bytes:
    """32bpp DIB：DIB 里是 BGRA 顺序，且行自下而上；AND 掩码全零。"""
    header = struct.pack(
        "<IiiHHIIiiII",
        40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0,
    )
    stride = size * 4
    rows = []
    for row in range(size - 1, -1, -1):
        line = rgba_top_down[row * stride:(row + 1) * stride]
        bgra = bytearray(stride)
        for i in range(0, stride, 4):
            r, g, b, a = line[i], line[i + 1], line[i + 2], line[i + 3]
            bgra[i] = b
            bgra[i + 1] = g
            bgra[i + 2] = r
            bgra[i + 3] = a
        rows.append(bytes(bgra))
    pixels = b"".join(rows)
    mask_stride = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_stride * size)
    return header + pixels + mask


def png_bytes(size: int) -> bytes:
    """把图标渲染成 PNG（界面里当 logo 用）。"""
    import zlib

    rgba = render(size)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    stride = size * 4
    rows = b"".join(b"\x00" + rgba[y * stride:(y + 1) * stride] for y in range(size))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9))
            + chunk(b"IEND", b""))


def main() -> int:
    out_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "src", "BD2ModManager.Gui", "Assets", "app.ico",
    )
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    images = []
    for s in SIZES:
        images.append((s, bmp_for_ico(s, render(s))))
        print(f"  渲染 {s}x{s}")

    dir_size = 6 + 16 * len(images)
    offset = dir_size
    entries = b""
    for s, data in images:
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    with open(out_path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        f.write(entries)
        for _, data in images:
            f.write(data)

    print(f"已写入 {out_path}（{os.path.getsize(out_path)} 字节）")

    logo_path = os.path.join(os.path.dirname(out_path), "logo.png")
    with open(logo_path, "wb") as f:
        f.write(png_bytes(128))
    print(f"已写入 {logo_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
