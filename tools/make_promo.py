"""GetText の紹介動画 (すべての機能の紹介) を作る。ナレーションは VOICEVOX (tools/voicevox)。

議事録の実演には、会議などの動画の一部を使う: 左に元の映像と音声、右に GetText が文字起こしした議事録の画面
(GetText.exe --doc-snapshots-minutes で、動画の 1 秒ごとの状態を描いたもの) を並べる。
使い方:
  python make_promo.py <shots> <実演の動画.mp4> <実演の画面 (minutes_t*.png) のフォルダ> <開始秒> <終了秒> <出典の表記> <出力.mp4> [作業フォルダ]
  shots: GetText.exe --doc-snapshots の画像
"""
import json
import os
import subprocess
import sys

import av
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_video as MV  # noqa: E402  (画面の部品: 枠・注目箇所・字幕など)
import voicevox_tts  # noqa: E402

W, H, FPS = MV.W, MV.H, MV.FPS
ACCENT = MV.ACCENT
DARK = (16, 24, 38)


def font(path, size):
    return MV.font(path, size)


# ───────── 紹介動画の部品 ─────────

def title_card(title, subtitle, note=""):
    """濃い色の背景に大きなタイトル (最初と最後)。"""
    img = Image.new("RGB", (W, H), DARK)
    d = ImageDraw.Draw(img)
    # 斜めの帯で奥行きを出す
    for i, c in enumerate([(0, 80, 160), (0, 103, 192), (30, 140, 220)]):
        d.polygon([(W - 760 + i * 170, 0), (W - 560 + i * 170, 0), (W - 1060 + i * 170, H), (W - 1260 + i * 170, H)], fill=c)
    lines = MV.wrap(d, subtitle, font(MV.JP_BOLD, 46), 1100)
    notes = MV.wrap(d, note, font(MV.JP, 28), 1100) if note else []
    height = 230 + len(lines) * 64 + (20 + len(notes) * 42 if notes else 0) + 50
    top = (H - height) // 2
    d.rounded_rectangle((120, top, 1360, top + height), 40, fill=(255, 255, 255))
    d.text((190, top + 40), title, font=font(MV.EN, 120), fill=DARK)
    y = top + 220
    for ln in lines:
        d.text((195, y), ln, font=font(MV.JP_BOLD, 46), fill=(30, 40, 60))
        y += 64
    y += 20
    for ln in notes:
        d.text((195, y), ln, font=font(MV.JP, 28), fill=(90, 100, 115))
        y += 42
    return img


def feature_grid(title, items):
    """機能の一覧 (4 つの枠に見出しと箇条書き)。"""
    img = Image.new("RGB", (W, H), MV.BG)
    d = ImageDraw.Draw(img)
    d.text((120, 70), title, font=font(MV.JP_BOLD, 64), fill=DARK)
    boxes = [(120, 200, 940, 600), (980, 200, 1800, 600), (120, 640, 940, 1020), (980, 640, 1800, 1020)]
    colors = [(0, 103, 192), (46, 158, 91), (196, 137, 43), (155, 79, 209)]
    for (x0, y0, x1, y1), (head, lines), color in zip(boxes, items, colors):
        d.rounded_rectangle((x0, y0, x1, y1), 28, fill="white")
        d.rounded_rectangle((x0, y0, x0 + 16, y1), 8, fill=color)
        d.text((x0 + 50, y0 + 30), head, font=font(MV.JP_BOLD, 44), fill=color)
        y = y0 + 110
        for ln in lines:
            d.text((x0 + 56, y), "・" + ln, font=font(MV.JP, 32), fill=(40, 50, 65))
            y += 52
    return img


def section_card(number, title, lines):
    img = Image.new("RGB", (W, H), MV.BG)
    d = ImageDraw.Draw(img)
    d.ellipse((150, 360, 330, 540), fill=ACCENT)
    d.text((240, 450), str(number), font=font(MV.EN, 100), fill="white", anchor="mm")
    d.text((390, 380), title, font=font(MV.JP_BOLD, 80), fill=DARK)
    y = 500
    for ln in lines:
        d.text((396, y), ln, font=font(MV.JP, 38), fill=(70, 80, 95))
        y += 60
    return img


# ───────── 実演 (元の動画 + GetText の画面) ─────────

def demo_frames(clip_path, start, end, shots_dir, credit):
    """左に元の映像、右に GetText の議事録の画面 (1 秒ごと) を並べた動画のフレームと音声を返す。"""
    base = Image.new("RGB", (W, H), DARK)
    d = ImageDraw.Draw(base)
    d.rounded_rectangle((40, 28, 600, 88), 30, fill=ACCENT)
    d.text((70, 36), "実演: 英語の講演を議事録に", font=font(MV.JP_BOLD, 34), fill="white")
    d.text((60, 640), "元の動画 (英語)", font=font(MV.JP_BOLD, 30), fill=(200, 210, 225))
    for i, ln in enumerate(["話し終わった発言から、話者つきで並びます。",
                            "英語の発言には日本語訳 (PC 内の翻訳)。",
                            "※ GetText が文字起こしした実際の結果を、",
                            "   話した時刻に合わせて表示しています。"]):
        d.text((60, 700 + i * 50), ln, font=font(MV.JP, 30), fill=(225, 230, 240) if i < 2 else (150, 160, 180))
    d.text((60, H - 60), credit, font=font(MV.JP, 24), fill=(150, 160, 180))
    video_box = (40, 110, 880, 583)          # 16:9
    panel_box = (920, 40, W - 30, H - 30)

    panels = {}

    def panel(sec):
        sec = int(min(max(sec, start), end))
        if sec not in panels:
            path = os.path.join(shots_dir, f"minutes_t{sec}.png")
            img = Image.open(path).convert("RGBA")
            frame, bar = MV.window_frame(img, "GetText — 議事録")
            canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            MV.place(canvas, frame, panel_box)
            panels[sec] = canvas
        return panels[sec]

    container = av.open(clip_path)
    vs = container.streams.video[0]
    container.seek(int(start * av.time_base), any_frame=False)
    frames = []
    audio = []
    sr = None
    resampler = av.AudioResampler(format="s16", layout="mono", rate=48000)
    for packet in container.demux():
        for f in packet.decode():
            t = float(f.pts * f.time_base)
            if t < start or t >= end:
                continue
            if isinstance(f, av.VideoFrame):
                frames.append((t, f.to_image()))
            else:
                for rf in resampler.resample(f):
                    audio.append(rf.to_ndarray().reshape(-1))
                    sr = 48000
        if frames and frames[-1][0] >= end - 1 / 30 and audio:
            break
    container.close()
    pcm = np.concatenate(audio).astype(np.float32) / 32768.0
    rms = float(np.sqrt(np.mean(pcm ** 2))) + 1e-9
    gain = min(10 ** ((-23.0 - 20 * np.log10(rms)) / 20), 0.95 / (float(np.max(np.abs(pcm))) + 1e-9))
    pcm = pcm * gain
    n = int((end - start) * FPS)
    out = []
    vi = 0
    for k in range(n):
        t = start + k / FPS
        while vi + 1 < len(frames) and frames[vi + 1][0] <= t:
            vi += 1
        img = base.copy().convert("RGBA")
        vid = frames[vi][1].resize((video_box[2] - video_box[0], video_box[3] - video_box[1]), Image.BILINEAR)
        img.paste(vid, (video_box[0], video_box[1]))
        img.alpha_composite(panel(t))
        out.append(np.asarray(img.convert("RGB")))
    return out, pcm, sr


# ───────── 台本 ─────────

def scenes(shots, demo):
    S = lambda n: Image.open(os.path.join(shots, n)).convert("RGBA")
    desk, tx, tbar = MV.desktop(shots)
    M = S("minutes_recording.png")
    E = S("minutes_empty.png")
    mins = "GetText — 議事録"
    out = []

    def add(img, text):
        out.append({"id": f"p{len(out) + 1:02d}", "img": img, "text": text})

    add(title_card("GetText", "画面の文字も、会議の声も。\nすべて PC の中で文字に。", "Windows 10 / 11 ・ GPU が無くても動きます"),
        "ゲットテキストは、画面の文字を読み取って翻訳し、会議の音声から議事録まで作れる、ウィンドウズのアプリです。処理はすべて PC の中で行うので、内容を外に送りません。")
    add(feature_grid("できること", [
        ("画面の文字の読み取り", ["枠を重ねるだけでリアルタイムに", "AI の読み取り (記号・表・小さな文字)", "日本語訳 (PC 内・回数無制限)", "蓄積・コピー・文字変換"]),
        ("会議の議事録", ["選んだアプリの音だけを文字に (録画からも)", "話者の聞き分け・声で名前を自動入力", "用語の登録・聞き間違いの補正", "15 言語・英語は日本語訳つき"]),
        ("まとめる・残す", ["AI の要約 (決定事項・やること)", "★ の印・メモ・音声の聞き直し", "Markdown・字幕・データで保存", "別の PC の議事録を開いて直す"]),
        ("かんたん導入", ["setup.bat を実行するだけ", "モデルも自動でダウンロード", "NVIDIA の GPU があれば高速", "すべて PC の中 (外に送らない)"]),
    ]), "大きく分けて、画面の文字の読み取り、会議の議事録、それをまとめて残す機能、そしてかんたんな導入の、4 つです。順に紹介します。")

    # 1. 画面の文字の読み取り
    add(section_card(1, "画面の文字の読み取り", ["枠を重ねた場所の文字を、リアルタイムに", "AI の読み取りと、PC の中での翻訳"]),
        "まずは、画面の文字の読み取りです。")
    add(MV.screen(desk, "1. 画面の文字の読み取り", "読みたい場所に枠を重ねるだけで、中の文字をリアルタイムで読み取ります。AI の読み取りは、記号や表、小さな文字まで正確です。画面に変化がないときは処理を休みます。",
                  [(120, 170, 960, 206)], framed=False),
        "読みたい場所に枠を重ねるだけで、中の文字をリアルタイムで読み取ります。AI の読み取りは、記号や表、小さな文字まで正確です。画面に変化がないときは、処理を休みます。")
    add(MV.screen(desk, "1. 画面の文字の読み取り", "英語などは日本語訳を自動で表示します。翻訳は PC の中のモデルなので、回数の制限も料金もありません (Google・DeepL も選べます)。",
                  [(tx + 12, tbar + 378, tx + 750, tbar + 678)], framed=False),
        "英語などの文字は、日本語訳を自動で表示します。翻訳は PC の中のモデルなので、回数の制限も、料金もありません。グーグルやディープエルも選べます。")
    add(MV.screen(desk, "1. 画面の文字の読み取り", "スクロールしながら読んだ内容をつなげる「蓄積」、コピーと保存、半角・全角・ひらがな・カタカナへの文字変換、どのアプリからでも使えるショートカットキー (Ctrl+Alt+C など) もあります。",
                  [(tx + 10, tbar + 5, tx + 750, tbar + 52)], framed=False),
        "スクロールしながら読んだ内容をつなげる蓄積、コピーと保存、半角や全角、ひらがなやカタカナへの文字変換、どのアプリからでも使えるショートカットキーもそろっています。")
    add(MV.screen(S("settings_2.png"), "1. 画面の文字の読み取り", "設定では、読み取りの方式や間隔、翻訳の方法、テーマ (ライト・ダーク)、文字の大きさなどを、その場で変えられます。",
                  [(285, 70, 1028, 455)], "GetText — 設定"),
        "設定では、読み取りの方式や間隔、翻訳の方法、ライトやダークのテーマ、文字の大きさなどを、その場で変えられます。")

    # 2. 会議の議事録
    add(section_card(2, "会議の議事録", ["会議アプリの音声を、時刻と話者つきの文字に", "英語の講演で試してみます"]),
        "続いて、会議の議事録です。")
    add(MV.screen(S("minutes_loading.png"), "2. 会議の議事録", "議事録の画面を開くと、音声認識の AI を準備します。どこまで進んだかが分かり、準備ができると自動で消えます。",
                  [(292, 234, 976, 636)], mins),
        "議事録の画面を開くと、音声認識の AI を準備します。どこまで進んだかが分かり、準備ができると、自動で消えます。")
    add(MV.screen(E, "2. 会議の議事録", "Zoom や Teams、ブラウザなど、文字にしたいウィンドウを選ぶだけ。そのアプリの音だけを取り込み、マイクの自分の声も一緒に記録できます。言語は日本語・英語など 15 言語から選べます。",
                  [(35, 55, 942, 107), (80, 122, 305, 166), (455, 125, 705, 166)], mins),
        "ズームやチームズ、ブラウザなど、文字にしたいウィンドウを選ぶだけ。そのアプリの音だけを取り込み、マイクの自分の声も一緒に記録できます。言語は、日本語や英語など、15 の言語から選べます。")
    add(MV.screen(M, "2. 会議の議事録", "発言は時刻と話者つきで並び、英語の発言には日本語訳が付きます。話者は声の特徴から自動で聞き分け、前後の文脈から聞き間違いも直します (鉛筆の印)。",
                  [(16, 322, 940, 640), (968, 350, 1240, 645)], mins),
        "発言は、時刻と話者つきで並び、英語の発言には日本語訳が付きます。話者は、声の特徴から自動で聞き分け、前後の文脈から、聞き間違いも直します。")
    add(MV.screen(M, "2. 会議の議事録", "名前を付けた人の声は覚えるので、次の会議からは自動で名前が付きます。人名や社名は「用語」に登録すれば、その表記で書きます。人数が分かっていれば「人数」も選べます。",
                  [(968, 350, 1240, 645), (330, 122, 428, 166)], mins),
        "名前を付けた人の声は覚えるので、次の会議からは、自動で名前が付きます。人名や社名は、用語に登録すれば、その表記で書きます。人数が分かっていれば、人数も選べます。")
    add(title_card("実演", "OpenAI DevDay 2026 の基調講演 (英語) を、GetText で議事録に", "元の映像と音声を約 1 分引用します"),
        "では、実際の動画で試してみましょう。オープンエーアイ、デブデイ 2026 の基調講演の一部を、ゲットテキストで文字起こしします。")
    out.append({"id": "demo", "demo": demo})
    last = Image.open(os.path.join(demo["shots"], f"minutes_t{int(demo['end']) - 1}.png")).convert("RGBA")
    final = Image.open(os.path.join(demo["shots"], "minutes_final.png")).convert("RGBA")
    add(MV.screen(last, "2. 会議の議事録", "講演する人と、登場する AI の声を話者ごとに分け、英語の発言には日本語訳を付けた議事録ができました。右の欄には、話者ごとの発言時間も出ます。",
                  [(16, 322, 940, 690), (968, 350, 1240, 640)], mins),
        "講演する人と、登場するエーアイの声を、話者ごとに分け、英語の発言には日本語訳を付けた議事録ができました。右の欄には、話者ごとの発言時間も出ます。")
    add(MV.screen(final, "2. 会議の議事録", "「要約」を押すと、概要・決定事項・やること・主な論点を PC の中の AI がまとめます。英語の会議でも、要約は日本語です。",
                  [(32, 316, 924, 698), (705, 818, 795, 858)], mins),
        "要約ボタンを押すと、概要、決定事項、やること、主な論点を、PC の中の AI がまとめます。英語の会議でも、要約は日本語で作ります。")

    # 3. まとめる・残す
    add(section_card(3, "まとめる・残す", ["★ の印・メモ・音声の聞き直し", "検索・保存・別の PC の議事録"]),
        "議事録を、まとめて残す機能も充実しています。")
    add(MV.screen(M, "3. まとめる・残す", "大事な発言には ★ の印。会議中のメモの書き込み、保存した会議の音声を、発言の時刻をクリックして聞き直すこともできます。",
                  [(33, 498, 922, 532), (16, 702, 940, 768), (33, 596, 130, 630)], mins),
        "大事な発言には、星の印を付けられます。会議中のメモの書き込みや、保存した会議の音声を、発言の時刻をクリックして聞き直すこともできます。")
    add(MV.screen(M, "3. まとめる・残す", "発言の検索、相づち (「はい」など) を隠す切り替え、話者ごとの発言時間の表示で、長い会議も見返しやすくなります。名前を書き換えれば、すべての発言に反映されます。",
                  [(33, 258, 913, 304), (18, 818, 640, 858), (968, 350, 1240, 615)], mins),
        "発言の検索、相づちを隠す切り替え、話者ごとの発言時間の表示で、長い会議も見返しやすくなります。話者の名前を書き換えれば、すべての発言に反映されます。")
    add(MV.screen(S("minutes_file.png"), "3. まとめる・残す", "録画・録音のファイルからも文字起こしできます。保存は Markdown・テキスト・字幕 (SRT / VTT)・議事録データで。別の PC で作った議事録を開いて、話者を直すこともできます。",
                  [(1008, 78, 1236, 170), (822, 818, 1245, 858)], mins),
        "録画や録音のファイルからも、文字起こしできます。保存は、マークダウン、テキスト、字幕、議事録データで。別の PC で作った議事録を開いて、話者を直すこともできます。")

    # 4. かんたん導入
    add(MV.screen(MV.console_image(), "4. かんたん導入", "別の PC には setup.bat を実行するだけ。必要なソフトと AI のモデルを自動でそろえます。NVIDIA の GPU があれば速く、無くても CPU で動きます。",
                  framed=False),
        "別の PC には、setup.bat を実行するだけ。必要なソフトと、AI のモデルを、自動でそろえます。NVIDIA の GPU があれば速く、無くても CPU で動きます。")
    add(title_card("GetText", "画面の文字と会議の記録を、\nもっと手軽に。",
                   f"ナレーション: {voicevox_tts.CREDIT}\n{demo['credit']}"),
        "ゲットテキストで、画面の文字と、会議の記録を、もっと手軽に。")
    return out


# ───────── 組み立て ─────────

def main():
    shots, clip, demo_shots, start, end, credit, out = sys.argv[1:8]
    work = sys.argv[8] if len(sys.argv) > 8 else os.path.join(os.path.dirname(out), "_promo_work")
    os.makedirs(work, exist_ok=True)
    demo = {"clip": clip, "shots": demo_shots, "start": float(start), "end": float(end), "credit": credit}
    sc = scenes(shots, demo)
    spoken = [s for s in sc if "text" in s]
    with open(os.path.join(work, "scenes.json"), "w", encoding="utf-8") as f:
        json.dump([{"id": s["id"], "text": s["text"]} for s in spoken], f, ensure_ascii=False)
    subprocess.run([sys.executable, os.path.join(HERE, "voicevox_tts.py"), os.path.join(work, "scenes.json"),
                    os.path.join(work, "tts")], check=True)
    sr = 48000
    lead, tail = 0.6, 1.0
    container = av.open(out, "w")
    vs = container.add_stream("libx264", rate=FPS)
    vs.width, vs.height, vs.pix_fmt = W, H, "yuv420p"
    vs.options = {"crf": "20", "preset": "medium"}
    as_ = container.add_stream("aac", rate=sr)
    as_.layout = "mono"
    as_.bit_rate = 128000
    audio_parts = []
    prev = None
    fade = int(MV.FADE_SEC * FPS)

    def emit(arr):
        for p in vs.encode(av.VideoFrame.from_ndarray(arr, format="rgb24")):
            container.mux(p)

    total = 0.0
    for s in sc:
        if "demo" in s:
            frames, pcm, _ = demo_frames(clip, demo["start"], demo["end"], demo_shots, credit)
            for k, arr in enumerate(frames):
                if prev is not None and k < fade:
                    t = (k + 1) / (fade + 1)
                    arr = (prev * (1 - t) + arr * t).astype(np.uint8)
                emit(arr)
            want = len(frames) / FPS
            pcm = np.pad(pcm, (0, max(0, int(want * sr) - len(pcm))))[: int(want * sr)]
            audio_parts.append(pcm)
            prev = frames[-1].astype(np.float32)
            total += want
            continue
        a, r = MV.read_wav(os.path.join(work, "tts", s["id"] + ".wav"))
        if r != sr:
            a = np.interp(np.arange(0, len(a), r / sr), np.arange(len(a)), a)
        dur = lead + len(a) / sr + tail
        n = int(round(dur * FPS))
        img = np.asarray(s["img"].convert("RGB")).astype(np.float32)
        s["img"].save(os.path.join(work, s["id"] + ".png"))
        for k in range(n):
            if prev is not None and k < fade:
                t = (k + 1) / (fade + 1)
                emit((prev * (1 - t) + img * t).astype(np.uint8))
            else:
                emit(img.astype(np.uint8))
        seg = np.concatenate([np.zeros(int(lead * sr)), a, np.zeros(int(tail * sr))])
        audio_parts.append(np.pad(seg, (0, max(0, int(n / FPS * sr) - len(seg))))[: int(n / FPS * sr)])
        prev = img
        total += n / FPS
    for p in vs.encode(None):
        container.mux(p)
    audio = np.concatenate(audio_parts)
    pcm = (np.clip(audio, -1, 1) * 32767).astype(np.int16)
    pts = 0
    for k in range(0, len(pcm), 1024):
        chunk = pcm[k:k + 1024].reshape(1, -1)
        frame = av.AudioFrame.from_ndarray(chunk, format="s16", layout="mono")
        frame.sample_rate = sr
        frame.pts = pts
        pts += chunk.shape[1]
        for p in as_.encode(frame):
            container.mux(p)
    for p in as_.encode(None):
        container.mux(p)
    container.close()
    print(f"{out}: {total:.0f} 秒, {os.path.getsize(out) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
