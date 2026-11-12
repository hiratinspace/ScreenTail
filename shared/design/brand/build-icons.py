#!/usr/bin/env python3
"""Builds the app's .ico files from the brand SVGs in this folder (Spec v0.6.0, section 2.0).

    python3 shared/design/brand/build-icons.py [--chrome PATH]

Writes, under client/ScreenTail.UI/Assets/:

- app.ico: the window, taskbar and executable icon. 16, 20 and 24 from app-icon-small.svg (small glyph),
  32, 40 and 48 from glyph-mid-on-navy.svg (mid glyph), 64, 128 and 256 from app-icon-256.svg (full mark).
- Tray/tray-<state>-<light|dark>.ico for idle, recording, paused, offline and draftready: 16, 20 and 24
  from the -16 file, 32, 40 and 48 from the -32 file. The tray picks the frame for the screen's DPI.

Draft ready has no file of its own in this folder. The spec gives it "the idle glyph with a count badge"
(section 2.0, tray glyph tiers), so this script builds it from the idle file: the same transparent ring
the other states cut into the glyph, and a filled rounded square in the screen's colour. The count is in
the tooltip and the menu's first row, since a digit is not legible at 16 px.

Every size is rasterised from its SVG at exactly that many pixels by headless Google Chrome (the SVGs use
masks, which most small rasterisers get wrong), then packed by Pillow's PNG reader and this script's own
ICO writer: 32-bit BMP frames below 256 for the widest compatibility, a PNG frame at 256.

Needs Python 3.10+, Pillow and Google Chrome (or Chromium). Chrome's antialiasing can differ by a level
between versions, so a rebuild may change a few edge pixels; the shapes never change.
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

BRAND = Path(__file__).resolve().parent
REPO = BRAND.parents[2]
ASSETS = REPO / "client" / "ScreenTail.UI" / "Assets"

APP_SIZES = {
    16: "app-icon-small.svg",
    20: "app-icon-small.svg",
    24: "app-icon-small.svg",
    32: "glyph-mid-on-navy.svg",
    40: "glyph-mid-on-navy.svg",
    48: "glyph-mid-on-navy.svg",
    64: "app-icon-256.svg",
    128: "app-icon-256.svg",
    256: "app-icon-256.svg",
}

TRAY_STATES = ["idle", "recording", "paused", "offline", "draftready"]
TRAY_THEMES = ["light", "dark"]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]

# The draft-ready badge: the screen's colour on each taskbar, so it reads as part of the mark.
DRAFT_BADGE = {"light": "#141030", "dark": "#F7F5FE"}

CHROME_CANDIDATES = [
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Chromium.app/Contents/MacOS/Chromium",
    "google-chrome",
    "chromium",
    "chromium-browser",
    "chrome",
]


def sized(svg: str, px: int) -> str:
    """The SVG with its root width and height set to px, so Chrome rasterises it at that size."""
    root = re.search(r"<svg\b[^>]*>", svg)
    if root is None:
        raise ValueError("no <svg> root")
    tag = re.sub(r'\s(width|height)="[^"]*"', "", root.group(0))
    tag = tag[:-1] + f' width="{px}" height="{px}">'
    return svg[: root.start()] + tag + svg[root.end():]


def draft_ready(idle: str, theme: str) -> str:
    """The idle tray glyph with a badge cut into it, built the way the other state files are."""
    mask_id = f"bt{theme}draftready"
    mask = (
        f'<mask id="{mask_id}" maskUnits="userSpaceOnUse" x="-4" y="-4" width="72" height="72">'
        '<rect x="-4" y="-4" width="72" height="72" fill="#fff"/>'
        '<circle cx="50" cy="50" r="15" fill="#000"/></mask>'
    )
    svg = idle.replace("</defs>", mask + "</defs>", 1)
    body_start = svg.index("</defs>") + len("</defs>")
    body_end = svg.rindex("</svg>")
    badge = f'<rect x="39" y="39" width="22" height="22" rx="4" fill="{DRAFT_BADGE[theme]}"/>'
    svg = (
        svg[:body_start]
        + f'<g mask="url(#{mask_id})">'
        + svg[body_start:body_end]
        + "</g>"
        + badge
        + svg[body_end:]
    )
    return svg.replace(", idle,", ", draft ready,")


def tray_svg(state: str, theme: str, source_px: int) -> str:
    if state == "draftready":
        return draft_ready((BRAND / f"tray-idle-{theme}-{source_px}.svg").read_text(), theme)
    return (BRAND / f"tray-{state}-{theme}-{source_px}.svg").read_text()


def find_chrome(explicit: str | None) -> str:
    for candidate in [explicit] if explicit else CHROME_CANDIDATES:
        if candidate and (Path(candidate).exists() or shutil.which(candidate)):
            return candidate
    sys.exit("Google Chrome or Chromium not found; pass --chrome PATH.")


PAGE = """<!DOCTYPE html><html><body><script>
const jobs = %s;
(async () => {
  const out = {};
  for (const [key, px, svg] of jobs) {
    const img = new Image();
    img.src = "data:image/svg+xml;base64," + btoa(svg);
    await img.decode();
    const canvas = document.createElement("canvas");
    canvas.width = px; canvas.height = px;
    canvas.getContext("2d").drawImage(img, 0, 0, px, px);
    out[key] = canvas.toDataURL("image/png");
  }
  const pre = document.createElement("pre");
  pre.id = "out";
  pre.textContent = JSON.stringify(out);
  document.body.appendChild(pre);
})();
</script></body></html>"""


def rasterise(chrome: str, jobs: list[tuple[str, int, str]]) -> dict[str, Image.Image]:
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "render.html"
        page.write_text(PAGE % json.dumps(jobs))
        dom = subprocess.run(
            [chrome, "--headless=new", "--disable-gpu", "--force-device-scale-factor=1",
             f"--user-data-dir={tmp}/profile", "--virtual-time-budget=20000", "--dump-dom", page.as_uri()],
            check=True, capture_output=True, text=True,
        ).stdout
    found = re.search(r'<pre id="out">(.*?)</pre>', dom, re.S)
    if found is None:
        sys.exit("Chrome produced no renders.")
    text = found.group(1).replace("&quot;", '"').replace("&amp;", "&")
    images = {}
    for key, url in json.loads(text).items():
        data = base64.b64decode(url.split(",", 1)[1])
        images[key] = Image.open(io.BytesIO(data)).convert("RGBA")
    return images


def bmp_frame(image: Image.Image) -> bytes:
    """A 32-bit BMP icon frame: header, bottom-up BGRA rows, then the 1-bit AND mask."""
    w, h = image.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = image.tobytes("raw", "BGRA")
    rows = [pixels[y * w * 4:(y + 1) * w * 4] for y in range(h)]
    xor = b"".join(reversed(rows))
    mask_stride = ((w + 31) // 32) * 4
    alpha = image.getchannel("A").tobytes()
    and_rows = []
    for y in range(h):
        row = bytearray(mask_stride)
        for x in range(w):
            if alpha[y * w + x] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        and_rows.append(bytes(row))
    return header + xor + b"".join(reversed(and_rows))


def png_frame(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, "PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: Path, frames: list[Image.Image]) -> None:
    frames = sorted(frames, key=lambda f: f.size[0])
    blobs = [png_frame(f) if f.size[0] >= 256 else bmp_frame(f) for f in frames]
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for frame, blob in zip(frames, blobs):
        w, h = frame.size
        out.write(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    for blob in blobs:
        out.write(blob)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(out.getvalue())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--chrome", help="path to Google Chrome or Chromium")
    args = parser.parse_args()
    chrome = find_chrome(args.chrome)

    jobs: list[tuple[str, int, str]] = []
    for px, name in APP_SIZES.items():
        jobs.append((f"app-{px}", px, sized((BRAND / name).read_text(), px)))
    for state in TRAY_STATES:
        for theme in TRAY_THEMES:
            for px in TRAY_SIZES:
                source = 16 if px < 28 else 32
                jobs.append((f"tray-{state}-{theme}-{px}", px, sized(tray_svg(state, theme, source), px)))

    images = rasterise(chrome, jobs)
    for key, image in images.items():
        px = int(key.rsplit("-", 1)[1])
        if image.size != (px, px):
            sys.exit(f"{key} came out {image.size}, not {px}x{px}.")

    write_ico(ASSETS / "app.ico", [images[f"app-{px}"] for px in APP_SIZES])
    for state in TRAY_STATES:
        for theme in TRAY_THEMES:
            write_ico(ASSETS / "Tray" / f"tray-{state}-{theme}.ico",
                      [images[f"tray-{state}-{theme}-{px}"] for px in TRAY_SIZES])
    print(f"Wrote {1 + len(TRAY_STATES) * len(TRAY_THEMES)} icons under {ASSETS.relative_to(REPO)}.")


if __name__ == "__main__":
    main()
