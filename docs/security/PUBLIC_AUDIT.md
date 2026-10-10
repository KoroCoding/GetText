# 公開リポジトリの監査 (Public Repository Privacy & Secret Audit)

- 実施: 2026-10-11 (PHASE 1)
- 対象: KoroCoding/GetText のすべての公開の参照 — `main` (`979b6da`)、`plugin-platform-rc` (`f007bcb`)、`ci/mac-results` (`59541f3`)、タグ `v1.0.0` — の**全履歴** (10 コミット、606 個のファイルの版)、コミットのメッセージと作者、GitHub Releases、Actions の成果物の一覧、Issues・Pull Requests・Discussions・Wiki・Pages
- 方法:
  - 秘密の情報: gitleaks と同じ種類の規則 (AWS・GitHub・OpenAI/Anthropic・Hugging Face・Google・Slack・Discord・DeepL のキー、秘密鍵、JWT、Bearer、パスワードの代入など) で、すべての版の文字と、画像・DLL・ZIP の中の文字列を走査した。gitleaks はこの PC に無く、ダウンロードはしていない
  - 個人情報: メール・電話番号・ユーザーフォルダ (`C:\Users\…`・`/Users/…`)・PC 名・作者の氏名や所属の文字列を同じく走査した
  - 画像・動画: 全履歴の画像 162 枚 (重複を除く) と、紹介の動画を 10 秒ごとに取り出した 55 コマを、目で確認した。PNG のテキストの情報・JPEG の Exif・動画と音声のメタデータも調べた
- 走査の詳しい結果 (場所の一覧) は、公開しない手元のファイル (`.local-audit/`、Git の対象外) にある。秘密の値はそこにも全文は書いていない

## 結果

**秘密の情報 (キー・トークン・パスワード・秘密鍵): 見つからなかった。** 失効・ローテーションが必要なものは無い。

| # | 重さ | 種類 | 場所 | 公開の範囲 | 確度 | 対応 | 再確認 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 低 | 内部の情報 | `plugin-platform-rc` のコミット `0479726` のメッセージに、作者の非公開リポジトリの名前 | 公開の履歴 | 確定 | 秘密の情報・個人情報ではない。消すには履歴の書き換え (force push) が要るため、そのままにする。以後、公開するコミットのメッセージも監査の対象にする | — |
| 2 | 情報 | 公開名 | 拡張機能の発行元の表示「KoroCoding」(画面の画像・`plugin.json`) | 公開 | 確定 | GitHub のアカウント名で、意図して公開している名前。本名ではない | — |
| 3 | 情報 | 作者のメール | コミットの作者 | 公開の履歴 | 確定 | すべて GitHub の noreply のアドレス。問題なし | 済 |
| 4 | 情報 | テストの音声 | `tests/assets/meeting.wav` | 公開 | 確定 | VOICEVOX の合成音声 (架空の会議・架空の人名)。クレジットは `tests/assets/README.md`。実在の人の声・発言は無い | 済 |
| 5 | 情報 | 画面の画像・動画 | `docs/images`・`docs/design/before`・`ci/mac-results` の画像・紹介の動画 | 公開 | 確定 | すべて見本のデータを入れたアプリの画面 (`--doc-snapshots`) と作った画像。人名は架空 (田中・佐藤など)、保存先のユーザー名は伏せ字、CI の Mac のユーザー名は `runner`。メタデータの文字情報も無い | 済 |
| 6 | 情報 | ユーザーフォルダの文字 | `tests/GetText.Tests/WindowListTests.cs` | 公開 | 確定 | テストの架空のユーザー名 (`C:\Users\u\…`)。問題なし | 済 |
| 7 | — | 未確認 | GitHub Releases の `v1.0.0` の添付ファイル 3 つ (Windows・Mac の ZIP、計 177 MB) | 公開 | 未確認 | 公開のタグから CI で作ったもの。中身の走査にはダウンロードが要るため、許可を得てから行う (同じ作り方の Mac の ZIP は `ci/mac-results` で走査済み・問題なし) | BLOCKED |
| 8 | — | 未確認 | Actions の成果物 25 個・実行のログ | 公開 (成果物の取得はログインが要る) | 未確認 | CI が見本のデータで作る画面の画像と配布用の ZIP。中身・ログはログインしないと取れないため未確認 | BLOCKED |

Issues・Pull Requests・Discussions・Wiki・GitHub Pages は無い。

## 依存の部品 (参考。詳しくは PHASE 2)

- NuGet (Windows・Mac・拡張機能・テスト): nuget.org の脆弱性の情報で照合し、既知の脆弱性は無かった (2026-10-11)
- GitHub Actions: 版の指定がタグ (`@v4` など) で、コミットの SHA に固定していない。権限 `contents: write` がワークフロー全体にかかっている → PHASE 2 で見直す

## 再発を防ぐ決まり

公開の参照に push する前に、毎回: 変更したファイルの一覧・秘密の情報の走査・個人情報の走査・画像と動画のメタデータ・依存の脆弱性・差分の確認・ビルド・単体テスト。画面の画像・音声・動画は見本のデータだけで作る。
