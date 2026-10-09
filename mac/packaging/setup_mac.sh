#!/bin/bash
# GetText (Mac 版) の AI 機能のセットアップ (AI OCR・オフライン翻訳・議事録)
# - ~/Library/Application Support/GetText/offline に Python 3.11 の専用環境 (venv) を作る
# - CTranslate2 / RapidOCR / faster-whisper などを入れ、モデルをダウンロードする (約 9GB。取得済みの分は省く)
# 使い方: bash setup_mac.sh [--no-asr] [--no-llm] [--english-only] [--with-nllb] [--skip-models] [--python <python3.11 の場所>] [--root <場所>]
#   英語以外の言語の翻訳モデル (NLLB-200、非商用ライセンス) は --with-nllb のとき・聞かれて y と答えたときだけ入れる
# GetText の設定 → 情報 → 「セットアップを実行」で、ターミナルから実行される。
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
scripts="$here/offline"                        # GetText.app/Contents/Resources/offline
[ -d "$scripts" ] || scripts="$here/../offline" # 開発中 (mac/packaging から実行したとき)
root="$HOME/Library/Application Support/GetText/offline"
python_base="${PYTHON:-}"
no_asr=0; no_llm=0; english_only=0; skip_models=0; with_nllb=0
while [ $# -gt 0 ]; do
    case "$1" in
        --no-asr) no_asr=1 ;;
        --no-llm) no_llm=1 ;;
        --english-only) english_only=1 ;;
        --with-nllb) with_nllb=1 ;;
        --skip-models) skip_models=1 ;;
        --python) python_base="${2:?--python には Python の場所が要ります}"; shift ;;
        --root) root="${2:?--root には入れる場所が要ります}"; shift ;;
        *) echo "知らない指定です: $1"; exit 2 ;;
    esac
    shift
done

finish() {
    local code=$?
    echo
    if [ $code -eq 0 ]; then echo "セットアップが完了しました: $root"; else echo "セットアップできませんでした (上のメッセージを見てください)"; fi
    # ターミナルから開かれたときは、結果を読めるよう閉じずに待つ
    if [ -t 0 ] && [ -z "${CI:-}" ]; then read -r -p "Enter キーで閉じます" _ || true; fi
}
trap finish EXIT

is311() { "$1" -c 'import sys; sys.exit(0 if sys.version_info[:2] == (3, 11) else 1)' 2>/dev/null; }

# Python 3.11 を探す (Homebrew・python.org の入れ方のどちらでもよい)
if [ -z "$python_base" ]; then
    for candidate in python3.11 /opt/homebrew/bin/python3.11 /usr/local/bin/python3.11 \
                     /Library/Frameworks/Python.framework/Versions/3.11/bin/python3.11 python3; do
        if command -v "$candidate" >/dev/null 2>&1 && is311 "$(command -v "$candidate")"; then
            python_base="$(command -v "$candidate")"
            break
        fi
    done
fi
if [ -z "$python_base" ] || ! is311 "$python_base"; then
    cat <<'MSG'
Python 3.11 が見つかりません。次のどちらかで入れてから、もう一度「セットアップを実行」を押してください。
  ・https://www.python.org/downloads/macos/ から「Python 3.11.x」の macOS 64-bit universal2 installer を入れる
  ・Homebrew を使っているなら、ターミナルで  brew install python@3.11
MSG
    exit 1
fi
echo "== Python: $python_base"

mkdir -p "$root"
venv="$root/venv"
python="$venv/bin/python3"
if [ -x "$python" ] && ! is311 "$python"; then
    echo "== Python 環境が 3.11 以外で作られているので作り直します"
    rm -rf "$venv"
fi
if [ ! -x "$python" ]; then
    echo "== Python 環境を作っています"
    "$python_base" -m venv "$venv"
fi

pip() { "$python" -m pip install --disable-pip-version-check --quiet "$@"; }
echo "== 翻訳エンジン (CTranslate2) と AI OCR (RapidOCR) を入れています"
pip --upgrade pip
# Intel の Mac 用の onnxruntime は 1.23 まで
if [ "$(uname -m)" = "arm64" ]; then onnx="onnxruntime==1.24.4"; else onnx="onnxruntime==1.23.2"; fi
pip ctranslate2==4.8.2 sentencepiece==0.2.2 tokenizers==0.23.2 rapidocr==3.9.2 "$onnx" certifi
if [ $no_asr -eq 0 ]; then
    echo "== 議事録用の音声認識 (faster-whisper) と話者認識 (sherpa-onnx) を入れています"
    pip --no-deps faster-whisper==1.2.1
    pip av==18.1.0 huggingface_hub==0.26.5 sherpa-onnx==1.13.8 janome==0.5.0
fi

if [ $skip_models -eq 1 ]; then echo "モデルのダウンロードを省きました"; exit 0; fi
# 英語以外の言語の翻訳 (中国語・韓国語など約 200 言語) は、非商用でのみ使えるモデルなので、選んだときだけ入れる
if [ $english_only -eq 0 ] && [ $with_nllb -eq 0 ] && [ ! -f "$root/models/nllb-200-1.3B/model.bin" ]; then
    answer=""
    if [ -t 0 ]; then
        echo
        echo "英語以外の言語 (中国語・韓国語など約 200 言語) の翻訳モデル NLLB-200 も入れますか？"
        echo "  ダウンロード 約 1.4GB。ライセンスは CC BY-NC 4.0 (非商用でのみ使えます。仕事など商用には使えません)。"
        echo "  入れないときは、英語以外の文字には日本語訳を付けません (翻訳を Google・DeepL にすれば訳せます)。"
        read -r -p "  入れる (y/N): " answer || true
    fi
    case "$answer" in y|Y|yes|はい) with_nllb=1 ;; *) english_only=1 ;; esac
fi
echo "== モデルをダウンロードしています (容量が大きいので時間がかかります)"
# python.org の Python は証明書の設定が無いことがあるので、certifi の証明書を使う
export SSL_CERT_FILE="$("$python" -m certifi)"
args=("$scripts/download_models.py" "$root/models")
[ $english_only -eq 1 ] && args+=(--english-only)
[ $no_asr -eq 1 ] && args+=(--no-asr)
[ $no_llm -eq 1 ] && args+=(--no-llm)
"$python" "${args[@]}"
