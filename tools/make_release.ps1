# ビルド済みの配布版 (GetText_配布版.zip) を作る。相手の PC では .NET SDK もビルドも要らない (アプリに .NET を入れる)。
# 使い方: powershell -ExecutionPolicy Bypass -File make_release.ps1 -Out <出力.zip>
# ZIP の中身: GetText\ (GetText.exe・offline\・setup.bat・setup.ps1・update.bat・update.ps1・README.md・紹介と使い方の動画)
#   更新するときは、新しい ZIP を展開して update.bat を実行すると、使っている GetText のファイルを置き換える。
#   相手の PC では ZIP を展開して setup.bat を実行すると、Python と AI のモデルを用意してショートカットを作る。
# ※ 署名のないアプリなので、Smart App Control が有効な PC では起動を止められることがある (README 参照)。
param([Parameter(Mandatory)][string]$Out)
# 相対の場所は PowerShell の今のフォルダーから (.NET の書き込みと Test-Path で場所がずれないように)
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) ("gettext_release_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$pkg = Join-Path $work 'GetText'
try {
    Write-Host '■ ビルドしています (.NET 入り・64 ビット)'
    Push-Location $root
    & dotnet publish GetText.csproj -c Release -r win-x64 --self-contained true -o $pkg --nologo -v q -p:DebugType=none
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0 -or -not (Test-Path (Join-Path $pkg 'GetText.exe'))) { throw 'ビルドに失敗しました' }
    foreach ($f in 'setup.bat', 'setup.ps1', 'update.bat', 'update.ps1', 'uninstall.bat', 'uninstall.ps1', 'README.md', 'LICENSE') { Copy-Item (Join-Path $root $f) $pkg }
    $video = Join-Path $root 'docs\GetText_紹介と使い方.mp4'
    if (-not (Test-Path -LiteralPath $video)) { throw "紹介と使い方の動画がありません: $video (tools\make_video.py で作ってください)" }
    if (Test-Path -LiteralPath $video) {
        New-Item -ItemType Directory -Force (Join-Path $pkg 'docs') | Out-Null
        Copy-Item $video (Join-Path $pkg 'docs')
    }
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out }
    # 日本語の名前が文字化けしないよう UTF-8 で、区切りは「/」で 1 つずつ入れる (make_update_zip.ps1 と同じ)
    $zipStream = [IO.File]::Open($Out, 'Create')
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    try {
        $baseLen = $work.Length + 1
        foreach ($file in Get-ChildItem $pkg -Recurse -File) {
            $name = $file.FullName.Substring($baseLen).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
        $zipStream.Dispose()
    }
    $count = (Get-ChildItem $pkg -Recurse -File).Count
    Write-Host "作成: $Out ($count ファイル、$([math]::Round((Get-Item -LiteralPath $Out).Length / 1MB)) MB)"
}
finally {
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
