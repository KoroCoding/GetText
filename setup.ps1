# GetText のセットアップ (別の PC で使うとき、最初に 1 回だけ setup.bat から実行する)
#  1. .NET 9 SDK と Python 3.11 が無ければ winget でインストールする
#  2. GetText をビルドする
#  3. AI 機能 (AI OCR・オフライン翻訳・議事録) の Python 環境とモデルを用意する (offline\setup_offline.ps1)
#  4. デスクトップとスタートメニューにショートカットを作る
# 使い方: setup.bat [-Mode Full|Standard|Minimal] [-Yes] [-WithNllb]
#   -Mode  入れる機能 (省略すると選ぶ画面を出す)
#   -WithNllb  英語以外の言語の翻訳モデル (NLLB-200、非商用ライセンス) も入れる (省略すると聞く。-Yes のときは入れない)
#   -Yes   確認を省略する
#   -SkipShortcut / -NoLaunch  確認用 (ショートカットを作らない / 最後に起動しない)
param([ValidateSet('', 'Full', 'Standard', 'Minimal')][string]$Mode = '', [switch]$Yes, [switch]$SkipShortcut, [switch]$NoLaunch, [switch]$WithNllb)
$ErrorActionPreference = 'Stop'
# 外部のプログラム (py / dotnet / winget / where) を呼ぶ。エラー表示 (標準エラー出力) を出しても中断しない。
# (ErrorActionPreference が Stop のとき、2>$null などで受けたエラー表示が PowerShell 5.1 では中断扱いになるため)
function Invoke-Native([scriptblock]$Block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $old }
}

$here = $PSScriptRoot
# ビルド済みの配布版 (GetText_配布版.zip) は、このフォルダに GetText.exe があり、ソース (GetText.csproj) は無い。
# そのときは .NET SDK もビルドも要らない (アプリに .NET が入っている)
$prebuilt = (Test-Path -LiteralPath (Join-Path $here 'GetText.exe')) -and -not (Test-Path -LiteralPath (Join-Path $here 'GetText.csproj'))
$exe = if ($prebuilt) { Join-Path $here 'GetText.exe' } else { Join-Path $here 'bin\Release\net9.0-windows10.0.19041.0\GetText.exe' }
$log = Join-Path $here 'setup.log'
try { Start-Transcript -Path $log -Force | Out-Null } catch { }

function Step($text) { Write-Host ''; Write-Host "■ $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  OK: $text" -ForegroundColor Green }
function Refresh-Path {
    # winget でインストールした直後は、このウィンドウの PATH に反映されていないので読み直す
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
# 日本語入力がオンのまま打った全角の「１」「ｙ」なども半角として受け取る
function Read-Answer($prompt) {
    $a = Read-Host $prompt
    if ($null -eq $a) { return '' }
    return $a.Trim().Normalize([Text.NormalizationForm]::FormKC)
}
# 入れるかどうかを聞く (Enter だけなら入れない)
function Confirm-Optional($question) {
    if ($Yes) { return $false }
    while ($true) {
        $a = Read-Answer "$question (y/N)"
        if ($a -eq '' -or $a -match '^(n|no|いいえ)$') { return $false }
        if ($a -match '^(y|yes|はい)$') { return $true }
        Write-Host '  y (はい) か N (いいえ) を入力してください。' -ForegroundColor Yellow
    }
}
function Confirm-Step($question) {
    if ($Yes) { return $true }
    while ($true) {
        $a = Read-Answer "$question (Y/n)"
        if ($a -eq '' -or $a -match '^(y|yes|はい)$') { return $true }
        if ($a -match '^(n|no|いいえ)$') { return $false }
        Write-Host '  Y (はい) か n (いいえ) を入力してください。' -ForegroundColor Yellow
    }
}
# この PC に合う機能のおすすめ (NVIDIA の GPU とメモリ 16GB 以上ならフル、メモリ 8GB 以上なら標準)
function Get-Recommended {
    $gpu = $false; $ramGb = 16
    try { $gpu = [bool](Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }) } catch { }
    try { $ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB) } catch { }
    if ($gpu -and $ramGb -ge 16) { return @{ Number = '1'; Reason = "NVIDIA の GPU とメモリ ${ramGb}GB があるため" } }
    if ($ramGb -ge 8) { return @{ Number = '2'; Reason = $(if ($gpu) { "メモリが ${ramGb}GB のため (文脈による補正の AI は約 8GB 使います)" } else { "GPU が無いため (文脈による補正の AI は CPU では重く、議事録が遅れます)" }) } }
    return @{ Number = '3'; Reason = "メモリが ${ramGb}GB と少ないため" }
}

try {
    Write-Host '==============================================' -ForegroundColor Cyan
    Write-Host ' GetText のセットアップ' -ForegroundColor Cyan
    Write-Host '==============================================' -ForegroundColor Cyan

    # ── Windows の確認
    $build = [Environment]::OSVersion.Version.Build
    if (-not [Environment]::Is64BitOperatingSystem) { throw '64 ビット版の Windows 10 / 11 が必要です。' }
    if ($build -lt 19041) { throw "Windows 10 バージョン 2004 (ビルド 19041) 以降が必要です (この PC: $build)。" }
    if ($build -lt 20348) { Write-Warning 'この Windows では、議事録の「ウィンドウを選んで音声を取り込む」機能が使えません (Windows 11 が必要)。マイクとファイルからの文字起こしは使えます。' }

    # ── 入れる機能を選ぶ
    if (-not $Mode) {
        $rec = Get-Recommended
        Write-Host ''
        Write-Host '入れる機能を選んでください (あとから setup.bat をもう一度実行すれば追加できます):'
        Write-Host '  1. フル    … 文字の読み取り・英語の翻訳・議事録 (文脈による聞き間違いの補正・要約まで)      ダウンロード 約 8GB'
        Write-Host '  2. 標準    … 1 から「文脈による聞き間違いの補正」と「要約」を除く                         ダウンロード 約 4GB'
        Write-Host '  (英語以外の言語の翻訳は、このあと入れるかを聞きます)'
        Write-Host '  3. 最小    … 文字の読み取りと英語の翻訳だけ (議事録なし)                                  ダウンロード 約 1GB'
        Write-Host "  おすすめ: $($rec.Number) ($($rec.Reason))" -ForegroundColor Green
        while (-not $Mode) {
            $choice = Read-Answer "番号 (Enter だけで $($rec.Number))"
            if ($choice -eq '') { $choice = $rec.Number }
            $Mode = @{ '1' = 'Full'; '2' = 'Standard'; '3' = 'Minimal' }[$choice]
            if (-not $Mode) { Write-Host "  1〜3 の番号を入力してください (入力: $choice)" -ForegroundColor Yellow }
        }
    }
    Ok "機能: $Mode"
    # 英語以外の言語の翻訳 (中国語・韓国語など約 200 言語) は、非商用でのみ使えるモデルなので、選んだときだけ入れる
    $nllbDir = Join-Path $env:LOCALAPPDATA 'GetText\offline\models\nllb-200-1.3B'
    $haveNllb = Test-Path -LiteralPath (Join-Path $nllbDir 'model.bin')
    if ($Mode -ne 'Minimal' -and -not $WithNllb -and -not $haveNllb) {
        Write-Host ''
        Write-Host '英語以外の言語 (中国語・韓国語など約 200 言語) の翻訳モデル NLLB-200 も入れますか？'
        Write-Host '  ダウンロード 約 1.4GB。ライセンスは CC BY-NC 4.0 (非商用でのみ使えます。仕事など商用には使えません)。'
        Write-Host '  入れないときは、英語以外の文字には日本語訳を付けません (翻訳を Google・DeepL にすれば訳せます)。'
        $WithNllb = Confirm-Optional '  入れる'
    }
    if ($haveNllb) { $WithNllb = $true } # (入っているものは消さずに使う)
    Ok ("英語以外の翻訳 (NLLB-200): " + $(if ($WithNllb) { '入れる' } else { '入れない' }))
    # ダウンロードの前に空き容量を確かめる (途中で足りなくなると、壊れたファイルが残るため)
    $needGb = @{ Full = 10; Standard = 6; Minimal = 2 }[$Mode] + $(if ($WithNllb -and $Mode -ne 'Minimal') { 2 } else { 0 })
    # 入れ直し・新しい版への更新では、もう入っている AI の部品 (モデル・Python の環境) の分は要らない
    $installed = Join-Path $env:LOCALAPPDATA 'GetText\offline'
    if (Test-Path -LiteralPath $installed) {
        $haveGb = [math]::Round(((Get-ChildItem -LiteralPath $installed -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum) / 1GB, 1)
        if ($haveGb -gt 0) {
            $needGb = [math]::Max(1, [math]::Round($needGb - $haveGb, 1))
            Ok "入っている AI の部品: ${haveGb}GB (そのまま使います)"
        }
    }
    try {
        $drive = (Get-Item $env:LOCALAPPDATA).PSDrive
        $freeGb = if ($drive -and $drive.Free) { [math]::Round($drive.Free / 1GB, 1) } else { [double]::MaxValue }  # ネットワーク上の場所などで調べられないときは止めない
        if ($freeGb -lt $needGb) {
            throw "$($drive.Name): ドライブの空きが足りません (空き ${freeGb}GB / 必要 約 ${needGb}GB)。不要なファイルを消すか、「2. 標準」「3. 最小」を選んでください。"
        }
        if ($freeGb -lt [double]::MaxValue) { Ok "空き容量: ${freeGb}GB (必要 約 ${needGb}GB)" }
    }
    catch [System.Management.Automation.ItemNotFoundException] { }
    # 動いていると、ビルドしたアプリや AI の環境を置き換えられないので、閉じてもらう
    Wait-Process GetText -Timeout 15 -ErrorAction SilentlyContinue  # GetText の「セットアップを実行」から起動したときは、閉じ終わるのを待つ
    while (Get-Process GetText -ErrorAction SilentlyContinue) {
        Write-Host '  GetText が起動中です。議事録を保存してから GetText を閉じ、Enter を押してください。' -ForegroundColor Yellow
        if ($Yes) { throw 'GetText が起動中です。閉じてからもう一度実行してください。' }
        Read-Host | Out-Null
    }

    # ── 1. .NET 9 SDK と Python 3.11
    Step $(if ($prebuilt) { '1/4 必要なソフト (Python 3.11) を確認しています (ビルド済みの配布版なので .NET は不要)' } else { '1/4 必要なソフト (.NET 9 SDK / Python 3.11) を確認しています' })
    Refresh-Path
    $hasDotnet = $prebuilt
    if ($prebuilt) {
        # インターネットから入手した ZIP の中のファイルは「ブロック」の印が付き、起動のたびに警告が出るので外す
        Get-ChildItem -LiteralPath $here -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    }
    elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $hasDotnet = [bool]((Invoke-Native { & dotnet --list-sdks 2>$null }) -match '^9\.')
    }
    function Find-Python {
        if (Get-Command py -ErrorAction SilentlyContinue) {
            $p = Invoke-Native { & py -3.11 -c 'import sys; print(sys.executable)' 2>$null }
            if ($LASTEXITCODE -eq 0 -and $p) { return $p.Trim() }
        }
        foreach ($p in @("$env:LOCALAPPDATA\Programs\Python\Python311\python.exe", "$env:ProgramFiles\Python311\python.exe")) {
            if (Test-Path -LiteralPath $p) { return $p }
        }
        return $null
    }
    $python = Find-Python
    $missing = @()
    if (-not $hasDotnet) { $missing += '.NET 9 SDK (Microsoft.DotNet.SDK.9)' }
    if (-not $python) { $missing += 'Python 3.11 (Python.Python.3.11)' }
    if ($missing.Count -gt 0) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw ("次のソフトを手動でインストールしてから、もう一度実行してください: " + ($missing -join '、') +
                "`n  .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0`n  Python 3.11: https://www.python.org/downloads/ (「Add python.exe to PATH」にチェック)")
        }
        Write-Host ('  次のソフトを winget でインストールします: ' + ($missing -join '、'))
        Write-Host '  (インストールすると、それぞれのソフトの利用条件に同意したことになります)'
        if (-not (Confirm-Step '  インストールしますか？')) { throw 'インストールを中止しました。' }
        if (-not $hasDotnet) {
            Invoke-Native { & winget install --id Microsoft.DotNet.SDK.9 -e --source winget --silent --accept-package-agreements --accept-source-agreements }
            Refresh-Path
            if (-not ((Invoke-Native { & dotnet --list-sdks 2>$null }) -match '^9\.')) { throw '.NET 9 SDK をインストールできませんでした。' }
        }
        if (-not $python) {
            Invoke-Native { & winget install --id Python.Python.3.11 -e --source winget --scope user --silent --accept-package-agreements --accept-source-agreements }
            Refresh-Path
            $python = Find-Python
            if (-not $python) { throw 'Python 3.11 をインストールできませんでした。' }
        }
    }
    if (-not $prebuilt) { Ok ".NET SDK: $((& dotnet --list-sdks | Select-String '^9\.' | Select-Object -First 1).ToString().Split(' ')[0])" }
    Ok "Python: $python"

    # ── 2. ビルド
    if ($prebuilt) {
        Step '2/4 GetText のビルド (ビルド済みの配布版なので省略します)'
        Ok "アプリ: $exe"
    }
    else {
        Step '2/4 GetText をビルドしています'
        Push-Location $here
        Invoke-Native { & dotnet build GetText.csproj -c Release --nologo -v q }
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw 'ビルドに失敗しました (上のエラーを確認してください)。' }
        Ok "ビルド完了: $exe"
    }

    # ── 3. AI 機能の Python 環境とモデル
    Step '3/4 AI 機能の環境を作り、モデルをダウンロードしています (回線によっては 30 分以上かかります)'
    $opts = @{ BasePython = $python }
    if ($Mode -eq 'Standard') { $opts.NoLlm = $true }
    if ($Mode -eq 'Minimal') { $opts.NoAsr = $true; $opts.EnglishOnly = $true }
    if (-not $WithNllb) { $opts.EnglishOnly = $true }
    & (Join-Path $here 'offline\setup_offline.ps1') @opts
    Ok 'AI 機能の準備完了'

    # ── 4. ショートカット
    Step '4/4 ショートカットを作成しています'
    $shell = New-Object -ComObject WScript.Shell
    $dirs = if ($SkipShortcut) { @() } else { @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ } }
    foreach ($dir in $dirs) {
        $lnk = $shell.CreateShortcut((Join-Path $dir 'GetText.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = Split-Path $exe
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = '画面の文字の読み取り・翻訳・議事録'
        $lnk.Save()
    }
    Ok 'デスクトップとスタートメニューに「GetText」を作りました'

    Write-Host ''
    Write-Host '==============================================' -ForegroundColor Green
    Write-Host ' セットアップが完了しました' -ForegroundColor Green
    Write-Host '==============================================' -ForegroundColor Green
    Write-Host ' 使い方は README.md と docs\GetText_紹介と使い方.mp4 を見てください。'
    if (-not $NoLaunch -and (Confirm-Step ' GetText を起動しますか？')) { Start-Process $exe }
}
catch {
    Write-Host ''
    Write-Host "エラー: セットアップを完了できませんでした: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  記録: $log"
    Write-Host '  問題を直してから、もう一度 setup.bat を実行してください (済んだ手順は飛ばします)。'
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}
try { Stop-Transcript | Out-Null } catch { }
