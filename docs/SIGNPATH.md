# 公開と SignPath の署名の手順

Windows の「スマート アプリ コントロール」が有効な PC でも GetText を起動できるよう、
[SignPath Foundation](https://signpath.org/) の無料のコード署名 (オープンソースのプロジェクト向け) を受けるための手順です。
署名の証明書は SignPath Foundation の名前で発行され、GetText の発行元は「SignPath Foundation」と表示されます。

## 1. GitHub に公開する

1. GitHub で新しい **公開 (Public)** リポジトリを作る (例: `KoroCoding/GetText`)。README・ライセンスは GitHub 側で作らない (このフォルダに入っている)。
2. このフォルダを push する。
   ```bash
   git remote add origin https://github.com/KoroCoding/GetText.git
   git push -u origin main
   ```
3. GitHub アカウントの **2 段階認証 (多要素認証)** をオンにする (SignPath の条件)。
4. Actions の「Windows」と「Mac」が成功することを確かめる (リポジトリの Actions のタブ)。
5. タグを付けて、署名していない最初の版を Releases に出す (SignPath の条件: 署名したい形で、すでに公開されていること)。
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```
   「Windows」が終わると、Releases に `GetText-windows-x64.zip` が置かれる。

## 2. SignPath Foundation に申し込む

1. <https://signpath.org/apply> から申し込む。聞かれること (例):
   - プロジェクト: GetText、リポジトリの URL、ライセンス: GPL-3.0-or-later
   - 何をするアプリか: README の最初の段落 (画面の文字の読み取り・翻訳、会議の議事録、画面の録画。すべて PC の中で動く)
   - 署名するもの: Windows の GetText.exe と GetText.dll (GitHub Actions でビルドしたもの)
   - コード署名のポリシーの場所: README の「コード署名のポリシー」
2. 審査に通ると、SignPath.io の組織 (Organization) とプロジェクトが作られる。SignPath のアカウントでも **多要素認証** をオンにする。

## 3. SignPath と GitHub Actions をつなぐ

SignPath.io の画面で:

1. **Trusted build system** に GitHub.com を追加し、このリポジトリのプロジェクトに結び付ける。
2. プロジェクトの **Artifact configuration** を作り、`.signpath/artifact-configuration.xml` の中身を貼る (slug は例えば `initial`)。
3. **Signing policy** を確かめる (SignPath Foundation が用意する。例: `test-signing`・`release-signing`)。`.github/workflows/windows.yml` は、タグのときに `release-signing`、それ以外は `test-signing` を使う。名前が違うときは、windows.yml の `signing-policy-slug` を直す。
4. **API トークン** を作る (CI 用のユーザーで、そのプロジェクトに署名を頼む権限だけ)。

GitHub のリポジトリの Settings → Secrets and variables → Actions で:

| 種類 | 名前 | 値 |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | 上で作った API トークン |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath の Organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | SignPath のプロジェクトの slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | 2. で作った Artifact configuration の slug |

`SIGNPATH_ORGANIZATION_ID` が設定されていないあいだは、署名の手順は飛ばして、署名していない ZIP を作る。

## 4. 署名した版を出す

1. 新しいタグを付けて push する (例: `v1.0.1`)。
2. 「Windows」の途中で、SignPath の承認者 (Approver) に承認を求める通知が届く。SignPath.io で承認する。
3. 署名した `GetText-windows-x64.zip` が Releases に置かれる。展開して、`GetText.exe` を右クリック → プロパティ → デジタル署名 に「SignPath Foundation」があることを確かめる。
4. スマート アプリ コントロールが有効な PC で起動できるかを確かめる。

## 守ること (SignPath Foundation の条件の要点)

- リポジトリとソースは公開し、GPL-3.0 のまま (すべての部品がオープンソース)。
- 署名するのは、このリポジトリのソースから GitHub Actions でビルドしたものだけ (手元でビルドしたものは署名しない)。
- 利用者のプライバシーやセキュリティを損なう機能を入れない。システムの設定を変えるときは知らせる。アンインストールの手順を用意する (`uninstall.bat`・README の「アンインストール」)。
- README の「プライバシー」「コード署名のポリシー」を、実際のとおりに保つ (役割の担当が変わったら直す)。
- 実行ファイルの製品名と版 (GetText.csproj の `Product`・`Version`) を入れておく。版を 2.x にするときは、`.signpath/artifact-configuration.xml` の `product-version` も直す。

条件の全文: <https://signpath.org/terms>
