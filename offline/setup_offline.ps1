# GetText の AI 機能のセットアップ (AI OCR・オフライン翻訳・議事録)
# - %LOCALAPPDATA%\GetText\offline に Python 3.11 の専用環境 (venv) を作る
# - CTranslate2 / RapidOCR / faster-whisper などをインストールし、モデルをダウンロードする
# - NVIDIA の GPU があり CUDA Toolkit が無い PC には、GPU 用の cuBLAS を pip で入れる
# 使い方: powershell -ExecutionPolicy Bypass -File setup_offline.ps1 [-BasePython <python.exe>] [-EnglishOnly] [-NoAsr] [-NoLlm]
#   -EnglishOnly  英語以外の翻訳モデル (NLLB-200) を入れない
#   -NoAsr        議事録 (音声認識・話者認識・文脈補正) のモデルを入れない
#   -NoLlm        議事録の文脈補正 (Qwen3-4B, 約 3.9GB) だけ入れない
#   -BasePython   venv の元にする Python 3.11 (省略すると py -3.11)
#   -Root / -SkipModels  確認用 (別の場所に作る / モデルをダウンロードしない)
param([string]$BasePython = '', [switch]$EnglishOnly, [switch]$NoAsr, [switch]$NoLlm, [string]$Root = '', [switch]$SkipModels)
$ErrorActionPreference = 'Stop'
# 外部のプログラム (py / dotnet / winget / where) を呼ぶ。エラー表示 (標準エラー出力) を出しても中断しない。
# (ErrorActionPreference が Stop のとき、2>$null などで受けたエラー表示が PowerShell 5.1 では中断扱いになるため)
function Invoke-Native([scriptblock]$Block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $old }
}


$root = if ($Root) { $Root } else { Join-Path $env:LOCALAPPDATA 'GetText\offline' }
$venv = Join-Path $root 'venv'
$python = Join-Path $venv 'Scripts\python.exe'
New-Item -ItemType Directory -Force $root | Out-Null

# 以前に別の版の Python で作った環境は、決まった版の部品が入らないので作り直す
if (Test-Path -LiteralPath $python) {
    $ver = (Invoke-Native { & $python -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null } | Select-Object -Last 1)
    if ("$ver".Trim() -ne '3.11') {
        Write-Host "== Python 環境が Python $ver で作られているので、Python 3.11 で作り直します" -ForegroundColor Yellow
        Remove-Item -LiteralPath $venv -Recurse -Force
    }
}
if (-not (Test-Path -LiteralPath $python)) {
    Write-Host '== Python 環境を作成しています'
    if ($BasePython) { Invoke-Native { & $BasePython -m venv $venv } }
    elseif (Get-Command py -ErrorAction SilentlyContinue) { Invoke-Native { & py -3.11 -m venv $venv } }
    else {
        # py ランチャーが無いときは PATH の python を使うが、3.11 でなければ使わない (Microsoft Store の仮のものなども含む)
        $ver = (Invoke-Native { & python -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null } | Select-Object -Last 1)
        if ("$ver".Trim() -ne '3.11') { throw "Python 3.11 が見つかりません (見つかった python: $ver)。setup.bat を実行すると Python 3.11 を入れられます。" }
        Invoke-Native { & python -m venv $venv }
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $python)) { throw 'Python 3.11 の venv を作成できませんでした。Python 3.11 をインストールしてください。' }
}

Write-Host '== 翻訳エンジン (CTranslate2) と AI OCR (RapidOCR / PP-OCRv6) をインストールしています'
Invoke-Native { & $python -m pip install --disable-pip-version-check --quiet --upgrade pip }
# CPU 版 onnxruntime は DirectML 版 (GPU 対応、CPU でも動く) と共存できないので外す
# (未インストール時の警告で止まらないよう、この行だけエラーで中断しない)
$ErrorActionPreference = 'Continue'
& $python -m pip uninstall -y --quiet onnxruntime 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
Invoke-Native { & $python -m pip install --disable-pip-version-check --quiet ctranslate2==4.8.2 sentencepiece==0.2.2 tokenizers==0.23.2 rapidocr==3.9.2 onnxruntime-directml==1.24.4 }
if ($LASTEXITCODE -ne 0) { throw 'パッケージのインストールに失敗しました' }

if (-not $NoAsr) {
    Write-Host '== 議事録用の音声認識 (faster-whisper) と話者認識 (sherpa-onnx) をインストールしています'
    # faster-whisper は CPU 版 onnxruntime を要求するが、DirectML 版で代用できるので依存関係は入れない
    Invoke-Native { & $python -m pip install --disable-pip-version-check --quiet --no-deps faster-whisper==1.2.1 }
    if ($LASTEXITCODE -ne 0) { throw 'faster-whisper のインストールに失敗しました' }
    Invoke-Native { & $python -m pip install --disable-pip-version-check --quiet av==18.1.0 huggingface_hub==0.26.5 sherpa-onnx==1.13.8 janome==0.5.0 }
    if ($LASTEXITCODE -ne 0) { throw 'パッケージのインストールに失敗しました' }
    Write-Host '   (上に「faster-whisper requires onnxruntime」と出ても問題ありません。GPU 対応版の onnxruntime-directml で動きます)'
}

# NVIDIA の GPU があるのに cuBLAS (CUDA Toolkit) が無ければ、pip 版を入れる (offline\gpu_paths.py が読み込む)
$hasNvidia = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
$hasCublas = [bool](Invoke-Native { & where.exe cublas64_12.dll 2>$null })
if ($hasNvidia -and -not $hasCublas) {
    Write-Host '== NVIDIA の GPU 用の部品 (cuBLAS / CUDA Runtime, 約 0.6GB) をインストールしています'
    Invoke-Native { & $python -m pip install --disable-pip-version-check --quiet "nvidia-cublas-cu12>=12.4,<13" "nvidia-cuda-runtime-cu12>=12.4,<13" }
    if ($LASTEXITCODE -ne 0) { Write-Warning 'GPU 用の部品を入れられませんでした。CPU で動きます (遅くなります)。' }
} elseif (-not $hasNvidia) {
    Write-Host '   (NVIDIA の GPU が見つからないので、翻訳と議事録は CPU で動かします。AI OCR は DirectML で GPU を使います)'
}

if ($SkipModels) { Write-Host 'モデルのダウンロードを省きました'; return }
Write-Host '== モデルをダウンロードしています (容量が大きいので時間がかかります)'
$models = Join-Path $root 'models'
$dlArgs = @((Join-Path $PSScriptRoot 'download_models.py'), $models)
if ($EnglishOnly) { $dlArgs += '--english-only' }
if ($NoAsr) { $dlArgs += '--no-asr' }
if ($NoLlm) { $dlArgs += '--no-llm' }
Invoke-Native { & $python @dlArgs }
if ($LASTEXITCODE -ne 0) { throw 'モデルのダウンロードに失敗しました' }

Write-Host ''
Write-Host "セットアップが完了しました: $root"
