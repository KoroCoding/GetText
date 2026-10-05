"""GetText の紹介と使い方の動画 (docs/GetText_紹介と使い方.mp4、配布用の ZIP に入れる) を作る。

1. GetText.exe --doc-snapshots <shots> で、見本のデータの実際の画面を PNG にする (画面の外で描くので、画面は映らない)
2. このスクリプトが、画面に説明の字幕と注目箇所の枠を重ね、ナレーションを付けて MP4 にする
   ナレーションは VOICEVOX (tools/voicevox に ENGINE を展開してあれば、PC 内で自然な声で読む。voicevox_tts.py)、
   無ければ Windows の日本語音声 (tts.ps1)
使い方: python make_video.py <shots フォルダ> <出力.mp4> [作業フォルダ]
必要なもの: Pillow, av (PyAV), numpy  (GetText の AI 機能の Python 環境に入っている)
"""
import json
import os
import subprocess
import sys
import wave

import av
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H, FPS = 1920, 1080, 30
FADE_SEC = 0.4
FONT_DIR = r"C:\Windows\Fonts"
JP = os.path.join(FONT_DIR, "BIZ-UDGothicR.ttc")
JP_BOLD = os.path.join(FONT_DIR, "BIZ-UDGothicB.ttc")
EN = os.path.join(FONT_DIR, "segoeui.ttf")
BG = (236, 241, 247)
ACCENT = (0, 103, 192)
HIGHLIGHT = (245, 158, 11)
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import voicevox_tts  # noqa: E402

USE_VOICEVOX = voicevox_tts.find_engine() is not None


def font(path, size):
    return ImageFont.truetype(path, size)


def wrap(draw, text, fnt, width):
    """日本語は 1 文字ずつ、英語は単語ごとに折り返す。"""
    lines = []
    for para in text.split("\n"):
        line = ""
        tokens = []
        word = ""
        for ch in para:
            if ch.isascii() and ch != " ":
                word += ch
            else:
                if word:
                    tokens.append(word)
                    word = ""
                tokens.append(ch)
        if word:
            tokens.append(word)
        for t in tokens:
            # 句読点や閉じかっこは行の頭に来ないよう、はみ出しても前の行に付ける
            if draw.textlength(line + t, font=fnt) > width and line and t not in "。、，．）」』！？)":
                lines.append(line.rstrip())
                line = t.lstrip()
            else:
                line += t
        lines.append(line)
    return lines


# ───────── 部品 ─────────

def card(title, lines, subtitle=None):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((160, 150, W - 160, H - 150), 36, fill="white")
    d.text((240, 230), title, font=font(JP_BOLD, 72), fill=(20, 30, 45))
    y = 360
    if subtitle:
        d.text((240, 330), subtitle, font=font(JP, 40), fill=(90, 100, 115))
        y = 430
    for ln in lines:
        d.text((260, y), ln, font=font(JP, 42), fill=(40, 50, 65))
        y += 72
    return img


def window_frame(shot, title, maximize=True):
    """画面の画像に、ウィンドウらしい枠 (タイトルバー・影) を付ける。"""
    bar = 44
    w, h = shot.size
    win = Image.new("RGBA", (w, h + bar), (255, 255, 255, 255))
    d = ImageDraw.Draw(win)
    d.rectangle((0, 0, w, bar), fill=(243, 243, 243, 255))
    d.text((16, 9), title, font=font(JP, 22), fill=(40, 40, 40))
    # 最小化・最大化・閉じるのボタン (フォントに記号が無いことがあるので線で描く)
    c, y = (70, 70, 70), bar // 2
    d.line((w - (150 if maximize else 100), y, w - (134 if maximize else 84), y), fill=c, width=2)
    if maximize:  # (機能を選ぶ画面・画面の録画は大きさを変えられないので、最大化のボタンが無い)
        d.rectangle((w - 100, y - 8, w - 84, y + 8), outline=c, width=2)
    d.line((w - 50, y - 8, w - 34, y + 8), fill=c, width=2)
    d.line((w - 50, y + 8, w - 34, y - 8), fill=c, width=2)
    win.paste(shot.convert("RGBA"), (0, bar), shot.convert("RGBA"))
    return win, bar


def place(canvas, img, box):
    """img を box (x0, y0, x1, y1) に収まるよう縮小して中央に置く。(倍率, 左上) を返す。"""
    x0, y0, x1, y1 = box
    s = min((x1 - x0) / img.width, (y1 - y0) / img.height, 1.0)
    im = img.resize((int(img.width * s), int(img.height * s)), Image.LANCZOS)
    ox = x0 + (x1 - x0 - im.width) // 2
    oy = y0 + (y1 - y0 - im.height) // 2
    shadow = Image.new("RGBA", (im.width + 60, im.height + 60), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((30, 34, im.width + 30, im.height + 34), 16, fill=(0, 0, 0, 70))
    shadow = shadow.filter(ImageFilter.GaussianBlur(14))
    canvas.paste(shadow, (ox - 30, oy - 30), shadow)
    canvas.paste(im, (ox, oy), im if im.mode == "RGBA" else None)
    return s, (ox, oy)


def spotlight(canvas, rects):
    """注目箇所以外を少し暗くし、注目箇所をオレンジの枠で囲む。"""
    if not rects:
        return
    alpha = Image.new("L", canvas.size, 90)  # 注目箇所以外を少し暗くする
    md = ImageDraw.Draw(alpha)
    for r in rects:
        md.rounded_rectangle((r[0] - 8, r[1] - 8, r[2] + 8, r[3] + 8), 12, fill=0)
    shade = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    shade.putalpha(alpha)
    canvas.alpha_composite(shade)
    d = ImageDraw.Draw(canvas)
    for r in rects:
        d.rounded_rectangle((r[0] - 8, r[1] - 8, r[2] + 8, r[3] + 8), 12, outline=HIGHLIGHT, width=6)


def screen(shot, chapter, caption, rects=(), title="GetText", framed=True):
    canvas = Image.new("RGBA", (W, H), BG + (255,))
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle((40, 28, 60 + d.textlength(chapter, font=font(JP_BOLD, 34)) + 20, 88), 30, fill=ACCENT)
    d.text((70, 36), chapter, font=font(JP_BOLD, 34), fill="white")
    img, bar = (window_frame(shot, title, maximize=title not in ("GetText", "GetText — 画面の録画")) if framed else (shot.convert("RGBA"), 0))
    s, (ox, oy) = place(canvas, img, (60, 110, W - 60, 820))
    spotlight(canvas, [(ox + r[0] * s, oy + (r[1] + bar) * s, ox + r[2] * s, oy + (r[3] + bar) * s) for r in rects])
    # 字幕
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle((60, 845, W - 60, H - 30), 24, fill=(20, 28, 40, 235))
    f = font(JP, 38)
    lines = wrap(d, caption, f, W - 220)[:4]
    y = 845 + (H - 30 - 845 - len(lines) * 52) // 2
    for ln in lines:
        d.text((110, y), ln, font=f, fill="white")
        y += 52
    return canvas.convert("RGB")


def desktop(shots):
    """読み取り枠を英語の画面 (見本) に重ね、右に文字の画面を並べた「デスクトップ」の画像。"""
    img = Image.new("RGBA", (1900, 760), (230, 234, 240, 255))
    d = ImageDraw.Draw(img)
    # 見本のブラウザ
    d.rounded_rectangle((0, 0, 1090, 760), 14, fill="white")
    d.rectangle((0, 0, 1090, 56), fill=(240, 242, 245))
    d.rounded_rectangle((90, 12, 1000, 44), 16, fill="white")
    d.text((110, 16), "https://example.com/gettext", font=font(EN, 20), fill=(90, 90, 90))
    d.text((60, 90), "About GetText", font=font(EN, 44), fill=(20, 20, 20))
    body = ["GetText reads the text inside the frame in real time.",
            "You can copy it, and it is translated into Japanese automatically.",
            "Translation runs on your PC by default."]
    y = 250
    for ln in body:
        d.text((150, y), ln, font=font(EN, 25), fill=(30, 30, 30))
        y += 56
    d.text((60, 680), "The frame can be moved and resized freely.", font=font(EN, 22), fill=(120, 120, 120))
    cap = Image.open(os.path.join(shots, "capture.png")).convert("RGBA")
    img.paste(cap, (120, 170), cap)
    txt = Image.open(os.path.join(shots, "text.png")).convert("RGBA")
    frame, bar = window_frame(txt, "GetText")
    img.paste(frame, (1120, 0), frame)
    return img, 1120, bar


def console_image():
    img = Image.new("RGB", (1500, 900), (12, 12, 12))
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, 1500, 44), fill=(40, 40, 40))
    d.text((16, 9), "C:\\Windows\\System32\\cmd.exe - setup.bat", font=font(JP, 22), fill=(220, 220, 220))
    lines = [
        ("c", "=============================================="),
        ("c", " GetText のセットアップ"),
        ("c", "=============================================="),
        ("w", "入れる機能を選んでください (あとから setup.bat をもう一度実行すれば追加できます):"),
        ("w", "  1. フル  … 読み取り・英語の翻訳・議事録 (補正・要約まで)        ダウンロード 約 8GB"),
        ("w", "  2. 標準  … 1 から「文脈による聞き間違いの補正」と「要約」を除く    ダウンロード 約 4GB"),
        ("w", "  3. 最小  … 文字の読み取りと英語の翻訳だけ (議事録なし)          ダウンロード 約 1GB"),
        ("g", "  おすすめ: 1 (NVIDIA の GPU とメモリ 32GB があるため)"),
        ("w", "番号 (Enter だけで 1): 1"),
        ("g", "  OK: 機能: Full"),
        ("w", "英語以外 (中国語・韓国語など) の翻訳モデル NLLB-200 も入れますか？ (非商用ライセンス) (y/N):"),
        ("g", "  OK: 英語以外の翻訳 (NLLB-200): 入れない"),
        ("g", "  OK: 空き容量: 120GB (必要 約 10GB)"),
        ("c", "■ 1/4 必要なソフト (Python 3.11) を確認しています (ビルド済みの配布版なので .NET は不要)"),
        ("g", "  OK: Python: ...\\Python311\\python.exe"),
        ("c", "■ 2/4 GetText のビルド (ビルド済みの配布版なので省略します)"),
        ("c", "■ 3/4 AI 機能の環境を作り、モデルをダウンロードしています (回線によっては 30 分以上かかります)"),
        ("c", "■ 4/4 ショートカットを作成しています"),
        ("g", "  OK: デスクトップとスタートメニューに「GetText」を作りました"),
        ("g", " セットアップが完了しました"),
    ]
    colors = {"c": (97, 214, 214), "w": (220, 220, 220), "g": (22, 198, 12)}
    y = 64
    for c, t in lines:
        d.text((24, y), t, font=font(JP, 26), fill=colors[c])
        y += 36
    return img


# ───────── 場面 ─────────

def crop(img, box):
    return img.crop(box)


def scenes(shots):
    S = lambda n: Image.open(os.path.join(shots, n)).convert("RGBA")
    desk, tx, tbar = desktop(shots)
    M = S("minutes_recording.png")
    E = S("minutes_empty.png")
    F = S("minutes_file.png")
    SM = S("minutes_summary.png")
    OPT = S("minutes_options.png")
    HOME, HOME2 = S("home.png"), S("home_open.png")
    REC, REC2 = S("recorder.png"), S("recorder_recording.png")
    out = []

    def add(id, img, text):
        out.append({"id": id, "img": img, "text": text})

    # ── はじめに
    add("s01", card("GetText の紹介と使い方", ["・文字の読み取りと翻訳 (画面の文字をリアルタイムに)",
                                           "・議事録 (会議の音声を、話者と時刻つきの文字に)",
                                           "・画面の録画 (選んだウィンドウを、音も入れて)",
                                           "・入れ方・設定・困ったとき"],
                    "処理は PC の中で行います (翻訳を Google・DeepL にしたときを除く)"),
        "こんにちは。この動画では、ゲットテキストでできることと、その使い方をご紹介します。"
        "ゲットテキストには、画面の文字を読み取って翻訳する機能、会議の音声から議事録を作る機能、"
        "そして、選んだアプリの画面を、音ごと録画する機能の、三つがあります。"
        "議事録や要約、録画は、パソコンの中だけで処理するので、会議の内容が外に送られることはありません。"
        "読み取った文字の翻訳も、最初の設定では、パソコンの中で行います。")

    # ── 1. 入れ方
    add("s02", card("1. 入れ方", ["① 配布された ZIP を、好きな場所に展開する",
                               "② 展開した GetText フォルダの setup.bat をダブルクリック",
                               "③ 入れる機能を選ぶ (フル・標準・最小。おすすめが出ます)",
                               "④ 終わると、デスクトップに「GetText」ができます",
                               "※ Windows 10 (2004 以降) / 11 の 64 ビット版。初回はネット接続が必要",
                               "※ 議事録・録画でアプリの音を取り込むには Windows 11"]),
        "まずは、入れ方です。配られたジップファイルを、好きな場所に展開して、"
        "中にある、セットアップ・バットを、ダブルクリックしてください。"
        "入れる機能を、フル、標準、最小の中から選びます。このパソコンに合うおすすめも表示されるので、迷ったら、そのまま進めて大丈夫です。")
    add("s03", screen(console_image(), "1. 入れ方", "必要なソフト (Python。無ければ確認のうえインストール) の確認、AI のモデルのダウンロード、ショートカットの作成までを行います。終わると、デスクトップとスタートメニューに「GetText」ができます。", framed=False),
        "あとは、必要なソフトの確認から、エーアイのモデルのダウンロード、ショートカットの作成までを、セットアップが進めてくれます。"
        "モデルのダウンロードには少し時間がかかるので、終わるまで待ちましょう。完了すると、デスクトップに、ゲットテキストのアイコンができます。")
    add("s03b", card("新しい版に入れ替えるとき", ["① GetText を終了して、今の GetText フォルダを削除する",
                                             "② 新しい ZIP を展開して、setup.bat を実行する (前と同じ機能を選ぶ)",
                                             "※ 設定・議事録・AI のモデルは別の場所にあるので、そのまま残ります",
                                             "   (取得済みのモデルはダウンロードし直しません)"]),
        "新しい版に入れ替えるときは、ゲットテキストを終了して、今のフォルダを削除してから、新しいジップを展開して、同じように、セットアップ・バットを実行します。"
        "設定や議事録、それにエーアイのモデルは、別の場所に保存されているので、消えずにそのまま使えます。")

    # ── 2. 機能を選ぶ画面
    add("s04", screen(HOME, "2. 機能を選ぶ画面", "起動すると、使う機能を選ぶ画面が開きます。「文字の読み取りと翻訳」「議事録」「画面の録画」から、使うものの「開く」を押します。いくつでも同時に開けます。",
                      [(512, 137, 655, 182), (512, 267, 655, 311), (512, 396, 655, 440)], "GetText"),
        "ゲットテキストを起動すると、まず、使う機能を選ぶ画面が開きます。"
        "文字の読み取りと翻訳、議事録、画面の録画の中から、使いたいものの、開くボタンを押してください。三つとも、同時に開いておくこともできます。")
    add("s05", screen(HOME2, "2. 機能を選ぶ画面", "開いている機能には「開いています」「記録中」「録画中」の印が付き、「前に出す」「閉じる」で切り替えられます。下の「設定」と「終了」もここにあります。",
                      [(315, 120, 422, 145), (195, 271, 270, 296), (230, 422, 305, 447), (512, 118, 655, 220), (480, 560, 676, 600)], "GetText"),
        "開いている機能には、開いています、記録中、録画中、といった印が付きます。"
        "前に出す、と、閉じる、のボタンで、ここから切り替えられます。"
        "機能を開くと、この画面はタスクバーにしまわれ、機能をすべて閉じると、また出てきます。"
        "ゲットテキストを終わるときは、この画面の、終了を押します。")

    # ── 3. 文字の読み取り
    add("s06", screen(desk, "3. 文字の読み取りと翻訳", "読み取りを開くと、青い読み取り枠と、読み取った文字の画面が出ます。読みたい文字の上に枠を重ねるだけで、中の文字をリアルタイムで読み取ります。", framed=False),
        "文字の読み取りを開くと、青い読み取り枠と、読み取った文字を表示する画面が出てきます。"
        "使い方は簡単で、読みたい文字の上に、この枠を重ねるだけです。枠の中の文字を、リアルタイムで読み取ります。")
    add("s07", screen(desk, "3. 文字の読み取りと翻訳", "枠は上の青い部分をドラッグして動かし、端や右下の角をドラッグして大きさを変えます。右上のボタンで、一時停止と、読み取りを閉じることができます (GetText は終わりません)。",
                      [(120, 170, 960, 206), (925, 585, 962, 622)], framed=False),
        "枠は、上の青い部分をドラッグすると動かせて、端や、右下の角をドラッグすると、大きさを変えられます。右上のボタンで、一時停止もできます。")
    add("s08", screen(desk, "3. 文字の読み取りと翻訳", "英語など日本語以外の文字には、下に日本語訳が付きます。翻訳は、最初の設定では PC の中の AI が行うので、回数の制限はありません。読み取った文字は選んでコピーできます。",
                      [(tx + 12, tbar + 60, tx + 750, tbar + 362), (tx + 12, tbar + 378, tx + 750, tbar + 678)], framed=False),
        "英語など、日本語以外の文字には、その下に日本語訳が付きます。"
        "翻訳は、最初の設定では、パソコンの中のエーアイが行うので、何回使っても制限はありません。読み取った文字は、そのまま選んでコピーできます。")
    add("s09", screen(desk, "3. 文字の読み取りと翻訳", "上のボタンで、一時停止・今すぐ読み取り・蓄積 (スクロールしながら読んだ文字をためる)・設定を使えます。",
                      [(tx + 10, tbar + 5, tx + 750, tbar + 52)], framed=False),
        "上に並んだボタンでは、一時停止や、今すぐ読み取り、それから、スクロールしながら読んだ文字を一つにためていく、蓄積、などが使えます。")

    # ── 4. 議事録
    add("s10", screen(E, "4. 議事録", "議事録を開いたら、文字にしたい音を出しているアプリ (会議アプリやブラウザ) を選びます。スピーカーの印は、いま音を出しているアプリです。",
                      [(35, 55, 942, 107)], "GetText — 議事録"),
        "次は、議事録です。議事録の画面を開いたら、まず、文字にしたい音を出しているアプリを選びます。"
        "会議アプリやブラウザなどですね。スピーカーの印が付いているのが、今、音を出しているアプリです。")
    add("s11", screen(S("minutes_loading.png"), "4. 議事録", "開いた直後は、音声認識の AI を読み込みます。進み具合と経過時間が出て、準備ができると自動で消えます。「裏で準備」で閉じて、待つ間に設定などもできます。",
                      [(292, 234, 976, 636)], "GetText — 議事録"),
        "開いた直後は、音声認識のエーアイを読み込むので、少し待ちます。どこまで進んだかが表示され、準備ができると、自動で消えます。")
    add("s12", screen(E, "4. 議事録", "「言語」で文字にする言語を選びます。「用語」には人名・社名・製品名を登録でき、読みが同じなら登録した表記に直します。「マイク (自分の声) も入れる」は最初からオン。自分の声を入れないときは外します。",
                      [(80, 122, 305, 166), (330, 122, 428, 166), (455, 125, 705, 166)], "GetText — 議事録"),
        "言語のボタンでは、文字にする言語を選びます。用語のボタンに、人の名前や会社名、製品名を登録しておくと、読みが同じなら、登録した書き方に直してくれます。"
        "自分の声も議事録に入れる、マイクも入れる、は、最初からオンになっています。自分の声を入れたくないときは、チェックを外してください。")
    add("s13", screen(E, "4. 議事録", "準備ができたら「記録を開始」。会議を記録するときは、参加者の同意を得てください。",
                      [(1010, 40, 1233, 92)], "GetText — 議事録"),
        "準備ができたら、記録を開始、を押します。なお、会議を記録するときは、参加している方の同意を、必ず得るようにしてください。")
    add("s14", screen(M, "4. 議事録", "話し終わった発言は、時刻と話者つきで一覧に並びます。英語の発言には日本語訳が付きます。話している途中の文字は、下の「確定前」の欄に出るので、一覧と重なりません。",
                      [(33, 290, 895, 612), (28, 628, 897, 694)], "GetText — 議事録"),
        "話し終わった発言は、時刻と話した人の名前つきで、一覧に並んでいきます。英語の発言には、日本語訳も付きます。"
        "まだ話している途中の文字は、下の、確定前、の欄に表示されるので、一覧の文字と重なって読みにくくなることはありません。")
    add("s15", screen(M, "4. 議事録", "話者は声から自動で聞き分けます。名前を書き換えて Enter で、すべての発言に反映されます。声も覚えるので、次の会議では、似た声の人に自動で名前が付きます。",
                      [(930, 230, 1250, 600)], "GetText — 議事録"),
        "話している人は、声の特徴から、自動で聞き分けます。右の欄で名前を書き換えてエンターを押すと、すべての発言に反映されます。"
        "声も覚えるので、次の会議からは、声が似ていれば、自動で名前が付きます。")
    add("s16", screen(M, "4. 議事録", "発言はクリックして書き直せます。右クリックで削除・話者の変更・コピー。大事な発言には ★ (Ctrl+B) を付け、「★ 重要」で絞り込めます。",
                      [(33, 474, 895, 508), (790, 240, 890, 275)], "GetText — 議事録"),
        "発言は、クリックすれば書き直せますし、右クリックで、削除や、話した人の変更もできます。"
        "大事な発言には、星の印を付けておくと、重要、のボタンで、星の付いた発言だけを表示できます。")
    add("s17", screen(M, "4. 議事録", "発言の時刻をクリックすると、その発言の音声だけを聞き直せます (「会議の音声も保存する」がオンのとき)。下の欄にメモを書いて Enter で、今の時刻にメモを差し込めます。",
                      [(33, 290, 130, 612), (33, 712, 895, 760)], "GetText — 議事録"),
        "会議の音声を保存していれば、発言の時刻をクリックすると、その発言の音声だけを、聞き直すことができます。聞き間違いを直したいときに便利です。"
        "下の欄にメモを書いてエンターを押すと、今の時刻に、メモとして差し込めます。")
    add("s18", screen(OPT, "4. 議事録", "「詳細」では、速さ、文脈による聞き間違いの補正、音声の保存のほか、「会議の画面も録画する」と「字幕つきの動画も作る」を選べます。",
                      [(20, 548, 620, 772)], "GetText — 議事録 (詳細)"),
        "詳細のボタンを開くと、認識の速さや、文脈による聞き間違いの補正などを選べます。"
        "さらに、会議の画面も録画する、にチェックを入れると、議事録を取りながら、会議の画面も一緒に録画できます。")
    add("s19", screen(OPT, "4. 議事録", "記録を止めると、録画の隣に字幕のファイル (.srt) を置き、発言を話者名つきの字幕にして焼き込んだ動画も作ります。後で発言を直したら、「保存…」→「字幕つきの動画」で作り直せます。",
                      [(60, 650, 620, 770)], "GetText — 議事録 (詳細)"),
        "字幕つきの動画も作る、をオンにしておけば、記録を止めたあとで、発言を、話した人の名前つきの字幕にして、動画に焼き込んでくれます。"
        "あとから発言を直した場合は、保存のメニューから、字幕つきの動画を選ぶと、直した内容で作り直せます。")
    add("s20", screen(F, "4. 議事録", "録画・録音済みのファイルは「ファイルから…」で選ぶと、同じように文字起こしできます (MP4・MP3・WAV など)。進み具合と残り時間が表示されます。",
                      [(920, 62, 1236, 162), (10, 786, 400, 810)], "GetText — 議事録"),
        "すでに録画や録音したファイルがあれば、ファイルから、を押して選ぶだけで、同じように文字起こしができます。進み具合と、残りの時間も表示されます。")
    add("s21", screen(SM, "4. 議事録", "「要約」を押すと、概要・決定事項・やること・主な論点を PC の中の AI がまとめます (フルで入れたとき)。AI は発言にない内容を書くことがあるので、確かめてから使ってください。",
                      [(32, 294, 895, 598), (705, 818, 795, 858)], "GetText — 議事録"),
        "要約を押すと、会議の概要や、決まったこと、やること、主な論点を、エーアイがまとめてくれます。要約は、フルで入れたときに使えます。"
        "ただ、エーアイは、発言にない内容を書いてしまうこともあるので、使う前に、内容を確かめてください。")
    add("s22", screen(SM, "4. 議事録", "「保存…」でワード・テキスト・字幕 (SRT) のほか、音声も入れたプロジェクトにも保存できます。記録中も自動で保存されます。「設定」ボタンで文字の大きさなどを変えられます。",
                      [(1145, 818, 1245, 858), (160, 818, 245, 858)], "GetText — 議事録"),
        "保存では、ワードやテキスト、字幕のほか、音声も一緒にまとめたプロジェクトとして保存することもできます。記録している間も、自動で保存されています。"
        "左下の設定ボタンからは、議事録の文字の大きさなどを変えられます。")

    # ── 5. 画面の録画
    add("s23", screen(crop(REC, (0, 0, 818, 330)), "5. 画面の録画", "録画するウィンドウを選び、アプリの音・マイクの声を入れるか、画質、なめらかさ (15〜240 コマ/秒。保存は最大 144 コマ/秒で、モニターの Hz より多くはなりません)、保存先を選びます。「画面全体」を選ぶとモニターごと録画します。",
                      [(40, 62, 728, 118), (40, 138, 560, 175), (40, 195, 680, 250), (40, 268, 770, 300)], "GetText — 画面の録画"),
        "最後は、画面の録画です。録画したいアプリの画面を選んで、そのアプリの音や、自分のマイクの声も入れるかを選びます。"
        "画質と、なめらかさも選べます。なめらかさは、一秒あたり十五コマから選べます。ただ、保存されるのは最大で百四十四コマまでで、モニターのリフレッシュレートより多くはなりません。")
    add("s24", screen(crop(REC, (0, 335, 818, 800)), "5. 画面の録画", "プレビューで、どう録画されるかを録画の前から確かめられます。ほかの窓の後ろにあっても録画でき、会議アプリやブラウザも黒くならずに録画できます。ただし、取り込みを禁止している窓は黒くなり、最小化すると画面が止まります。",
                      [(35, 70, 783, 445)], "GetText — 画面の録画"),
        "プレビューで、どんなふうに録画されるかを、始める前から確かめられます。"
        "録画するアプリは、ほかの窓の後ろに隠れていても大丈夫です。会議アプリやブラウザも、黒くならずに、きれいに録画できます。"
        "ただし、主催者が画面の取り込みを禁止した会議や、著作権で保護された動画は、黒く録画されます。また、最小化すると画面が止まるので、最小化はしないでください。")
    add("s25", screen(crop(REC2, (0, 805, 818, 1125)), "5. 画面の録画", "「録画を開始」→「録画を停止」で MP4 に保存します。録画中は経過時間とファイルの大きさが出て、PC はスリープしません。会議を録画するときは、参加者の同意を得てください。",
                      [(40, 23, 282, 80), (300, 23, 520, 85)], "GetText — 画面の録画"),
        "録画を開始、を押すと録画が始まり、停止を押すと、エムピーフォーのファイルに保存されます。"
        "録画している間は、経過時間とファイルの大きさが表示されます。会議を録画するときも、参加している方の同意を、忘れずに得てください。")

    # ── 6. 設定
    add("s26", screen(S("settings_0.png"), "6. 設定", "「設定」では、読み取りの方式や間隔、翻訳の方法、表示 (テーマ・文字の大きさ)、議事録、ショートカットキーを変えられます。",
                      [(0, 15, 276, 330)], "GetText — 設定"),
        "設定の画面では、読み取りの方式や間隔、翻訳の方法、テーマや文字の大きさ、議事録の表示、それにショートカットキーなどを、変えることができます。")
    add("s27", screen(S("settings_5.png"), "6. 設定", "「セットアップ・情報」では、AI の機能が使える状態かを確かめられます。足りない機能は、ここのセットアップのボタンで後から入れられます (セットアップの間は GetText を閉じます)。",
                      [(285, 70, 1028, 228)], "GetText — 設定"),
        "セットアップと情報のタブでは、エーアイの機能が使える状態になっているかを確かめられます。足りない機能は、ここから、あとで追加することもできます。")

    credit = [f"ナレーション: {voicevox_tts.CREDIT}"] if USE_VOICEVOX else []
    add("s28", card("困ったとき", ["・使い方と仕組みの詳しい説明: GetText フォルダの README.md",
                                "・セットアップの記録: setup.log",
                                "・もう一度 setup.bat を実行すると、足りない分だけ入れ直します",
                                "・Mac 版もあります (README の「Mac で使う」)", ""] + credit),
        "うまく動かないときは、フォルダの中の、リードミーと、セットアップの記録を見てみてください。セットアップ・バットをもう一度実行すると、足りない分だけを入れ直します。"
        "ゲットテキストの紹介は、以上です。ご覧いただき、ありがとうございました。")
    return out


# ───────── 音声と動画 ─────────

def read_wav(path):
    with wave.open(path) as w:
        sr, n, ch, sw = w.getframerate(), w.getnframes(), w.getnchannels(), w.getsampwidth()
        data = np.frombuffer(w.readframes(n), np.int16 if sw == 2 else np.uint8).astype(np.float32)
    if ch > 1:
        data = data.reshape(-1, ch).mean(1)
    return data / 32768.0, sr


def main():
    shots, out = sys.argv[1], sys.argv[2]
    work = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(out), "_video_work")
    os.makedirs(work, exist_ok=True)
    sc = scenes(shots)
    with open(os.path.join(work, "scenes.json"), "w", encoding="utf-8") as f:
        json.dump([{"id": s["id"], "text": s["text"]} for s in sc], f, ensure_ascii=False)
    if USE_VOICEVOX:
        subprocess.run([sys.executable, os.path.join(HERE, "voicevox_tts.py"),
                        os.path.join(work, "scenes.json"), os.path.join(work, "tts")], check=True)
    else:
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, "tts.ps1"),
                        "-InputJson", os.path.join(work, "scenes.json"), "-OutDir", os.path.join(work, "tts")], check=True)
    sr = None
    clips = []
    for s in sc:
        a, r = read_wav(os.path.join(work, "tts", s["id"] + ".wav"))
        sr = sr or r
        if r != sr:
            a = np.interp(np.arange(0, len(a), r / sr), np.arange(len(a)), a)
        clips.append(a)
        s["img"].save(os.path.join(work, s["id"] + ".png"))
    lead, tail = 0.7, 1.1  # 場面の最初と最後の間 (画面が切り替わってから、ひと呼吸おいて話し始める)
    durs = [lead + len(a) / sr + tail for a in clips]
    audio = np.concatenate([np.concatenate([np.zeros(int(lead * sr)), a, np.zeros(int(tail * sr))]) for a in clips])
    # 場面の間もまったくの無音にしない (ナレーションと同じくらいのごく小さな部屋の音)
    rng = np.random.default_rng(1)
    hum = np.convolve(rng.standard_normal(len(audio)).astype(np.float32), np.ones(24) / 24, "same")
    audio = audio + hum / (np.sqrt(np.mean(hum ** 2)) or 1) * 10 ** (-68 / 20)

    container = av.open(out, "w")
    vs = container.add_stream("libx264", rate=FPS)
    vs.width, vs.height, vs.pix_fmt = W, H, "yuv420p"
    vs.options = {"crf": "20", "preset": "medium", "tune": "stillimage"}
    as_ = container.add_stream("aac", rate=sr)
    as_.layout = "mono"
    frames = [np.asarray(s["img"].convert("RGB")) for s in sc]
    fade = int(FADE_SEC * FPS)
    for i, (img, dur) in enumerate(zip(frames, durs)):
        n = int(round(dur * FPS))
        for k in range(n):
            if i > 0 and k < fade:  # 前の場面から滑らかに切り替える
                t = (k + 1) / (fade + 1)
                arr = (frames[i - 1] * (1 - t) + img * t).astype(np.uint8)
            else:
                arr = img
            for p in vs.encode(av.VideoFrame.from_ndarray(arr, format="rgb24")):
                container.mux(p)
    for p in vs.encode(None):
        container.mux(p)
    resampler = av.AudioResampler(format="fltp", layout="mono", rate=sr)
    pcm = (np.clip(audio, -1, 1) * 32767).astype(np.int16)
    pts = 0
    for k in range(0, len(pcm), 1024):
        chunk = pcm[k:k + 1024].reshape(1, -1)
        frame = av.AudioFrame.from_ndarray(chunk, format="s16", layout="mono")
        frame.sample_rate = sr
        frame.pts = pts
        pts += chunk.shape[1]
        for rf in resampler.resample(frame):
            for p in as_.encode(rf):
                container.mux(p)
    for p in as_.encode(None):
        container.mux(p)
    container.close()
    print(f"{out}: {sum(durs):.0f} 秒, {os.path.getsize(out) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
