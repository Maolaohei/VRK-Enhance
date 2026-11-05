# -*- coding: utf-8 -*-
"""Generate the crosshair sprite used by VRK FlatFPS.

Output is a raw 128x128 RGBA buffer (the format the plugin uploads with
Texture2D.SetPixels32) plus a PNG preview for humans. Everything is drawn from
scratch with distance fields -- no third-party art involved.

    python fixtures/make_crosshair.py

Shape: a soft-red ring with a hairline light rim on both sides, plus four short
light tick marks crossing the ring on the vertical/horizontal axes. Geometry is
given for a 128 px sprite and rendered with 6x supersampling for smooth edges.
"""
import os
import struct
import zlib

SIZE = 128
SS = 6                      # supersampling factor per axis

CENTRE = SIZE / 2.0

RING_IN = 34.0              # soft-red annulus
RING_OUT = 49.0
RIM_THICK = 1.6             # hairline rim hugging the ring, both sides
TICK_IN = 30.0              # tick marks cross the ring
TICK_OUT = 55.0
TICK_HALF_WIDTH = 2.4

C_RING = (255, 111, 111)
A_RING = 231
C_RIM = (255, 255, 255)
A_RIM = 145
C_TICK = (255, 243, 243)
A_TICK = 165

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_BIN = os.path.join(HERE, "crosshair_128.bin")
OUT_PNG = os.path.join(HERE, "crosshair_128.png")


def sample(px, py):
    """Colour of one sub-pixel sample, top layer first."""
    dx, dy = px - CENTRE, py - CENTRE
    ax, ay = abs(dx), abs(dy)
    r = (dx * dx + dy * dy) ** 0.5

    # tick marks: short bars on the axes that run across the ring
    if TICK_IN - 0.5 <= r <= TICK_OUT + 0.5:
        if ax <= TICK_HALF_WIDTH or ay <= TICK_HALF_WIDTH:
            return C_TICK + (A_TICK,)

    if RING_IN <= r <= RING_OUT:
        return C_RING + (A_RING,)

    if (RING_OUT < r <= RING_OUT + RIM_THICK) or (RING_IN - RIM_THICK <= r < RING_IN):
        return C_RIM + (A_RIM,)

    return (0, 0, 0, 0)


def render():
    step = 1.0 / SS
    total = SS * SS
    out = []
    for y in range(SIZE):
        for x in range(SIZE):
            pr = pg = pb = pa = 0.0
            for sy in range(SS):
                py = y + (sy + 0.5) * step
                for sx in range(SS):
                    px = x + (sx + 0.5) * step
                    r, g, b, a = sample(px, py)
                    if a:
                        af = a / 255.0
                        pr += r * af
                        pg += g * af
                        pb += b * af
                        pa += af
            if pa <= 0.0:
                out.append((0, 0, 0, 0))
                continue
            # un-premultiply, then restore the average alpha
            a_avg = pa / total
            out.append((int(round(pr / pa)), int(round(pg / pa)),
                        int(round(pb / pa)), int(round(a_avg * 255))))
    return out


def write_bin(pixels, path):
    buf = bytearray()
    for r, g, b, a in pixels:
        buf += bytes((r, g, b, a))
    if len(buf) != SIZE * SIZE * 4:
        raise SystemExit("unexpected buffer length %d" % len(buf))
    with open(path, "wb") as fh:
        fh.write(buf)
    return len(buf)


def write_png(pixels, path):
    rows = []
    for y in range(SIZE):
        row = bytearray(b"\x00")
        for x in range(SIZE):
            row += bytes(pixels[y * SIZE + x])
        rows.append(bytes(row))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)
    return len(png)


if __name__ == "__main__":
    px = render()
    opaque = sum(1 for p in px if p[3] > 40)
    print("sprite %dx%d, visible pixels %d (%.1f%%)" % (SIZE, SIZE, opaque, 100.0 * opaque / (SIZE * SIZE)))
    print("wrote %s (%d bytes)" % (OUT_BIN, write_bin(px, OUT_BIN)))
    print("wrote %s (%d bytes)" % (OUT_PNG, write_png(px, OUT_PNG)))
