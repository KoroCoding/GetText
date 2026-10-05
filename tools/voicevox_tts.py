"""操作手順の動画のナレーションを VOICEVOX (PC 内の音声合成) で WAV にする。

VOICEVOX ENGINE (CPU 版) を tools/voicevox に展開しておくと、make_video.py がこれを使う。
エンジンはこのスクリプトが起動して、終わったら止める (CPU だけ・優先度を下げて動かす)。
使い方: python voicevox_tts.py scenes.json 出力フォルダ
  scenes.json: [{"id": "s01", "text": "..."}, ...]
"""
import io
import json
import os
import random
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import wave

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE_DIR = os.path.join(HERE, "voicevox")
PORT = 50121                  # ほかの VOICEVOX と重ならない番号
# 声 (既定は落ち着いたアナウンスの声)。環境変数 GETTEXT_VOICE="キャラクター/スタイル" で変えられる (例: No.7/読み聞かせ)
SPEAKER_NAME, STYLE_NAME = (os.environ.get("GETTEXT_VOICE") or "No.7/アナウンス").split("/", 1)
SPEED = 1.05                  # 少しだけ速め (1.12 では早口で聞きづらかった)
INTONATION = 1.15             # 抑揚を少し強める (アナウンスの声は平たく聞こえやすい)
PAUSE = 1.3                   # 読点「、」の間を長めに (息つぎの間がないと機械的に聞こえる)
CREDIT = f"VOICEVOX:{SPEAKER_NAME}"  # 動画に入れるクレジット (VOICEVOX の利用規約で必要)

# 読み方が崩れる語の読み (字幕はそのまま、読み上げだけ置き換える)。
# 英字の語と、VOICEVOX が読み違えた語 (右上 → みぎじょう、同じ人 → どうじじん など)
READINGS = [
    ("GetText", "ゲットテキスト"), ("setup.bat", "セットアップバット"), ("setup.log", "セットアップログ"),
    ("update.bat", "アップデートバット"), ("GitHub", "ギットハブ"), ("README", "リードミー"), ("winget", "ウィンゲット"),
    ("Python", "パイソン"), ("Markdown", "マークダウン"), ("Enter", "エンター"), ("Ctrl", "コントロール"),
    ("WAV", "ウェーブ"), ("SRT", "エスアールティー"),
    ("Zoom", "ズーム"), ("ZIP", "ジップ"), ("OpenAI", "オープンエーアイ"), ("DevDay", "デブデイ"), ("AI", "エーアイ"), ("git clone", "ギットクローン"),
    ("右上", "みぎうえ"), ("同じ人", "同じひと"),
    ("MP4", "エムピーフォー"), ("MP3", "エムピースリー"), ("OCR", "オーシーアール"), ("GPU", "ジーピーユー"), ("CPU", "シーピーユー"),
    ("NVIDIA", "エヌビディア"), ("Windows", "ウィンドウズ"), ("Mac", "マック"), ("Word", "ワード"), ("Teams", "チームズ"),
    ("ウィンドウ", "ウインドウ"), ("VOICEVOX", "ボイスボックス"), ("H.264", "エイチニーロクヨン"), ("PC", "ピーシー"),
]


def reading(text):
    for word, kana in READINGS:
        text = text.replace(word, kana)
    # 和文と英字の間に入れた空白は、読み上げでは不自然な間になるので詰める
    return re.sub(r"(?<=[^\x00-\x7f]) +| +(?=[^\x00-\x7f])", "", text)


def find_engine():
    for root, _, files in os.walk(ENGINE_DIR):
        if "run.exe" in files:
            return os.path.join(root, "run.exe")
    return None


def api(path, data=None, params=None, raw=False):
    url = f"http://127.0.0.1:{PORT}{path}" + ("?" + urllib.parse.urlencode(params) if params else "")
    body = None if data is None else (data if isinstance(data, bytes) else json.dumps(data).encode("utf-8"))
    req = urllib.request.Request(url, data=body, method="POST" if data is not None else "GET",
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        out = r.read()
    return out if raw else json.loads(out)


def start_engine():
    exe = find_engine()
    if exe is None:
        raise FileNotFoundError(f"VOICEVOX ENGINE が {ENGINE_DIR} にありません")
    threads = max(1, min(4, (os.cpu_count() or 4) // 2))
    proc = subprocess.Popen([exe, "--host", "127.0.0.1", "--port", str(PORT), "--cpu_num_threads", str(threads)],
                            cwd=os.path.dirname(exe), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            creationflags=subprocess.BELOW_NORMAL_PRIORITY_CLASS | subprocess.CREATE_NO_WINDOW)
    for _ in range(240):
        try:
            api("/version")
            return proc
        except Exception:
            if proc.poll() is not None:
                raise RuntimeError("VOICEVOX ENGINE が起動できませんでした")
            time.sleep(0.5)
    proc.kill()
    raise TimeoutError("VOICEVOX ENGINE の起動を待ちきれませんでした")


def style_id():
    for sp in api("/speakers"):
        if sp["name"] == SPEAKER_NAME:
            for st in sp["styles"]:
                if st["name"] == STYLE_NAME:
                    return st["id"]
    raise LookupError(f"{SPEAKER_NAME} ({STYLE_NAME}) の声がありません")


RATE = 48000


def synthesize(text, speaker, rng=None):
    """1 文を読む (float32 の配列)。rng を渡すと、文ごとに速さ・高さ・抑揚をごくわずかに変える
    (どの文も同じ調子だと機械的に聞こえるため。人が読むときも文ごとに少しずつ違う)。"""
    query = api("/audio_query", data=b"", params={"text": reading(text), "speaker": speaker})
    jitter = (lambda r: rng.uniform(-r, r)) if rng else (lambda r: 0.0)
    query["speedScale"] = SPEED * (1 + jitter(0.025))
    query["pitchScale"] = jitter(0.012)
    query["intonationScale"] = INTONATION * (1 + jitter(0.05))
    query["pauseLengthScale"] = PAUSE
    query["prePhonemeLength"] = 0.05
    query["postPhonemeLength"] = 0.05
    query["outputSamplingRate"] = RATE
    data = api("/synthesis", data=query, params={"speaker": speaker}, raw=True)
    with wave.open(io.BytesIO(data)) as w:
        pcm = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float32) / 32768
    return trim(pcm)


def trim(a, threshold=0.004):
    """前後の無音を切る (文の間は自分で決めるため)。"""
    loud = np.where(np.abs(a) > threshold)[0]
    if len(loud) == 0:
        return a[:0]
    return a[max(0, loud[0] - int(0.02 * RATE)):loud[-1] + int(0.06 * RATE)]


def sentences(text):
    """文に分ける (「。」「？」「！」の後)。"""
    return [p for p in re.split(r"(?<=[。？！])", text) if p.strip()]


def normalize(a, target_db=-19.0):
    """声の大きさを揃える (文によって大きさが変わらないように)。"""
    rms = np.sqrt(np.mean(a ** 2)) if len(a) else 0
    return a * (10 ** (target_db / 20) / rms) if rms > 1e-6 else a


def room_tone(n, rng, level_db=-66.0):
    """ごく小さな部屋の空気の音 (無音がまったくの無音だと、合成した声だと分かりやすい)。"""
    white = np.asarray([rng.gauss(0, 1) for _ in range(min(n, RATE))], np.float32)
    noise = np.convolve(np.tile(white, n // len(white) + 1)[:n], np.ones(24) / 24, "same")  # 低めの音にする
    rms = np.sqrt(np.mean(noise ** 2)) or 1
    return noise / rms * 10 ** (level_db / 20)


def speak(text, speaker, rng):
    """場面のナレーション: 文ごとに読み、文の長さに合わせた自然な間をあけてつなぐ。"""
    parts = []
    for i, sent in enumerate(sentences(text)):
        if i:
            # 文の間: 0.45〜0.7 秒 (長い文の後は少し長く、毎回少しずつ違う)
            parts.append(np.zeros(int(RATE * min(0.7, 0.42 + 0.004 * len(sent) + rng.uniform(0, 0.08))), np.float32))
        parts.append(normalize(synthesize(sent, speaker, rng)))
    a = np.concatenate(parts) if parts else np.zeros(RATE // 2, np.float32)
    # 話し始めと終わりを滑らかにする
    fade = int(0.01 * RATE)
    if len(a) > 2 * fade:
        a[:fade] *= np.linspace(0, 1, fade)
        a[-fade:] *= np.linspace(1, 0, fade)
    a = a + room_tone(len(a), rng)
    return np.clip(a, -0.98, 0.98)


def write_wav(path, a):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((a * 32767).astype(np.int16).tobytes())


def main():
    scenes = json.load(open(sys.argv[1], encoding="utf-8"))
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    proc = start_engine()
    try:
        speaker = style_id()
        for s in scenes:
            rng = random.Random(s["id"])  # 作り直しても同じ読み方になるように
            write_wav(os.path.join(out_dir, s["id"] + ".wav"), speak(s["text"], speaker, rng))
        print(f"{len(scenes)} 件の音声を作りました ({CREDIT})")
    finally:
        proc.kill()


if __name__ == "__main__":
    main()
