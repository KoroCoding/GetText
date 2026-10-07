#!/bin/bash
# GetText.app (Mac 版) を作る。Mac で実行する (Xcode のコマンドラインツールと .NET 9 SDK が必要)。
# 使い方: bash build_app.sh <出力フォルダ> [arm64|x64]   (省略すると、この Mac の種類)
# 出力: <出力フォルダ>/GetText.app と GetText-mac-<arm64|x64>.zip
#   GetText.app/Contents/MacOS      GetText (アプリ本体) と GetTextHelper (Mac の機能を受け持つ補助プログラム)
#   GetText.app/Contents/Resources  offline/ (Python のスクリプト)・bundled-plugins/ (同梱の拡張機能)・setup_mac.sh・GetText.icns
# 署名は「その場の署名」(ad-hoc)。初めて開くときは右クリック → 開く (README の「Mac 版」を参照)。
set -euo pipefail

out="$(mkdir -p "$1" && cd "$1" && pwd)"
arch="${2:-$(uname -m)}"
[ "$arch" = "x86_64" ] && arch=x64
here="$(cd "$(dirname "$0")" && pwd)"
mac="$(cd "$here/.." && pwd)"
root="$(cd "$mac/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$mac/GetText.Mac.csproj" | head -1)"
work="$out/build-$arch"
app="$out/GetText.app"
rm -rf "$work" "$app"
mkdir -p "$work" "$app/Contents/MacOS" "$app/Contents/Resources"

echo "== 補助プログラム (Swift) をビルドしています"
target=$([ "$arch" = "arm64" ] && echo arm64-apple-macos14.0 || echo x86_64-apple-macos14.0)
swiftc -O -swift-version 5 -target "$target" "$mac/helper/GetTextHelper.swift" -o "$app/Contents/MacOS/GetTextHelper" \
    -framework AppKit -framework AVFoundation -framework Carbon -framework CoreAudio -framework CoreGraphics \
    -framework CoreMedia -framework ImageIO -framework PDFKit -framework ScreenCaptureKit -framework Security -framework Vision

echo "== アプリ (C# / Avalonia) をビルドしています ($arch)"
dotnet publish "$mac/GetText.Mac.csproj" -c Release -r "osx-$arch" --self-contained true \
    -p:UseAppHost=true -p:DebugType=none -o "$work/publish" --nologo -v q
# 本体と .NET・Avalonia の部品は MacOS に、Python のスクリプトとセットアップは Resources に置く
cp -R "$work/publish/." "$app/Contents/MacOS/"
rm -rf "$app/Contents/MacOS/offline" "$app/Contents/MacOS/setup_mac.sh"
mkdir -p "$app/Contents/Resources/offline"
cp "$root"/offline/*.py "$app/Contents/Resources/offline/"
cp "$here/setup_mac.sh" "$app/Contents/Resources/"
# 同梱の拡張機能 (設定 → 拡張機能 の「見つける」に出る。入れるまでは読み込まない。見本は入れない)
if command -v pwsh >/dev/null 2>&1; then
    echo "== 同梱の拡張機能を作っています"
    pwsh -NoProfile -File "$root/tools/make_plugins.ps1" -Out "$app/Contents/Resources/bundled-plugins" -Skip gettext.sample
else
    echo "   (PowerShell (pwsh) が無いので、拡張機能は同梱しません)"
fi
chmod +x "$app/Contents/Resources/setup_mac.sh" "$app/Contents/MacOS/GetText" "$app/Contents/MacOS/GetTextHelper"
sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

echo "== アイコンを作っています"
iconset="$work/GetText.iconset"
mkdir -p "$iconset"
# 元の絵: 1024 px の PNG (tools/make_icon.py が作る。無ければ app.ico から)
if [ -f "$root/Assets/app_1024.png" ]; then cp "$root/Assets/app_1024.png" "$work/icon.png"; else sips -s format png "$root/Assets/app.ico" --out "$work/icon.png" >/dev/null; fi
for size in 16 32 128 256 512; do
    sips -z $size $size "$work/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$work/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/GetText.icns" || echo "   (アイコンを作れませんでした。アイコンなしで続けます)"

echo "== 署名しています (ad-hoc)"
codesign --force --deep --sign - "$app"
codesign --verify --deep "$app"

zip="$out/GetText-mac-$arch.zip"
rm -f "$zip"
(cd "$out" && ditto -c -k --keepParent GetText.app "$zip")
echo "完成: $app"
echo "      $zip ($(du -h "$zip" | cut -f1))"
