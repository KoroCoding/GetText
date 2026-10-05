"""GetText 議事録サーバー: 音声をリアルタイムに文字起こしし、話者を聞き分ける。

GetText から子プロセスとして起動され、標準入出力で通信する。
  起動中:   {"event": "loading", "step": "asr" | "speakers"}  読み込み中のモデル (画面の準備中の表示)
  起動完了: {"ready": true, "device": "cuda"}
  音声:     {"cmd": "audio", "src": "win" | "mic", "len": N}\n + 16kHz モノラル 16bit PCM (N バイト)
  区切り:   {"cmd": "flush"}  話し途中の音声も確定させる (停止時)
  初期化:   {"cmd": "reset"}  話者の記憶と時刻を消す (新しい議事録)
  再開:     {"cmd": "restart"} 時刻だけを 0 に戻す (録音の再開。話者の記憶は残す)
  設定:     {"cmd": "config", "sensitivity": 0.0, "llm": true, "languages": ["ja", "en"]}
            sensitivity: 声の聞き分けの細かさ (-1 = まとめる 〜 +1 = 細かく分ける)
            merge: [2, 1]  画面で話者 2 を話者 1 にまとめた (以後の分け直しでも 1 人として扱う)
            speed: "auto" | "light" | "accurate"  速さ。light は速い日本語の音声認識 (ReazonSpeech、CPU でも記録に追いつく)、
            accurate は kotoba-whisper (正確だが CPU だけでは 1 発言に約 10 秒かかる)。auto は GPU があれば accurate、無ければ light
            languages: 対象の言語 (Whisper の言語コード)。1 つならその言語として文字にし、複数なら発言ごとにその中から判定する。
            日本語 (ja) は kotoba-whisper、それ以外は Whisper large-v3 turbo で文字にする
  ファイル: {"cmd": "file", "path": "C:\\...\\会議.mp4"}  動画・音声ファイルを文字起こしする (時刻はファイルの先頭から)
            {"cmd": "cancel"}  ファイルの文字起こしを途中でやめる
  出力:     {"event": "segment", "id": 7, "src": "win", "start": 12.3, "end": 15.8, "speaker": 1, "text": "...", "lang": "ja"}
            {"event": "revise", "ids": [7, 8], "text": "...", "stage": "asr" | "llm", "before": "..."}
                まとめて認識し直した結果 (asr) や、前後の文脈で聞き間違いを直した結果 (llm)。
                ids の発言を 1 つにまとめて text に置き換える
            {"event": "partial", "src": "win", "speaker": 1, "text": "..."}
                話している途中の暫定の文字 (約 0.7 秒ごと)。text が空なら消す。確定すると segment が届く
            {"event": "respeaker", "speakers": {"7": 2, ...}}
                発言が増えて全体を分け直した結果、話者が変わった以前の発言
            {"event": "status", "llm": "loading" | "gpu" | "cpu" | "unavailable" | "off"}
            {"event": "status", "multilingual": "loading" | "ready" | "missing"}  英語などの音声認識モデルの状態
            {"event": "status", "speed": "light" | "accurate", "gpu": false, "fast": true}  いまの速さ (auto を決めた結果)
            {"event": "status", "foreign": true}  日本語の設定なのに日本語以外が話されているらしい
            {"event": "status", "lag": 4.2}  処理が音声に追いついていない秒数 (0 になったら 0 を送る)
            {"event": "progress", "done": 120.5, "total": 3600.0}  ファイルの処理済みの秒数
  変換:     {"cmd": "encode", "src": "...wav", "dst": "...m4a"} → {"event": "encoded", "dst": "...", "ok": true}
  字幕:     {"cmd": "burn", "src": "...mp4", "dst": "..._字幕.mp4", "cues": [[始まり秒, 終わり秒, "文字"], ...]} / {"cmd": "burn_cancel", "dst": "..."}
            → {"event": "burn_progress", "dst": "...", "done": 12.0, "total": 60.0} / {"event": "burned", "dst": "...", "ok": true}
  AI の訳:  {"cmd": "translate", "job": 1, "items": [{"text": "...", "context": ["直前の発言", ...]}]}
            → {"event": "translated", "job": 1, "texts": ["訳", ...]} (失敗すれば "error")
  要約:     {"cmd": "summarize", "job": 1, "text": "..."} / {"cmd": "summary_cancel"}
            → {"event": "summary", "job": 1, "state": "loading" | "progress" | "done" | "cancelled" | "error", ...}
            {"event": "file_done", "duration": 3600.0, "cancelled": false}
            {"event": "flushed"} / {"event": "refined"} (文脈補正が追いついた) / {"event": "error", "message": "..."}
処理の流れ: Silero VAD で発言の区切りを見つけ → kotoba-whisper で文字にし → CAM++ の声の特徴で話者を割り当てる。
マイク (src=mic) の発言は話者 0 (自分) とする。
"""
import difflib
import json
import os
import math
import queue
import re
import sys
import threading

import app_data
import fast_asr
import time

import numpy as np

import gpu_paths  # noqa: F401  (GPU 用の DLL を CTranslate2 より先に登録する)
from minutes_refine import Refiner, LLM_IDLE_SEC
from minutes_summary import Summarizer
from voices import VoiceBook
from minutes_translate import AiTranslator
import speakers as speaker_stats
from speakers import Speakers

RATE = 16000
MODELS_DIR = app_data.MODELS_DIR
ASR_PATH = os.path.join(MODELS_DIR, "kotoba-whisper-v2.0")
MULTI_PATH = os.path.join(MODELS_DIR, "whisper-large-v3-turbo")  # 英語など日本語以外 + 言語の判定
SHORT_DETECT_SEC = 1.5    # これより短い発言は言語の判定が不安定なので、はっきり判定できたときだけ直前の言語から切り替える
BEAM_CPU = 5              # CPU で確定させるときの探索の幅 (2 や 1 にしても速さは変わらず、誤りが増えた)
REASR_MAX_LAG = 10.0      # 記録中に処理がこれ以上遅れたら、まとめて認識し直すのを休んで追いつく (CPU だけの PC 向け)
HOTWORDS_MAX = 40         # 音声認識に渡す用語の数の上限 (長すぎると認識の文脈を圧迫する)
SHORT_KEEP_SEC = 1.0      # これより短い発言は判定せず、直前の言語とみなす

MIN_SILENCE_SEC = 0.45    # この長さの無音で発言が終わったとみなす (細かく区切れても 2 段階目でまとめ直す)
PARTIAL_INTERVAL_SEC = 0.7  # 話している途中の暫定の文字を出す間隔
PARTIAL_MIN_SEC = 0.5       # これより短い話し始めは暫定表示しない
MAX_SEGMENT_SEC = 15.0    # 区切れずに続く場合はここで切る (Whisper は 20 秒を超える音声をまとめて読むと、途中の言い回しを丸ごと飛ばすことがあった)
CUT_MIN_SEC = 6.0         # 長く続く発言を切るときは、始まりからこの長さより後の、いちばん静かな所 (息継ぎ) で切る
FINAL_MIN_SEC = 0.6       # 停止時に確定させる話し途中の音声は、これより短ければ捨てる (雑音のことが多い)
LAG_SKIP_PARTIAL_SEC = 1.5  # 処理がこれ以上遅れていたら暫定表示を省いて確定を優先する
MIN_RMS = 0.004           # これより小さい音 (-48 dBFS) の発言は雑音とみなす
# 無音や雑音から Whisper がよく作ってしまう定型文 (動画の字幕で学習しているため)
HALLUCINATION_PHRASES = ("ご視聴ありがとうございました", "ご清聴ありがとうございました", "チャンネル登録",
                         "字幕視聴", "次回もお楽しみに", "最後までご覧", "おやすみなさい", "エンディング",
                         "(音楽)", "(拍手)", "♪",
                         "thank you for watching", "thanks for watching", "please subscribe", "subtitles by",
                         "see you in the next video", "like and subscribe")


def looks_foreign(text):
    """
    日本語向けのモデルが英語などを聞いたときの書き方か: 空白で区切ったカタカナ語が並び、ひらがながほとんどない
    (例: 「エネルギー エクセリックス チャリック」)。
    """
    words = [w for w in text.replace("、", " ").replace("。", " ").split() if w]
    if len(words) < 3:
        return False
    kata = sum(1 for c in text if "\u30a0" <= c <= "\u30ff")
    hira = sum(1 for c in text if "\u3040" <= c <= "\u309f")
    return kata >= 6 and kata > hira * 2


def looks_hallucinated(text, segs, audio):
    """雑音・無音から作られた文かどうか。"""
    body = text.strip("。、.,!?！？ 　")
    if not body:
        return True
    dur = len(audio) / RATE
    if float(np.sqrt(np.mean(audio ** 2))) < MIN_RMS:
        return True
    if segs:
        no_speech = float(np.mean([s.no_speech_prob for s in segs]))
        logprob = float(np.mean([s.avg_logprob for s in segs]))
        if no_speech > 0.55 and logprob < -0.7:
            return True
        if logprob < -1.2:
            return True
        if max(s.compression_ratio for s in segs) > 2.4:
            return True  # 同じ言葉の繰り返し
    if dur < 5 and any(p in text.lower() for p in HALLUCINATION_PHRASES):
        return True
    return False

out_lock = threading.Lock()
# 用語の登録の「誤り→正しい」の置き換えと、読みで当てはめる用語 (発言・補正・話し途中の文字に当てる)
RULES = []
MATCHER = [None]
TERMS_VERSION = [0]


def parse_terms(items):
    """登録した用語を、音声認識に優先させる語と、置き換えの決まり (誤り→正しい) に分ける。"""
    hot, rules = [], []
    for raw in items or []:
        item = str(raw).strip()
        if not item or item.startswith("#"):
            continue
        for arrow in ("→", "->", "=>"):
            if arrow in item:
                wrong, right = (x.strip() for x in item.split(arrow, 1))
                if wrong and right:
                    rules.append((wrong, right))
                    if right not in hot:
                        hot.append(right)
                break
        else:
            if item not in hot:
                hot.append(item)
    return hot, rules


def apply_rules(text):
    if MATCHER[0] is not None:
        try:
            text = MATCHER[0].fix(text)
        except Exception as e:  # 読みの解析に失敗しても文字起こしは続ける
            log("用語の当てはめに失敗しました:", e)
    for wrong, right in RULES:
        if wrong in right:
            # 正しい語が間違いの語を含む (ラウド→クラウド) ときは、もう正しい所を置き換えない (ククラウドにしない)
            text = re.sub(f"{re.escape(right)}|{re.escape(wrong)}", lambda m: right, text)
        else:
            text = text.replace(wrong, right)
    return text


def set_terms(hot, rules):
    """用語を設定する。読みの解析 (Janome) の準備に 1〜2 秒かかるので裏で行う。"""
    RULES[:] = rules
    MATCHER[0] = None
    TERMS_VERSION[0] += 1
    version = TERMS_VERSION[0]
    if hot:
        def build():
            try:
                from terms import TermMatcher
                matcher = TermMatcher(hot)
            except Exception as e:
                log("用語の準備に失敗しました:", e)
                return
            if version == TERMS_VERSION[0]:  # 作っている間に用語が変わっていなければ使う
                MATCHER[0] = matcher
                log(f"[terms] 読みで当てはめる用語 {len(matcher.terms)} 件")
        threading.Thread(target=build, daemon=True).start()


def _finite(o):
    """NaN / 無限大を null にする (C# の JSON の読み取りが止まらないように)。"""
    if isinstance(o, float) and not math.isfinite(o):
        return None
    if isinstance(o, dict):
        return {k: _finite(v) for k, v in o.items()}
    if isinstance(o, (list, tuple)):
        return [_finite(v) for v in o]
    return o


SESSION = [0]  # アプリが付けた記録の番号 (reset / restart / file で受け取る)。発言に付けて返す


def emit(obj):
    with out_lock:
        if obj.get("event") == "segment":
            # どの記録の発言か (停止の待ちが切れた後に届いた前の記録の発言を、次の記録の時刻で並べないように)
            obj = {**obj, "session": SESSION[0]}
        if (RULES or MATCHER[0] is not None) and obj.get("event") in ("segment", "revise", "partial") and obj.get("text"):
            obj = {**obj, "text": apply_rules(obj["text"])}
        try:
            line = json.dumps(obj, ensure_ascii=False, allow_nan=False)
        except ValueError:
            line = json.dumps(_finite(obj), ensure_ascii=False)
        print(line, flush=True)


def log(*args):
    print(*args, file=sys.stderr, flush=True)


def encode_audio(src, dst):
    """記録した音声 (WAV) を AAC の .m4a にする (32kbps、1 時間で約 14MB)。終わったら encoded を知らせる。"""
    try:
        import av
        tmp = dst + ".part"
        with av.open(src) as inp, av.open(tmp, "w", format="mp4") as out:
            stream = out.add_stream("aac", rate=16000)
            stream.layout = "mono"
            stream.bit_rate = 32000
            for frame in inp.decode(audio=0):
                frame.pts = None
                for packet in stream.encode(frame):
                    out.mux(packet)
            for packet in stream.encode(None):
                out.mux(packet)
        os.replace(tmp, dst)
        emit({"event": "encoded", "dst": dst, "ok": True})
    except Exception as e:
        log("音声を変換できませんでした:", e)
        try:
            os.remove(dst + ".part")
        except OSError:
            pass
        emit({"event": "encoded", "dst": dst, "ok": False, "message": f"{type(e).__name__}: {e}"})


BURN_CANCEL = set()  # 中止した字幕の焼き込み (書き出し先)


def burn_subtitles(src, dst, cues):
    """録画した動画に字幕を焼き込む (subtitle_video.py)。進み具合と終わりを知らせる。"""
    try:
        import subtitle_video
        encoder = subtitle_video.burn(
            src, dst, cues,
            progress=lambda done, total: emit({"event": "burn_progress", "dst": dst, "done": done, "total": total}),
            cancel=lambda: dst in BURN_CANCEL)
        log("字幕を焼き込みました:", encoder)
        emit({"event": "burned", "dst": dst, "ok": True})
    except Exception as e:
        log("字幕を焼き込めませんでした:", e)
        try:
            os.remove(dst + ".part")
        except OSError:
            pass
        emit({"event": "burned", "dst": dst, "ok": False, "message": f"{type(e).__name__}: {e}"})
    finally:
        BURN_CANCEL.discard(dst)


def decode_int16(path):
    """動画・音声ファイルの音声を 16kHz モノラル 16bit で少しずつ読み込む。音声が無ければ ValueError。"""
    import av
    chunks = []
    with av.open(path) as container:
        if not container.streams.audio:
            raise ValueError("このファイルには音声がありません")
        stream = container.streams.audio[0]
        resampler = av.AudioResampler(format="s16", layout="mono", rate=RATE)
        for frame in container.decode(stream):
            for out in resampler.resample(frame):
                chunks.append(out.to_ndarray().reshape(-1).copy())
        for out in resampler.resample(None):
            chunks.append(out.to_ndarray().reshape(-1).copy())
    if not chunks:
        raise ValueError("音声を読み取れませんでした")
    return np.concatenate(chunks)


def has_gpu():
    if os.environ.get("GETTEXT_ASR_CPU"):
        return False
    try:
        import ctranslate2
        return ctranslate2.get_cuda_device_count() > 0
    except Exception:
        return False


def kotoba_installed():
    return os.path.isfile(os.path.join(ASR_PATH, "model.bin"))


def resolve_speed(setting, gpu):
    """速さの設定 (auto / light / accurate) から、実際に使う方を決める。片方のモデルしか無ければそちら。"""
    if not fast_asr.installed():
        return "accurate"
    if not kotoba_installed():
        return "light"
    if setting == "accurate":
        return "accurate"
    if setting == "light":
        return "light"
    return "accurate" if gpu else "light"


def load_asr(path=ASR_PATH, language="ja"):
    from faster_whisper import WhisperModel
    try:
        import ctranslate2
        if ctranslate2.get_cuda_device_count() > 0 and not os.environ.get("GETTEXT_ASR_CPU"):
            model = WhisperModel(path, device="cuda", compute_type="float16")
            # CUDA が使えるか確かめる (transcribe は結果を取り出すまで計算しないので、list で実際に動かす。
            # cuDNN・cuBLAS が無い・合わないときは、ここで失敗して CPU に切り替える)
            segments, _ = model.transcribe(np.zeros(RATE, np.float32), language=language)
            list(segments)
            return model, "cuda"
    except Exception as e:
        log("GPU を使えないため CPU で動かします:", e)
    # CPU の論理コアの半分で、8 まで (文脈補正の AI も同時に 8 まで使う)。16 にすると AI と取り合って
    # 1 発言の認識が約 1 秒 → 15 秒に遅くなった (AI なしでは 143 → 122 秒と少し速くなるだけだった)
    threads = int(os.environ.get("GETTEXT_ASR_THREADS", "0")) or max(2, min(8, (os.cpu_count() or 8) // 2))
    return WhisperModel(path, device="cpu", compute_type="int8", cpu_threads=threads), "cpu"


VAD_FRAME = 512    # Silero VAD の 1 枠 (サンプル)
VAD_CONTEXT = 64   # 1 枠の前に付ける、前の枠の終わりの音 (サンプル)


def vad_session():
    from faster_whisper.vad import get_vad_model
    return get_vad_model().session


def speech_timestamps(probs, total, threshold=0.5, min_silence_ms=500, speech_pad_ms=200, min_speech_ms=0):
    """
    声らしさ (枠ごと) から発言の区間を求める。faster-whisper (MIT) の get_speech_timestamps と同じ決め方で、
    声らしさの計算だけを外に出したもの (記録中は新しく届いた枠だけ計算すればよいようにするため)。
    """
    window = VAD_FRAME
    neg_threshold = max(threshold - 0.15, 0.01)
    min_speech = RATE * min_speech_ms / 1000
    pad = RATE * speech_pad_ms / 1000
    min_silence = RATE * min_silence_ms / 1000
    min_silence_at_max = RATE * 98 / 1000
    triggered = False
    speeches, current = [], {}
    temp_end = prev_end = next_start = 0
    for i, prob in enumerate(probs):
        if prob >= threshold and temp_end:
            temp_end = 0
            if next_start < prev_end:
                next_start = window * i
        if prob >= threshold and not triggered:
            triggered = True
            current["start"] = window * i
            continue
        if prob < neg_threshold and triggered:
            if not temp_end:
                temp_end = window * i
            if window * i - temp_end > min_silence_at_max:
                prev_end = temp_end
            if window * i - temp_end < min_silence:
                continue
            current["end"] = temp_end
            if current["end"] - current["start"] > min_speech:
                speeches.append(current)
            current = {}
            prev_end = next_start = temp_end = 0
            triggered = False
    if current and total - current["start"] > min_speech:
        current["end"] = total
        speeches.append(current)
    for i, sp in enumerate(speeches):
        if i == 0:
            sp["start"] = int(max(0, sp["start"] - pad))
        if i != len(speeches) - 1:
            silence = speeches[i + 1]["start"] - sp["end"]
            if silence < 2 * pad:
                sp["end"] += int(silence // 2)
                speeches[i + 1]["start"] = int(max(0, speeches[i + 1]["start"] - silence // 2))
            else:
                sp["end"] = int(min(total, sp["end"] + pad))
                speeches[i + 1]["start"] = int(max(0, speeches[i + 1]["start"] - pad))
        else:
            sp["end"] = int(min(total, sp["end"] + pad))
    return speeches


class Source:
    """1 系統 (ウィンドウ / マイク) の音声をためて、発言の区切りを見つける。"""

    def __init__(self, name):
        self.name = name
        self.buf = np.zeros(0, np.float32)
        self.start = 0          # buf の先頭が録音開始から何サンプル目か
        self.checked = 0        # 前回 VAD をかけたときの buf の長さ
        self.ongoing = None     # 話している途中の発言の開始位置 (buf 内)。話していなければ None
        self.probs = np.zeros(0, np.float32)  # buf の先頭からの、枠ごとの声らしさ (計算済みの分)
        self.state = np.zeros((2, 1, 128), np.float32)  # Silero VAD の内部の状態 (h と c。枠から枠へ引き継ぐ)
        self.context = np.zeros(VAD_CONTEXT, np.float32)  # buf の先頭の枠の前の音 (捨てた分の終わり)

    def speech_probs(self):
        """
        buf の枠ごとの声らしさ。新しく届いた枠だけを、内部の状態を引き継ぎながら 1 枠ずつ計算する (Silero VAD の
        本来の使い方)。以前は 0.25 秒ごとに buf 全体 (最大 25 秒) を計算し直していて、2 系統で CPU を使っていた。
        """
        full = len(self.buf) // VAD_FRAME
        have = len(self.probs)
        if full <= have:
            return self.probs
        session = vad_session()
        out = np.empty(full - have, np.float32)
        h, c = self.state[0:1], self.state[1:2]
        for k in range(have, full):
            if k:
                frame = self.buf[k * VAD_FRAME - VAD_CONTEXT:(k + 1) * VAD_FRAME]
            else:
                frame = np.concatenate([self.context, self.buf[:VAD_FRAME]])
            prob, h, c = session.run(None, {"input": frame[None, :], "h": h, "c": c})
            out[k - have] = float(np.asarray(prob).reshape(-1)[0])
        self.state = np.concatenate([h, c], 0)
        self.probs = np.concatenate([self.probs, out])
        return self.probs

    def quiet_point(self, lo, hi):
        """lo〜hi (サンプル) の中で、いちばん静かな所 (声らしさと音の大きさが小さい、0.25 秒ほど続く所) の枠の区切り。
        同じくらい静かなら後ろの方 (切った後の残りが短い方) を選ぶ。"""
        a, b = lo // VAD_FRAME, min(hi // VAD_FRAME, len(self.probs))
        if b - a < 16:
            return hi
        probs = self.probs[a:b]
        frames = self.buf[a * VAD_FRAME:b * VAD_FRAME].reshape(-1, VAD_FRAME)
        loud = np.sqrt(np.mean(frames ** 2, axis=1))
        loud = loud / (np.percentile(loud, 90) + 1e-9)
        k = 8  # 約 0.25 秒
        kernel = np.ones(k) / k

        def smooth(x):
            # 0 で埋めてならすと両端が低く出て、いつも端 (話の途中) が選ばれてしまうので、端の値で埋める
            return np.convolve(np.pad(x, (k // 2, k - 1 - k // 2), mode="edge"), kernel, "valid")

        score = smooth(probs) + smooth(loud)
        score -= np.linspace(0, 0.05, len(score))  # 後ろの方を少し選びやすく
        return (a + int(np.argmin(score))) * VAD_FRAME

    def drop(self, n):
        """buf の先頭から約 n サンプルを捨てる (枠の区切りに合わせて少なめに捨て、計算済みの声らしさと状態は引き継ぐ)。
        実際に捨てた数を返す。"""
        n = min(n - n % VAD_FRAME, len(self.probs) * VAD_FRAME)
        if n >= VAD_CONTEXT:
            self.context = self.buf[n - VAD_CONTEXT:n].copy()  # 次の最初の枠の前の音
        self.buf = self.buf[n:]
        self.start += n
        self.probs = self.probs[n // VAD_FRAME:]
        return n

    def add(self, pcm):
        self.add_samples(np.frombuffer(pcm, np.int16).astype(np.float32) / 32768.0)

    def add_samples(self, samples):
        self.buf = np.concatenate([self.buf, samples])

    def ready(self):
        return len(self.buf) - self.checked >= RATE // 4

    def ongoing_audio(self):
        """話している途中の音声 (暫定表示用)。"""
        if self.ongoing is None or len(self.buf) - self.ongoing < PARTIAL_MIN_SEC * RATE:
            return None
        return self.buf[self.ongoing:]

    def take_segments(self, final=False):
        """確定した発言 (start, end, audio) を取り出す。final なら話し途中も確定させる (短すぎる断片は捨てる)。"""
        self.checked = len(self.buf)
        if len(self.buf) < RATE // 4:
            return []
        ts = speech_timestamps(self.speech_probs(), len(self.buf), threshold=0.5,
                               min_silence_ms=int(MIN_SILENCE_SEC * 1000), speech_pad_ms=200)
        total = len(self.buf)
        done = []
        self.ongoing = None
        cut_open = False  # 話し続けている途中で切った (残りはまだ話している途中)
        if final:
            ts = [t for t in ts if t["end"] - t["start"] >= FINAL_MIN_SEC * RATE or t["end"] < total - MIN_SILENCE_SEC * RATE]
        for t in ts:
            finished = final or t["end"] < total - MIN_SILENCE_SEC * RATE
            too_long = total - t["start"] > MAX_SEGMENT_SEC * RATE
            if finished or too_long:
                # 話し途中で切るときは、計算済みの枠の区切りで切る (次の発言と数百サンプル重ならないように)
                end = t["end"] if finished and t["end"] < total else min(total, len(self.probs) * VAD_FRAME)
                if not finished:
                    # 言葉の途中で切ると、その言葉が前後どちらでも文字にならず (Whisper は最後の言葉を落とす)、
                    # 発言の音声には聞こえるのに文字に無い所ができる。息継ぎの所で切る
                    end = self.quiet_point(t["start"] + int(CUT_MIN_SEC * RATE), end)
                    cut_open = True
                done.append((t["start"], end))
            else:
                self.ongoing = t["start"]  # まだ話している
                break
        if done:
            segments = [(self.start + s, self.start + e, self.buf[s:e].copy()) for s, e in done]
            dropped = self.drop(done[-1][1])
            if self.ongoing is not None:
                self.ongoing = max(0, self.ongoing - dropped)
            elif cut_open:
                self.ongoing = 0  # (切った後も話している途中なので、話している途中の文字を消さない)
            self.checked = len(self.buf)
            return segments
        if not ts and total > 10 * RATE:
            # 無音が続くだけならためておく必要はない
            self.drop(total - RATE)
            self.checked = len(self.buf)
        return []


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    stdin = sys.stdin.buffer

    emit({"event": "loading", "step": "asr"})       # 日本語の音声認識モデルを読み込み中 (画面の準備中の表示)
    gpu = has_gpu()
    speed = {"setting": os.environ.get("GETTEXT_SPEED", "auto"), "now": ""}
    speed["now"] = resolve_speed(speed["setting"], gpu)
    jp = {"whisper": None, "fast": None, "device": "cuda" if gpu else "cpu"}
    jp_lock = threading.Lock()

    kotoba_lock, fast_lock = threading.Lock(), threading.Lock()  # それぞれのモデルの読み込みを 1 回にする

    def kotoba():
        """正確な日本語の音声認識 (kotoba-whisper)。初めて使うときに読み込む。"""
        model = jp["whisper"]
        if model is not None:
            return model
        with kotoba_lock:
            if jp["whisper"] is None:
                model, device_used = load_asr()
                with jp_lock:
                    jp["whisper"], jp["device"] = model, device_used
            return jp["whisper"]

    def fast():
        """速い日本語の音声認識 (ReazonSpeech)。初めて使うときに読み込む。"""
        model = jp["fast"]
        if model is not None:
            return model
        with fast_lock:
            if jp["fast"] is None:
                # 1 スレッドにする。2 スレッドだと待っている間も CPU を回し続け (onnxruntime の待ち方)、速さは同じなのに
                # CPU を約 2 コア分使っていた (実時間で流した計測: CPU 225% → 34%、遅れ・誤り率は同じ)
                model = fast_asr.FastAsr(threads=int(os.environ.get("GETTEXT_FAST_THREADS", "1")))
                with jp_lock:
                    jp["fast"] = model
            return jp["fast"]

    if speed["now"] == "light":
        fast()
    else:
        kotoba()
    device = jp["device"]
    emit({"event": "loading", "step": "speakers"})  # 話者の聞き分けのモデルを読み込み中
    speakers = Speakers()
    languages = ["ja"]            # 対象の言語 (1 つなら固定、複数ならその中から発言ごとに判定)
    hotwords = []                 # 用語の登録: 音声認識で優先する語 (人名・社名・専門用語)
    last_lang = {"win": "ja", "mic": "ja"}
    foreign = {"streak": 0, "told": False}
    multi = {"model": None, "state": ""}
    multi_lock = threading.Lock()

    def multilingual():
        """英語などの音声認識モデル (初めて使うときに読み込む)。無ければ None。"""
        if multi["state"] == "missing" and os.path.exists(os.path.join(MULTI_PATH, "model.bin")):
            multi["state"] = ""  # 後からセットアップした
        with multi_lock:
            if multi["model"] is None and multi["state"] != "missing":
                if not os.path.exists(os.path.join(MULTI_PATH, "model.bin")):
                    multi["state"] = "missing"
                    emit({"event": "status", "multilingual": "missing"})
                else:
                    emit({"event": "status", "multilingual": "loading"})
                    try:
                        multi["model"], _ = load_asr(MULTI_PATH, "en")
                        multi["state"] = "ready"
                    except Exception as e:
                        log("英語などの音声認識モデルを読み込めません:", e)
                        multi["state"] = "missing"
                    emit({"event": "status", "multilingual": multi["state"]})
            return multi["model"]

    def release_unused():
        """軽さ優先で使わないモデル (kotoba-whisper、日本語も選んでいるときの英語などのモデル) を手放してメモリを空ける。"""
        import gc
        with jp_lock:
            if speed["now"] == "light" and jp["whisper"] is not None:
                jp["whisper"] = None
                log("[speed] kotoba-whisper を手放しました")
        if speed["now"] == "light" and "ja" in languages and multi["model"] is not None and not multi_lock.locked():
            with multi_lock:
                multi["model"] = None
                multi["state"] = ""
                log("[speed] 英語などの音声認識モデルを手放しました")
        gc.collect()

    def choose_lang(audio, src):
        """発言の言語。対象が複数なら、その中から Whisper で判定する (はっきりしないときは直前の言語)。"""
        if len(languages) == 1:
            return languages[0]
        if speed["now"] == "light" and "ja" in languages:
            return "ja"  # 軽さ優先では日本語だけ (言語の判定に大きいモデルを使うと、CPU では 1 発言に 10 秒ほどかかるため)
        if multi["model"] is None and multi_lock.locked():
            return last_lang.get(src, languages[0])  # 英語などのモデルの読み込み中は待たない (記録が遅れないように)
        m = multilingual()
        if m is None:
            return "ja" if "ja" in languages else languages[0]
        previous = last_lang.get(src, languages[0])
        if len(audio) < SHORT_KEEP_SEC * RATE:
            return previous  # 1 秒に満たない発言 (「はい」など) は判定がぶれやすく、英語の「Thank you.」などにされるので直前の言語のまま
        _, _, all_probs = m.detect_language(audio)
        # 対象の言語の中だけから選ぶ (約 100 言語から選ぶと、日本語の発言を韓国語などと誤ることがある)
        probs = dict(all_probs)
        scores = {l: probs.get(l, 0.0) for l in languages}
        lang = max(scores, key=scores.get)
        confidence = scores[lang] / max(sum(scores.values()), 1e-9)
        # 短い発言は判定がぶれやすいので、はっきりしているときだけ切り替える
        if len(audio) < SHORT_DETECT_SEC * RATE:
            return lang if confidence >= 0.9 and probs.get(lang, 0.0) >= 0.8 else previous
        return lang if confidence >= 0.7 else previous

    # 確定させるときの探索の幅。GPU では 5、CPU では 2 (CPU で 5 だと時間がかかり、記録中の文字起こしが遅れる)
    beam_default = int(os.environ.get("GETTEXT_ASR_BEAM", "0")) or (5 if device == "cuda" else BEAM_CPU)

    def asr_text(audio, beam_size=None, src="win", lang=None):
        """音声を文字にする。(文字, Whisper の区間, 言語) を返す。"""
        beam_size = beam_size or beam_default
        lang = lang or choose_lang(audio, src)
        if lang == "ja" and speed["now"] == "light":
            return fast().transcribe(audio), [], "ja"
        m = multilingual() if lang != "ja" else None
        if m is None:
            # 日本語、または英語などのモデルが無いとき
            if speed["now"] == "light" or not kotoba_installed():
                return fast().transcribe(audio), [], "ja"
            m = kotoba()
            lang = "ja"
        model = jp["whisper"]
        # 直前の発言を initial_prompt で渡すと kotoba-whisper は文を途中で打ち切り、句読点も消えるので渡さない
        # 登録した用語は、英語などのモデル (Whisper large-v3 turbo) には hotwords として渡す。
        # 日本語の kotoba-whisper (蒸留モデル) は hotwords がほとんど効かず、かえって誤ることがあったので渡さず、
        # 文字にした後で読みを当てはめる (terms.py)
        segs, _ = m.transcribe(audio, language=lang, beam_size=beam_size, vad_filter=False,
                               condition_on_previous_text=False, without_timestamps=True,
                               hotwords=" ".join(hotwords[:HOTWORDS_MAX]) if hotwords and m is not model else None)
        segs = list(segs)
        return "".join(s.text for s in segs).strip(), segs, lang

    # まとめて認識し直す (前後の音声が文脈になって正確になる)。CPU だけのときは時間がかかるので、測った結果で決める
    reasr = not os.environ.get("GETTEXT_NO_REASR")

    def reasr_text(audio, src="win"):
        # 記録中に処理が遅れているとき、ファイルの文字起こしを中止したときは認識し直さない
        if not reasr or stats["lag"] >= REASR_MAX_LAG or cancel_file.is_set() or speed["now"] == "light":
            return None  # 軽さ優先では認識し直さない (速いモデルは 1 発言ずつでも正確で、つなげても良くならない)
        return asr_text(audio, src=src)[0]

    refiner = Refiner(reasr_text, emit, log,
                      speaker_of=lambda seg_id: speakers.items.get(seg_id),
                      # 相手側は、声が集まって話者の聞き分けが始まってからまとめる (マイクは 1 人なのでいつでもよい)
                      can_merge=lambda src: src == "mic" or len(speakers.wins) >= 2 * speaker_stats.MIN_CLUSTER)
    summarizer = Summarizer(refiner.llm, emit, log)  # 議事録の要約 (文脈補正と同じ AI を使う)
    ai_translator = AiTranslator(refiner.llm, emit, log)  # 議事録の日本語訳 (AI で、前後の発言と用語を使って訳す)
    emit({"ready": True, "device": device, "speed": speed["now"], "gpu": gpu})
    emit({"event": "status", "speed": speed["now"], "gpu": gpu, "fast": fast_asr.installed()})
    # 文脈補正の AI は大きい (約 4GB) ので、最初の補正のときに読み込む (GPU のメモリを先に使い切らないため)
    emit({"event": "status", "llm": refiner.llm.state})
    next_id = [1]
    stats = {"win": 0.0, "mic": 0.0, "segments": 0, "asr_ms": [], "since": time.time(), "lag": 0.0}

    inbox = queue.Queue()
    cancel_file = threading.Event()

    def reader():
        while True:
            header = stdin.readline()
            if not header:
                inbox.put(None)
                return
            if not header.strip():
                continue
            try:
                msg = json.loads(header)
                if msg.get("cmd") == "cancel":
                    cancel_file.set()  # ファイルの処理中でもすぐ伝わるよう、キューを通さない
                    continue
                if msg.get("cmd") == "file":
                    cancel_file.clear()
                # 要約は裏で作るので、ファイルの処理中でも受け付ける
                if msg.get("cmd") == "summarize":
                    summarizer.start(msg.get("job", 0), msg.get("text", ""))
                    continue
                if msg.get("cmd") == "translate":
                    ai_translator.start(msg.get("job", 0), msg.get("items") or [])
                    continue
                if msg.get("cmd") == "summary_cancel":
                    summarizer.cancel.set()
                    continue
                # 記録した音声 (WAV) を小さな .m4a にする (裏で行う)
                if msg.get("cmd") == "encode":
                    threading.Thread(target=encode_audio, args=(msg["src"], msg["dst"]), daemon=True).start()
                    continue
                # 録画した動画に字幕を焼き込む (裏で行う。長い動画は数分かかる)
                if msg.get("cmd") == "burn":
                    BURN_CANCEL.discard(msg["dst"])  # (前に同じ場所へ作ったときの、間に合わなかった中止を持ち越さない)
                    threading.Thread(target=burn_subtitles, args=(msg["src"], msg["dst"], msg.get("cues") or []), daemon=True).start()
                    continue
                if msg.get("cmd") == "burn_cancel":
                    BURN_CANCEL.add(msg.get("dst", ""))
                    continue
                if msg.get("cmd") == "audio":
                    n = msg["len"]
                    data = bytearray()
                    while len(data) < n:
                        chunk = stdin.read(n - len(data))
                        if not chunk:
                            inbox.put(None)
                            return
                        data += chunk
                    msg["data"] = bytes(data)
                msg["t"] = time.time()
                inbox.put(msg)
            except Exception as e:
                emit({"event": "error", "message": f"{type(e).__name__}: {e}"})

    threading.Thread(target=reader, daemon=True).start()

    sources = {"win": Source("win"), "mic": Source("mic")}
    recent_win = []  # 最近のウィンドウ側の発言 (マイクが拾った相手の声を重複として捨てるため)

    voicebook = VoiceBook(log)   # 話者の声の登録 (名前を付けた声を覚え、次の会議で名前を付ける)
    voice_reported = {}          # 話者の番号 → 知らせた名前
    enrolled_here = set()        # この会議で名前を付けて覚えさせた話者の番号 (取り消さない)

    def check_voices():
        """覚えた声に似た話者がいれば知らせる (同じ番号に同じ名前は 1 回だけ)。"""
        if not voicebook.voices or not speakers.centroids:
            return
        counts = {}
        for w in speakers.wins:
            counts[w["label"]] = counts.get(w["label"], 0) + 1
        matches = voicebook.match(speakers.centroids, counts)
        for label, (name, sim) in matches.items():
            if voice_reported.get(label) != name:
                voice_reported[label] = name
                log(f"[voices] 話者{label} は覚えた声に似ています (類似度 {sim})")
                emit({"event": "voice_match", "speaker": int(label), "name": name, "sim": sim})
        # 声が集まって、ほかの話者の方が似ていると分かったら取り消す (画面は、自動で付けた名前だけ元に戻す)
        for label in [l for l in voice_reported if l not in matches and l in speakers.centroids]:
            if voice_reported[label] is not None and label not in enrolled_here:
                voice_reported[label] = None
                emit({"event": "voice_match", "speaker": int(label), "name": None})

    def new_session():
        """新しい会議 (reset・ファイルの文字起こし) のために、会議ごとの状態を消す。"""
        nonlocal sources
        sources = {"win": Source("win"), "mic": Source("mic")}
        recent_win.clear()
        speakers.reset()
        refiner.reset()
        voice_reported.clear()
        enrolled_here.clear()
        foreign.update(streak=0, told=False)
        first = "ja" if "ja" in languages else languages[0]
        last_lang.update(win=first, mic=first)

    def emit_changes(changes):
        if changes:
            emit({"event": "respeaker", "speakers": {str(k): v for k, v in changes.items()}})
        check_voices()

    def transcribe(src, start, end, audio):
        # 相手側の音声は、途中で話者が替わっていれば分けてから文字にする (声の特徴は聞き分けに使い回す)
        if src != "mic":
            for s, e, embs in speakers.split(audio):
                transcribe_part(src, start + s, start + e, audio[s:e], embs)
            return
        transcribe_part(src, start, end, audio)

    def transcribe_part(src, start, end, audio, embs=None):
        t_start = time.time()
        text, segs, lang = asr_text(audio, src=src)
        stats["asr_ms"].append((time.time() - t_start) * 1000)
        if looks_hallucinated(text, segs, audio):
            return
        last_lang[src] = lang
        if languages == ["ja"] and not foreign["told"]:
            # 3 発言続けて日本語以外らしければ、言語の設定を見直すよう知らせる (1 回だけ)
            foreign["streak"] = foreign["streak"] + 1 if looks_foreign(text) else 0
            if foreign["streak"] >= 3:
                foreign["told"] = True
                emit({"event": "status", "foreign": True})
        t0, t1 = start / RATE, end / RATE
        if src == "mic":
            for ws, we, wt in recent_win:
                overlap = min(t1, we) - max(t0, ws)
                if overlap > -1.0 and difflib.SequenceMatcher(None, text, wt).ratio() > 0.5:
                    return  # スピーカーの音をマイクが拾っただけ
            speaker = 0
        seg_id = next_id[0]
        next_id[0] += 1
        if src != "mic":
            speaker, changes = speakers.assign(audio, seg_id, embs)
            emit_changes(changes)
            recent_win.append((t0, t1, text))
            del recent_win[:-20]
        emit({"event": "segment", "id": seg_id, "src": src, "start": round(t0, 2), "end": round(t1, 2),
              "speaker": speaker, "text": text, "lang": lang})
        stats["segments"] += 1
        refiner.add(src, speaker, seg_id, start, end, audio, text)

    partial_on = [not os.environ.get("GETTEXT_NO_PARTIAL")]  # 話している途中の暫定表示 (config の "partial" で切り替える)
    partial_at = {"win": 0.0, "mic": 0.0}
    partial_cost = {"win": 0.0, "mic": 0.0}
    partial_shown = {"win": False, "mic": False}

    def clear_partial(src):
        if partial_shown[src]:
            partial_shown[src] = False
            emit({"event": "partial", "src": src, "speaker": 0 if src == "mic" else -1, "text": ""})

    def show_partial(src, s):
        """話している途中の音声を速い設定 (ビーム幅 1) で文字にして暫定表示する。"""
        audio = s.ongoing_audio()
        if audio is None or not partial_on[0]:
            clear_partial(src)
            return
        now = time.time()
        # 間隔は、前回の暫定表示にかかった時間の 4 倍以上にする (遅い PC で暫定表示が CPU を使い切らないように)
        if now - partial_at[src] < max(PARTIAL_INTERVAL_SEC, partial_cost[src] * 4):
            return
        partial_at[src] = now
        lang = last_lang[src] if len(languages) > 1 else languages[0]
        if speed["now"] == "light" and "ja" in languages:
            text, segs = fast().partial(audio), []
        else:
            text, segs, _ = asr_text(audio[-int(MAX_SEGMENT_SEC * RATE):], beam_size=1, src=src, lang=lang)
        partial_cost[src] = time.time() - now
        if looks_hallucinated(text, segs, audio):
            return
        speaker = 0 if src == "mic" else (speakers.last or -1)
        partial_shown[src] = True
        emit({"event": "partial", "src": src, "speaker": speaker, "text": text})

    def process(final=False):
        for src, s in sources.items():
            if final or s.ready():
                segments = s.take_segments(final)
                if segments:
                    clear_partial(src)
                for start, end, audio in segments:
                    try:
                        transcribe(src, start, end, audio)
                    except Exception as e:
                        emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
            if not final and stats["lag"] < LAG_SKIP_PARTIAL_SEC:
                try:
                    show_partial(src, s)
                except Exception as e:
                    emit({"event": "error", "message": f"暫定表示: {type(e).__name__}: {e}"})
            else:
                clear_partial(src)

    def report():
        """処理の遅れを知らせ、10 秒ごとに動作の記録を出す (話した内容は含めない)。"""
        now = time.time()
        if now - stats["since"] < 10:
            return
        asr = stats["asr_ms"]
        log(f"[stats] audio win={stats['win']:.1f}s mic={stats['mic']:.1f}s segments={stats['segments']} "
            f"asr_avg={(sum(asr) / len(asr)) if asr else 0:.0f}ms asr_max={max(asr) if asr else 0:.0f}ms "
            f"lag={stats['lag']:.1f}s llm={refiner.llm.state}")
        if speaker_stats.LAST_INFO:
            log(f"[speakers] {speaker_stats.LAST_INFO}")  # 窓の数・推定した人数・重心どうしの類似度 (話した内容は含まない)
        stats.update(win=0.0, mic=0.0, segments=0, asr_ms=[], since=now)

    lag_reported = [0.0]

    def transcribe_file(path):
        """
        動画・音声ファイルを先頭から順に、録音と同じ流れ (区切り → 文字 → 話者) で処理する。
        話している途中の暫定表示は出さず、できるだけ速く進める。
        """
        new_session()  # (中止の印は、命令を受け取ったときに消している)
        try:
            audio = decode_int16(path)  # PyAV (FFmpeg) で映像を除いて 16kHz モノラルにする
        except Exception as e:
            message = f"ファイルを読み込めませんでした: {e}" if isinstance(e, ValueError) else f"ファイルを読み込めませんでした: {type(e).__name__}: {e}"
            emit({"event": "file_done", "duration": 0, "cancelled": False, "error": message})
            return
        try:
            run_file(audio)
        except Exception as e:
            log("[file] 失敗:", repr(e))
            emit({"event": "file_done", "duration": round(len(audio) / RATE, 1), "cancelled": False,
                  "error": f"文字起こしの途中で失敗しました: {type(e).__name__}: {e}"})
        finally:
            cancel_file.clear()

    def run_file(audio):
        total = len(audio) / RATE
        log(f"[file] {total:.1f}s を処理します")
        started, shown = time.time(), 0.0
        s = sources["win"]
        step = 2 * RATE
        for k in range(0, len(audio), step):
            if cancel_file.is_set():
                break
            s.add_samples(audio[k:k + step].astype(np.float32) / 32768.0)
            for start, end, seg in s.take_segments():
                try:
                    transcribe("win", start, end, seg)
                except Exception as e:
                    emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
            refiner.tick()
            report()  # ファイルの処理中も、動作の記録 (話者の分け直しの様子など) を残す
            if time.time() - shown >= 0.5:
                shown = time.time()
                emit({"event": "progress", "done": round(min(total, (k + step) / RATE), 1), "total": round(total, 1)})
        cancelled = cancel_file.is_set()
        if not cancelled:
            for start, end, seg in s.take_segments(final=True):
                try:
                    transcribe("win", start, end, seg)
                except Exception as e:
                    emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
        emit_changes(speakers.finalize())
        refiner.tick(final=True)  # 中止したときは認識し直さない (reasr_text が休む)
        log(f"[file] 完了 {time.time() - started:.1f}s (音声 {total:.1f}s){' 中止' if cancelled else ''}")
        if not cancelled:
            emit({"event": "progress", "done": round(total, 1), "total": round(total, 1)})
        emit({"event": "file_done", "duration": round(total, 1), "cancelled": cancelled})

    def handle(msg):
        nonlocal sources
        cmd = msg.get("cmd")
        if cmd == "audio":
            src = sources.get(msg.get("src"))
            if src is not None:
                src.add(msg["data"])
                stats[msg["src"]] += len(msg["data"]) / 2 / RATE
            stats["lag"] = time.time() - msg["t"]
        elif cmd == "flush":
            try:
                process(final=True)
                emit_changes(speakers.finalize())
                refiner.tick(final=True)
            finally:
                emit({"event": "flushed", "seq": msg.get("seq", 0)})
        elif cmd == "file":
            SESSION[0] = msg.get("session", 0)
            transcribe_file(msg["path"])
        elif cmd == "reset":
            SESSION[0] = msg.get("session", 0)
            cancel_file.clear()  # ファイルの終わりの直後に届いた中止を、次の記録に持ち越さない
            new_session()
        elif cmd == "restart":
            SESSION[0] = msg.get("session", 0)
            cancel_file.clear()
            sources = {"win": Source("win"), "mic": Source("mic")}
            recent_win.clear()
        elif cmd == "config":
            if "sensitivity" in msg:
                emit_changes(speakers.set_sensitivity(float(msg["sensitivity"])))
            if "merge" in msg:
                emit_changes(speakers.merge(int(msg["merge"][0]), int(msg["merge"][1])))
            if "speakers" in msg:
                emit_changes(speakers.set_expected(msg["speakers"]))  # 人数 (0 = 自動)
            if isinstance(msg.get("enroll"), dict):
                # 画面で名前を付けた話者の声を覚える
                label, name = int(msg["enroll"].get("speaker", -1)), str(msg["enroll"].get("name", "")).strip()
                centroid = speakers.centroids.get(label)
                if centroid is None:
                    embs = [w["emb"] for w in speakers.wins if w["label"] == label]
                    centroid = np.mean(embs, axis=0) if len(embs) >= 4 else None
                if name and centroid is not None and label != 0:
                    try:
                        n = voicebook.enroll(name, centroid)
                    except Exception as e:
                        emit({"event": "voices", "count": len(voicebook.voices), "error": f"声を保存できませんでした: {e}"})
                    else:
                        voice_reported[label] = name
                        enrolled_here.add(label)
                        log(f"[voices] 話者{label} の声を覚えました (覚えた声 {n} 人)")
                        emit({"event": "voices", "count": n, "enrolled": name})
                elif name and label != 0:
                    emit({"event": "voices", "count": len(voicebook.voices),
                          "error": f"「{name}」さんの声はまだ少ないので覚えられませんでした (もう少し話してから名前を付け直してください)"})
            if "forget" in msg:
                try:
                    voicebook.forget(msg["forget"] or None)
                    emit({"event": "voices", "count": len(voicebook.voices)})
                except Exception as e:
                    emit({"event": "voices", "count": len(voicebook.voices), "error": f"覚えた声を消せませんでした: {e}"})
            if msg.get("voices_reload"):
                voicebook.reload()
            if msg.get("speed") in ("auto", "light", "accurate"):
                speed["setting"] = msg["speed"]
                now = resolve_speed(speed["setting"], gpu)
                if now != speed["now"]:
                    speed["now"] = now
                    log(f"[speed] {now}")
                    # 使うモデルを先に読み込んでおく (次の発言を待たせないように)
                    threading.Thread(target=fast if now == "light" else kotoba, daemon=True).start()
                    if now == "light":
                        release_unused()
                emit({"event": "status", "speed": speed["now"], "gpu": gpu, "fast": fast_asr.installed()})
            if "partial" in msg:
                partial_on[0] = bool(msg["partial"])
            refiner.llm_on = bool(msg.get("llm", refiner.llm_on))
            if isinstance(msg.get("terms"), list):
                hot, rules = parse_terms(msg["terms"])
                hotwords[:] = [re.sub(r"\s*[（(].*?[）)]$", "", h) for h in hot]  # 「九条(くじょう)」の読みは除く
                set_terms(hot, rules)
                refiner.llm.terms = list(hotwords)
                summarizer.terms = list(hotwords)
                ai_translator.terms = list(hotwords)
                log(f"[terms] 用語 {len(hot)} 件、置き換え {len(rules)} 件")
            if isinstance(msg.get("languages"), list) and msg["languages"]:
                languages[:] = [str(l) for l in msg["languages"]]
                foreign.update(streak=0, told=False)
                first = "ja" if "ja" in languages else languages[0]
                if last_lang["win"] not in languages:
                    last_lang.update(win=first, mic=first)
                # 先に読み込んでおく (軽さ優先で日本語も選んでいるときは、日本語だけを文字にするので読み込まない)
                if languages != ["ja"] and not (speed["now"] == "light" and "ja" in languages):
                    threading.Thread(target=multilingual, daemon=True).start()

    while True:
        try:
            batch = [inbox.get(timeout=0.25)]
        except queue.Empty:
            batch = []
            stats["lag"] = 0.0
        # たまっている分をまとめて取り込む
        while True:
            try:
                batch.append(inbox.get_nowait())
            except queue.Empty:
                break
        closing = None in batch
        if closing:
            batch = batch[:batch.index(None)]
        for msg in batch:
            try:
                handle(msg)
            except Exception as e:
                log("[error] 命令を処理できませんでした:", msg.get("cmd"), repr(e))
                emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
                if msg.get("cmd") == "file":
                    emit({"event": "file_done", "duration": 0, "cancelled": False, "error": f"{type(e).__name__}: {e}"})
        try:
            process()
            refiner.tick()
        except Exception as e:
            log("[error] 文字起こしの処理に失敗しました:", repr(e))
            emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
        if closing:
            break
        # 遅れが大きいとき / 解消したときに知らせる
        lag = stats["lag"]
        if (lag >= 3 and abs(lag - lag_reported[0]) >= 1) or (lag < 1 and lag_reported[0] >= 3):
            lag_reported[0] = lag if lag >= 3 else 0.0
            emit({"event": "status", "lag": round(lag_reported[0], 1)})
        # 文脈による補正をオフにして、要約・AI の訳にも使っていなければ、AI のモデルを手放してメモリを空ける
        if not refiner.llm_on and refiner.llm.unload_if_idle(LLM_IDLE_SEC):
            log("[llm] 使っていないので文脈補正の AI を手放しました")
        report()


if __name__ == "__main__":
    main()
