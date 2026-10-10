# 開発者向けの API

GetText の読み取り・翻訳を、自分のスクリプトから使うための API です。**既定はオフ** です。

- 設定 → 診断 → 「開発者向けの API」をオンにすると、この PC の中 (`127.0.0.1`) だけで受けます (ほかの PC からは届きません)。
- すべての要求に **トークン** が要ります (`Authorization: Bearer <トークン>`)。トークンは同じ場所の「トークンをコピー」でコピーでき、「作り直す」で前のトークンを使えなくできます。トークンは `%LOCALAPPDATA%\GetText\api-token` (Mac は `~/Library/Application Support/GetText/api-token`) にあります。人に見せないでください。
- ブラウザからは使えません (`Origin` の付いた要求と、`Host` が `127.0.0.1` / `localhost` でない要求は断ります)。
- 本文は 1 MB まで、10 秒で打ち切ります。同時に 4 つまで。
- `/v1/ocr` で読めるのは、この PC の中のフォルダから書いた画像のファイル (png・jpg・bmp・gif・tif・webp) だけです。ネットワークの場所 (`\\サーバー\…`) やデバイスの名前は断ります。
- ログ (`logs/plugins.log`) には、メソッド・パス・結果だけを書きます。文字・トークンは書きません。
- 既定の番号 (ポート) は `47390` です。設定で変えられます。

## 入口

| メソッド | パス | 内容 |
| --- | --- | --- |
| GET | `/v1/status` | 版・環境・読み取りの画面が開いているか・翻訳が使えるか |
| GET | `/v1/text` | 読み取りの画面に表示している原文 (`text`) と訳 (`translation`) |
| GET | `/v1/plugins` | 入れた拡張機能と、読み込めたか |
| POST | `/v1/ocr` | 画像のファイルを読む。`{"path": "C:\\scan.png", "provider": "ai"}` (provider は省略可) → `text` と行の位置 (`lines[].box` = 左・上・右・下) |
| POST | `/v1/translate` | この PC の中の翻訳で日本語に訳す。`{"texts": ["Hello"]}` → `{"translations": ["こんにちは"]}` (200 文まで。オンラインの翻訳には送りません) |

問題があれば `{"error": "理由"}` を返します (400・401・403・404・405)。

## 例

PowerShell:

```powershell
$token = Get-Content "$env:LOCALAPPDATA\GetText\api-token"
Invoke-RestMethod http://127.0.0.1:47390/v1/text -Headers @{ Authorization = "Bearer $token" }
```

```powershell
$body = @{ path = "C:\Users\me\Pictures\scan.png" } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:47390/v1/ocr -Method Post -Body $body -ContentType "application/json; charset=utf-8" -Headers @{ Authorization = "Bearer $token" }
```

Mac・curl:

```bash
TOKEN=$(cat ~/Library/Application\ Support/GetText/api-token)
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:47390/v1/status
```

```bash
curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"texts":["Good morning"]}' http://127.0.0.1:47390/v1/translate
```
