# GetText の更新 (update.bat から実行する)
# - GetText のフォルダーで実行したとき: ビルドし直す (画面の部分の変更を反映し、offline の Python のファイルもアプリのフォルダーへ写す)
# - 更新用の ZIP を展開したフォルダーで実行したとき: セットアップ済みの GetText を探して、ここにあるファイルで上書きしてからビルドし直す
# - ビルド済みの配布版 (GetText.exe があり、ソースが無い) は、ビルドせずにファイルを置き換える (置き換える前に控えを取る)
# - Python の環境やモデルはそのまま使う (入れ直さない)
param([string]$Target = '', [switch]$NoLaunch)  # Target: 更新する GetText の場所 (省略すると探す) / NoLaunch: 確認用 (最後に起動しない)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Invoke-Native([scriptblock]$Block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $old }
}

function Test-SourceRoot($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'GetText.csproj')) -and (Test-Path -LiteralPath (Join-Path $dir 'setup.bat'))
}
# ビルド済みの配布版のフォルダー
function Test-PrebuiltRoot($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'GetText.exe')) -and (Test-Path -LiteralPath (Join-Path $dir 'setup.bat')) -and -not (Test-Path -LiteralPath (Join-Path $dir 'GetText.csproj'))
}
function Test-GetTextRoot($dir) { return (Test-SourceRoot $dir) -or (Test-PrebuiltRoot $dir) }

function Find-GetText {
    # セットアップが作ったショートカット: <GetText>\bin\Release\net<版>-...\GetText.exe
    $shell = New-Object -ComObject WScript.Shell
    # (環境によってはフォルダーの場所が空で返るので、空のものは飛ばす)
    $dirs = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ }
    foreach ($lnk in @($dirs | ForEach-Object { Join-Path $_ 'GetText.lnk' })) {
        if (Test-Path -LiteralPath $lnk) {
            $exe = $shell.CreateShortcut($lnk).TargetPath
            if ($exe) {
                if (Test-PrebuiltRoot (Split-Path $exe)) { return (Split-Path $exe) }
                $root = Split-Path (Split-Path (Split-Path (Split-Path $exe)))
                if (Test-GetTextRoot $root) { return $root }
            }
        }
    }
    foreach ($base in @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", [Environment]::GetFolderPath('Desktop')) | Where-Object { $_ }) {
        foreach ($dir in @("$base\GetText", "$base\GetText\GetText")) {
            if (Test-GetTextRoot $dir) { return $dir }
        }
    }
    return $null
}

try {
    Write-Host '■ GetText を更新します' -ForegroundColor Cyan
    $root = $here
    $copyFrom = $null
    if ($Target) { $root = $Target; if (-not (Test-SourceRoot $here)) { $copyFrom = $here } }
    elseif (Test-PrebuiltRoot $here) {
        # 新しい配布版を展開したフォルダーで実行された → 使っている GetText (ショートカットの先) を探して置き換える
        $found = Find-GetText
        if ($found -and (Resolve-Path -LiteralPath $found).ProviderPath -ne (Resolve-Path -LiteralPath $here).ProviderPath) { $copyFrom = $here; $root = $found }
    }
    elseif (-not (Test-SourceRoot $here)) {
        # 更新用のファイルだけのフォルダーで実行された → 更新する GetText を探して上書きする
        $copyFrom = $here
        $root = Find-GetText
    }
    while (-not (Test-GetTextRoot $root)) {
        Write-Host '  GetText のフォルダー (setup.bat があるフォルダー) が見つかりませんでした。場所を入力してください' -ForegroundColor Yellow
        Write-Host '  (エクスプローラーでそのフォルダーを開き、上のアドレス欄をコピーして貼り付けます)'
        $root = "$(Read-Host '  場所')".Trim('"', ' ', '　')
    }
    # 場所の書き方をそろえる (末尾の「\」を取り、[ ] などの記号もそのまま扱う)
    $root = (Resolve-Path -LiteralPath $root).ProviderPath.TrimEnd('\')
    if ($copyFrom) { $copyFrom = (Resolve-Path -LiteralPath $copyFrom).ProviderPath.TrimEnd('\') }
    Write-Host "  更新する GetText: $root"
    # 動いていると上書きできないので、閉じてもらう
    while (Get-Process GetText -ErrorAction SilentlyContinue) {
        Write-Host '  GetText が起動中です。議事録を保存してから GetText を閉じ、Enter を押してください。' -ForegroundColor Yellow
        Read-Host | Out-Null
    }
    $prebuilt = Test-PrebuiltRoot $root
    if ($copyFrom -and ($copyFrom -ne $root) -and (Test-PrebuiltRoot $copyFrom) -and (Test-SourceRoot $root)) {
        # ソースからビルドして使っている GetText に配布版を上書きすると、古いソースでビルドし直されて新しい版にならない
        throw "使っている GetText ($root) はソースからビルドしたものなので、配布版では置き換えられません。新しい GetText_配布版.zip を別のフォルダーに展開して、その中の setup.bat を実行してください (AI の環境とモデルはそのまま使えます)。"
    }
    if ($copyFrom -and ($copyFrom -ne $root)) {
        $count = (Get-ChildItem -LiteralPath $copyFrom -Recurse -File).Count
        $backup = $null
        if ($prebuilt) {
            # 配布版は置き換える前に控えを取り、途中で失敗したら元に戻す (アプリが起動しなくならないように)
            $backup = "$root.bak"
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
            Copy-Item -LiteralPath $root -Destination $backup -Recurse -Force
        }
        try {
            Get-ChildItem -LiteralPath $copyFrom -Force | Copy-Item -Destination $root -Recurse -Force
        }
        catch {
            $failure = $_
            if ($backup) {
                Write-Host '  置き換えに失敗したので、元に戻しています…' -ForegroundColor Yellow
                try { Get-ChildItem -LiteralPath $backup -Force | Copy-Item -Destination $root -Recurse -Force }
                catch { Write-Host "  元に戻せませんでした。控えのフォルダー ($backup) の中身を $root に写してください。" -ForegroundColor Red }
            }
            throw $failure
        }
        # ZIP から展開したファイルに付く「インターネットから取得」の印を外す (起動のたびに警告が出ないように)
        Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
        Write-Host "  OK: $count 個のファイルを置き換えました" -ForegroundColor Green
        if ($backup) { Write-Host "  (元のファイルの控え: $backup。動作を確かめたら消してかまいません)" }
    }
    if ($prebuilt) {
        # ビルド済みの配布版: ビルドは要らない (アプリに .NET が入っている)
        $exe = Join-Path $root 'GetText.exe'
        $dest = Join-Path $root 'offline'
    }
    else {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK が見つかりません。先に setup.bat を実行してください。' }
        $exe = Join-Path $root 'bin\Release\net10.0-windows10.0.19041.0\GetText.exe'
        Push-Location $root
        # ZIP から上書きしたファイルは日時が古いことがあり、差分だけのビルドだと反映されないので、すべて作り直す
        Invoke-Native { & dotnet build GetText.csproj -c Release --nologo -v q --no-incremental }
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw 'ビルドに失敗しました (上のエラーを確認してください)。' }
        # .NET の版が変わると実行ファイルの場所が変わる (net9.0-… → net10.0-…): 古い場所を指すショートカットを新しい場所に向け直す
        $shell = New-Object -ComObject WScript.Shell
        foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ }) {
            $path = Join-Path $dir 'GetText.lnk'
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $lnk = $shell.CreateShortcut($path)
            $old = $lnk.TargetPath
            if ($old -and $old -ne $exe -and $old.StartsWith((Join-Path $root 'bin\Release\'), [StringComparison]::OrdinalIgnoreCase)) {
                $lnk.TargetPath = $exe
                $lnk.WorkingDirectory = Split-Path $exe
                $lnk.IconLocation = "$exe,0"
                $lnk.Save()
                Write-Host "  ショートカットを新しい場所に向け直しました: $path"
            }
        }
        # 議事録・翻訳・読み取りの処理 (Python) は、日時にかかわらずアプリのフォルダーへ写す
        $dest = Join-Path (Split-Path $exe) 'offline'
        Get-ChildItem -LiteralPath (Join-Path $root 'offline') -File |
            Where-Object { $_.Extension -in '.py', '.ps1', '.bat' } | Copy-Item -Destination $dest -Force
    }
    # 新しい版で増えた Python の部品を入れる (用語の登録の読みの解析 Janome など。入っていれば何もしない)
    $venvPython = Join-Path $env:LOCALAPPDATA 'GetText\offline\venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $venvPython) {
        Write-Host '  追加の部品を確認しています (初回は 20MB ほどダウンロードします)'
        Invoke-Native { & $venvPython -m pip install --disable-pip-version-check --quiet janome==0.5.0 }
        if ($LASTEXITCODE -ne 0) { Write-Host '  注意: 追加の部品を入れられませんでした (用語を読みで直す機能だけ使えません)' -ForegroundColor Yellow }
        # 議事録を入れている PC には、GPU が無くても記録に追いつく速い音声認識 (ReazonSpeech、約 160MB) を足す
        $models = Join-Path $env:LOCALAPPDATA 'GetText\offline\models'
        $hasAsr = Test-Path -LiteralPath (Join-Path $models 'kotoba-whisper-v2.0\model.bin')
        # (4 つのファイルがすべてそろっているか。欠けていれば入れ直す)
        $fastMissing = @('encoder-epoch-99-avg-1.int8.onnx', 'decoder-epoch-99-avg-1.int8.onnx', 'joiner-epoch-99-avg-1.int8.onnx', 'tokens.txt') |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $models "reazonspeech-k2-v2\$_")) }
        $hasFast = @($fastMissing).Count -eq 0
        # 文字の読み取り: 小さい・ぼやけた文字を画面が止まったときに読み直す高精度の認識モデル (約 77MB)
        $hasOcr = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'GetText\offline\venv\Lib\site-packages\rapidocr\__init__.py')
        if ($hasOcr -and -not (Test-Path -LiteralPath (Join-Path $models 'ppocrv6-rec-medium\PP-OCRv6_rec_medium.onnx'))) {
            Write-Host '  小さい・ぼやけた文字を読み直す認識モデル (約 77MB) をダウンロードしています'
            Invoke-Native { & $venvPython (Join-Path $dest 'download_models.py') $models --only ppocrv6-rec-medium }
            if ($LASTEXITCODE -ne 0) { Write-Host '  注意: ダウンロードできませんでした (読み取りは今までどおり動きます)' -ForegroundColor Yellow }
        }
        if ($hasAsr -and -not $hasFast) {
            Write-Host '  議事録の速い音声認識 (GPU の無い PC 向け、約 160MB) をダウンロードしています'
            Invoke-Native { & $venvPython (Join-Path $dest 'download_models.py') $models --only reazonspeech-k2-v2 }
            if ($LASTEXITCODE -ne 0) { Write-Host '  注意: ダウンロードできませんでした (議事録は今までどおり動きます。後で「セットアップを実行」で入れられます)' -ForegroundColor Yellow }
        }
    }
    Write-Host '  OK: 更新しました' -ForegroundColor Green
    if (-not $NoLaunch) {
        $a = "$(Read-Host ' GetText を起動しますか？ (Y/n)')".Trim().Normalize([Text.NormalizationForm]::FormKC)
        if ($a -eq '' -or $a -match '^[yY]') { Start-Process -FilePath $exe }
    }
    exit 0  # 途中の pip などの終了コードを引き継がない (更新用 ZIP の apply_update.ps1 が失敗と誤解しないように)
}
catch {
    Write-Host "エラー: 更新できませんでした: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ("  (場所: " + $_.InvocationInfo.ScriptLineNumber + " 行目 " + $_.InvocationInfo.Line.Trim() + ")") -ForegroundColor DarkGray
    exit 1
}
