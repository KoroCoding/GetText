"""
録画した動画に字幕を焼き込む (議事録の発言を、話者の名前つきで画面の下に出す)。

    burn(src, dst, cues, progress=None, cancel=None)

cues は [(始まり秒, 終わり秒, 文字), ...] (動画の先頭からの秒)。音はそのまま写し、映像だけを作り直す
(GPU の H.264 があれば使う)。字幕の絵は 1 つずつ 1 回だけ作り、各画面では下の帯だけを重ねるので軽い。
"""
import bisect
import os
import sys
import textwrap

import av
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ENCODERS = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "h264_videotoolbox", "libx264", "libopenh264", "mpeg4"]

FONTS = [
    r"C:\Windows\Fonts\YuGothB.ttc", r"C:\Windows\Fonts\meiryob.ttc", r"C:\Windows\Fonts\YuGothM.ttc", r"C:\Windows\Fonts\meiryo.ttc",
    "/System/Library/Fonts/ヒラギノ角ゴシック W6.ttc", "/System/Library/Fonts/Hiragino Sans GB.ttc",
    "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
]


def find_font(size):
    for path in FONTS:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except OSError:
                continue
    return ImageFont.load_default()


def wrap(text, font, width, draw):
    """画面の幅に収まるように折り返す (日本語は 1 文字ずつ、英語は単語で)。最大 3 行。"""
    lines = []
    for para in text.split("\n"):
        line = ""
        for ch in para:
            trial = line + ch
            if draw.textlength(trial, font=font) <= width or not line:
                line = trial
            else:
                # 英語は単語の途中で切らない
                cut = line.rfind(" ")
                if ch != " " and cut > len(line) // 2:
                    lines.append(line[:cut])
                    line = line[cut + 1:] + ch
                else:
                    lines.append(line)
                    line = ch.lstrip()
        if line:
            lines.append(line)
    if len(lines) > 3:
        lines = lines[:2] + [lines[2][:-1] + "…"]
    return lines


class Caption:
    """1 つの字幕の絵 (YUV420 の各面と、重ねる強さ)。"""

    def __init__(self, text, width, height):
        size = max(16, height // 20)
        font = find_font(size)
        probe = ImageDraw.Draw(Image.new("L", (1, 1)))
        lines = wrap(text, font, int(width * 0.88), probe)
        line_h = int(size * 1.35)
        # 小さい窓の録画でも画面からはみ出さないよう、入る行数までにする
        fit = max(1, (height - size // 2) // line_h)
        if len(lines) > fit:
            lines = lines[:fit - 1] + [lines[fit - 1][:-1] + "…"] if fit > 1 else [lines[0][:-1] + "…"]
        box_h = min(height - height % 2, line_h * len(lines) + size // 2)
        box_h += box_h % 2
        top = max(0, height - box_h - height // 25)
        top -= top % 2
        self.top, self.bottom = top, top + box_h
        img = Image.new("RGBA", (width, box_h), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        widest = max((d.textlength(l, font=font) for l in lines), default=0)
        pad = size // 2
        left = int((width - widest) / 2) - pad
        d.rounded_rectangle([left, 0, left + widest + pad * 2, box_h - 1], radius=size // 3, fill=(0, 0, 0, 165))
        for i, l in enumerate(lines):
            x = (width - d.textlength(l, font=font)) / 2
            y = size // 4 + i * line_h
            d.text((x, y), l, font=font, fill=(255, 255, 255, 255), stroke_width=max(1, size // 18), stroke_fill=(0, 0, 0, 255))
        rgba = np.asarray(img).astype(np.float32)
        r, g, b, a = rgba[..., 0], rgba[..., 1], rgba[..., 2], rgba[..., 3] / 255.0
        y = 0.257 * r + 0.504 * g + 0.098 * b + 16
        u = -0.148 * r - 0.291 * g + 0.439 * b + 128
        v = 0.439 * r - 0.368 * g - 0.071 * b + 128
        self.y = y
        self.a = a
        # 色の面は縦横が半分
        self.u = u[0::2, 0::2]
        self.v = v[0::2, 0::2]
        self.a2 = a[0::2, 0::2]

    def apply(self, planes, width):
        y, u, v = planes
        t, b = self.top, self.bottom
        region = y[t:b, :width].astype(np.float32)
        y[t:b, :width] = (region * (1 - self.a) + self.y * self.a).astype(np.uint8)
        t2, b2 = t // 2, b // 2
        for plane, src in ((u, self.u), (v, self.v)):
            reg = plane[t2:b2, : width // 2].astype(np.float32)
            plane[t2:b2, : width // 2] = (reg * (1 - self.a2) + src * self.a2).astype(np.uint8)


def _setup(ctx, name, width, height, rate, bit_rate, time_base):
    ctx.width = width
    ctx.height = height
    ctx.pix_fmt = "yuv420p" if name != "h264_qsv" else "nv12"
    ctx.bit_rate = bit_rate
    ctx.time_base = time_base
    # B フレームを使わない。録画は画面の間隔がばらばらなので、B フレームがあると書き出し (Intel の QSV など) が
    # 等間隔を前提に並べた順番の時刻が戻ってしまい、書き出しが失敗する
    ctx.options = {"bf": "0"}


def open_encoder(out, template, width, height, rate, bit_rate):
    """この PC で使える H.264 の書き出しを選んで、動画の流れを作る (GPU があれば GPU)。"""
    from fractions import Fraction
    time_base = template.time_base or 1 / Fraction(rate).limit_denominator(1001)
    last = None
    for name in ENCODERS:
        # 使えるかは、ファイルとは別の書き出しを開いて確かめる (失敗した流れをファイルに残すと、書き出しの始めで止まる)
        try:
            probe = av.CodecContext.create(name, "w")
            _setup(probe, name, width, height, rate, bit_rate, time_base)
            probe.framerate = Fraction(rate).limit_denominator(1001)
            probe.open()
            del probe
        except Exception as e:  # この PC で使えない書き出し (GPU が無いなど)
            last = e
            continue
        stream = out.add_stream(name, rate=rate)
        _setup(stream, name, width, height, rate, bit_rate, time_base)
        # 書き出しの時刻の単位も元の動画と同じにする。決めないと 1/コマ数 になり、録画 (画面が変わった時だけ
        # 画面が届くので間隔がばらばら) の近い 2 枚が同じ時刻に丸められて、書き出しが失敗する
        stream.codec_context.time_base = time_base
        return stream, name
    raise RuntimeError(f"動画を書き出せません: {last}")


def burn(src, dst, cues, progress=None, cancel=None):
    """字幕を焼き込んだ動画を dst に作る (一時ファイルに書いてから入れ替える)。"""
    tmp = dst + ".part"
    with av.open(src) as inp:
        vin = inp.streams.video[0]
        ain = inp.streams.audio[0] if inp.streams.audio else None
        width, height = vin.codec_context.width, vin.codec_context.height
        rate = vin.average_rate or vin.guessed_rate or 30
        duration = float(inp.duration or 0) / 1e6 or None
        # 元の動画と同じくらいの画質にする。ただし止まった画面の録画は元がとても小さいので、字幕の文字がつぶれない量は使う
        floor = int(width * height * min(float(rate), 60) * 0.08)
        bit_rate = max(vin.bit_rate or inp.bit_rate or 0, floor)
        captions = {}
        # 同じ時刻に始まるときは短い方を後に (いま出ている字幕のうち、後から並ぶ方を出すので、短い方が隠れない)
        order = sorted(((float(s), float(e), t) for s, e, t in cues if t and float(e) > float(s)), key=lambda c: (c[0], -c[1]))
        starts = [c[0] for c in order]
        reach = []  # その字幕までの、いちばん遅い終わり (それより前を探さなくてよい所を知るため)
        for c in order:
            reach.append(max(c[1], reach[-1] if reach else 0.0))
        with av.open(tmp, "w", format="mp4") as out:
            vout, encoder = open_encoder(out, vin, width, height, rate, int(bit_rate))
            aout = out.add_stream_from_template(ain) if ain is not None else None
            last_report = -1.0
            for packet in inp.demux(*(s for s in (vin, ain) if s is not None)):
                if cancel is not None and cancel():
                    raise InterruptedError("中止しました")
                if packet.stream == ain:
                    if packet.dts is None:
                        continue
                    packet.stream = aout
                    out.mux(packet)
                    continue
                for frame in packet.decode():
                    t = float(frame.time or 0)
                    # いま出ている字幕のうち、いちばん後から始まったもの (同時に話した発言が重なっても、新しい方が隠れない)
                    planes = None
                    cue = None
                    j = bisect.bisect_right(starts, t) - 1
                    while j >= 0 and reach[j] > t:
                        if order[j][1] > t:
                            cue = order[j]
                            break
                        j -= 1
                    if cue is not None:
                        key = (cue[0], cue[2])
                        if key not in captions:
                            captions.clear()  # 前の字幕の絵は使わない
                            captions[key] = Caption(cue[2], width, height)
                        arr = frame.to_ndarray(format="yuv420p")
                        flat = arr.reshape(-1)  # Y (高さ×幅) → U (半分×半分) → V (半分×半分) の順に並ぶ
                        cw, ch = width // 2, height // 2
                        y = flat[: width * height].reshape(height, width)
                        u = flat[width * height: width * height + cw * ch].reshape(ch, cw)
                        v = flat[width * height + cw * ch: width * height + 2 * cw * ch].reshape(ch, cw)
                        captions[key].apply((y, u, v), width)
                        new = av.VideoFrame.from_ndarray(arr, format="yuv420p")
                        planes = new
                    out_frame = planes if planes is not None else frame.reformat(format="yuv420p")
                    out_frame.pts = frame.pts
                    out_frame.time_base = frame.time_base
                    for p in vout.encode(out_frame):
                        out.mux(p)
                    if progress and duration and t - last_report >= 1.0:
                        last_report = t
                        progress(min(t, duration), duration)
            for p in vout.encode(None):
                out.mux(p)
    os.replace(tmp, dst)
    return encoder


def selftest(folder):
    """動作確認: 3 秒の動画 (灰色の画面と音) を作って字幕を焼き込み、字幕が出る時だけ下に白い文字があるかを確かめる。"""
    import math
    import time
    os.makedirs(folder, exist_ok=True)
    src = os.path.join(folder, "burn_src.mp4")
    dst = os.path.join(folder, "burn_out.mp4")
    width, height, fps, rate = 640, 360, 30, 48000
    with av.open(src, "w", format="mp4") as out:
        from fractions import Fraction
        # 録画と同じく、画面の間隔をばらばらにする (止まった画面は 0.2 秒ごと、近い 2 枚は 7 ミリ秒差など)
        template = type("T", (), {"time_base": Fraction(1, 1000)})()
        vs, _ = open_encoder(out, template, width, height, fps, 1_000_000)
        aus = out.add_stream("aac", rate=rate)
        aus.layout = "stereo"
        times, ms, gaps = [], 0, [33, 7, 200, 16, 50, 9, 200, 200, 33, 21]
        while ms < 3000:
            times.append(ms)
            ms += gaps[len(times) % len(gaps)]
        for ms in times:
            frame = av.VideoFrame.from_ndarray(np.full((height, width, 3), 90, np.uint8), format="rgb24").reformat(format="yuv420p")
            frame.pts = ms
            frame.time_base = Fraction(1, 1000)
            for p in vs.encode(frame):
                out.mux(p)
        for p in vs.encode(None):
            out.mux(p)
        n = 1024
        for k in range(rate * 3 // n):
            t = (np.arange(n) + k * n) / rate
            wave = (0.2 * np.sin(2 * math.pi * 440 * t)).astype(np.float32)
            frame = av.AudioFrame.from_ndarray(np.stack([wave, wave]).reshape(1, -1), format="flt", layout="stereo")
            frame.sample_rate = rate
            frame.pts = k * n
            for p in aus.encode(frame):
                out.mux(p)
        for p in aus.encode(None):
            out.mux(p)
    t0 = time.time()
    seen = []
    # 2 つ目は同じ時刻に始まって先に終わる字幕 (重なっても出ること)
    encoder = burn(src, dst, [(1.0, 2.0, "田中: 字幕の確認です。"), (1.0, 1.1, "はい")], progress=lambda d, t: seen.append(d))
    elapsed = time.time() - t0
    bright, bright_frames = {}, []
    with av.open(src) as inp:
        src_frames = sum(1 for _ in inp.decode(video=0))
    try:
        Caption("小さい窓の録画でも字幕が画面に収まるかの確認です。" * 3, 128, 70)  # 3 行より低い画面
        tiny = True
    except Exception:
        tiny = False
    with av.open(dst) as inp:
        has_audio = bool(inp.streams.audio)
        length = float(inp.duration or 0) / 1e6
        for frame in inp.decode(video=0):
            t = float(frame.time or 0)
            y = frame.to_ndarray(format="gray")
            bright[round(t, 1)] = int((y[int(height * 0.7):, :] > 200).sum())
            bright_frames.append(t)
    on = max(v for k, v in bright.items() if 1.2 <= k <= 1.8)
    off = max(v for k, v in bright.items() if k <= 0.8 or k >= 2.2)
    checks = [
        ("字幕が出る時は下に白い文字がある", on > 300, f"{on} 点"),
        ("字幕が無い時は何も足さない", off == 0, f"{off} 点"),
        ("音が残っている", has_audio, ""),
        ("長さが変わらない", abs(length - 3) < 0.3, f"{length:.2f} 秒"),
        ("画面の間隔がばらばらでも、すべての画面を書き出す", len(bright_frames) == src_frames, f"{len(bright_frames)} / {src_frames} 枚"),
        ("小さい窓の録画でも字幕が収まる", tiny, ""),
    ]
    ok = all(c[1] for c in checks)
    lines = [f"字幕の焼き込み: {encoder} ({elapsed:.1f} 秒)"] + [f"{'OK ' if c[1] else 'NG '} {c[0]} {c[2]}" for c in checks]
    lines.append("すべて合格" if ok else "不合格あり")
    text = "\n".join(lines)
    print(text)
    with open(os.path.join(folder, "burn.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    return ok


if __name__ == "__main__":
    # 動作確認: python subtitle_video.py --selftest 出力フォルダ / python subtitle_video.py 入力.mp4 出力.mp4
    if sys.argv[1] == "--selftest":
        sys.exit(0 if selftest(sys.argv[2]) else 1)
    import time
    t0 = time.time()
    used = burn(sys.argv[1], sys.argv[2], [(0.5, 2.5, "田中: では定例会議を始めます。まず開発の進捗を確認させてください。"),
                                            (2.6, 4.5, "Sarah: Great. Could you share the test results by Friday?")],
                progress=lambda d, t: print(f"{d:.1f}/{t:.1f}", flush=True))
    print("encoder", used, "elapsed", round(time.time() - t0, 1))
