# 現在の HEAD の監査 (Plugin Platform に入る前)

- 基準: KoroCoding/GetText `979b6da` (2026-10-07。HEAD はこれ以上進んでいない)。開発は別のブランチで行い、CI で確かめてから main に反映する
- 手元の PC はスマート アプリ コントロールで GetText.exe を起動できないため、画面の確認は CI の画像・UI の確認で行う

## IMPLEMENTED (実装済み・壊さない)
- デザイントークン (`DesignTokens.cs`)・Windows / Mac のテーマ (`Theme.cs`・`mac/MacTheme.cs`)・意味の名前のアイコン (`AppIcons.cs`・`IconSource`・`IconValidator`)
- コマンドの一覧 Ctrl+K / ⌘K (`Commands.cs` の `CommandRegistry`・`CommandSearch`、`AppCommands.cs`、`CommandPalette`)
- ホーム (`FeatureInfo` からタイル・状態・閉じる・最近の議事録)
- 読み取り: Ctrl+F 検索・原文の欄と読み取り枠の上の印 (`TextSearch`・`SearchHighlightAdorner`・`CaptureWindow.SetHighlights`)、枠・列のまとまり (`OcrLayout`)
- AI OCR の準備中の代替 (Windows OCR / Vision)、使えないときの帯
- 設定 9 ページと検索・プライバシーのページ
- CI: Windows (単体テスト・画面の画像・UI の確認)、Mac (ビルド・画像・UI・起動)

## PARTIAL
- 検索の画面の上の位置: `TextSearch.ToBoxes` は行の左右を文字数で割っている (文字幅が違うとずれる) → Phase 0 で直す
- 拡張機能: `CommandRegistry.Register`・`FeatureInfo`・`IconSource.Svg` はあるが、読み込み・パッケージ・状態の管理が無い
- モデル: セットアップ (Full / Standard / Minimal、`setup.ps1`・`offline/download_models.py`) で一括。機能ごとに選べない
- 「常に手前」: `AppSettings.Topmost` は読み取りの画面だけ

## NOT IMPLEMENTED
- Plugin の契約・マニフェスト・パッケージ・読み込み・安全な展開・Extensions の画面・索引・モデルの管理
- Keyword Monitor・Presentation Capture・OCR History・Quick OCR・PDF / Batch OCR・QR・訳の重ね表示・CLI
- Safe Mode・Plugin の診断

## POTENTIAL BUG
- `TextSearch.ToBoxes`: 比例配分で、句読点・全角半角・比例字体の行で印がずれる (上の PARTIAL)
- `AppCommands.Registry` は static で、窓を作り直すと登録が置き換わる (同じ Id で上書きなので重複はしない。ホームを 2 つ作る見本の画面でだけ起こる)
- 画面の判定が `AiOcr.IsInstalled`・`TranscriptionService.IsInstalled`・`LocalTranslator.IsInstalled` の直接参照 (69 か所に機能の有無・設定の直接参照) → 提供元の登録先 (Registry) に段階的に移す

## TECHNICAL DEBT
- OCR の提供元・翻訳の提供元が `TextWindow` の中で分岐している (Windows OCR / AI OCR / Vision)
- Windows 版と Mac 版の `TextWindow` に同じ流れが 2 つある (共有できる部分を増やす)
- 英語の画面が無い (文字が XAML とコードに直接ある)
