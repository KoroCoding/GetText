"""GetText オフライン翻訳サーバー (CTranslate2)。

GetText から子プロセスとして起動され、標準入出力の JSON 1 行で通信する。
  起動完了: {"ready": true, "device": "cpu", "models": [...]}
  要求:     {"id": 1, "texts": ["Hello", ...]}
  応答:     {"id": 1, "translations": ["こんにちは", ...], "ms": 42}
英語は FuguMT、それ以外の言語は NLLB-200 で日本語に翻訳する。
標準入力が閉じられたら (GetText が終了したら) 自動で終了する。
"""
import json
import os
import re
import sys
import threading
import time

import app_data
import gpu_paths  # noqa: F401  (GPU 用の DLL を CTranslate2 より先に登録する)
import ctranslate2

MODELS_DIR = app_data.MODELS_DIR
THREADS = max(1, min(8, (os.cpu_count() or 4) // 2))


def log(*args):
    print(*args, file=sys.stderr, flush=True)


def load_translator(path):
    """GPU が使えれば GPU、だめなら CPU で読み込む。"""
    if ctranslate2.get_cuda_device_count() > 0:
        try:
            t = ctranslate2.Translator(path, device="cuda", compute_type="int8_float16")
            t.translate_batch([["▁a", "</s>"]], max_decoding_length=2)  # CUDA ライブラリの有無を確認
            return t, "cuda"
        except Exception as e:  # cuBLAS / cuDNN が無いなど
            log("CUDA を使えないため CPU で動かします:", e)
    return ctranslate2.Translator(path, device="cpu", compute_type="int8", intra_threads=THREADS), "cpu"


# ───────── 言語判定 ─────────

STOPWORDS = {
    "eng_Latn": "the and of to is in that it for you with on are this be was have not as at by from or can will your",
    "fra_Latn": "le la les de des et est un une que qui dans pour pas vous nous sur avec ce il elle au du sont",
    "deu_Latn": "der die das und ist nicht ein eine zu den mit sich des auf für von dem sie es ich wir sind auch",
    "spa_Latn": "el la los las de que y en un una es por con para no se lo del al como más pero sus",
    "ita_Latn": "il lo la gli le di che e è un una per non con del della sono si ma come anche al",
    "por_Latn": "o a os as de que e do da em um uma para com não se na no por mais como mas são",
    "nld_Latn": "de het een en van is dat niet te in op met voor zijn je ik ze er maar ook",
    "ind_Latn": "yang dan di ini itu dengan untuk tidak dari dalam akan pada ke ada saya adalah juga",
    "tur_Latn": "ve bir bu da de için ile çok ne daha gibi olarak ama değil var ben sen",
    "pol_Latn": "i w nie na się z że to do jest jak co ale po tak od za",
    "swe_Latn": "och att det som en på är av för med till den har inte jag de om",
}
STOPWORDS = {k: set(v.split()) for k, v in STOPWORDS.items()}
TRADITIONAL = set("這個們來說時會對過還點經國學與為發開關體實應樣們該廣電話機車馬門問間")


def detect(text):
    counts = {}
    for ch in text:
        o = ord(ch)
        if 0xAC00 <= o <= 0xD7AF or 0x1100 <= o <= 0x11FF:
            k = "kor_Hang"
        elif 0x4E00 <= o <= 0x9FFF or 0x3400 <= o <= 0x4DBF:
            k = "han"
        elif 0x0400 <= o <= 0x04FF:
            k = "cyr"
        elif 0x0600 <= o <= 0x06FF:
            k = "arb_Arab"
        elif 0x0E00 <= o <= 0x0E7F:
            k = "tha_Thai"
        elif 0x0370 <= o <= 0x03FF:
            k = "ell_Grek"
        elif 0x0590 <= o <= 0x05FF:
            k = "heb_Hebr"
        elif 0x0900 <= o <= 0x097F:
            k = "hin_Deva"
        elif ch.isalpha():
            k = "latin"
        else:
            continue
        counts[k] = counts.get(k, 0) + 1
    if not counts:
        return "eng_Latn"
    script = max(counts, key=counts.get)
    if script == "han":
        return "zho_Hant" if sum(ch in TRADITIONAL for ch in text) >= 2 else "zho_Hans"
    if script == "cyr":
        return "ukr_Cyrl" if re.search("[іїєґІЇЄҐ]", text) else "rus_Cyrl"
    if script != "latin":
        return script
    if re.search("[ơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹđ]", text.lower()):
        return "vie_Latn"
    words = re.findall(r"[a-zà-öø-ÿąćęłńóśźżçğıöşü]+", text.lower())
    scores = {lang: sum(w in sw for w in words) for lang, sw in STOPWORDS.items()}
    best = max(scores, key=scores.get)
    return best if scores[best] > scores["eng_Latn"] else "eng_Latn"


# ───────── 文分割 ─────────

SENTENCE_END = re.compile(r"(?<=[.!?。！？])\s+(?=\S)|(?<=[。！？])")


def split_sentences(text):
    parts = [p.strip() for p in SENTENCE_END.split(text)]
    return [p for p in parts if p]


# ───────── モデル ─────────

class FuguMT:
    def __init__(self, path):
        import sentencepiece as spm
        # 場所に日本語 (ユーザー名など) があると SentencePiece が開けないので、中身を読んで渡す
        with open(os.path.join(path, "source.spm"), "rb") as f:
            self.src = spm.SentencePieceProcessor(model_proto=f.read())
        with open(os.path.join(path, "target.spm"), "rb") as f:
            self.tgt = spm.SentencePieceProcessor(model_proto=f.read())
        self.translator, self.device = load_translator(path)

    def translate(self, sentences):
        tokens = [self.src.encode(s, out_type=str) + ["</s>"] for s in sentences]
        results = self.translator.translate_batch(
            tokens, beam_size=3, max_batch_size=32,
            max_decoding_length=256, repetition_penalty=1.1)
        return [self.tgt.decode([t for t in r.hypotheses[0] if t != "</s>"]) for r in results]


class NLLB:
    def __init__(self, path):
        from tokenizers import Tokenizer
        self.tok = Tokenizer.from_file(os.path.join(path, "tokenizer.json"))
        self.translator, self.device = load_translator(path)

    def translate(self, sentences, src_langs):
        tokens = [[lang] + self.tok.encode(s, add_special_tokens=False).tokens + ["</s>"]
                  for s, lang in zip(sentences, src_langs)]
        results = self.translator.translate_batch(
            tokens, target_prefix=[["jpn_Jpan"]] * len(tokens), beam_size=3, max_batch_size=16,
            max_decoding_length=256, repetition_penalty=1.1, no_repeat_ngram_size=4)
        out = []
        for r in results:
            ids = [self.tok.token_to_id(t) for t in r.hypotheses[0][1:]]
            out.append(self.tok.decode([i for i in ids if i is not None], skip_special_tokens=True))
        return out


JA = r"぀-ヿ一-鿿！-～"


def tidy_japanese(text):
    """NLLB などが出す半角の句読点や、和文の間の余分な空白を整える。"""
    text = re.sub(rf"(?<=[{JA}])\s*,\s*", "、", text)
    text = re.sub(rf"(?<=[{JA}])\s*\.(?!\d)", "。", text)
    text = re.sub(rf"(?<=[{JA}、。])\s+(?=[{JA}])", "", text)
    return text.strip()


class LazyNLLB:
    """NLLB は大きいので裏で読み込み、英語以外の翻訳が来たときだけ読み込み完了を待つ。"""

    def __init__(self, path):
        self.model = None
        self.error = None
        self.ready = threading.Event()
        self.path = path
        self._started = threading.Lock()
        self._thread = None

    def _load(self, path):
        try:
            self.model = NLLB(path)
            self.model.translate(["Bonjour."], ["fra_Latn"])  # 初回の遅さをここで済ませる
        except Exception as e:
            self.error = e
            log("NLLB を読み込めませんでした:", e)
        finally:
            self.ready.set()

    def get(self):
        # 大きい (約 1.4GB) ので、英語以外の文が来て初めて読み込む (GPU のメモリを無駄に使わないため)
        with self._started:
            if self._thread is None:
                self._thread = threading.Thread(target=self._load, args=(self.path,), daemon=True)
                self._thread.start()
        self.ready.wait()
        return self.model


def translate_texts(texts, fugu, lazy_nllb):
    nllb = lazy_nllb.get() if lazy_nllb and any(detect(t) != "eng_Latn" for t in texts) else None
    # 全文を文に分けて、モデルごとにまとめて一括処理する
    jobs = []  # (文の位置, 文, 言語)
    spans = []
    for text in texts:
        start = len(jobs)
        lang = detect(text)
        for s in split_sentences(text):
            jobs.append((s, lang))
        spans.append((start, len(jobs)))

    outputs = [""] * len(jobs)
    # 英語以外の翻訳モデル (NLLB-200、選んだときだけ入れる) が無いときは、英語の文だけを訳す
    # (英語のモデルで中国語などを訳すと、意味の分からない訳になる)
    fugu_idx = [i for i, (_, lang) in enumerate(jobs) if lang == "eng_Latn"]
    nllb_idx = [i for i, (_, lang) in enumerate(jobs) if lang != "eng_Latn" and nllb is not None]
    if fugu_idx:
        for i, t in zip(fugu_idx, fugu.translate([jobs[i][0] for i in fugu_idx])):
            outputs[i] = t
    if nllb_idx:
        for i, t in zip(nllb_idx, nllb.translate([jobs[i][0] for i in nllb_idx], [jobs[i][1] for i in nllb_idx])):
            outputs[i] = t
    return [tidy_japanese("".join(outputs[a:b])) for a, b in spans]


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")

    fugu = FuguMT(os.path.join(MODELS_DIR, "fugumt-en-ja"))
    nllb_path = os.path.join(MODELS_DIR, "nllb-200-1.3B")
    nllb = LazyNLLB(nllb_path) if os.path.exists(os.path.join(nllb_path, "model.bin")) else None
    # 最初の 1 回はメモリの確保などで遅いので、起動時に済ませておく
    translate_texts(["Hello."], fugu, None)

    models = ["fugumt"] + (["nllb"] if nllb else [])
    print(json.dumps({"ready": True, "device": fugu.device, "models": models}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        req = {}
        try:
            req = json.loads(line)
            t0 = time.perf_counter()
            translations = translate_texts(req["texts"], fugu, nllb)
            ms = int((time.perf_counter() - t0) * 1000)
            resp = {"id": req.get("id"), "translations": translations, "ms": ms}
        except Exception as e:
            resp = {"id": req.get("id"), "error": str(e)}
        print(json.dumps(resp, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
