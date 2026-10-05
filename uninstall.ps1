# GetText のアンインストール
#  1. デスクトップとスタートメニューのショートカットを消す
#  2. 聞いてから、AI の環境とモデル・議事録・設定を消す (議事録と設定は、残すのが既定)
#  3. GetText のフォルダ (このファイルのあるフォルダ) は、終わってから手で消す (動いている自分は消せないため)
# 使い方: uninstall.bat [-Yes]   (-Yes: 確認せずに、ショートカットと AI の環境・モデルだけを消す。議事録と設定は残す)
param([switch]$Yes)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Read-Answer($prompt) {
    Write-Host -NoNewline "$prompt "
    $a = [Console]::ReadLine()
    if ($null -eq $a) { return '' }
    return $a.Trim().Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant()
}
# 消すかを聞く。defaultYes なら Enter だけで消す
function Confirm-Remove($question, [bool]$defaultYes) {
    if ($Yes) { return $defaultYes }
    $hint = if ($defaultYes) { '(Y/n)' } else { '(y/N)' }
    while ($true) {
        $a = Read-Answer "$question $hint"
        if ($a -eq '') { return $defaultYes }
        if ($a -match '^(y|yes|はい)$') { return $true }
        if ($a -match '^(n|no|いいえ)$') { return $false }
        Write-Host '  y (はい) か n (いいえ) を入力してください。' -ForegroundColor Yellow
    }
}
function Remove-Folder($path, $label) {
    if (-not (Test-Path -LiteralPath $path)) { Write-Host "  ($label はありません)"; return }
    Remove-Item -LiteralPath $path -Recurse -Force
    Write-Host "  消しました: $label ($path)" -ForegroundColor Green
}

try {
    Write-Host '==============================================' -ForegroundColor Cyan
    Write-Host ' GetText のアンインストール' -ForegroundColor Cyan
    Write-Host '==============================================' -ForegroundColor Cyan
    if (Get-Process GetText -ErrorAction SilentlyContinue) {
        throw 'GetText が動いています。GetText を終了してから、もう一度実行してください。'
    }

    # ── 1. ショートカット
    foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ }) {
        $lnk = Join-Path $dir 'GetText.lnk'
        if (Test-Path -LiteralPath $lnk) {
            Remove-Item -LiteralPath $lnk -Force
            Write-Host "  消しました: ショートカット ($lnk)" -ForegroundColor Green
        }
    }

    # ── 2. AI の環境とモデル・議事録・設定
    $local = Join-Path $env:LOCALAPPDATA 'GetText'
    $offline = Join-Path $local 'offline'
    $minutes = Join-Path $local 'minutes'
    $settings = Join-Path $env:APPDATA 'GetText'
    Write-Host ''
    if (Confirm-Remove "AI の環境とモデル ($offline、数 GB) を消しますか？" $true) { Remove-Folder $offline 'AI の環境とモデル' }
    if (Confirm-Remove "議事録と記録した音声 ($minutes) も消しますか？ (消すと元に戻せません)" $false) { Remove-Folder $minutes '議事録と記録した音声' }
    if (Confirm-Remove "設定・覚えた話者の声・DeepL のキー ($settings ほか) も消しますか？" $false) {
        Remove-Folder $settings '設定'
        $voices = Join-Path $local 'voices.json'
        if (Test-Path -LiteralPath $voices) { Remove-Item -LiteralPath $voices -Force; Write-Host "  消しました: 覚えた話者の声 ($voices)" -ForegroundColor Green }
    }
    $logs = Join-Path $local 'logs'
    if (Test-Path -LiteralPath $logs) { Remove-Folder $logs '記録 (ログ)' }
    # 何も残っていなければ、入れ物のフォルダも消す
    if ((Test-Path -LiteralPath $local) -and -not (Get-ChildItem -LiteralPath $local -Force)) { Remove-Item -LiteralPath $local -Force }

    Write-Host ''
    Write-Host '==============================================' -ForegroundColor Green
    Write-Host ' アンインストールが終わりました' -ForegroundColor Green
    Write-Host '==============================================' -ForegroundColor Green
    Write-Host " 最後に、GetText のフォルダ ($here) を消してください。"
    Write-Host ' 画面の録画で保存した動画 (既定は ビデオ\GetText) は消していません。'
    Write-Host ' セットアップで入れた Python 3.11 は、ほかのアプリも使うことがあるので消していません (要らなければ「設定 → アプリ」から消せます)。'
}
catch {
    Write-Host ''
    Write-Host "アンインストールを中断しました: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
