# GetText Final Implementation Audit

- 対象: Plugin Platform と追加機能 (基準 `979b6da` → `plugin-platform-rc`)。確かめは GitHub Actions (Windows・Mac)
- 手元の PC はスマート アプリ コントロール (SAC) が有効で、手元でビルドした GetText.exe・DLL を止めることがある (毎回ではない)。SAC は変更・回避しない。止められたときの確かめは CI で行った
- 記号: ✅ 済み (テスト・CI で確認) ・ ◐ 一部 ・ — 対象外 (理由つき)

## Phase ごとの結果

| Phase | 内容 | 結果 | 確かめ |
| --- | --- | --- | --- |
| 0 | 現在の HEAD の監査 | ✅ [HEAD_AUDIT.md](HEAD_AUDIT.md) | — |
| 1 | 検索の印の位置 (文字・単語の位置。無ければ行全体) | ✅ Windows OCR の単語・RapidOCR の文字・Vision の単語 | SearchGeometryTests |
| 2 | Plugin の契約・マニフェスト | ✅ Plugin API v1・plugin.json・SemVer・互換・依存 | PluginPlatformTests |
| 3 | 読み込み・ライフサイクル | ✅ AssemblyLoadContext・Commit / Rollback・落ちたら止める / 戻す・Safe Mode・終了 2 秒 | PluginRuntimeTests (壊れた拡張機能) |
| 4 | Extensions の画面 | ✅ 入れたもの・見つける・更新・検索・信頼・要求されるアクセス・再起動・診断 (Windows・Mac) | UI の確認・画像 8 状態 |
| 5 | パッケージの安全 | ✅ 安全な展開・SHA-256・https・信頼の印 / ◐ 署名の検証は未 | PluginPlatformTests・PluginCatalogTests |
| 6 | モデルの管理 | ✅ 1 つずつ入れる (取得済みは省く)・消す・ライセンス (NLLB 非商用)・フル / 標準 / 最小のセットアップはそのまま | ModelCatalogTests (download_models.py と照合) |
| 7 | 見本の拡張機能・文書 | ✅ Sample・docs/plugins (4 つ)・ROADMAP・README | パッケージ作成 (CI) |
| 8 | Keyword Monitor | ✅ | KeywordMonitorTests・本物の読み込み |
| 9 | Presentation Capture | ✅ dHash / pHash / 変わった面積・厳しめ / 普通 / ゆるめ・PNG と PPTX を自前で (COM なし) / ◐ PowerPoint で開く確認は未 | PresentationCaptureTests (PPTX の部品・関係の整合) |
| 10 | OCR History | ✅ 既定は保存しない・保存期間・除くアプリ / 言葉・すべて消す・Ctrl+K で検索 | HistoryTests・本物の読み込み |
| 11 | Quick / Silent OCR | ✅ Ctrl+Alt+Q (⌃⌥Q)・範囲を選んで 1 回読んでコピー | 方式の選び方のテスト / ◐ 実際の操作は手で |
| 12 | AI OCR を方式の 1 つに (adapter) | ✅ AiOcrProvider・HostOcrService (読み取りの画面はそのまま) | HostOcrServiceTests |
| 13 | ローカル翻訳 (adapter) | ✅ ITranslationService (この PC の中だけ) | 訳を重ねる・API のテスト |
| 14 | PDF OCR | ✅ Windows.Data.Pdf / PDFKit でページを画像に → TXT・Markdown・JSON / ◐ 文字の入った PDF の書き出しは未 | DocumentOcrTests (手で作った PDF・回転・拡大率) |
| 15 | Batch OCR | ✅ ファイルをいくつでも・フォルダごと・中止・続ける | 書き出しのテスト |
| 16 | QR / Barcode | ✅ QR・JAN など (拡張機能の中だけ ZXing.Net)・URL は押したときだけ | BarcodeTests |
| 17 | Translation Overlay | ✅ GetText が描く・クリックを通す・読み取りに写らない | TranslationOverlayTests・本物の読み込み |
| 18 | Developer CLI / API | ✅ 127.0.0.1・トークン・Origin / Host・既定オフ・curl / PowerShell の例 | DeveloperApiTests |
| 19 | 最終の監査 | ✅ この文書 | 下の「見直し」 |

ほかに求められたもの:

| 内容 | 結果 |
| --- | --- |
| 窓ごとの常に手前 (読み取り・議事録・録画) と Topmost → TextTopmost の引き継ぎ | ✅ (SettingsMigrationTests) / — スライドの記録・履歴は窓を持たない (知らせとコマンドだけ) |
| 保護された画面の見分け (一色) と知らせだけ | ✅ 読み取りの画面・スライドの記録 (回避しない) |
| Safe Mode・読み込み中の印・伏せ字のログ・診断をコピー | ✅ |
| 性能・長く動かす・故障の注入のテスト | ✅ 短いもの (遅い拡張機能で読み取りが待たされない・止まった検索の打ち切り・2 万件の履歴) / 重いものは手で |
| CI: SDK・見本・契約のテスト | ✅ 単体テストで SDK・見本・テスト用の拡張機能をビルドし、CI でパッケージを作る |

## 守ったこと (絶対に守ること 15 項目)

| | 確かめたこと |
| --- | --- |
| デザインを壊さない・統一する | 新しい画面はデザイントークン・既存の部品 (SettingRow・StatusPill・InfoBar・GtIcon) だけ。CI の画像で確認 |
| 二重に作らない | Home・FeatureInfo・CommandRegistry・Ctrl+K・検索・OcrLayout・プライバシーは作り直さず、登録先として使った |
| WPF と Avalonia + Swift を保つ・共有する | 一覧・文・判定・API は共有のファイル (PluginHost・ModelCatalog・HostOcrService・DeveloperApi など)。OS の部分だけ別 |
| 大きな書き直しをしない | 読み取りの画面の流れはそのまま。adapter を足した |
| 拡張機能に自由に描かせない | 画面は GetText の部品。重ねる文字も GetText が描く |
| 既定で送らない・テレメトリ無し・アカウント不要 | オンラインの一覧は押したときだけ。翻訳の adapter・訳を重ねるは PC の中だけ。API は既定オフで 127.0.0.1 だけ |
| 保護を回避しない | 一色の画面は知らせるだけ。WDA_EXCLUDEFROMCAPTURE・ScreenCaptureKit の決まりに従う |
| 大きな部品を足さない | 外部の部品は ZXing.Net だけ (QR の拡張機能の中だけ)。PNG・PPTX・HTTP は自前 |
| テストのためだけの細工をしない | 落ちたテストは原因を直した (下の「直した不具合」)。検査を弱めていない |
| 正常な機能を壊さない | 既存のテストはすべて合格のまま (CI) |

## 直した不具合 (原因から)

| 症状 | 原因 | 直し方 |
| --- | --- | --- |
| 読み込む前の PluginStore で書くと、ほかの拡張機能の記録が消える | 記録を読まずに保存していた | 書く前に必ず読む (State) |
| ネットワークを使う拡張機能が「PC 内」と出る | 一覧に local が無いと true にしていた | permissions から決める |
| 消しても設定・記録 (.data) が残る | 消す対象に入っていなかった | 一緒に消し、消せなければ次の起動でまた試す |
| CI の画面の画像が終わらない (30 分以上) | 見本づくりが UI のスレッドで非同期の処理を GetResult() で待っていた | 同期で入れる・止まった UI のスレッドの上で作るテスト |
| PDF のページが 1.5 倍の大きさになる | Windows.Data.Pdf の描く大きさは DIP (画面の拡大率がかかる) | 拡大率で割って頼み、決めた大きさにそろえる |
| 箇条書きが 1 行増えても「ゆるめ」で残らない | dHash・pHash は小さな追加に鈍い | 32×32 の升目で変わった面積も見る |

## 見直し (バグ・安全・性能)

別の目で、拡張機能の土台・公式の拡張機能・API のコードを読み直した (読むだけ)。見つかったものと対応:

| 重さ | 見つかったこと | 対応 |
| --- | --- | --- |
| 高 | 新しい版の読み込みが (落ちずに) 失敗すると、その場で前の版を消して戻せなくなる | 読めたときだけ前の版を消し、読めなければ次の起動で前の版に戻す (テスト) |
| 高 | 消す予定にした後で同じ版をファイルから入れ直すと、使っている版のフォルダを「前の版」として消す | 同じ版なら今のフォルダをそのまま使い、消すのを取り消すだけ (テスト) |
| 中 | 訳し終えるのが遅れると、読み取りの画面を閉じた後に訳が出たまま残る | 読み取りの画面が隠れている間は、GetText が重ねない |
| 中 | スライドの記録の開始と停止を続けて押すと、2 つ動く・記録中のフォルダを消す | 止まっている → 始めている → 記録中 → 止めている の順でしか動かない |
| 中 | URL から入れるとき、展開と SHA-256 が UI のスレッドで動く | 続きを UI に戻さない (ConfigureAwait(false)) |
| 中 | 記録の保存の失敗 (共有違反など) で読み込みが止まり、印が残って次の起動で問題の無い拡張機能を止める | 読み込みの印・問題の保存は失敗してもログに書いて続ける。書き込みは 1 つずつ |
| 中 | API: 時間切れ・思わぬ例外で答えずに切る、空の要求で例外、ゆっくり送る相手が受け口を占める | 503 / 500 で答える (種類だけログ)、空の要求を拒む、要求の受け取りは 5 秒まで |
| 低 | ログに選んだファイルのパス・Bearer のトークンが残りうる | パス (引用符の中の空白を含むものも) と Bearer を伏せる (テスト) |
| 低 | 履歴の件数のために UI のスレッドで全ファイルを読む | 読んであるときだけ件数を出し、始めたときに裏で読む |
| 低 | 壊れた画像の COM の例外で、まとめて読むのが止まる | GetText の側で InvalidOperationException にそろえる |
| 低 | 進み具合の知らせを中止で閉じた後、タイマーが動き続ける | 閉じた知らせのタイマーを動かさない |
| 低 | 画面の取り込みの画像を用意できないとき、落ちる | 確かめて「取り込めなかった」とする |
| 低 | 白い無地のスライドを「保護された画面」として残さない | 保護とみなすのは暗い無地だけ (読み取りの画面も同じ) |
| 低 | 更新待ちの版を別の版で入れ替えると、古い更新待ちのフォルダが残る | 消す |

問題が見つからなかった所: パッケージの展開 (パス・リンク・大きさ)・AppCommands への登録 (UI のスレッドだけ)・PNG / PPTX の書き出し・キーワードの見張り・QR の URL の扱い・API の認証 (Origin / Host / トークンは振り分けの前・一定時間で比べる)。

残した低いもの: 読み取りの履歴は保存期間の全件をメモリに持つ (90 日・大量のときは重い)。ROADMAP の「絞り込む検索」と合わせて、索引を持つ形にする。

## 確かめた範囲

- 開発中の CI (Windows: 単体テスト・拡張機能のパッケージ・ビルド・画面の画像・UI の確認 / Mac: arm64・x64 のビルド・補助プログラム・UI・画像・起動) は、性能・故障の注入のテストと見直しの修正の前の版で**すべて成功**
- 見直しの修正の後は、手元で: 単体テスト 480 件すべて合格・Windows / Mac のビルド成功・公式の拡張機能 6 つのパッケージと索引の作成を確認。
  最終の確かめは、この公開リポジトリの `plugin-platform-rc` ブランチの CI (`windows.yml`・`mac.yml`) で行う

## 残っていること・危険

- 拡張機能は GetText と同じ権限で動く (囲いの中ではない)。申告されたアクセスを見せるだけ。署名の検証は未 (SignPath の準備と合わせて)
- PPTX は部品と関係の整合をテストで確かめたが、PowerPoint で開く確認はしていない (この PC では Office を使わない方針)
- Mac の補助プログラム (Swift: capture_target・window_at・image_load・pdf_render) は CI でビルドを確認。実機での操作は未確認
- Quick OCR・訳を重ねる・範囲の選択は、手元で起動できない (SAC) ため、実際の操作は未確認。ロジックはテスト、画面は CI の画像
- 開発者向けの API の /v1/ocr は、トークンを持つ人がこの PC の画像のファイルを読ませられる (利用者本人のスクリプト向け。既定オフ)
