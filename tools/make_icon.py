"""GetText のアイコンを作る (python tools/make_icon.py)。

形: 画面を切り取る枠の四隅の角 (⌜ ⌝ ⌞ ⌟) と、その中の 2 本の文字の行。アプリの印・読み取り枠・README で同じ形を使う。
- Assets/app.ico      : Windows 用 (16〜256 px。小さい大きさは線を太く・行を 1 本にして読めるようにする)
- mac/Assets/app.ico  : Mac 版の窓のアイコン (同じもの)
- Assets/app_1024.png : Mac の .icns の元 (macOS のアイコンの余白に合わせて小さめに描く)
- docs/images/mark.svg: README の印
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOP, BOTTOM = (0x1E, 0x7B, 0xE0), (0x00, 0x5F, 0xB8)  # 青 (上が明るい)


def tile(size, inset=0.0):
    """size × size の絵。inset はまわりの余白の割合 (Mac 用)。"""
    s = size * 8  # 大きく描いて縮める (なめらかにする)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    pad = int(s * inset)
    box = s - pad * 2
    # 縦のグラデーションの角丸の四角
    grad = Image.new("RGBA", (box, box))
    gd = ImageDraw.Draw(grad)
    for y in range(box):
        t = y / max(1, box - 1)
        gd.line([(0, y), (box, y)], fill=tuple(int(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (box, box), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, box - 1, box - 1], radius=int(box * 0.22), fill=255)
    img.paste(grad, (pad, pad), mask)

    d = ImageDraw.Draw(img)
    small = size <= 24
    w = int(box * (0.11 if small else 0.075))       # 線の太さ
    m = int(box * (0.2 if small else 0.22))          # 角の位置 (内側の余白)
    arm = int(box * (0.22 if small else 0.2))        # 角の腕の長さ
    white = (255, 255, 255, 255)
    x0, y0, x1, y1 = pad + m, pad + m, pad + box - m, pad + box - m

    def line(a, b):
        d.line([a, b], fill=white, width=w)
        r = w // 2
        for (x, y) in (a, b):
            d.ellipse([x - r, y - r, x + r, y + r], fill=white)

    for (cx, cy, dx, dy) in ((x0, y0, 1, 1), (x1, y0, -1, 1), (x0, y1, 1, -1), (x1, y1, -1, -1)):
        line((cx, cy), (cx + dx * arm, cy))
        line((cx, cy), (cx, cy + dy * arm))
    # 中の文字の行
    lx = pad + int(box * 0.34)
    if small:
        line((lx, pad + box // 2), (pad + int(box * 0.66), pad + box // 2))
    else:
        line((lx, pad + int(box * 0.43)), (pad + int(box * 0.66), pad + int(box * 0.43)))
        line((lx, pad + int(box * 0.58)), (pad + int(box * 0.54), pad + int(box * 0.58)))
    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [tile(n) for n in sizes]
    for path in ("Assets/app.ico", "mac/Assets/app.ico"):
        frames[-1].save(os.path.join(ROOT, path), format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    tile(1024, inset=0.09).save(os.path.join(ROOT, "Assets", "app_1024.png"))
    os.makedirs(os.path.join(ROOT, "docs", "images"), exist_ok=True)
    with open(os.path.join(ROOT, "docs", "images", "mark.svg"), "w", encoding="utf-8") as f:
        f.write(
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="64" height="64" fill="none" '
            'stroke-linecap="round" stroke-linejoin="round" stroke-width="2">\n'
            '  <path d="M3 8V3h5M16 3h5v5M21 16v5h-5M8 21H3v-5" stroke="#1E7BE0"/>\n'
            '  <path d="M8 10h8M8 14h5" stroke="#8A8A8A"/>\n'
            "</svg>\n")
    print("ok")


if __name__ == "__main__":
    main()
