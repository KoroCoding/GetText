# 拡張機能のパッケージの署名

## 仕組み

- パッケージ (`.gtplugin`) の中の `signature.json` に、ほかのすべてのファイルの「SHA-256 と名前」の一覧 (名前の順) への **ECDSA P-256 (SHA-256)** の署名を入れる (`PluginHost/PluginSignature.cs`)。ファイルを 1 つ書き換えても、足しても、署名と合わなくなる
- GetText は、信頼する鍵の一覧 (`PluginSignatures.TrustedKeys`: 鍵の名前・発行元・使える id の始まり・信頼・公開鍵) で確かめる
  - 信頼する鍵で確かめた → その鍵の信頼 (公式 / 確認済み)。鍵は決めた id の始まり (公式なら `gettext.`) にだけ効く
  - 署名が中身と合わない・形が正しくない → **入れない**
  - 署名が無い・知らない鍵 → コミュニティ (GetText に同梱の公式の拡張機能だけは、配布物を根拠に公式)
- 署名は「だれが作ったか」を確かめるもの。拡張機能は GetText と同じ権限で動くので、署名があっても「安全」という意味ではない

## 鍵の用意 (開発元が行う。まだ行っていない)

1. 鍵を作る (秘密鍵はファイルに書き、画面には出さない):
   `dotnet run --project tools/PluginSign -- keygen <秘密鍵のファイル>` → 公開鍵が表示される
2. 秘密鍵を GitHub の Secrets `GETTEXT_PLUGIN_SIGNING_KEY` に入れ、手元のファイルは安全な場所に移すか消す。リポジトリ・Issue・ログに書かない
3. 公開鍵を `PluginSignatures.TrustedKeys` に登録する (`new PluginSigningKey("gettext-official-1", "KoroCoding", "gettext.", PluginTrust.Official, "<公開鍵>")`)
4. CI の `tools/make_plugins.ps1` は、環境変数 `GETTEXT_PLUGIN_SIGNING_KEY` があるときだけ署名する (鍵の名前は `GETTEXT_PLUGIN_SIGNING_KEY_ID`、既定 `gettext-official-1`)

鍵をなくした・漏れたときは、新しい鍵を作って `TrustedKeys` を差し替え、古い鍵を外した GetText を出す (外すまでは、漏れた鍵の署名も信頼されてしまう)。

## まだできていないこと

- 本番の鍵は未作成 (上の 1〜3 は開発元の作業)。それまで、オンラインの一覧から入れた拡張機能はコミュニティになる
- 鍵を失効させる一覧 (revocation) は無い。鍵を外した GetText に更新することで代える
- 一覧 (plugins-index.json) 自体の署名は無い。古い版への差し替え (rollback) は、署名があっても防げない (今入れている版より古い版は、一覧の「更新」には出さない)
