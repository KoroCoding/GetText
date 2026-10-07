"""README の画像を作る (python tools/make_readme_images.py <Windows の画面の画像のフォルダ> [Mac の画面の画像のフォルダ])。

画面の画像は CI (ci/gettext-windows-results の snapshots_light、ci/gettext-mac-results の snapshots) が作る。
docs/images/ に、最初に見せる 1 枚 (hero.png) と、機能ごとの画像を置く。
"""
import os
import sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "images")
BG = (243, 243, 243)


def load(folder, name, scale=1 / 1.5):
    im = Image.open(os.path.join(folder, name)).convert("RGB")
    if scale != 1:
        im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
    return im


def framed(im, radius=10):
    """角を丸めて細い枠を付ける (窓らしく見せる)。"""
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, im.width - 1, im.height - 1], radius=radius, fill=255)
    out = Image.new("RGBA", im.size, (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    ImageDraw.Draw(out).rounded_rectangle([0, 0, im.width - 1, im.height - 1], radius=radius, outline=(210, 210, 210), width=1)
    return out


def row(images, gap=24, pad=24, valign="top"):
    w = sum(i.width for i in images) + gap * (len(images) - 1) + pad * 2
    h = max(i.height for i in images) + pad * 2
    canvas = Image.new("RGB", (w, h), BG)
    x = pad
    for im in images:
        y = pad if valign == "top" else pad + (h - pad * 2 - im.height) // 2
        canvas.paste(im, (x, y), im if im.mode == "RGBA" else None)
        x += im.width + gap
    return canvas


def save(im, name, max_width=1400):
    if im.width > max_width:
        im = im.resize((max_width, round(im.height * max_width / im.width)), Image.LANCZOS)
    im.save(os.path.join(OUT, name), optimize=True)
    print(name, im.size)


def main():
    win = sys.argv[1]
    mac = sys.argv[2] if len(sys.argv) > 2 else None
    os.makedirs(OUT, exist_ok=True)
    save(row([framed(load(win, "home_open.png")), framed(load(win, "text_search.png")), framed(load(win, "capture_search.png"))], valign="center"), "hero.png", 1600)
    save(row([framed(load(win, "text_search.png")), framed(load(win, "capture_search.png"))], valign="center"), "search.png")
    save(row([framed(load(win, "capture_roster.png")), framed(load(win, "text_groups.png"))], valign="center"), "layout.png")
    save(row([framed(load(win, "palette_search.png"))]), "palette.png", 900)
    save(row([framed(load(win, "minutes_recording.png"))]), "minutes.png", 1100)
    save(row([framed(load(win, "recorder_recording.png"))]), "recorder.png", 800)
    save(row([framed(load(win, "settings_privacy.png"))]), "privacy.png", 1100)
    if mac:
        save(row([framed(load(mac, "home_open.png", 1)), framed(load(mac, "text_search.png", 1))], valign="center"), "mac.png", 1300)


if __name__ == "__main__":
    main()
