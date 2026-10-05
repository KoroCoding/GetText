"""議事録の要約 (概要・決定事項・やること・主な論点)。

文脈補正と同じ LLM (Qwen3-4B-Instruct, CTranslate2) を PC の中で使う。
長い会議は区切って要点を書き出してから (map)、それを 1 つにまとめる (reduce)。
"""
import re
import threading
import time

SINGLE_TOKENS = 6000   # 文字起こしがこれ以下なら 1 回でまとめる
CHUNK_TOKENS = 3500    # 長いときに区切る大きさ
NOTES_TOKENS = 450     # 区切りごとの要点メモの長さの上限
SUMMARY_TOKENS = 900   # 要約の長さの上限

SYSTEM_PROMPT = (
    "あなたは会議の議事録をまとめる担当者です。"
    "与えられた文字起こし (またはメモ) に書かれていることだけをもとに、日本語で簡潔にまとめてください。"
    "書かれていないこと (推測、一般論、架空の担当者や期限、数字) は書かないでください。"
    "文字起こしには音声認識の誤り、相づち、言いよどみが含まれます。意味が通るように読み取ってください。"
    "話者名 (「話者3」なども) はそのまま使ってください。"
    "行頭に ★ の付いた発言は、利用者が重要と印を付けた発言です。必ず要約に反映してください。"
    "行頭に 📝 の付いた「メモ」は、利用者が会議中に書き込んだメモです。発言と同じように内容を要約に反映してください。"
)

FORMAT = """次の形式の Markdown だけを出力してください。該当する内容が無い項目は「- なし」と書いてください。

### 概要
(会議の目的と主な内容を 2〜4 文で)

### 決定事項
- (決まったこと)

### やること
- (やること。担当や期限が発言にあるときだけ、後ろに「(担当: 名前、期限: いつ)」を付ける。発言に無いときは「未定」「未明記」なども書かない)

### 主な論点
- (話し合われた論点・課題・未決のこと)"""


def chat(system, user):
    return (f"<|im_start|>system\n{system}<|im_end|>\n"
            f"<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n")


def chunks(lines, count, limit):
    """行を、トークン数が limit 以下のまとまりに分ける (1 行が長すぎるときはその行だけで 1 つ)。"""
    out, cur, size = [], [], 0
    for line in lines:
        n = count(line) + 1
        if cur and size + n > limit:
            out.append(cur)
            cur, size = [], 0
        cur.append(line)
        size += n
    if cur:
        out.append(cur)
    return out


def clean(text):
    """前置きや囲みを取り除く (「以下が要約です」、```markdown など)。"""
    text = text.strip()
    if text.startswith("```"):
        text = text.split("\n", 1)[1] if "\n" in text else ""
        if text.rstrip().endswith("```"):
            text = text.rstrip()[:-3]
    i = text.find("### ")
    if i > 0:
        text = text[i:]
    return "\n".join(drop_unknown(line).rstrip() for line in text.strip().splitlines())


UNKNOWN = r"(?:未定|未明記|不明|記載なし|言及なし|指定なし|なし)"


def drop_unknown(line):
    """
    発言に無い担当・期限の書き込みを消す (「(未定)」「期限: 未明記」など)。
    この大きさのモデルは、書かないように指示しても付けることがあるため。
    """
    line = re.sub(rf"[、,]\s*(?:担当|期限)\s*[:：]\s*{UNKNOWN}", "", line)       # (担当: 自分、期限: 未明記) → (担当: 自分)
    line = re.sub(rf"(?:担当|期限)\s*[:：]\s*{UNKNOWN}\s*[、,]\s*", "", line)     # (担当: 未定、期限: 金曜日) → (期限: 金曜日)
    line = re.sub(rf"\s*[（(]\s*(?:(?:担当|期限)\s*[:：]\s*)?{UNKNOWN}\s*[)）]", "", line)  # (未定)、(担当: 未定)
    line = re.sub(rf"[、,]\s*{UNKNOWN}(?=\s*[)）])", "", line)                      # (話者3、未明記) → (話者3)
    line = re.sub(rf"(?<=[（(])\s*{UNKNOWN}\s*[、,]\s*", "", line)                    # (未定、鈴木さん) → (鈴木さん)
    line = re.sub(rf"\s*(?:担当|期限)\s*[:：]\s*{UNKNOWN}\s*$", "", line)              # … 期限: 未定 (括弧なし)
    return line


class Summarizer:
    """要約を裏で作り、途中経過と結果を通知する。"""

    def __init__(self, llm, emit, log):
        self.llm = llm
        self.emit = emit
        self.log = log
        self.cancel = threading.Event()
        self.thread = None
        self.terms = []  # 用語の登録 (要約でもこの表記で書く)

    def start(self, job, transcript):
        if self.thread is not None and self.thread.is_alive():
            self.emit({"event": "summary", "job": job, "state": "error", "message": "要約を作っているところです"})
            return
        self.cancel.clear()
        self.thread = threading.Thread(target=self._run, args=(job, transcript), daemon=True)
        self.thread.start()

    def _run(self, job, transcript):
        started = time.time()
        try:
            if not self.llm.enabled and self.llm.generator is None:
                raise RuntimeError("要約には AI (Qwen3-4B) が必要です。設定 → 情報 → セットアップで入れてください")
            self.emit({"event": "summary", "job": job, "state": "loading"})
            if not self.llm.load():
                raise RuntimeError("要約の AI を読み込めませんでした")
            lines = [l for l in transcript.splitlines() if l.strip()]
            if not lines:
                raise RuntimeError("要約する発言がありません")
            count = self.llm.count_tokens
            total_tokens = sum(count(l) + 1 for l in lines)
            if total_tokens <= SINGLE_TOKENS:
                self._progress(job, 0, 1)
                text = self._summarize("\n".join(lines), "会議の文字起こし")
            else:
                notes = self._notes(job, chunks(lines, count, CHUNK_TOKENS))
                # メモも長すぎるときは、さらに区切ってまとめる
                while count("\n".join(notes)) > SINGLE_TOKENS:
                    notes = [self._generate(f"次は会議の要点メモです。重複をまとめ、重要な点を残して短くしてください。\n\n{chr(10).join(g)}",
                                            NOTES_TOKENS) for g in chunks(notes, count, CHUNK_TOKENS)]
                text = self._summarize("\n\n".join(notes), "会議を区切って書き出した要点メモ")
            self.log(f"[summary] 完了 {time.time() - started:.1f}s (文字起こし {total_tokens} トークン)")
            self.emit({"event": "summary", "job": job, "state": "done", "text": clean(text)})
        except Cancelled:
            self.log("[summary] 中止")
            self.emit({"event": "summary", "job": job, "state": "cancelled"})
        except Exception as e:
            self.log("[summary] 失敗:", e)
            self.emit({"event": "summary", "job": job, "state": "error", "message": str(e)})

    def _progress(self, job, done, total):
        self.emit({"event": "summary", "job": job, "state": "progress", "done": done, "total": total})

    def _notes(self, job, groups):
        notes = []
        total = len(groups) + 1  # 最後にまとめる 1 回を含める
        for i, group in enumerate(groups):
            self._progress(job, i, total)
            user = (f"次は会議の文字起こしの一部 ({i + 1}/{len(groups)}) です。"
                    "この部分で話された要点、決まったこと、やること (担当・期限が発言にあれば) を、"
                    "箇条書きで簡潔に書き出してください。\n\n" + "\n".join(group))
            notes.append(f"【{i + 1}/{len(groups)} の要点】\n" + self._generate(user, NOTES_TOKENS))
        self._progress(job, len(groups), total)
        return notes

    def _summarize(self, body, kind):
        glossary = f"用語 (人名・社名などは、この表記で書いてください): {'、'.join(self.terms)}\n\n" if self.terms else ""
        return self._generate(f"次は{kind}です。会議全体の議事録の要約を作ってください。\n\n{glossary}{FORMAT}\n\n---\n{body}",
                              SUMMARY_TOKENS)

    def _generate(self, user, max_new):
        if self.cancel.is_set():
            raise Cancelled()
        out = self.llm.generate(chat(SYSTEM_PROMPT, user), max_new, repetition_penalty=1.05, stop=self.cancel.is_set)
        if self.cancel.is_set():
            raise Cancelled()
        return out


class Cancelled(Exception):
    pass
