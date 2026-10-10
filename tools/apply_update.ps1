# GetText の更新を当てる (更新用の ZIP を展開したフォルダーの「更新する.bat」から実行する)
#  1. セットアップ済みの GetText の場所を探す (デスクトップ / スタートメニューのショートカット → よくある場所 → 入力)
#  2. files フォルダーの中身を GetText に上書きする
#  3. GetText の update.ps1 でビルドし直す (Python の環境とモデルはそのまま)
param([string]$Target = '', [switch]$NoLaunch)  # NoLaunch: 確認用 (最後に起動しない)
$ErrorActionPreference = 'Stop'
$files = Join-Path $PSScriptRoot 'files'

function Test-GetTextRoot($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'GetText.csproj')) -and (Test-Path -LiteralPath (Join-Path $dir 'setup.bat'))
}

function Find-GetText {
    # セットアップが作ったショートカット: <GetText>\bin\Release\net<版>-...\GetText.exe
    $shell = New-Object -ComObject WScript.Shell
    # (環境によってはフォルダーの場所が空で返るので、空のものは飛ばす)
    $dirs = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ }
    foreach ($lnk in @($dirs | ForEach-Object { Join-Path $_ 'GetText.lnk' })) {
        if (Test-Path -LiteralPath $lnk) {
            $exe = $shell.CreateShortcut($lnk).TargetPath
            if ($exe) {
                # 配布版 (ビルド済み) は GetText.exe と setup.bat が同じフォルダにある
                $dir = Split-Path $exe
                if ((Test-Path -LiteralPath (Join-Path $dir 'setup.bat')) -and -not (Test-Path -LiteralPath (Join-Path $dir 'GetText.csproj'))) { return $dir }
                $root = Split-Path (Split-Path (Split-Path (Split-Path $exe)))
                if (Test-GetTextRoot $root) { return $root }
            }
        }
    }
    # よくある場所 (ZIP をそのまま展開すると 1 段深くなることもある)
    foreach ($base in @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", [Environment]::GetFolderPath('Desktop')) | Where-Object { $_ }) {
        foreach ($dir in @("$base\GetText", "$base\GetText\GetText")) {
            if (Test-GetTextRoot $dir) { return $dir }
        }
    }
    return $null
}

try {
    Write-Host '■ GetText の更新' -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $files) -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'deleted.txt'))) { throw "更新するファイル (files フォルダー) が見つかりません: $files" }
    $root = if ($Target) { $Target } else { Find-GetText }
    if ($root -and (Test-Path -LiteralPath (Join-Path $root 'GetText.exe')) -and -not (Test-Path -LiteralPath (Join-Path $root 'GetText.csproj'))) {
        throw "この GetText はビルド済みの配布版です ($root)。配布版は更新用 ZIP では更新できません。新しい GetText_配布版.zip を展開して、同じ場所に上書きしてください (AI の環境とモデルはそのまま使えます)。"
    }
    while (-not (Test-GetTextRoot $root)) {
        Write-Host '  GetText のフォルダーが見つかりませんでした。setup.bat があるフォルダーの場所を入力してください' -ForegroundColor Yellow
        Write-Host '  (エクスプローラーでそのフォルダーを開き、上のアドレス欄をコピーして貼り付けます)'
        $root = (Read-Host '  場所').Trim('"', ' ')
    }
    $root = (Resolve-Path -LiteralPath $root).ProviderPath.TrimEnd('\')
    Write-Host "  更新する GetText: $root"
    while (Get-Process GetText -ErrorAction SilentlyContinue) {
        Write-Host '  GetText が起動中です。議事録を保存してから GetText を閉じ、Enter を押してください。' -ForegroundColor Yellow
        Read-Host | Out-Null
    }
    # [ ] などを含む場所でも、ワイルドカードとして扱わずにそのまま写す
    $count = 0
    if (Test-Path -LiteralPath $files) {
        $count = (Get-ChildItem -LiteralPath $files -Recurse -File).Count
        Get-ChildItem -LiteralPath $files -Force | Copy-Item -Destination $root -Recurse -Force
    }
    Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
    Write-Host "  OK: $count 個のファイルを置き換えました" -ForegroundColor Green
    # 新しい版で消した・名前を変えたファイルを消す (残ると古いソースがビルドに入る)
    $deletedList = Join-Path $PSScriptRoot 'deleted.txt'
    if (Test-Path -LiteralPath $deletedList) {
        foreach ($rel in [IO.File]::ReadAllLines($deletedList)) {
            if (-not $rel -or $rel.Contains('..')) { continue }
            $old = Join-Path $root ($rel -replace '/', '\')
            if (Test-Path -LiteralPath $old -PathType Leaf) { Remove-Item -LiteralPath $old -Force }
        }
    }
    & (Join-Path $root 'update.ps1') -NoLaunch:$NoLaunch
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
catch {
    Write-Host "エラー: 更新できませんでした: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
