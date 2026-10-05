# 更新用の ZIP を作る: 指定したコミット以降に変わったファイルだけを入れる (テスト・動画・ツールは除く)
# 使い方: powershell -ExecutionPolicy Bypass -File make_update_zip.ps1 -Base <コミット> -Out <出力.zip>
# ZIP の中身: 更新する.bat / apply_update.ps1 / files\ (GetText フォルダーと同じ並びの変更ファイル)
param([Parameter(Mandatory)][string]$Base, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
# 相対の場所は PowerShell の今のフォルダーから (.NET の書き込みと Test-Path で場所がずれないように)
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$repo = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$rel = @(& git -C $repo -c core.quotepath=false diff --name-only --diff-filter=d $Base HEAD -- GetText |
    Where-Object { $_ -notmatch '^GetText/(tests|docs|tools|mac)/' } | ForEach-Object { $_.Substring('GetText/'.Length) })
# 消したファイル・名前を変えた前の名前 (使っている GetText からも消す。残るとビルドで型が重なる)
$deleted = @(& git -C $repo -c core.quotepath=false diff --name-only --diff-filter=D --no-renames $Base HEAD -- GetText |
    Where-Object { $_ -notmatch '^GetText/(tests|docs|tools|mac)/' } | ForEach-Object { $_.Substring('GetText/'.Length) })
if ($rel.Count -eq 0 -and $deleted.Count -eq 0) { throw "$Base 以降に変わったファイルがありません" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("gettext_update_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$pkg = Join-Path $work 'GetText_更新'
New-Item -ItemType Directory -Force (Join-Path $pkg 'files') | Out-Null
# 変更ファイルを files\ に取り出す (git archive → 展開)
$tmpZip = Join-Path $work 'files.zip'
if ($rel.Count -gt 0) {
    & git -C $repo archive --format=zip -o $tmpZip HEAD:GetText @rel
    if ($LASTEXITCODE -ne 0) { throw "git archive に失敗しました (終了コード $LASTEXITCODE)" }
    Expand-Archive -LiteralPath $tmpZip -DestinationPath (Join-Path $pkg 'files')
}
if ($deleted.Count -gt 0) { [IO.File]::WriteAllLines((Join-Path $pkg 'deleted.txt'), [string[]]$deleted, (New-Object Text.UTF8Encoding $false)) }
Copy-Item (Join-Path $PSScriptRoot 'apply_update.ps1') $pkg
# bat の名前は日本語でもよいが、中身は ASCII のみ (どのコードページでも動くように)
Set-Content -Path (Join-Path $pkg '更新する.bat') -Encoding Default -Value @(
    '@echo off',
    'cd /d "%~dp0"',
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply_update.ps1" %*',
    'echo.',
    'pause')
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out }
# 日本語の名前が文字化けしないよう、名前を UTF-8 で記録する
Add-Type -AssemblyName System.IO.Compression.FileSystem
# (Windows PowerShell 5.1 の CreateFromDirectory は区切りが「\」になり、展開ソフトによってはフォルダーにならないので、「/」で 1 つずつ入れる)
$zipStream = [IO.File]::Open($Out, 'Create')
$archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
try {
    $baseLen = (Split-Path $pkg).Length + 1
    foreach ($file in Get-ChildItem -LiteralPath $pkg -Recurse -File) {
        $name = $file.FullName.Substring($baseLen).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
    $zipStream.Dispose()
}
Write-Host "作成: $Out ($($rel.Count) ファイル、消すファイル $($deleted.Count))"
$rel | ForEach-Object { Write-Host "  $_" }
$deleted | ForEach-Object { Write-Host "  (消す) $_" }
