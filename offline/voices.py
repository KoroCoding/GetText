"""話者の声の登録: 名前を付けた話者の声の特徴を覚えておき、次の会議で似た声の話者に名前を付ける。

覚えるのは声の特徴 (CAM++ の 192 次元のベクトルの平均) だけで、音声そのものは保存しない。
保存先: %LOCALAPPDATA%\\GetText\\voices.json  ({"名前": {"emb": [...], "n": 覚えた回数}})
"""
import json
import os
import threading

import app_data

import numpy as np

PATH = os.environ.get("GETTEXT_VOICES_PATH") or os.path.join(app_data.app_dir(), "voices.json")
TAU = 0.70         # これ以上似ていれば同じ人とみなす (同じ会議の別の人どうしでも 0.5 台まであった)
MARGIN = 0.06      # 2 番目に似た人との差がこれ以上あること (似た声の人が複数いるときは決めない)
MIN_WINDOWS = 8    # 声の窓がこれだけ集まった話者だけ調べる (約 6 秒の発言。後で違うと分かれば取り消す)


def unit(v):
    v = np.asarray(v, np.float32)
    return v / (np.linalg.norm(v) + 1e-9)


class VoiceBook:
    def __init__(self, log, path=PATH):
        self.log = log
        self.path = path
        self.lock = threading.Lock()
        self.voices = {}
        self.reload()

    def reload(self):
        with self.lock:
            try:
                with open(self.path, encoding="utf-8") as f:  # 開いたままだと、壊れたときに別名にできない (Windows)
                    data = json.load(f)
                voices = {}
                for k, v in data.items():
                    try:
                        voices[k] = {"emb": unit(v["emb"]), "n": int(v.get("n", 1))}
                    except Exception as e:  # 1 人分が壊れていても、ほかの人の声は残す
                        self.log(f"覚えた声「{k}」を読めませんでした:", e)
                self.voices = voices
            except FileNotFoundError:
                self.voices = {}
            except Exception as e:
                # 壊れたファイルは別名で残し (上書きして消さない)、空から始める
                self.log("覚えた声を読めませんでした (voices.json.bad に残します):", e)
                try:
                    os.replace(self.path, self.path + ".bad")
                except OSError:
                    pass
                self.voices = {}

    def _save(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({k: {"emb": [round(float(x), 5) for x in v["emb"]], "n": v["n"]} for k, v in self.voices.items()},
                      f, ensure_ascii=False)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.path)

    def enroll(self, name, emb):
        """name の声として覚える (前に覚えていれば、回数で重みを付けて平均する)。"""
        with self.lock:
            emb = unit(emb)
            old = self.voices.get(name)
            if old is not None:
                emb = unit(old["emb"] * old["n"] + emb)
            self.voices[name] = {"emb": emb, "n": (old["n"] + 1) if old else 1}
            self._save()
            return len(self.voices)

    def forget(self, name=None):
        with self.lock:
            if name:
                self.voices.pop(name, None)
            else:
                self.voices.clear()
            self._save()

    def match(self, centroids, counts):
        """
        話者の番号 → 重心 の中から、覚えた声に似た話者を探す。{番号: (名前, 類似度)} を返す。
        1 つの名前は最も似た 1 人にだけ付ける。
        """
        with self.lock:
            if not self.voices or not centroids:
                return {}
            names = list(self.voices)
            V = np.array([self.voices[n]["emb"] for n in names])
            labels = [l for l in centroids if l != 0 and counts.get(l, 0) >= MIN_WINDOWS
                      and np.all(np.isfinite(centroids[l]))]
            if not labels:
                return {}
            S = np.array([V @ unit(centroids[l]) for l in labels])  # 話者 × 覚えた声
            result = {}
            for i, label in enumerate(labels):
                j = int(np.argmax(S[i]))
                best = float(S[i, j])
                # 話者から見て 2 番目に似た声との差、覚えた声から見て 2 番目に似た話者との差の両方を見る
                # (新しく加わった人の声が、覚えた人に似ていることがあるため。似た 2 人のどちらとも決めない)
                other_names = np.delete(S[i], j)
                other_labels = np.delete(S[:, j], i)
                if best < TAU:
                    continue
                if other_names.size and best - float(other_names.max()) < MARGIN:
                    continue
                if other_labels.size and best - float(other_labels.max()) < MARGIN:
                    continue
                result[label] = (names[j], round(best, 3))
            return result
