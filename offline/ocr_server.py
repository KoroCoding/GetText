"""GetText AI OCR サーバー (RapidOCR + PP-OCRv6)。

GetText から子プロセスとして起動され、標準入出力で通信する。
  起動完了: {"ready": true, "model": "PP-OCRv6 small"}
  要求:     {"id": 1, "w": 640, "h": 480, "len": 1228800}\n + BGRA の生データ (len バイト)
            "accurate": true を付けると、高精度の認識モデル (PP-OCRv6 rec medium) で読む (遅い。画面が止まったときの読み直し用)
  応答:     {"id": 1, "lines": [{"text": "...", "score": 0.98, "box": [[x, y] x4]}], "ms": 85}
標準入力が閉じられたら (GetText が終了したら) 自動で終了する。
"""
import json
import os
import sys
import time

import numpy as np
from rapidocr import RapidOCR
from rapidocr.ch_ppocr_rec import utils as rec_utils

import app_data

MEDIUM_REC = os.path.join(app_data.MODELS_DIR, "ppocrv6-rec-medium", "PP-OCRv6_rec_medium.onnx")


def use_japanese_characters():
    """
    認識の結果の 1 行に、かな (ひらがな・カタカナ) があれば日本語の行とみなし、日本語で使わない漢字 (簡体字など) を
    候補から外して読み直す (ぼやけた日本語の字が、形の似た簡体字に読まれるのを防ぐ)。かなの無い行 (中国語など) はそのまま。
    """
    original = rec_utils.CTCLabelDecode.__call__

    def is_kana(c):
        return "\u3041" <= c <= "\u30ff"

    def decode(self, preds, return_word_box=False, **kwargs):
        if not hasattr(self, "_non_japanese"):
            banned = []
            for i, c in enumerate(self.character):
                if len(c) == 1 and ("\u4e00" <= c <= "\u9fff" or "\u3400" <= c <= "\u4dbf"):
                    try:
                        c.encode("shift_jis_2004")  # JIS X 0213 (常用漢字の「剝・塡・頰」も含む) で書けない字を外す
                    except UnicodeEncodeError:
                        banned.append(i)
            self._non_japanese = np.array(banned, np.int64)
        lines, words = original(self, preds, return_word_box, **kwargs)
        japanese = [i for i, (text, _) in enumerate(lines) if any(is_kana(c) for c in text)]
        if not japanese or len(self._non_japanese) == 0:
            return lines, words
        masked = preds[japanese].copy()
        masked[:, :, self._non_japanese] = 0
        kw = dict(kwargs)
        if "wh_ratio_list" in kw:
            kw["wh_ratio_list"] = [kw["wh_ratio_list"][i] for i in japanese]
        redo, redo_words = original(self, masked, return_word_box, **kw)
        lines = list(lines)
        words = list(words) if words is not None else words
        for n, (i, (text, score)) in enumerate(zip(japanese, redo)):
            # 候補を外すと確からしさが下がり、短い行が丸ごと捨てられることがあるので、元の確からしさを下回らないようにする
            lines[i] = (text, max(float(score), float(lines[i][1])))
            if return_word_box and words is not None and n < len(redo_words):
                words[i] = redo_words[n]
        return lines, words

    rec_utils.CTCLabelDecode.__call__ = decode


use_japanese_characters()


def read_exact(stream, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            raise EOFError
        buf += chunk
    return bytes(buf)


def create_engine(medium=False):
    """GPU (DirectML) が使えれば GPU、だめなら CPU で OCR エンジンを作る。medium なら高精度の認識モデルを使う。"""
    params = {
        "Global.log_level": "error",
        "Global.use_cls": False,  # 画面の文字は回転していない
        # 既定 (min) は短辺を 736px 以上に拡大するため、横長の画面キャプチャだと巨大になり遅い
        "Det.limit_type": "max",
        # 少し低めのしきい値にすると、表の「：」のような孤立した記号も検出できる
        "Det.thresh": 0.2,
        "Det.box_thresh": 0.3,
        # 全コアを使うとかえって遅くなる (32 スレッドより 16 スレッドの方が速かった)
        "EngineConfig.onnxruntime.intra_op_num_threads": max(1, min(16, (os.cpu_count() or 8) // 2)),
    }
    if medium:
        from rapidocr.utils.typings import OCRVersion, ModelType
        params.update({"Rec.model_path": MEDIUM_REC, "Rec.ocr_version": OCRVersion.PPOCRV6, "Rec.model_type": ModelType.MEDIUM})
    warm = np.full((48, 320, 3), 255, np.uint8)
    warm[16:32, 20:300] = 0

    import onnxruntime
    if "DmlExecutionProvider" in onnxruntime.get_available_providers():
        try:
            engine = RapidOCR(params={**params, "EngineConfig.onnxruntime.use_dml": True})
            engine(warm)  # 最初の 1 回の遅さを起動時に済ませる
            return engine, "gpu"
        except Exception as e:
            print("GPU (DirectML) を使えないため CPU で動かします:", e, file=sys.stderr, flush=True)
    engine = RapidOCR(params=params)
    engine(warm)
    return engine, "cpu"


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    stdin = sys.stdin.buffer

    engine, device = create_engine()
    accurate = {"engine": None}
    print(json.dumps({"ready": True, "model": "PP-OCRv6 small", "device": device,
                      "accurate": os.path.isfile(MEDIUM_REC)}), flush=True)

    while True:
        header = stdin.readline()
        if not header:
            break
        if not header.strip():
            continue
        req = {}
        try:
            req = json.loads(header)
            data = read_exact(stdin, req["len"])
            w, h = req["w"], req["h"]
            # BGRA → BGR (RapidOCR は OpenCV と同じ BGR 順)
            img = np.ascontiguousarray(np.frombuffer(data, np.uint8).reshape(h, w, 4)[:, :, :3])
            t0 = time.perf_counter()
            use = engine
            if req.get("accurate") and os.path.isfile(MEDIUM_REC):
                if accurate["engine"] is None:
                    accurate["engine"], _ = create_engine(medium=True)  # 初めて使うときに読み込む
                use = accurate["engine"]
            result = use(img)
            ms = int((time.perf_counter() - t0) * 1000)
            lines = []
            if result.txts:
                for box, text, score in zip(result.boxes, result.txts, result.scores):
                    lines.append({"text": text, "score": round(float(score), 3), "box": np.asarray(box).tolist()})
            resp = {"id": req.get("id"), "lines": lines, "ms": ms}
        except EOFError:
            break
        except Exception as e:
            resp = {"id": req.get("id"), "error": f"{type(e).__name__}: {e}"}
        print(json.dumps(resp, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
