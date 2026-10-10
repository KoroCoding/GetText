<#
  GetText の拡張機能 (plugins/*) をビルドして、パッケージ (.gtplugin) と索引 (plugins-index.json) を作る。
    powershell -ExecutionPolicy Bypass -File tools\make_plugins.ps1 -Out <フォルダ> [-ReleaseTag v1.1.0] [-Skip gettext.sample]
  - 各拡張機能は dotnet publish し、plugin.json・bin/ (GetText.Plugin.Abstractions は除く)・README.md・LICENSE を ZIP にする
  - 索引には、同梱のファイル名 (file) と、GitHub Releases の URL (url。ReleaseTag を渡したとき) と SHA-256・大きさを書く
  Windows PowerShell 5.1 と PowerShell 7 (Mac の CI) の両方で動く。ZIP の中の区切りは / にする。
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ReleaseTag = "",
    [string]$Configuration = "Release",
    # 入れない拡張機能の id (配布物に見本を入れないときなど)
    [string[]]$Skip = @()
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("gettext-plugins-" + [Guid]::NewGuid().ToString("N"))
$entries = @()

foreach ($proj in Get-ChildItem (Join-Path $root "plugins") -Directory) {
    $csproj = Get-ChildItem $proj.FullName -Filter *.csproj | Select-Object -First 1
    $manifestPath = Join-Path $proj.FullName "plugin.json"
    if (-not $csproj -or -not (Test-Path $manifestPath)) { continue }
    $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($Skip -contains $manifest.id) { continue }
    $bin = Join-Path $work ($manifest.id + "\bin")
    & dotnet publish $csproj.FullName -c $Configuration -o $bin --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "ビルドできませんでした: $($csproj.Name)" }
    # GetText のものを使う部品は入れない
    Get-ChildItem $bin -File | Where-Object { $_.Name -like "GetText.Plugin.Abstractions.*" -or $_.Extension -eq ".pdb" -or $_.Name -eq "plugin.json" -or $_.Name -eq "README.md" } | Remove-Item -Force

    $name = "$($manifest.id)-$($manifest.version).gtplugin"
    $package = Join-Path $out $name
    if (Test-Path $package) { Remove-Item $package -Force }
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        function Add-File([string]$source, [string]$entryName) {
            $entry = $zip.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try { $bytes = [IO.File]::ReadAllBytes($source); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        Add-File $manifestPath "plugin.json"
        foreach ($f in Get-ChildItem $bin -File -Recurse) {
            $rel = $f.FullName.Substring($bin.Length + 1).Replace('\', '/')
            Add-File $f.FullName ("bin/" + $rel)
        }
        $readme = Join-Path $proj.FullName "README.md"
        if (Test-Path $readme) { Add-File $readme "README.md" }
        Add-File (Join-Path $root "LICENSE") "LICENSE"
    } finally {
        $zip.Dispose()
    }
    # 署名 (開発元の秘密鍵が環境変数にあるときだけ。CI の Secrets。docs/security/PLUGIN_SIGNING.md)
    $signed = $false
    if ($env:GETTEXT_PLUGIN_SIGNING_KEY) {
        $keyId = if ($env:GETTEXT_PLUGIN_SIGNING_KEY_ID) { $env:GETTEXT_PLUGIN_SIGNING_KEY_ID } else { "gettext-official-1" }
        & dotnet run --project (Join-Path $root "tools\PluginSign\PluginSign.csproj") -c Release -- sign $package $keyId
        if ($LASTEXITCODE -ne 0) { throw "署名できませんでした: $name" }
        $signed = $true
    }

    $installed = (Get-ChildItem $bin -File -Recurse | Measure-Object Length -Sum).Sum + (Get-Item $manifestPath).Length
    $sha = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $entry = [ordered]@{
        id = $manifest.id; name = $manifest.name; publisher = $manifest.publisher; description = $manifest.description
        version = $manifest.version; file = $name; sha256 = $sha; size = (Get-Item $package).Length; installedSize = $installed
        platforms = $manifest.platforms; minHostVersion = $manifest.minHostVersion; apiVersion = $manifest.apiVersion
        permissions = $manifest.permissions; models = $manifest.models; license = $manifest.license
        icon = $manifest.icon; signed = $signed; local = -not ($manifest.permissions -contains "network")
    }
    if ($ReleaseTag) { $entry.url = "https://github.com/KoroCoding/GetText/releases/download/$ReleaseTag/$name" }
    $entries += [pscustomobject]$entry
    Write-Host "  $name  $([Math]::Round((Get-Item $package).Length / 1KB)) KB  sha256 $sha"
}

$index = [ordered]@{ schema = 1; generated = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); plugins = $entries }
$json = $index | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $out "plugins-index.json"), $json, (New-Object Text.UTF8Encoding($false)))
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "拡張機能 $($entries.Count) 個 → $out"
