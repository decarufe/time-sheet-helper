"""Génère les PNG (16 à 256 px) et un .ico multi-taille pour chaque concept SVG.

Usage : pip install resvg-py && python build-icons.py
"""
import struct
from pathlib import Path

import resvg_py

HERE = Path(__file__).parent
PNG_SIZES = [16, 24, 32, 48, 64, 128, 256]
ICO_SIZES = [16, 24, 32, 48, 256]


def render(svg_path: Path, size: int) -> bytes:
    return bytes(resvg_py.svg_to_bytes(svg_path=str(svg_path), width=size, height=size))


def write_ico(path: Path, images: dict[int, bytes]) -> None:
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries, data = b"", b""
    for size, png in images.items():
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        data += png
        offset += len(png)
    path.write_bytes(header + entries + data)


def main() -> None:
    out = HERE / "png"
    out.mkdir(exist_ok=True)
    for svg in sorted(HERE.glob("icon-*.svg")):
        name = svg.stem
        rendered = {s: render(svg, s) for s in PNG_SIZES}
        for size, png in rendered.items():
            (out / f"{name}-{size}.png").write_bytes(png)
        write_ico(HERE / f"{name}.ico", {s: rendered[s] for s in ICO_SIZES})
        print(f"{name}: {len(PNG_SIZES)} PNG + .ico")


if __name__ == "__main__":
    main()
