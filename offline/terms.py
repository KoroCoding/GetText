"""用語の登録: 読みが同じで書き方の違う語を、登録した表記に直す (例: しののめ → 東雲、高取 → 鷹取)。

kotoba-whisper は人名・社名などを、ひらがなや別の漢字で書くことが多い (hotwords で渡してもほとんど変わらなかった)。
そこで、文字にした文と登録した用語をそれぞれ読み (カタカナ) にして、語の区切りに合わせて読みが一致するところを置き換える。
読みは Janome (形態素解析、Apache-2.0) で調べる。用語には「九条(くじょう)」のように読みを添えられる。
"""
import re
import threading

_tokenizer = None
_init_lock = threading.Lock()
_lock = threading.RLock()


def tokenizer():
    """Janome (入っていなければ None)。初回だけ読み込む (1〜2 秒)。"""
    global _tokenizer
    with _init_lock:
        if _tokenizer is None:
            try:
                from janome.tokenizer import Tokenizer
                _tokenizer = Tokenizer()
            except Exception:
                _tokenizer = False
        return _tokenizer or None


def to_katakana(s):
    return "".join(chr(ord(c) + 0x60) if "ぁ" <= c <= "ゖ" else c for c in s)


_VOWEL = {}
for row, vowel in (("アカサタナハマヤラワガザダバパャァ", "ア"), ("イキシチニヒミリギジヂビピィ", "イ"),
                   ("ウクスツヌフムユルグズヅブプュゥヴ", "ウ"), ("エケセテネヘメレゲゼデベペェ", "エ"),
                   ("オコソトノホモヨロヲゴゾドボポョォ", "オ")):
    for c in row:
        _VOWEL[c] = vowel


def normalize(reading):
    """読みのゆれをそろえる: 長音 (ー) は前の母音に、オウ・エイの長音も表記どおりに比べられるよう ー を母音にする。"""
    out = []
    for c in to_katakana(reading):
        if c == "ー" and out:
            out.append(_VOWEL.get(out[-1], ""))
        elif "ァ" <= c <= "ヶ" or c == "ー":
            out.append(c)
    return "".join(out)


def reading_of(text):
    tk = tokenizer()
    if tk is None:
        return None
    parts = []
    with _lock:  # Janome を複数のスレッドから同時に使わない
        for t in tk.tokenize(text):
            r = t.reading if t.reading and t.reading != "*" else t.surface
            parts.append(r)
    return normalize("".join(parts))


class TermMatcher:
    """登録した用語の読みを覚えておき、文の中の読みが一致する語を置き換える。"""

    MIN_READING = 3  # 短い読み (2 文字以下) は別の語と取り違えやすいので使わない

    def __init__(self, terms):
        self.terms = []  # (表記, 読み)
        for raw in terms:
            m = re.match(r"^(.+?)\s*[（(]\s*([ぁ-ゖァ-ヺー]+)\s*[）)]$", raw)
            surface, reading = (m.group(1), normalize(m.group(2))) if m else (raw, None)
            if reading is None:
                reading = reading_of(surface)
            if reading and len(reading) >= self.MIN_READING:
                self.terms.append((surface, reading))
        self.terms.sort(key=lambda x: -len(x[1]))  # 長い用語を先に当てる

    def fix(self, text):
        if not self.terms or not text:
            return text
        with _lock:  # Janome を複数のスレッドから同時に使わない
            return self._fix(text)

    def _fix(self, text):
        tk = tokenizer()
        if tk is None:
            return text
        # 漢字などを含む語は語ごと、かなだけの語は 1 文字ずつ (「とくじょうさん」の「くじょう」も当てられるように)
        tokens = []
        for t in tk.tokenize(text):
            if re.fullmatch(r"[ぁ-ゖァ-ヺー]+", t.surface):
                for c in t.surface:
                    k = to_katakana(c)
                    if k == "ー" and tokens:
                        k = _VOWEL.get(tokens[-1][1][-1:] or "", "")
                    tokens.append((c, k))
            else:
                tokens.append((t.surface, normalize(t.reading if t.reading and t.reading != "*" else t.surface)))
        out, i = [], 0
        while i < len(tokens):
            done = False
            for surface, reading in self.terms:
                # 語の区切りから始めて、読みがちょうど一致する語の並びを探す (小さい「ゃゅょ」などからは始めない)
                if tokens[i][0] in "ぁぃぅぇぉゃゅょっゎァィゥェォャュョッヮー":
                    break
                acc, j = "", i
                while j < len(tokens) and len(acc) < len(reading):
                    acc += tokens[j][1]
                    j += 1
                if acc == reading:
                    span = "".join(t[0] for t in tokens[i:j])
                    out.append(surface if span != surface else span)
                    i, done = j, True
                    break
            if not done:
                out.append(tokens[i][0])
                i += 1
        return "".join(out)
