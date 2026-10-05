"""話者の聞き分け: 発言を 1.5 秒の窓に分けて声の特徴 (CAM++ の埋め込み) を取り、全体をまとめて分け直す。

実際の人の声は、同じ人でも 1.5 秒ずつの特徴どうしの類似度が 0.3〜0.5 程度しかなく、別の人との差が小さい。
そのため「この類似度以上なら同じ人」という固定の基準では、同じ人が何人にも分かれてしまう。
ここでは次の順に分ける (AMI の実際の会議音声で評価して決めた):
  1. スペクトラルクラスタリング (NME-SC) で、似た窓どうしのつながりから人数を自動で推定して分ける
  2. 各まとまりの重心から遠い窓の中に、互いに似たグループ (あまり話さない人) があれば取り出す
  3. 重心 (平均) どうしがよく似たまとまりはまとめ、窓が少なすぎるまとまりは最も近い人に入れる
重心どうしなら、同じ人は 0.8 以上、別の人は 0.5 前後以下とはっきり分かれる。
話者の番号は前回とできるだけ同じになるように引き継ぎ、変わった発言は respeaker として知らせる。
"""
import numpy as np

RATE = 16000
WIN_SEC, HOP_SEC = 1.5, 0.75   # 声の特徴を取る窓の長さと間隔
MIN_WIN_SEC = 0.5              # これより短い発言は声の特徴を取らず、直前の話者にする
MIN_CLUSTER = 8                # これより窓が少ない (約 6 秒未満の) まとまりは、最も近い人に入れる
MAX_SPEAKERS = 8
CLUSTER_CAP = 600              # 分け直しに使う窓の数の上限 (直近は全部、それより前は間引く。長い会議でも速さを保つ)
FREEZE_WINDOWS = 400           # 分け直しで話者を付け直すのは直近のこの数の窓 (約 5 分の発言) まで。それより前は固定する
FREEZE_START = 1200            # 窓がこの数 (約 15 分の発言) に満ちるまでは固定しない (始めのうちは声が少なく分けすぎるので、全体を分け直す)
MIN_PART_SEC = 0.8             # 発言の途中で話者が替わったとみなして分けるときの、各部分の最低の長さ
SPLIT_TAU = 0.6                # 重心から遠い窓のグループが、残りとこれ未満の類似度なら別の人として取り出す
MERGE_TAU = 0.85               # 重心どうしがこれ以上似ていれば同じ人としてまとめる
# 迷うときは分ける側に倒す: 同じ人が 2 人に分かれても、画面で同じ名前を付ければ 1 人にまとまるが、
# 別の人が 1 人にまとめられると発言ごとに直すしかないため


def unit(v):
    return v / (np.linalg.norm(v) + 1e-9)


def windows(audio):
    """発言を窓に分けた (開始, 終了) サンプル位置のリスト。最後の窓は末尾にそろえる。"""
    n, w, h = len(audio), int(WIN_SEC * RATE), int(HOP_SEC * RATE)
    if n < MIN_WIN_SEC * RATE:
        return []
    if n <= w:
        return [(0, n)]
    starts = list(range(0, n - w + 1, h))
    if starts[-1] + w < n:
        starts.append(n - w)
    return [(s, s + w) for s in starts]


# ───────── まとめ方 ─────────

def kmeans(X, k, restarts=8, iters=60, seed=0):
    rng = np.random.default_rng(seed)
    best, best_inertia = None, np.inf
    for _ in range(restarts):
        centers = [X[rng.integers(len(X))]]
        for _ in range(1, k):
            d = np.min([((X - c) ** 2).sum(1) for c in centers], axis=0)
            centers.append(X[rng.choice(len(X), p=d / d.sum())] if d.sum() > 0 else X[rng.integers(len(X))])
        C = np.array(centers)
        for _ in range(iters):
            labels = np.argmin(((X[:, None, :] - C[None]) ** 2).sum(2), axis=1)
            new = np.array([X[labels == j].mean(0) if (labels == j).any() else C[j] for j in range(k)])
            if np.allclose(new, C):
                break
            C = new
        inertia = ((X - C[labels]) ** 2).sum()
        if inertia < best_inertia:
            best, best_inertia = labels, inertia
    return best


def nme_sc(E, max_speakers=MAX_SPEAKERS, k_force=None):
    """
    NME-SC (Park et al., 2019): 近い p 個の窓とだけつないだグラフのラプラシアンの固有値の差 (eigengap) から人数を推定し、
    固有ベクトルを k-means で分ける。p も固有値の差が最もはっきりする値を選ぶので、類似度の基準を決めなくてよい。
    """
    n = len(E)
    if n < 4:
        return np.zeros(n, int)
    S = np.clip(E @ E.T, 0, 1)
    order = np.argsort(-S, axis=1)
    best = None
    for p in np.unique(np.linspace(2, max(2, n // 4), 10).astype(int)):
        A = np.zeros_like(S)
        A[np.repeat(np.arange(n), p + 1), order[:, :p + 1].ravel()] = 1
        A = (A + A.T) / 2
        L = np.diag(A.sum(1)) - A
        w = np.linalg.eigvalsh(L)
        gaps = np.diff(w[:min(max_speakers + 1, n)])
        k = int(np.argmax(gaps)) + 1
        ratio = p / n / (gaps.max() / (w[-1] + 1e-10) + 1e-10)
        if best is None or ratio < best[0]:
            best = (ratio, k, L)
    _, k, L = best
    if k_force is not None:
        k = min(k_force, n - 1)
    if k <= 1:
        return np.zeros(n, int)
    _, v = np.linalg.eigh(L)
    X = v[:, :k]
    return kmeans(X / (np.linalg.norm(X, axis=1, keepdims=True) + 1e-10), k)


def ahc_merges(E):
    """平均連結法を最近傍連鎖法 (O(n^2)) で最後までまとめ、(i, j, 類似度) を類似度の高い順に返す。"""
    n = len(E)
    S = E @ E.T
    np.fill_diagonal(S, -np.inf)
    size = np.ones(n)
    active = np.ones(n, bool)
    merges, chain = [], []
    while active.sum() > 1:
        if not chain:
            chain.append(int(np.nonzero(active)[0][0]))
        a = chain[-1]
        row = np.where(active, S[a], -np.inf)
        row[a] = -np.inf
        b = int(np.argmax(row))
        if len(chain) > 1 and row[chain[-2]] >= row[b]:
            b = chain[-2]  # 同点なら 1 つ前を選ぶ (連鎖が循環しないように)
        if len(chain) > 1 and b == chain[-2]:
            chain.pop()
            chain.pop()
            merges.append((a, b, float(S[a, b])))
            new = (size[a] * S[a] + size[b] * S[b]) / (size[a] + size[b])
            S[a] = new
            S[:, a] = new
            S[a, a] = -np.inf
            active[b] = False
            S[b] = -np.inf
            S[:, b] = -np.inf
            size[a] += size[b]
        else:
            chain.append(b)
    merges.sort(key=lambda m: -m[2])
    return merges


def cut(n, merges, threshold):
    """まとめた順を、類似度が threshold 以上のところまでたどってラベルにする。"""
    parent = list(range(n))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for i, j, sim in merges:
        if sim < threshold:
            break
        parent[find(j)] = find(i)
    roots = {}
    return np.array([roots.setdefault(find(x), len(roots)) for x in range(n)])


def split_outliers(X, labels, tau, min_part, low_q=0.3, max_clusters=MAX_SPEAKERS + 2):
    """
    各まとまりで重心から遠い窓 (下位 30%) を集め、その中で互いに似たグループがあり、
    残りの重心と tau 未満しか似ていなければ別の人として取り出す (あまり話さない人が、よく話す人に埋もれるのを防ぐ)。
    """
    labels = labels.copy()
    changed = True
    while changed and len(np.unique(labels)) < max_clusters:
        changed = False
        for c in np.unique(labels):
            idx = np.nonzero(labels == c)[0]
            if len(idx) < 3 * min_part:
                continue
            sims = X[idx] @ unit(X[idx].mean(0))
            low = idx[sims < np.quantile(sims, low_q)]
            if len(low) < min_part:
                continue
            S = X[idx] @ X[idx].T
            within = float(S[np.triu_indices(len(idx), 1)].mean())  # このまとまりの窓どうしの平均的な似かた
            groups = cut(len(low), ahc_merges(X[low]), within)
            best = None
            for g in np.unique(groups):
                gi = low[groups == g]
                if len(gi) < min_part:
                    continue
                cs = float(unit(X[gi].mean(0)) @ unit(X[np.setdiff1d(idx, gi)].mean(0)))
                if cs < tau and (best is None or cs < best[0]):
                    best = (cs, gi)
            if best is None:
                continue
            # 取り出したグループと残りの重心で、そのまとまりの窓を振り分け直す
            cg, cr = unit(X[best[1]].mean(0)), unit(X[np.setdiff1d(idx, best[1])].mean(0))
            to_new = (X[idx] @ cg) > (X[idx] @ cr)
            if to_new.sum() < min_part or (~to_new).sum() < min_part:
                continue
            labels[idx[to_new]] = labels.max() + 1
            changed = True
            break
    return labels


def merge_close(X, labels, tau, min_size):
    """重心どうしが tau 以上似ているまとまりをまとめ、窓が min_size 未満のまとまりは最も近い大きなまとまりに入れる。"""
    sums = {c: X[labels == c].sum(0) for c in np.unique(labels)}
    cnt = {c: int((labels == c).sum()) for c in sums}
    parent = {c: c for c in sums}
    while len(sums) > 1:
        keys = [k for k in sums if cnt[k] >= min_size]  # 小さいまとまりの重心はぶれるので基準では比べない
        if len(keys) < 2:
            break
        C = np.array([unit(sums[k]) for k in keys])
        S = C @ C.T
        np.fill_diagonal(S, -np.inf)
        i, j = np.unravel_index(np.argmax(S), S.shape)
        if S[i, j] < tau:
            break
        a, b = keys[i], keys[j]
        sums[a] = sums[a] + sums.pop(b)
        cnt[a] += cnt.pop(b)
        parent[b] = a

    def root(c):
        while parent[c] != c:
            c = parent[c]
        return c

    out = np.array([root(c) for c in labels])
    big = [k for k in sums if cnt[k] >= min_size] or [max(cnt, key=cnt.get)]
    cents = {k: unit(sums[k]) for k in big}
    for k in list(sums):
        if k not in cents:
            m = out == k
            v = X[m].mean(0)
            out[m] = max(cents, key=lambda c: float(v @ cents[c]))
    return out


# 直近の分け直しの様子 (動作の記録用。話した内容は含まない)
LAST_INFO = {}


def cluster(X, split_tau=SPLIT_TAU, merge_tau=MERGE_TAU, expected=None):
    """
    窓の特徴 (正規化済み) を話者に分ける。まとまりの番号 (0..) を返す。
    expected: 人数が分かっているとき (画面の「人数」)。声が十分に集まったら、その人数に分ける。
    """
    LAST_INFO.clear()
    LAST_INFO["windows"] = len(X)
    if len(X) < 2 * MIN_CLUSTER:
        return np.zeros(len(X), int)  # まだ声が少ないうちは 1 人とみなす
    labels = nme_sc(X)
    LAST_INFO["nme"] = len(np.unique(labels))
    if len(np.unique(labels)) == 1 and len(X) >= 4 * MIN_CLUSTER:
        # 回線や部屋の響きが全員に共通だと、別の人どうしもつながって 1 人と推定されやすい。
        # 無理に 2 つに分けてみて、どちらも十分な大きさで重心がはっきり違えば 2 人とする
        # (AMI の会議音声で、1 人を無理に分けた重心どうしは 0.64 以上、別の 2 人は 0.16〜0.85)
        two = nme_sc(X, k_force=2)
        a, b = X[two == 0], X[two == 1]
        if min(len(a), len(b)) >= 2 * MIN_CLUSTER:
            sim = float(unit(a.mean(0)) @ unit(b.mean(0)))
            LAST_INFO["force2_sim"] = round(sim, 2)
            if sim < split_tau + 0.05:
                labels = two
    labels = split_outliers(X, labels, split_tau, MIN_CLUSTER)
    LAST_INFO["split"] = len(np.unique(labels))
    labels = merge_close(X, labels, merge_tau, MIN_CLUSTER)
    if expected and expected >= 1:
        # 人数が分かっているときは、それより多く分かれた分を、重心の近いまとまりから順にまとめる
        # (ちょうどその人数に無理に分けると、長く話す 1 人が複数に分かれて正しさが下がった。AMI で 78.9% → 66.7%)
        if len(np.unique(labels)) > expected:
            labels = nme_sc(X, k_force=int(expected)) if expected > 1 else np.zeros(len(X), int)
        LAST_INFO["expected"] = expected
    ids = np.unique(labels)
    LAST_INFO["final"] = len(ids)
    LAST_INFO["sizes"] = sorted((int((labels == c).sum()) for c in ids), reverse=True)
    if len(ids) > 1:
        C = np.array([unit(X[labels == c].mean(0)) for c in ids])
        LAST_INFO["max_centroid_sim"] = round(float(np.max(C @ C.T - 2 * np.eye(len(ids)))), 2)
    return labels


# ───────── 記録中の聞き分け ─────────

class Speakers:
    def __init__(self, extractor=None):
        if extractor is None:
            import os
            import sherpa_onnx
            import app_data
            path = os.path.join(app_data.MODELS_DIR, "speaker-campplus", "model.onnx")
            extractor = sherpa_onnx.SpeakerEmbeddingExtractor(
                sherpa_onnx.SpeakerEmbeddingExtractorConfig(
                    model=path, num_threads=int(os.environ.get("GETTEXT_SPEAKER_THREADS", "1")), provider="cpu"))
        self.extractor = extractor
        self.sensitivity = 0.0
        self.expected = None   # 画面で指定した人数 (None = 自動)
        self.reset()

    def reset(self):
        self.wins = []        # 窓: {"emb", "dur", "item", "label"}
        self.items = {}       # 発言: id -> 話者番号
        self.centroids = {}   # 話者番号 -> 重心
        self.last = None
        self.pending = 0      # 前回分け直してから増えた窓の数
        self.merged = set()   # 画面で (同じ名前を付けて) まとめた話者の番号

    def embed(self, audio):
        stream = self.extractor.create_stream()
        stream.accept_waveform(RATE, audio)
        stream.input_finished()
        v = np.asarray(self.extractor.compute(stream), np.float32)
        return unit(v)

    def set_sensitivity(self, value):
        """聞き分けの細かさ (-1 = まとめる 〜 +1 = 細かく分ける)。記録済みの発言も分け直して、付け直しを返す。"""
        self.sensitivity = float(np.clip(value, -1, 1))
        return self.recluster()

    def set_expected(self, count):
        """聞き分ける人数 (0 / None = 自動)。記録済みの発言も分け直して、付け直しを返す。"""
        self.expected = int(count) if count and int(count) > 0 else None
        return self.recluster()

    def _taus(self):
        s = self.sensitivity
        return SPLIT_TAU + 0.1 * s, min(0.95, MERGE_TAU + 0.1 * s)

    def merge(self, frm, into):
        """画面で 2 人の話者を 1 人にまとめた。以後の分け直しでも into の番号にまとめる。付け直しを返す。"""
        if frm == into:
            return {}
        for w in self.wins:
            if w["label"] == frm:
                w["label"] = into
        changes = {}
        for k, v in self.items.items():
            if v == frm:
                self.items[k] = into
                changes[k] = into
        self.merged.add(into)
        embs = [w["emb"] for w in self.wins if w["label"] == into]
        self.centroids.pop(frm, None)
        if embs:
            self.centroids[into] = unit(np.mean(embs, axis=0))
        if self.last == frm:
            self.last = into
        return changes

    def _nearest(self, v):
        if not self.centroids:
            return None
        return max(self.centroids, key=lambda k: float(v @ self.centroids[k]))

    def split(self, audio):
        """
        発言の途中で話者が替わっていれば分ける。[(開始, 終了, 窓の特徴)] を返す (特徴は assign に渡して使い回す)。
        窓ごとに最も近い話者を調べ、2 窓 (約 2 秒) 以上続けて別の人になった所で分ける。
        """
        wins = [(s, e, self.embed(audio[s:e])) for s, e in windows(audio)]
        labels = [self._nearest(v) for _, _, v in wins]
        cuts = []
        i = 0
        while i < len(labels):
            j = i
            while j + 1 < len(labels) and labels[j + 1] == labels[i]:
                j += 1
            # labels[i..j] が同じ人。次の人も 2 窓以上続き、前の人も 2 窓以上なら、その間で分ける
            if labels[i] is not None and j + 1 < len(labels) and j - i + 1 >= 2:
                k = j + 1
                while k + 1 < len(labels) and labels[k + 1] == labels[j + 1]:
                    k += 1
                if k - j >= 2:
                    boundary = (wins[j][0] + wins[j][1] + wins[j + 1][0] + wins[j + 1][1]) // 4  # 窓の中心どうしの中間
                    if boundary >= MIN_PART_SEC * RATE and len(audio) - boundary >= MIN_PART_SEC * RATE \
                            and (not cuts or boundary - cuts[-1] >= MIN_PART_SEC * RATE):
                        cuts.append(boundary)
            i = j + 1
        bounds = [0] + cuts + [len(audio)]
        parts = []
        for s, e in zip(bounds[:-1], bounds[1:]):
            # その部分に中心が入る窓の特徴を渡す
            embs = [(v, (we - ws) / RATE) for ws, we, v in wins if s <= (ws + we) // 2 < e]
            parts.append((s, e, embs))
        return parts

    def assign(self, audio, seg_id, embs=None):
        """発言の話者番号と、付け直しになった以前の発言 {id: 話者番号} を返す。"""
        if embs is None:
            embs = [(self.embed(audio[s:e]), (e - s) / RATE) for s, e in windows(audio)]
        if not embs:
            # 短い相づちなどは直前の話者にする (声の特徴が不安定なので分け直しにも使わない)
            label = self.last if self.last is not None else self._new_label()
            self.items[seg_id] = label
            self.last = label
            return label, {}
        for v, dur in embs:
            label = self._nearest(v)
            if label is None:
                label = self.last if self.last is not None else self._new_label()
            self.wins.append({"emb": v, "dur": dur, "item": seg_id, "label": label})
        self.items[seg_id] = self._item_label(seg_id)
        self.pending += len(embs)
        changes = {}
        # 分け直しは窓が少し増えるたびに行う (会議が長くなるほど間隔を空けて速さを保つ)
        if self.pending >= max(4, len(self.wins) // 20):
            changes = self.recluster()
        label = self.items[seg_id]
        changes.pop(seg_id, None)
        self.last = label
        return label, changes

    def finalize(self):
        """記録の終わりに、最後の発言まで含めて分け直す。"""
        return self.recluster() if self.pending else {}

    def _item_label(self, seg_id):
        votes = {}
        for w in self.wins:
            if w["item"] == seg_id:
                votes[w["label"]] = votes.get(w["label"], 0.0) + w["dur"]
        return max(votes, key=votes.get) if votes else self.items.get(seg_id)

    def _new_label(self, taken=()):
        """まだ使われていない最も小さい番号 (分け直しで消えた番号は使い回し、「話者1, 話者4…」と飛ばないようにする)。"""
        used = set(self.items.values()) | {w["label"] for w in self.wins} | set(taken)
        label = 1
        while label in used:
            label += 1
        return label

    def recluster(self):
        """
        窓を分け直す。話者が変わった発言 {id: 新しい番号} を返す。
        付け直すのは直近の FREEZE_WINDOWS 個の窓だけで、それより前の発言の話者は変えない
        (長い会議で分け直すたびに人数が揺れ、30 分前の発言が別の人にまとめられる・分かれるのを防ぐ)。
        """
        self.pending = 0
        n = len(self.wins)
        if not n:
            return {}
        E = np.array([w["emb"] for w in self.wins])
        # 分け直しに使う窓: 直近は全部、それより前は時間にそって均等に間引く (今話している人の声が毎回入るように)
        use = np.arange(n)
        if n > CLUSTER_CAP:
            half = CLUSTER_CAP // 2
            older = np.linspace(0, n - half - 1, CLUSTER_CAP - half).astype(int)
            use = np.unique(np.concatenate([older, np.arange(n - half, n)]))
        split_tau, merge_tau = self._taus()
        core = cluster(E[use], split_tau, merge_tau, self.expected)
        cents = {c: unit(E[use][core == c].mean(0)) for c in np.unique(core)}
        keys = list(cents)
        C = np.array([cents[c] for c in keys])
        clusters = np.array(keys)[np.argmax(E @ C.T, axis=1)]  # 間引いた窓も含め、最も近い重心の人にする
        frozen_upto = max(0, n - FREEZE_WINDOWS) if n >= FREEZE_START else 0  # これより前の窓は話者を変えない
        # 新しいまとまりに、以前の番号を「重なりの大きい順」に引き継ぐ (固定した窓があればその重なりを優先する)
        ref = range(frozen_upto) if frozen_upto > 0 else range(n)
        overlap = {}
        for i in ref:
            w = self.wins[i]
            overlap[(clusters[i], w["label"])] = overlap.get((clusters[i], w["label"]), 0.0) + w["dur"]
        mapping, used = {}, set()
        for (c, old), _ in sorted(overlap.items(), key=lambda kv: -kv[1]):
            # 画面でまとめた話者は、声では別のまとまりに分かれても同じ番号にする
            if c not in mapping and (old not in used or old in self.merged):
                mapping[c] = old
                used.add(old)
        if frozen_upto > 0:
            # 固定した部分に出てこないまとまり (最近話し始めた人など) は、直近の窓での重なりで引き継ぐ
            recent = {}
            for i in range(frozen_upto, n):
                if clusters[i] not in mapping:
                    key = (clusters[i], self.wins[i]["label"])
                    recent[key] = recent.get(key, 0.0) + self.wins[i]["dur"]
            for (c, old), _ in sorted(recent.items(), key=lambda kv: -kv[1]):
                if c not in mapping and old not in used:
                    mapping[c] = old
                    used.add(old)
        # 新しい番号: 引き継いだ番号、固定した窓の番号、窓のない短い発言の番号は使わない
        with_windows = {w["item"] for w in self.wins}
        taken = set(mapping.values()) | {self.wins[i]["label"] for i in range(frozen_upto)} \
                | {v for k, v in self.items.items() if k not in with_windows}
        for c in keys:
            if c not in mapping:
                label = 1
                while label in taken:
                    label += 1
                mapping[c] = label
                taken.add(label)
        for i in range(frozen_upto, n):
            self.wins[i]["label"] = mapping[clusters[i]]
        # 最も近い話者を探すための重心は、付いている番号ごとの窓の平均 (固定した窓も含む)
        by_label = {}
        for w in self.wins:
            by_label.setdefault(w["label"], []).append(w["emb"])
        self.centroids = {l: unit(np.mean(v, axis=0)) for l, v in by_label.items() if len(v) >= MIN_CLUSTER}
        if not self.centroids:
            self.centroids = {mapping[c]: cents[c] for c in keys}
        votes = {}
        for w in self.wins[frozen_upto:]:
            v = votes.setdefault(w["item"], {})
            v[w["label"]] = v.get(w["label"], 0.0) + w["dur"]
        changes = {}
        for seg_id, v in votes.items():
            new = max(v, key=v.get)
            if new != self.items.get(seg_id):
                self.items[seg_id] = new
                changes[seg_id] = new
        if self.last is not None and self.last not in self.centroids and self.wins:
            self.last = self.wins[-1]["label"]
        return changes
