# 操作手順の動画のナレーションを、Windows の日本語音声 (Microsoft Haruka など) で WAV にする。
# 使い方: powershell -ExecutionPolicy Bypass -File tts.ps1 -InputJson scenes.json -OutDir tts
#   scenes.json: [{"id": "s01", "text": "..."}, ...]
param([Parameter(Mandatory)][string]$InputJson, [Parameter(Mandatory)][string]$OutDir, [double]$Rate = 1.1)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }

$voice = [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | Where-Object { $_.Language -eq 'ja-JP' -and $_.Gender -eq 'Female' } | Select-Object -First 1
if (-not $voice) { $voice = [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | Where-Object { $_.Language -eq 'ja-JP' } | Select-Object -First 1 }
if (-not $voice) { throw '日本語の音声がありません (Windows の設定 → 時刻と言語 → 音声認識 で日本語の音声を追加してください)' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$scenes = Get-Content $InputJson -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($s in $scenes) {
    $synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
    $synth.Voice = $voice
    $synth.Options.SpeakingRate = $Rate
    $stream = Await ($synth.SynthesizeTextToStreamAsync($s.text)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
    $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
    $null = Await ($reader.LoadAsync([uint32]$stream.Size)) ([uint32])
    $bytes = New-Object byte[] ($stream.Size)
    $reader.ReadBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $OutDir "$($s.id).wav"), $bytes)
    $synth.Dispose()
}
Write-Host "$($scenes.Count) 件の音声を作りました ($($voice.DisplayName))"
