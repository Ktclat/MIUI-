"""Build an original MIUI- icon from geometry; no vendor image input or packages."""
import argparse
from pathlib import Path
import struct


SIZES = (16, 24, 32, 48, 64)


def segment_distance_squared(x, y, start, end):
    dx, dy = end[0] - start[0], end[1] - start[1]
    t = max(0.0, min(1.0, ((x - start[0]) * dx + (y - start[1]) * dy) / (dx * dx + dy * dy)))
    return (x - start[0] - t * dx) ** 2 + (y - start[1] - t * dy) ** 2


def sample(x, y):
    # A blue rounded square and an independently drawn white M-minus mark.
    nearest_x, nearest_y = max(0.23, min(0.77, x)), max(0.23, min(0.77, y))
    if (x - nearest_x) ** 2 + (y - nearest_y) ** 2 > 0.19 ** 2:
        return None
    points = ((0.21, 0.68), (0.21, 0.32), (0.37, 0.51), (0.53, 0.32), (0.53, 0.68))
    segments = tuple(zip(points, points[1:])) + (((0.65, 0.50), (0.81, 0.50)),)
    if any(segment_distance_squared(x, y, a, b) <= 0.035 ** 2 for a, b in segments):
        return (255, 255, 255)
    return (32, 103, 218)


def dib(size):
    pixels = bytearray()
    scale = 4
    for row in range(size - 1, -1, -1):
        for col in range(size):
            colors = [sample((col + (sx + 0.5) / scale) / size,
                             (row + (sy + 0.5) / scale) / size)
                      for sy in range(scale) for sx in range(scale)]
            opaque = [color for color in colors if color is not None]
            alpha = round(255 * len(opaque) / (scale * scale))
            rgb = tuple(round(sum(c[k] for c in opaque) / len(opaque)) for k in range(3)) if opaque else (0, 0, 0)
            pixels.extend((*reversed(rgb), alpha))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytearray(mask_stride * size)
    for row in range(size):
        for col in range(size):
            if pixels[(row * size + col) * 4 + 3] == 0:
                mask[row * mask_stride + col // 8] |= 0x80 >> (col % 8)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + pixels + mask


def make_icon():
    payloads = [dib(size) for size in SIZES]
    result = bytearray(struct.pack("<HHH", 0, 1, len(payloads)))
    offset = 6 + 16 * len(payloads)
    for size, payload in zip(SIZES, payloads):
        result.extend(struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    return bytes(result) + b"".join(payloads)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(make_icon())
