# GetText が外と通信する経路 (Network)

既定では、読み取った文字・画面・音声をインターネットに送らない。テレメトリ (利用の記録の送信) も無い。アカウントは要らない。
下の通信は、どれも利用者が選んだ・押したときだけ起きる。設定 → プライバシー でも「PC 内」「オンライン」の印で見られる。

| 経路 | いつ | 送るもの | 送り先 | コード |
| --- | --- | --- | --- | --- |
| 翻訳 (Google) | 読み取りの画面・議事録の翻訳で Google を選んだとき | 外国語と判定した文 | translate.googleapis.com / clients5.google.com | `Translator.cs` |
| 翻訳 (DeepL) | DeepL を選び、キーを入れたとき | 外国語と判定した文・DeepL のキー | api-free.deepl.com / api.deepl.com | `Translator.cs` |
| 拡張機能の一覧 | 設定 → 拡張機能 で「オンラインで探す」「更新を確認」を押したとき | なし (一覧を読むだけ) | github.com (Releases の plugins-index.json) | `PluginHost/PluginIndex.cs` |
| 拡張機能のダウンロード | 一覧から「入れる」「更新」を押したとき | なし | 一覧に書かれた https の URL (SHA-256 と署名で確かめる) | `PluginHost/PluginStore.cs` |
| モデルのダウンロード | セットアップ・モデルの一覧で「入れる」を押したとき | なし | huggingface.co / github.com / modelscope.cn | `offline/download_models.py` |
| Python の部品 | セットアップのとき | なし | pypi.org (版を固定) | `offline/setup_offline.ps1` |
| .NET・Python の入手 | `setup.bat` で足りないときだけ (winget) | なし | winget の配布元 | `setup.ps1` |
| リンクを開く | 画面のリンク・QR の URL のボタンを押したとき (http / https だけ) | URL | その URL (既定のブラウザで開く) | `IPluginUi.OpenUrl` |
| 開発者向けの API | 利用者がオンにしたときだけ。外には出ない | — | 127.0.0.1 (この PC の中だけで受ける) | `DeveloperApi.cs` |

自動の更新の確認・送信は無い。オンラインの翻訳を選んでいても、「訳を画面に重ねる」は日本語訳の欄と同じ訳を使うだけで、追加では送らない。

## まだ改善の余地があるもの

- Python の部品は版を固定しているが、ハッシュ (`--require-hashes`) では固定していない
- モデルのダウンロードは配布元の HTTPS を信頼している (ファイルごとのハッシュの照合は、モデルによって無い)
