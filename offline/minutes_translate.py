"""議事録の日本語訳を AI (文脈補正と同じ Qwen3-4B) で作る。

翻訳モデル (FuguMT) は 1 文ずつ訳すので、前後の流れや固有名詞を取り違えることがある
(例: 新製品の名前「dots」を「点」と訳す)。直前の発言と登録した用語を渡して、会議の流れに合った訳にする。
"""
import threading

SYSTEM_PROMPT = (
    "あなたは会議の通訳者です。英語など外国語の発言を、自然で読みやすい日本語に訳してください。"
    "直前の発言は文脈として使い、訳すのは【訳す発言】だけにしてください。"
    "製品名・サービス名・人名などの固有名詞は、むやみに日本語の普通の言葉に置き換えず、元の表記 (またはカタカナ) のままにしてください。"
    "用語が示されていれば、その表記を使ってください。"
    "出力は訳文だけにしてください。説明や引用符は付けないでください。"
)


def chat(system, user):
    return (f"<|im_start|>system\n{system}<|im_end|>\n"
            f"<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n")


class AiTranslator:
    def __init__(self, llm, emit, log):
        self.llm = llm
        self.emit = emit
        self.log = log
        self.terms = []
        self.lock = threading.Lock()

    def start(self, job, items):
        """items: [{"text": 訳す発言, "context": [直前の発言, ...]}]。終わったら translated を知らせる。"""
        threading.Thread(target=self._run, args=(job, items), daemon=True).start()

    def _run(self, job, items):
        try:
            if not self.llm.load():
                raise RuntimeError("AI を読み込めませんでした")
            out = []
            with self.lock:
                for item in items:
                    out.append(self.translate(item.get("text", ""), item.get("context") or []))
            self.emit({"event": "translated", "job": job, "texts": out})
        except Exception as e:
            self.log("[translate] 失敗:", e)
            self.emit({"event": "translated", "job": job, "error": str(e)})

    def translate(self, text, context):
        if not text.strip():
            return ""
        glossary = f"【用語】\n{'、'.join(self.terms)}\n\n" if self.terms else ""
        before = "\n".join(context[-4:]) if context else "(なし)"
        user = f"{glossary}【直前の発言】\n{before}\n\n【訳す発言】\n{text}"
        n = self.llm.count_tokens(text)
        result = self.llm.generate(chat(SYSTEM_PROMPT, user), min(600, int(n * 2.5) + 32))
        result = result.strip().strip("「」\"")
        for prefix in ("訳:", "訳文:", "日本語訳:"):
            if result.startswith(prefix):
                result = result[len(prefix):].strip()
        return result
