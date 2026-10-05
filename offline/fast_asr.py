"""GPU の無い PC 向けの速い日本語の音声認識 (ReazonSpeech k2 v2、sherpa-onnx)。

kotoba-whisper は CPU だけだと 1 発言に約 10 秒かかる (音声を必ず 30 秒分に広げて処理するため) が、
このモデルは 1 発言 0.1 秒ほどで文字にでき、記録中でも遅れない。句読点を出さないので、間 (ま) と文末の言い回しから付ける。
"""
import os
import re

import numpy as np

import app_data

RATE = 16000
PATH = os.path.join(app_data.MODELS_DIR, "reazonspeech-k2-v2")
# 前後に足す無音 (秒)。足さないと長い発言の初めが抜けることがあり、足し方で抜け方が変わるので 2 通り試して長い方を使う
PADDINGS = ((0.3, 0.5), (0.6, 0.3))
# 文の終わりの言い回し。この後に続きがあれば「。」で区切る (「ですが」「ますので」「ですね」などは続きなので区切らない)
SENTENCE_END = re.compile(
    r"(ませんでした|ました|ません|ます|でした|です|でしょう|ましょう|ください)(か)?"
    r"(?=(?!が|けど|けれど|ので|から|し[、て]|ね|よ|と|って|な|わ|か|。|、|？|！)\S)")
# 発言の頭の返事の後に続きがあれば「、」(「はいアンケートの案は…」→「はい、アンケートの案は…」)
REPLY_START = re.compile(r"^((?:はい)+|ええ|いいえ)(?=[^、。？！?!ーとっ])")
# 息継ぎで分けた切れ目の前が文の終わりなら「。」、そうでなければ「、」
PIECE_END = re.compile(r"(ませんでした|ました|ません|ます|でした|です|でしょう|ましょう|ください)か?$")
LONG_SEC = 6.0    # これより長い音声だけ、無音の足し方を 2 通り試す (初めが抜けるのは長い発言のときだけなので)
PARTIAL_SEC = 8.0  # 話している途中の暫定表示は、直近のこの長さだけを文字にする (長い発言で毎回全体を認識し直さない)
PAUSE_MIN = 0.25  # この長さ以上の無音 (息継ぎ) で分ける (秒)
PIECE_MIN = 0.6   # 分けた切れ端の最短 (秒)。短すぎると前後の文脈が無くなり聞き間違えやすい


FILES = ("encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt")


def installed():
    """モデルのファイルがそろっているか (欠けていると読み込みで失敗するので、すべて確かめる)。"""
    return all(os.path.isfile(os.path.join(PATH, name)) and os.path.getsize(os.path.join(PATH, name)) > 0 for name in FILES)


class FastAsr:
    def __init__(self, threads=1):
        import sherpa_onnx
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
            encoder=os.path.join(PATH, "encoder-epoch-99-avg-1.int8.onnx"),
            decoder=os.path.join(PATH, "decoder-epoch-99-avg-1.int8.onnx"),
            joiner=os.path.join(PATH, "joiner-epoch-99-avg-1.int8.onnx"),
            tokens=os.path.join(PATH, "tokens.txt"),
            num_threads=threads, sample_rate=RATE, feature_dim=80, decoding_method="greedy_search")

    def _decode(self, audio, pre, post):
        padded = np.concatenate([np.zeros(int(pre * RATE), np.float32), audio.astype(np.float32),
                                 np.zeros(int(post * RATE), np.float32)])
        stream = self.recognizer.create_stream()
        stream.accept_waveform(RATE, padded)
        self.recognizer.decode_stream(stream)
        r = stream.result
        return list(r.tokens), [t - pre for t in r.timestamps]

    def _decode_text(self, audio, pre, post):
        tokens, _ = self._decode(audio, pre, post)
        return "".join(tokens).replace("▁", " ").strip()

    def _text(self, audio, dual=None):
        best = ""
        if dual is None:
            dual = len(audio) >= LONG_SEC * RATE
        for pre, post in (PADDINGS if dual else PADDINGS[:1]):
            tokens, _ = self._decode(audio, pre, post)
            text = "".join(tokens).replace("▁", " ").strip()
            if len(text) > len(best):
                best = text
        return best

    def partial(self, audio):
        """話している途中の暫定表示用 (直近 PARTIAL_SEC 秒を 1 回だけ認識する。速さ優先)。"""
        return punctuate_text(self._text(audio[-int(PARTIAL_SEC * RATE):], dual=False))

    def transcribe_whole(self, audio):
        """音声を分けずに文字にする (句読点は文の終わりの言い回しからだけ付ける)。"""
        return punctuate_text(self._text(audio))

    def transcribe(self, audio):
        """音声 (float32, 16kHz) を文字にする。発言の中の息継ぎで音声を分けて文字にし、切れ目に「、」か「。」を付ける
        (このモデルは句読点を出さず、文字の出た時刻もずれるので、音の切れ目で区切る)。"""
        cuts = pauses(audio)
        if not cuts:
            return punctuate_text(self._text(audio))
        bounds = [0] + cuts + [len(audio)]
        pieces = [t for t in (self._text(audio[a:b]) for a, b in zip(bounds, bounds[1:])) if t]
        # 分けると文字が抜けることがあるので、分けない方 (確かめるだけなので 1 回だけ認識) より短くなったら分けない方を使う
        whole = self._text(audio, dual=False)
        if len("".join(pieces)) < len(whole) * 0.95:
            if len(audio) >= LONG_SEC * RATE:
                # 長い発言は、もう 1 通りの無音の足し方でも読み、長い方を使う (1 通り目は読んだばかり)
                other = self._decode_text(audio, *PADDINGS[1])
                whole = other if len(other) > len(whole) else whole
            return punctuate_text(whole)
        text = ""
        for piece in pieces:
            if text:
                text += "。" if PIECE_END.search(text) else "、"
            text += piece
        return punctuate_text(text)


def pauses(audio):
    """発言の中の息継ぎ (PAUSE_MIN 秒以上の無音) の真ん中の位置 (サンプル)。"""
    frame = RATE // 50
    n = len(audio) // frame
    if n < 10:
        return []
    energy = np.sqrt(np.mean(audio[:n * frame].astype(np.float32).reshape(n, frame) ** 2, axis=1))
    loud = np.percentile(energy, 90)
    # 声の大きさに対して十分小さく、背景の雑音よりは少し大きいところまでを無音とみなす
    threshold = min(max(loud * 0.1, np.percentile(energy, 10) * 1.5), loud * 0.3)
    quiet = energy < threshold
    cuts, i = [], 0
    while i < n:
        if not quiet[i]:
            i += 1
            continue
        j = i
        while j < n and quiet[j]:
            j += 1
        if i > 0 and j < n and (j - i) * frame >= PAUSE_MIN * RATE:
            cuts.append((i + j) // 2 * frame)
        i = j
    out, last = [], 0
    for c in cuts:
        if c - last >= PIECE_MIN * RATE and len(audio) - c >= PIECE_MIN * RATE:
            out.append(c)
            last = c
    return out


# 文の途中で終わる言い回し (話の間 (ま) で発言が区切られて、続きが次の発言になったとき)。最後に「。」を付けない
CONTINUES = re.compile(r"(?<!こんにち)(?<!こんばん)(が|は|を|に|へ|で|と|も|や|て|し|ので|のに|けど|けれど|けれども|から|ば|たら|なら|って)$")


def punctuate_text(text):
    """文の終わりの言い回しの後に続きがあれば「。」、発言の頭の返事の後に「、」、最後にも「。」を付ける
    (文の途中で終わっているとき (「音が」「〜ので」など) は、続きが次の発言なので付けない)。"""
    text = SENTENCE_END.sub(lambda m: m.group(0) + "。", text)
    text = REPLY_START.sub(lambda m: m.group(1) + "、", text)
    if text and text[-1] not in "、。？！?!" and not (CONTINUES.search(text) and len(text) >= 4):
        text += "。"
    return text
