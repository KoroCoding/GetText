# パッケージの形 (package format)

## .gtplugin

拡張機能のパッケージは ZIP で、拡張子は `.gtplugin` です。区切りは `/`。

```
gettext.keyword-monitor-1.0.0.gtplugin
├── plugin.json          (必須)
├── bin/                 (DLL と .deps.json。GetText.Plugin.Abstractions は入れない)
├── resources/           (任意)
├── README.md            (任意)
└── LICENSE              (任意)
```

`tools/make_plugins.ps1` が `plugins/*` をビルドしてこの形にし、一覧 (`plugins-index.json`) も作ります。

### 入れるときに拒むもの

- `..`・絶対パス・ドライブ (`C:`)・`\\` で始まるパス、`:` `*` `?` `"` `<` `>` `|`・制御文字、末尾の `.` や空白、予約名 (`CON`・`NUL`・`COM1` など)
- シンボリックリンク、同じ名前のファイルが 2 つ
- 合計 512 MB・1 ファイル 256 MB・4000 ファイルを超えるもの、圧縮率が 200 倍を超えるもの (ZIP 爆弾)。宣言より大きく展開されたら途中で止める
- `plugin.json` が無い・正しくない、この GetText で使えない、入口の DLL が無い
- 一覧の SHA-256 と一致しない (一覧・同梱から入れるとき)

中身をすべて確かめてから作業用のフォルダに展開し、問題が無ければ `plugins/<id>/<版>/` に移します。

## plugin.json

```json
{
  "id": "gettext.keyword-monitor",
  "name": { "ja": "キーワードの見張り", "en": "Keyword Monitor" },
  "version": "1.0.0",
  "publisher": "KoroCoding",
  "description": { "ja": "読み取った文字に決めた言葉が出たら知らせます", "en": "Notifies you when a keyword appears on screen" },
  "apiVersion": 1,
  "minHostVersion": "1.1.0",
  "platforms": ["any"],
  "capabilities": ["keyword-monitor"],
  "permissions": ["screen-text", "notifications", "storage"],
  "dependencies": [],
  "optionalDependencies": [],
  "entryPoint": { "assembly": "bin/GetText.Plugin.KeywordMonitor.dll", "type": "GetText.Plugins.KeywordMonitor.KeywordMonitorPlugin" },
  "requiresRestart": true,
  "models": [],
  "license": "GPL-3.0-or-later",
  "homepage": "https://github.com/KoroCoding/GetText",
  "icon": "Search"
}
```

| 項目 | 必須 | 内容 |
| --- | --- | --- |
| `id` | ○ | 小文字・数字を `.` か `-` でつなぐ (2〜8 個・80 文字まで)。例 `gettext.keyword-monitor`。`gettext.` で始まる id は GetText の開発元のもの |
| `name`・`description` | ○ | 文字列、または `{ "ja": "…", "en": "…" }` |
| `version` | ○ | SemVer (`1.2.3`、`1.2.3-beta.1`) |
| `publisher` | ○ | 発行元 |
| `apiVersion` | ○ | Plugin API の版 (今は `1`) |
| `minHostVersion` | ○ | 必要な GetText の版 |
| `platforms` | ○ | `any`・`win-x64`・`osx-arm64`・`osx-x64` |
| `permissions` | | 使うと申告するアクセス (下の表)。知らない名前があると入れられない |
| `capabilities` | | この拡張機能が加える機能の名前 (ほかの拡張機能が `HasCapability` で調べる) |
| `dependencies`・`optionalDependencies` | | `{ "id": "…", "version": ">=1.0.0" }`。版の条件は `>=1.2.0`・`1.2.0`・`*` |
| `entryPoint` | | `assembly` (パッケージの中の .dll の相対パス) と `type` (`IGetTextPlugin` を実装した型) |
| `requiresRestart` | | 今は常に再起動で反映 |
| `models` | | 使う AI のモデルの id (`ModelCatalog`。パッケージには入れない。設定 → モデルの一覧 で入れる) |
| `license`・`homepage`・`icon` | | `icon` は `AppIcon` の名前 (`Search`・`History`・`Document` など) |

知らない項目は読み飛ばし、診断に出します (新しい版のマニフェストを古い GetText で読んでも止まらない)。

### permissions (要求されるアクセス)

| 名前 | 画面の名前 | 内容 |
| --- | --- | --- |
| `screen-text` | 読み取った画面の文字 | 読み取りの画面が読んだ文字を受け取る |
| `screen-capture` | 画面の画像 | 画面・窓の画像を撮る (OS の正式な方法だけ) |
| `clipboard` | クリップボード | クリップボードに文字を入れる |
| `files` | 選んだファイル | 利用者が選んだファイル・フォルダを読み書きする |
| `storage` | 自分のデータの保存 | 拡張機能のフォルダに保存する |
| `notifications` | 知らせ | GetText の画面に知らせを出す |
| `network` | インターネット | インターネットにつなぐ (画面に「オンライン」と出る) |

申告であって、GetText が制限するものではありません ([security.md](security.md))。

## plugins-index.json (拡張機能の一覧)

GitHub Releases に置く一覧 (専用のサーバーはありません)。同梱の一覧 (`bundled-plugins/plugins-index.json`) も同じ形です。

```json
{
  "schema": 1,
  "generated": "2026-10-07T00:00:00Z",
  "plugins": [
    {
      "id": "gettext.keyword-monitor", "name": { "ja": "キーワードの見張り" }, "publisher": "KoroCoding",
      "description": "…", "version": "1.0.0",
      "file": "gettext.keyword-monitor-1.0.0.gtplugin",
      "url": "https://github.com/KoroCoding/GetText/releases/download/v1.1.0/gettext.keyword-monitor-1.0.0.gtplugin",
      "sha256": "…64 桁…", "size": 12345, "installedSize": 40000,
      "platforms": ["any"], "minHostVersion": "1.1.0", "apiVersion": 1,
      "permissions": ["screen-text", "notifications"], "models": [], "license": "GPL-3.0-or-later",
      "icon": "Search", "signed": true, "local": true
    }
  ]
}
```

- `url` は https だけ。`file` は同梱の一覧で使う (一覧と同じフォルダの相対パス)。
- `sha256` が無い・64 桁でない項目、形の違う項目は読み飛ばします (1 件のせいで全部を捨てない)。
- `local` を書かなければ、`permissions` に `network` が無いときだけ「この PC の中」と出します。
- 同じ id は新しい版を使い、同じ版なら同梱を優先します (ネットワークが要らない)。
