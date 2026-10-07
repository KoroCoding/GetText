# 拡張機能のしくみ (architecture)

GetText は **小さな本体 (Core)** と、必要なものだけ入れる **拡張機能 (Plugin)**、選んで入れる **AI のモデル** でできています。
拡張機能が無くても、文字の読み取り・翻訳・議事録・画面の録画はそのまま使えます。

```
┌──────────── GetText (Windows: WPF / Mac: Avalonia + Swift の補助) ────────────┐
│ ホーム・Ctrl+K・設定・知らせ (GetText の部品で描く)                            │
│        ▲ FeatureInfo / AppCommand / 設定の項目 / 知らせ                        │
│ PluginHost (共有)  PluginRuntime ─ PluginContext (拡張機能 1 つずつ)            │
│                    PluginStore (入れる・更新・戻す・消す) ─ PluginCatalog (一覧) │
│                    PluginIndex (同梱・オンラインの一覧)                         │
│        ▲ GetText.Plugin.Abstractions (Plugin API v1。GetText と共有)            │
└────────┼──────────────────────────────────────────────────────────────────────┘
         │ AssemblyLoadContext (拡張機能ごと)
   拡張機能の DLL (IGetTextPlugin)
```

## 部品

| 場所 | 役割 |
| --- | --- |
| `sdk/GetText.Plugin.Abstractions` | Plugin API (`IGetTextPlugin`・`IPluginContext`・コマンド・機能・設定・知らせ・読み取りの結果・サービス)。拡張機能が参照する唯一の部品 |
| `PluginHost/PluginManifest.cs` | `plugin.json` の検査、版 (SemVer)・互換・依存の解決 |
| `PluginHost/PluginPackage.cs` | `.gtplugin` の安全な展開 |
| `PluginHost/PluginStore.cs` | 置き場所と記録 (`plugin-state.json`)。入れる・更新・戻す・消すは次の起動で反映 |
| `PluginHost/PluginRuntime.cs` | 読み込み・`IPluginContext`・終了・読み取りの結果を渡す・ログ (`PluginLog`) |
| `PluginHost/PluginIndex.cs` | 拡張機能の一覧 (`plugins-index.json`) |
| `PluginHost/PluginCatalog.cs` | 設定 → 拡張機能 の一覧と文 (Windows・Mac で共有) |
| `WindowsPluginHost.cs`・`PluginToast.cs` / `mac/MacPluginHost.cs` | OS ごとの部品 (知らせ・ファイルを選ぶ・進み具合・Finder / エクスプローラーで表示・クリップボード・URL) |
| `ModelCatalog.cs` | AI のモデルの一覧・状態・1 つずつ入れる/消す |

Windows 版と Mac 版は同じ `PluginHost` のファイルを使います (Mac のプロジェクトはリンクで取り込む)。

## 起動から終了まで

1. **Prepare** (起動の早い段階・速い): `plugin-state.json` を読み、前回の続きを片付ける (`ApplyPending`)。
   - 前回、読み込みの途中で落ちた拡張機能 (`Loading` の印が残っている) は、前の版があれば戻し、無ければ止める。
   - 消す予定のものを消す (ファイルと設定・記録)。更新の予定 (`PendingVersion`) を今の版にする。作業途中のフォルダを消す。
2. ホームを出す (拡張機能を待たない)。
3. **LoadAll** (手が空いたとき): 有効なものを、互換 (Plugin API・GetText の版・環境) と依存を確かめて、依存の順に読み込む。
   - 読み込む前に `Loading` の印を書き、終わったら消す (落ちたら次の起動で分かる)。
   - `Initialize` の中で登録したコマンド・機能は、最後まで成功したときだけ GetText に登録する (`Commit`)。途中で例外なら全部外す (`Rollback`)。
4. 使っている間: コマンド・タイル・設定は GetText が描く。読み取りの結果 (`OcrFrameRead`) は、受け取る拡張機能があるときだけ作り、別のスレッドで渡す。前の分を処理中なら次の分は飛ばす (読み取りを遅くしない)。
5. **ShutdownAll** (終了時): 逆の順に `Shutdown` を呼び、1 つにつき 2 秒で待つのをやめる。

**Safe Mode** (`--safe-mode`、または環境変数 `GETTEXT_SAFE_MODE=1`): 拡張機能を読み込まずに起動します。設定 → 拡張機能 で止める・消すはできます。

## 画面は GetText が描く

拡張機能は自分の窓を作らず、情報だけを渡します。

| 拡張機能が渡すもの | GetText が描く場所 |
| --- | --- |
| `PluginFeature` | ホームのタイル (状態の印・閉じる) |
| `PluginCommand` | コマンドの一覧 (Ctrl+K / ⌘K)。分類は「拡張機能」、id は `plugin:<拡張機能の id>:<コマンドの id>` |
| `PluginSettingsPage` | 設定 → 拡張機能 の各行の「設定」(切り替え・文字・数・選択) |
| `PluginNotification` | 画面の右下の知らせ (アイコン・色はデザイントークン) |
| `IPluginUi.BeginProgress` | 右下の進み具合 (中止のボタンつき) |

名前に絵文字は使えません (アイコンは `AppIcon` の名前か、確かめた SVG の線)。これで、拡張機能が増えても GetText の見た目はそろったままです。

## 読み込み (AssemblyLoadContext)

- 拡張機能ごとに `AssemblyLoadContext` を作り、その拡張機能のフォルダの DLL だけを読みます (`AssemblyDependencyResolver`)。
- `GetText.Plugin.Abstractions` だけは GetText のものを共有します (型が一致するため)。
- 読み込んだものは外しません (collectible にしない)。入れる・更新・止める・消すは再起動で反映します。

## 置き場所

| | Windows | Mac |
| --- | --- | --- |
| 拡張機能 | `%LOCALAPPDATA%\GetText\plugins\<id>\<版>\` | `~/Library/Application Support/GetText/plugins/…` (.NET の LocalApplicationData) |
| 記録 | `plugins\plugin-state.json` | 同じ |
| 拡張機能のデータ・設定 | `plugins\.data\<id>\` (`IPluginContext.DataDirectory`) | 同じ |
| ログ | `%LOCALAPPDATA%\GetText\logs\plugins.log` | 同じ構成 |
| 同梱の拡張機能 | `GetText.exe` と同じフォルダの `bundled-plugins\` | `GetText.app/Contents/Resources/bundled-plugins/` |

GetText のフォルダ (配布物) には書き込みません。

## ログと診断

`plugins.log` には、入れた・更新・止めた・問題などだけを書きます。読み取った文字・訳・議事録の文・キーは書きません。
利用者名と「key=…」「token: …」のような文字列は伏せます (`PluginLog.Redact`)。
設定 → 拡張機能 → 詳細 の「診断をコピー」は、版・環境・状態・最後の問題 (伏せ字) だけを出します。
