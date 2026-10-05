"""議事録の 2 段階目の補正。

1. 再認識: 同じ話者が続けて話した発言 (最大 25 秒) をまとめて音声認識し直す。
   前後の音声が文脈になるので、発言ごとに認識したときより正確になりやすい。
2. 文脈補正: 直前の数発言と次の発言を文脈として LLM (Qwen3-4B-Instruct, CTranslate2) に渡し、
   聞き間違い (同音異義語・似た音の語) だけを直させる。元の文から大きく変わる結果は捨てる。
"""
import difflib
import re
import os
import app_data
import queue
import threading
import time

import numpy as np

RATE = 16000
LLM_PATH = os.path.join(app_data.MODELS_DIR, "qwen3-4b-instruct")

BLOCK_GAP_SEC = 1.5      # これ以上あいたら別のまとまり
BLOCK_MAX_SEC = 25.0     # 音声認識にまとめてかけられる長さ (Whisper は 30 秒ずつ処理するので、つなぎの無音も含めてこれ以下)
JOIN_GAP_SEC = 0.3       # まとめて認識し直すときに発言の間に入れる無音
MIN_LENGTH_RATIO = 0.85  # 認識し直した文がこれより短くなったら (一部が抜けたとみなして) 採用しない
LOST_CHUNK_CHARS = 6     # 認識し直した文で、元の文のこの文字数以上のまとまりが抜けていたら採用しない
BLOCK_IDLE_SEC = 4.0     # 次の発言が来なければまとまりを閉じる
NEXT_WAIT_SEC = 6.0      # 文脈補正は次の発言を少し待ってから (後ろの文脈も使うため)
CONTEXT_BLOCKS = 6

# 直す 7 文・直さない 4 文で比べて最も良かった指示 (6/7 を修正、誤った変更 0)。
# 直し方の例を見せる (few-shot) や「読みを疑え」という指示は、この大きさのモデルではかえって悪くなった
SYSTEM_PROMPT = (
    "あなたは会議の文字起こしを校正する専門家です。"
    "音声認識による聞き間違い (同音異義語、音の似た別の語、文脈に合わない語、数字や固有名詞の誤り) だけを、"
    "前後の発言の文脈に合わせて最小限に直してください。"
    "言い換え、要約、敬語や語尾の変更、文の追加や削除はしないでください。"
    "句読点は読みやすくなるように補ってかまいません。"
    "直すところがなければ、校正する文をそのまま返してください。"
    "出力は校正後の文だけにしてください。説明や引用符は付けないでください。"
)


def speaker_label(speaker):
    return "自分" if speaker == 0 else f"話者{speaker}"


FILLER_RUN = re.compile(r"(えー+(っと)?|えっと|あのー*|そのー*|まあ|うーん|ですね)")


def lost_chunk(before, after, limit=LOST_CHUNK_CHARS):
    """after で、before のまとまった言葉 (limit 文字以上) が抜けているか。
    長い音声をつなげて認識し直すと、Whisper が途中の言い回しを丸ごと飛ばすことがある (全体の長さはあまり変わらない)。
    言い直し・つなぎの言葉 (えー・あの・ですね など) が消えたもの、ひらがなを漢字や数字にしたもの (「さんびゃくにじゅうごにん」→
    「325人」)、言葉の順番が入れ替わったものは、抜けとはみなさない。"""
    before, after = FILLER_RUN.sub("", before), FILLER_RUN.sub("", after)
    sm = difflib.SequenceMatcher(None, before, after, autojunk=False)
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        lost = before[i1:i2]
        if tag == "replace" and (j2 - j1) > max(1, (i2 - i1) // 4):
            continue  # ほかの書き方に置き換えた (かなを漢字・数字にした など)
        if tag in ("delete", "replace") and (i2 - i1) - (j2 - j1) >= limit and lost not in after:
            return True
    return False


class Block:
    def __init__(self, src, speaker):
        self.src = src
        self.speaker = speaker
        self.ids = []
        self.parts = []          # (start, end, audio, text)
        self.text = ""
        self.updated = time.time()
        self.closed_at = None
        self.before = []          # 閉じたときの直前の発言 (文脈補正に使う)
        self.next_blocks = []     # 次に閉じたまとまり (後ろの文脈に使う)
        self.session = 0

    @property
    def end(self):
        return self.parts[-1][1]

    @property
    def duration(self):
        return sum(e - s for s, e, _, _ in self.parts) / RATE

    def accepts(self, src, speaker, start, end):
        # 足したあとの長さ (つなぎの無音を含む) で上限を確かめる
        total = self.duration + (end - start) / RATE + JOIN_GAP_SEC * len(self.parts)
        return (src == self.src and speaker == self.speaker and self.parts
                and (start - self.end) / RATE < BLOCK_GAP_SEC and total <= BLOCK_MAX_SEC)

    def add(self, seg_id, start, end, audio, text):
        self.ids.append(seg_id)
        self.parts.append((start, end, audio, text))
        self.text += text
        self.updated = time.time()


LLM_VRAM_MB = 4300        # GPU で動かすときに必要なメモリ (モデル約 4GB + 作業領域)
VRAM_RESERVE_MB = 1500    # 通話アプリや画面表示のために残しておく GPU メモリ


def free_vram_mb():
    """GPU の空きメモリ (MB)。調べられなければ None。"""
    import subprocess
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=5, creationflags=0x08000000).stdout
        return int(out.strip().splitlines()[0])
    except Exception:
        return None


CPU_PENDING_MAX = 20  # CPU で補正を待つまとまりの上限
LLM_IDLE_SEC = 90     # 補正をオフにしてから、この秒数使わなければ AI のモデルを手放す


class LlmCorrector:
    """Qwen3-4B-Instruct (CTranslate2) で文脈補正する。読み込みは初回に裏で行う。"""

    def __init__(self, emit, log):
        self.emit = emit
        self.log = log
        # GETTEXT_NO_LLM=1 で無効 (話者の聞き分けの評価などで LLM を読み込まないため)
        self.enabled = os.path.exists(os.path.join(LLM_PATH, "model.bin")) and not os.environ.get("GETTEXT_NO_LLM")
        self.generator = None
        self.tokenizer = None
        self.state = "off" if not self.enabled else "idle"
        self._loading = threading.Lock()
        self._generating = threading.Lock()  # 文脈補正と要約で同じモデルを使うので、1 つずつ生成する
        self.terms = []  # 用語の登録 (人名・社名・専門用語。この表記に直してよい)
        self.last_used = time.time()

    def unload_if_idle(self, idle_sec):
        """しばらく使っていなければモデルを手放す (CPU では約 8GB のメモリを使うため)。手放したら True。"""
        if self.generator is None or time.time() - self.last_used < idle_sec or self._loading.locked():
            return False
        if not self._generating.acquire(blocking=False):
            return False
        try:
            if self.generator is None:
                return False
            self.generator = None
            self.status("idle")
        finally:
            self._generating.release()
        import gc
        gc.collect()
        return True

    def status(self, state):
        self.state = state
        self.emit({"event": "status", "llm": state})

    def load(self):
        with self._loading:
            if self.generator is not None or not self.enabled:
                return self.generator is not None
            self.status("loading")
            try:
                import ctranslate2
                from tokenizers import Tokenizer
                self.tokenizer = Tokenizer.from_file(os.path.join(LLM_PATH, "tokenizer.json"))
            except Exception as e:
                self.log("文脈補正の LLM を読み込めませんでした:", e)
                self.enabled = False
                self.status("unavailable")
                return False
            # GPU に十分な空きがあるときだけ GPU を使う。足りないのに載せると、あふれた分が普通のメモリに逃がされて
            # 音声認識まで極端に遅くなる (話しても文字が出ず、停止後にまとめて出る)
            free = free_vram_mb()
            use_gpu = ctranslate2.get_cuda_device_count() > 0 and free is not None and free >= LLM_VRAM_MB + VRAM_RESERVE_MB
            self.log(f"文脈補正の LLM: GPU の空き {free} MB → {'GPU' if use_gpu else 'CPU'} で動かします")
            attempts = [("cuda", "int8_float16"), ("cpu", "int8")] if use_gpu else [("cpu", "int8")]
            for device, compute in attempts:
                try:
                    gen = ctranslate2.Generator(LLM_PATH, device=device, compute_type=compute,
                                                intra_threads=max(1, min(8, (os.cpu_count() or 8) // 2)))
                    if device == "cuda":
                        # 読み込めても計算で失敗する (cuBLAS が合わないなど) ことがあるので、1 語だけ作って確かめる
                        gen.generate_batch([self.tokenizer.encode("はい", add_special_tokens=False).tokens], max_length=1)
                    self.generator = gen
                    self.last_used = time.time()  # 読み込んだ直後に「使っていない」として手放さないように
                    self.status("gpu" if device == "cuda" else "cpu")
                    return True
                except Exception as e:  # GPU のメモリ不足など
                    self.log(f"文脈補正の LLM を {device} で読み込めませんでした:", e)
            self.enabled = False
            self.status("unavailable")
            return False

    def correct(self, text, before, after):
        if not self.load():
            return None
        context = "\n".join(before) if before else "(なし)"
        following = "\n".join(after) if after else "(なし)"
        glossary = f"【用語 (この表記で書く)】\n{'、'.join(self.terms)}\n\n" if self.terms else ""
        user = f"{glossary}【直前の発言】\n{context}\n\n【直後の発言】\n{following}\n\n【校正する文】\n{text}"
        prompt = (f"<|im_start|>system\n{SYSTEM_PROMPT}<|im_end|>\n"
                  f"<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n")
        max_new = min(400, self.count_tokens(text) * 1.6 + 16)
        return accept_correction(text, self.generate(prompt, int(max_new)))

    def count_tokens(self, text):
        return len(self.tokenizer.encode(text, add_special_tokens=False).ids)

    def generate(self, prompt, max_new, repetition_penalty=1.0, stop=None):
        """
        prompt (チャット形式に組み立て済み) の続きを生成して、文字列で返す。
        stop() が True を返すと、生成の途中でもそこでやめる (要約の中止)。
        """
        tokens = self.tokenizer.encode(prompt, add_special_tokens=False).tokens
        with self._generating:
            self.last_used = time.time()
            if self.generator is None and not self.load():  # 使わない間に手放していたら読み込み直す
                return ""
            try:
                result = self.generator.generate_batch(
                    [tokens], max_length=max_new, sampling_topk=1, include_prompt_in_result=False,
                    repetition_penalty=repetition_penalty, end_token=["<|im_end|>", "<|endoftext|>"],
                    callback=(lambda step: bool(stop())) if stop else None)[0]
            finally:
                self.last_used = time.time()
        return self.tokenizer.decode(result.sequences_ids[0], skip_special_tokens=True).strip()


def accept_correction(original, corrected):
    """LLM の出力が「聞き間違いの修正」の範囲に収まっているときだけ採用する。"""
    if not corrected:
        return None
    for prefix in ("校正後の文:", "校正後:", "校正:"):
        if corrected.startswith(prefix):
            corrected = corrected[len(prefix):].strip()
    if corrected.startswith("「") and corrected.endswith("」") and not original.startswith("「"):
        corrected = corrected[1:-1]
    corrected = corrected.splitlines()[0].strip() if corrected else ""
    if not corrected or corrected == original:
        return None
    ratio = len(corrected) / max(1, len(original))
    if ratio < 0.75 or ratio > 1.35:
        return None  # 要約や付け足しをしている
    similarity = difflib.SequenceMatcher(None, strip_punct(original), strip_punct(corrected)).ratio()
    if similarity < 0.6:
        return None  # 言い換えている
    if strip_punct(original) == strip_punct(corrected) and len(corrected) < len(original):
        return None  # 句読点を消しただけ
    if not only_word_fixes(strip_punct(original), strip_punct(corrected)):
        return None  # 前後の文脈から言葉を書き足した・削った (話していない内容が入る)
    if not same_sound(strip_punct(original), strip_punct(corrected)):
        return None  # 読みの違う語に変えた (聞き間違いの修正ではなく、文脈からの言い換え)
    return corrected


ALNUM = re.compile(r"[0-9A-Za-z０-９Ａ-Ｚａ-ｚ]+")


def same_sound(a, b):
    """
    読みがほぼ同じか (聞き間違い = 同じ音の別の字の修正だけを認める。例: 県→件、制度→精度、置き→おき)。
    補正の AI は前後の文脈から「人数→利用者」「来月→来週」のような読みの違う語に変えることがあり、
    会議の音声 72 秒の確認で 4 件中 3 件がそうだった (話した内容が変わってしまう)。
    読みを調べられないとき (Janome が無いなど) は判定しない。
    """
    # 数字・英字は読みの比較から落ちるので、そのまま比べる (「3時→4時」「CPU→GPU」を通さない)
    if ALNUM.findall(a) != ALNUM.findall(b):
        return False
    try:
        import terms
        ra, rb = terms.reading_of(a), terms.reading_of(b)
    except Exception:
        return True
    if not ra or not rb:
        return True
    changed = sum(max(i2 - i1, j2 - j1) for op, i1, i2, j1, j2 in
                  difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes() if op != "equal")
    distance = sum(max(i2 - i1, j2 - j1) for op, i1, i2, j1, j2 in
                   difflib.SequenceMatcher(None, ra, rb, autojunk=False).get_opcodes() if op != "equal")
    return distance <= max(1, round(changed * 0.2))


def only_word_fixes(a, b):
    """
    変更が「聞き間違えた語の置き換え」だけか (例: 制度→精度、疑似録→議事録)。
    LLM は前後の文脈から「来週の」のような語を書き足すことがあるが、話していない内容になるので認めない。
    2 文字以上の書き足し・削除、5 文字以上の置き換え、長さが 2 文字以上変わる置き換えは認めない。
    """
    changed = 0
    for op, i1, i2, j1, j2 in difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes():
        la, lb = i2 - i1, j2 - j1
        if op == "insert" and lb >= 2 or op == "delete" and la >= 2:
            return False
        if op == "replace" and (max(la, lb) > 4 or abs(la - lb) > 1):
            return False
        if op != "equal":
            changed += max(la, lb)
    return changed <= max(4, len(a) // 4)


def strip_punct(s):
    return "".join(c for c in s if c not in "、。，．,.!！?？ 　「」")


class Refiner:
    """発言をまとまりに集め、閉じたまとまりを再認識 → 文脈補正する。"""

    def __init__(self, transcribe_audio, emit, log, speaker_of=None, can_merge=None):
        self.transcribe_audio = transcribe_audio   # audio(np.float32) -> text
        # 発言の今の話者番号 (話者の聞き分けは、発言が増えると以前の発言も付け直すため)
        self.speaker_of = speaker_of or (lambda seg_id: None)
        # まとめてよいか (話者の聞き分けが固まる前は、別の人の発言を 1 人分としてまとめてしまうのでまとめない)
        self.can_merge = can_merge or (lambda src: True)
        self.emit = emit
        self.llm = LlmCorrector(emit, log)
        self.llm_on = True
        self.open = {}           # src -> Block
        self.history = []        # 閉じたまとまり (補正済みの文を持つ)
        self.queue = queue.Queue()
        self.lock = threading.Lock()
        self.session = 0   # reset のたびに増やす (前の会議の補正待ちを捨てるため)
        threading.Thread(target=self._llm_worker, daemon=True).start()

    def reset(self):
        with self.lock:
            self.open.clear()
            self.history.clear()
            self.session += 1
        flushed = False
        while True:  # 補正の順番待ちも捨てる
            try:
                flushed |= self.queue.get_nowait() == "flush"
            except queue.Empty:
                break
        if flushed:
            self.queue.put("flush")  # 停止の区切りは残す (アプリは「補正が追いついた」を待っている)

    def add(self, src, speaker, seg_id, start, end, audio, text):
        block = self.open.get(src)
        if block is not None and self.can_merge(src) and block.accepts(src, speaker, start, end):
            block.add(seg_id, start, end, audio, text)
            return
        if block is not None:
            self._close(block)
        block = Block(src, speaker)
        block.add(seg_id, start, end, audio, text)
        self.open[src] = block

    def tick(self, final=False):
        """しばらく続きが来ないまとまりを閉じる。final ならすべて閉じる。"""
        now = time.time()
        for src, block in list(self.open.items()):
            if final or now - block.updated > BLOCK_IDLE_SEC:
                self._close(block)
                del self.open[src]
        if final:
            self.queue.put("flush")

    def _close(self, block):
        # 閉じるまでの間に話者が付け直されていたら、今の話者ごとに分けてから扱う
        labels = [self.speaker_of(i) for i in block.ids]
        labels = [block.speaker if l is None else l for l in labels]
        if len(set(labels)) > 1:
            run = None
            for seg_id, part, label in zip(block.ids, block.parts, labels):
                if run is None or run.speaker != label:
                    if run is not None:
                        self._close(run)
                    run = Block(block.src, label)
                run.add(seg_id, *part)
            self._close(run)
            return
        block.speaker = labels[0]
        # 1. 複数の発言からなるまとまりは、音声をつなげて認識し直す
        if len(block.parts) >= 2:
            gap = np.zeros(int(RATE * JOIN_GAP_SEC), np.float32)
            audio = np.concatenate([x for _, _, a, _ in block.parts for x in (a, gap)])
            try:
                text = self.transcribe_audio(audio, block.src)
            except TypeError:
                text = self.transcribe_audio(audio)
            except Exception:
                text = None
            if text and text != block.text:
                similarity = difflib.SequenceMatcher(None, strip_punct(block.text), strip_punct(text)).ratio()
                ratio = len(strip_punct(text)) / max(1, len(strip_punct(block.text)))
                # 大きく食い違うとき、一部が抜けて短くなったときは元のまま
                if similarity > 0.6 and ratio >= MIN_LENGTH_RATIO and not lost_chunk(strip_punct(block.text), strip_punct(text)):
                    self.emit({"event": "revise", "ids": block.ids, "text": text, "stage": "asr", "before": block.text})
                    block.text = text
        block.closed_at = time.time()
        block.parts = [(s, e, None, t) for s, e, _, t in block.parts]  # 音声はもう使わないので手放す (長い会議でメモリが増え続けないように)
        with self.lock:
            # 直前の発言は閉じた時点で決めておく (補正が遅れて履歴から外れても、文脈なしにならないように)
            block.before = [f"{speaker_label(b.speaker)}: {b.text}" for b in self.history[-CONTEXT_BLOCKS:]]
            if self.history:
                self.history[-1].next_blocks.append(block)
            block.session = self.session
            self.history.append(block)
            del self.history[:-50]
        self.queue.put(block)

    def _llm_worker(self):
        pending = []
        while True:
            try:
                item = self.queue.get(timeout=1.0)
            except queue.Empty:
                item = None
            flush = item == "flush"
            if isinstance(item, Block):
                pending.append(item)
                # CPU で補正が追いつかないときは、古いものから補正をあきらめる (待ちが増え続けて、補正が会議の後まで遅れないように)
                if self.llm.state == "cpu" and len(pending) > CPU_PENDING_MAX:
                    del pending[:len(pending) - CPU_PENDING_MAX]
            # 次の発言が来るか、少し待ってから補正する (後ろの文脈も使うため)
            while pending:
                block = pending[0]
                if getattr(block, "session", self.session) != self.session:
                    pending.pop(0)  # 前の会議の補正待ちは捨てる
                    continue
                with self.lock:
                    has_next = bool(block.next_blocks)
                    before = list(getattr(block, "before", []))
                    nxt = block.next_blocks[:2]
                    if nxt and nxt[0].next_blocks:
                        nxt = [nxt[0], nxt[0].next_blocks[0]]
                    after = [f"{speaker_label(b.speaker)}: {b.text}" for b in nxt[:2]]
                if not (has_next or flush or time.time() - block.closed_at > NEXT_WAIT_SEC):
                    break
                pending.pop(0)
                if not (self.llm_on and self.llm.enabled):
                    continue
                if not any("\u3040" <= c <= "\u30ff" for c in block.text):
                    continue  # 補正の指示は日本語向けなので、英語などの発言は直さない
                try:
                    corrected = self.llm.correct(block.text, before, after)
                except Exception as e:
                    self.emit({"event": "error", "message": f"文脈補正: {type(e).__name__}: {e}"})
                    corrected = None
                if corrected:
                    self.emit({"event": "revise", "ids": block.ids, "text": corrected, "stage": "llm", "before": block.text})
                    block.text = corrected
            if flush:
                self.emit({"event": "refined"})
