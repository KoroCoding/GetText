"""オフライン用のモデルを取得する (標準ライブラリのみ使用)。

版 (コミット) を固定し、重みファイルは SHA-256 で改ざんがないことを確認する。
使い方: python download_models.py <保存先フォルダ> [--english-only] [--no-asr] [--no-llm] [--only <名前>]
"""
import hashlib
import os
import sys
import urllib.request

HF = "https://huggingface.co/{repo}/resolve/{rev}/{file}"

MODELS = {
    # 英語 → 日本語 (FuguMT, CC BY-SA 4.0)
    "fugumt-en-ja": {
        "repo": "jkawamoto/fugumt-en-ja-ct2",
        "rev": "5bdad25200fcf251bbf019a6ee613002df52869b",
        "files": {
            "config.json": None,
            "model.bin": "4c05943b9c91597e52600e4dbd8ece8388ee804da97a782bd1ea91aef93e2104",
            "shared_vocabulary.json": None,
            "source.spm": None,
            "target.spm": None,
        },
    },
    # 約 200 言語 → 日本語 (NLLB-200 distilled 1.3B int8, CC BY-NC 4.0)
    "nllb-200-1.3B": {
        "repo": "OpenNMT/nllb-200-distilled-1.3B-ct2-int8",
        "rev": "70f572adafa4794890ce7826156a4209717855af",
        "files": {
            "config.json": None,
            "model.bin": "89dc4b9eb7f4dcd3ff36023c9b1c55fcecb47ddc0df97ab31b068ef9cf194940",
            "shared_vocabulary.json": None,
            "tokenizer.json": "e316b82de11d0f951f370943b3c438311629547285129b0b81dadabd01bca665",
        },
    },
    # 議事録: 日本語の音声認識 (kotoba-whisper v2.0, MIT)
    "kotoba-whisper-v2.0": {
        "asr": True,
        "repo": "kotoba-tech/kotoba-whisper-v2.0-faster",
        "rev": "f44edd35eaeb2274e85ac7b31fb2c6f59ff1c4bc",
        "files": {
            "config.json": None,
            "model.bin": "60d2bc2e33de9d43f2745be09caefe1161acab670f6796d4a750d8d848382b36",
            "preprocessor_config.json": None,
            "tokenizer.json": None,
            "vocabulary.json": None,
        },
    },
    # 議事録: 英語など日本語以外の音声認識と言語の判定 (OpenAI Whisper large-v3 turbo の CTranslate2 版, MIT)
    "whisper-large-v3-turbo": {
        "asr": True,
        "repo": "deepdml/faster-whisper-large-v3-turbo-ct2",
        "rev": "4df90f75321148c3a29a9e2351b7ddf8f5b115a8",
        "files": {
            "config.json": None,
            "model.bin": "e76620f83d5f5b69efd3d87e3dc180c1bd21df9fbebacfd4335e5e1efcc018da",
            "preprocessor_config.json": None,
            "tokenizer.json": None,
            "vocabulary.json": None,
        },
    },
    # 議事録: GPU の無い PC 向けの速い日本語の音声認識 (ReazonSpeech k2 v2, Apache-2.0。CPU だけでも記録に追いつく)
    "reazonspeech-k2-v2": {
        "asr": True,
        "repo": "reazon-research/reazonspeech-k2-v2",
        "rev": "291488c8151be24d7da4bf7af26e533fad96e407",
        "files": {
            "encoder-epoch-99-avg-1.int8.onnx": "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d",
            "decoder-epoch-99-avg-1.int8.onnx": "8f0bff94d38797b03b762634ed03211a8e303d06cc4603cdd0cf4199d6eb1485",
            "joiner-epoch-99-avg-1.int8.onnx": "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de",
            "tokens.txt": None,
        },
    },
    # 文字の読み取り: 小さい・ぼやけた文字を、画面が止まったときに読み直す高精度の認識モデル
    # (PP-OCRv6 rec medium, Apache-2.0。RapidOCR の公式の配布。ぼやけた会議資料の画像で誤り率 33.6% → 28.9%)
    "ppocrv6-rec-medium": {
        "urls": {
            "PP-OCRv6_rec_medium.onnx": (
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx",
                "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b",
            ),
        },
    },
    # 議事録: 話者の聞き分け (3D-Speaker CAM++, Apache-2.0。sherpa-onnx の公式リリース)
    "speaker-campplus": {
        "asr": True,
        "urls": {
            "model.onnx": (
                "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/"
                "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
                "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2",
            ),
        },
    },
    # 議事録: 前後の文脈による聞き間違いの補正 (Qwen3-4B-Instruct-2507 を CTranslate2 int8 に変換したもの, Apache-2.0)
    "qwen3-4b-instruct": {
        "asr": True,
        "repo": "jncraton/Qwen3-4B-Instruct-2507-ct2-int8",
        "rev": "ab26c167dd687295980bbfc9f6b696f455b794c4",
        "files": {
            "config.json": None,
            "generation_config.json": None,
            "model.bin": "4307805dc2a0f2b7a10e2d514e4627db153924ad2d4a808dc9a3ea593c1d2083",
            "tokenizer.json": "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4",
            "tokenizer_config.json": None,
            "vocabulary.json": None,
        },
    },
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download(url, dest):
    tmp = dest + ".part"
    req = urllib.request.Request(url, headers={"User-Agent": "GetText-setup"})
    with urllib.request.urlopen(req, timeout=60) as r, open(tmp, "wb") as f:  # 通信が止まったら待ち続けない
        total = int(r.headers.get("Content-Length") or 0)
        done = 0
        last = -1
        while True:
            chunk = r.read(1 << 20)
            if not chunk:
                break
            f.write(chunk)
            done += len(chunk)
            if total:
                pct = done * 100 // total
                if pct != last and pct % 5 == 0:
                    print(f"    {pct:3d}% ({done / 1e6:.0f} / {total / 1e6:.0f} MB)", flush=True)
                    last = pct
    # 通信が途中で切れても例外にならず、途中までのファイルで終わることがある (検証の値が無いファイルは見分けられない)
    if total and done != total:
        raise IOError(f"ダウンロードが途中で切れました ({done} / {total} バイト): {url}")
    os.replace(tmp, dest)


def fetch(url, dest, expected, file):
    if os.path.exists(dest) and (expected is None or sha256(dest) == expected):
        print(f"  {file}: 取得済み", flush=True)
        return
    print(f"  {file}: ダウンロード中", flush=True)
    download(url, dest)
    if expected is not None:
        actual = sha256(dest)
        if actual != expected:
            os.remove(dest)
            raise SystemExit(f"  {file}: SHA-256 が一致しません (期待 {expected}, 実際 {actual})")
        print(f"  {file}: SHA-256 OK", flush=True)


def main():
    root = sys.argv[1]
    names = [n for n, spec in MODELS.items()
             if not ("--no-asr" in sys.argv and spec.get("asr"))
             and not ("--english-only" in sys.argv and n == "nllb-200-1.3B")
             and not ("--no-llm" in sys.argv and n == "qwen3-4b-instruct")]
    if "--only" in sys.argv:
        names = [sys.argv[sys.argv.index("--only") + 1]]
    for name in names:
        spec = MODELS[name]
        folder = os.path.join(root, name)
        os.makedirs(folder, exist_ok=True)
        print(f"[{name}] {spec.get('repo', 'GitHub release')}", flush=True)
        for file, expected in spec.get("files", {}).items():
            fetch(HF.format(repo=spec["repo"], rev=spec["rev"], file=file), os.path.join(folder, file), expected, file)
        for file, (url, expected) in spec.get("urls", {}).items():
            fetch(url, os.path.join(folder, file), expected, file)
    print("完了", flush=True)


if __name__ == "__main__":
    main()
