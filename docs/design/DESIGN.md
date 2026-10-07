# GetText デザインシステム

GetText の画面は「静か・速い・その場に合う・OS になじむ・読みやすい・拡張しても崩れない」を目指す。
Windows と Mac で **機能の構造と操作のしかたは同じ**、**見た目は各 OS に合わせる** (Windows は Fluent 2、Mac は Apple の HIG)。

## 1. トークン (`DesignTokens.cs`)

画面の XAML に色・字体・大きさを直接書かない。DesignTokens の値を資源として使う (Windows: `Theme.cs`、Mac: `mac/MacTheme.cs`)。

| 種類 | Windows (WPF) | Mac (Avalonia) |
| --- | --- | --- |
| 色 | `{DynamicResource Gt.TextPrimary}` など (ライト / ダークで入れ替わる) | 同じ名前 |
| 文字の大きさ | `{StaticResource Gt.Font.Body}` (Caption 12・Body 14・Subtitle 20・Title 28・LargeTitle 40・Dense 13) | Caption 11・Body 13・Subtitle 15・Title 22・LargeTitle 26 |
| 角の丸み | `Gt.Radius.Small 2`・`Button 4`・`Input 4`・`Card 8`・`Dialog 8` | 4・6・6・10・12 |
| 余白 | 2・4・8・12・16・24・32・48 だけを使う | 同じ |
| 字体 | Segoe UI Variable → Segoe UI → (日本語) Yu Gothic UI | システムの字体 → (日本語) ヒラギノ |

色は意味で名前を付ける。

| 名前 | 使う所 |
| --- | --- |
| Background / Surface / SurfaceSecondary / SurfaceHover / SurfacePressed | 窓の背景 / カード / 薄い面 / マウスを乗せた・押した面 |
| Border / BorderStrong / Divider | カードの枠 / 操作の部品の枠 (3:1 以上) / 区切り |
| TextPrimary / TextSecondary / TextTertiary / TextDisabled | 本文 / 説明 / 案内の文字 / 使えない |
| Accent (Hover・Pressed) / OnAccent / AccentText / AccentSubtle | 主な操作・選択中だけ。ブランドの飾りに多用しない |
| Success / Warning / Critical (+ Subtle) | 状態を伝えるときだけ (開いています・オンライン・録画中・エラー) |
| SearchHighlight / SearchHighlightActive (+ Border) | 検索で見つかった所 / 今の一致 (太い枠と左の印でも区別) |
| FocusRing | キーボードで選んでいる所 |
| SubtleHover / SubtlePressed | 背景に重ねる薄い色 (半透明) |

**コントラスト** (単体テスト `DesignSystemTests` が 4 つのパレットすべてで確かめる):
普通の文字 4.5:1 以上、アイコン・操作の部品・キーボードの枠 3:1 以上。
Windows の操作の主役の色 (AccentFillColorDefaultBrush) は利用者の Windows のアクセントの色に合わせる。

## 2. アイコン (`AppIcons.cs`)

文字コードや絵文字を画面に直接書かず、意味の名前 (`AppIcon.Search` など) を使う。

- Windows: `{local:Icon Search}` → Segoe Fluent Icons (Windows 10 は Segoe MDL2 Assets) の文字 (`WindowsIcons`)
- Mac: `<local:GtIcon Icon="Search"/>` → 24×24 の線の絵 (`MacIcons`。太さ 1.6、端は丸い)
- 拡張機能: 意味の名前か、確かめた SVG の線 (`IconSource.Svg`。命令と数だけ・4096 文字まで)。名前に絵文字は使えない (`IconValidator`)
- 意味がわかりにくいアイコンには、必ずツールチップと読み上げの名前 (AutomationProperties.Name) を付ける

## 3. 印 (ブランド)

画面を切り取る枠の角 **⌜ ⌝ ⌞ ⌟** と、その中の文字の行。

- アプリのアイコン: `tools/make_icon.py` (Assets/app.ico・Assets/app_1024.png・docs/images/mark.svg)
- ホームの見出し・読み取りの画面の空の状態: `BrandCorners`・`BrandLines` (App.xaml の線)
- 読み取り枠: 下の角 (⌞) とサイズ変更の角 (⌟)
- 飾りに使いすぎない (1 画面に 1 か所まで)

## 4. 部品

| 部品 | Windows | Mac | 使う所 |
| --- | --- | --- | --- |
| 操作バーのボタン | `CommandPush`・`CommandToggle`・`CommandMenu` | `Classes="command"` | 読み取りの画面の操作バー (最大 3 つのまとまり: 読み取り / 検索・表示 / 固定・その他) |
| カード | `Card` (塗り + 細い枠、影なし) | `Classes="card"` | 原文・訳・設定の行 |
| タイル | `TileButton` (全体を押せる) | `Classes="tile"` | ホームの機能 |
| 行 | `RowButton` | `Classes="row"` | 最近の議事録・検索の結果 |
| 知らせの帯 | `InfoBar` (情報・警告。アイコン + 題 + 説明 + 操作) | `Classes="infoBar"` | AI OCR の準備中・使えない・蓄積中・案内 |
| 状態の印 | `StatusPill` (点やアイコン + 文字) | 同じ | 開いています・録画中・PC 内 / オンライン |
| キーの表示 | `KeyCap` | 同じ見た目 | Ctrl+K・Esc |

止める必要があるとき (録画中の終了など) だけダイアログ。それ以外はその場の帯か状態の文字で知らせる。

## 5. コマンドと機能 (拡張機能もここに登録する)

- `CommandRegistry` (`Commands.cs`): Id・名前・別名 (英語など)・種類・アイコン・キー・関係する画面・使えるか・実行。
  コマンドの一覧 (Ctrl+K / ⌘K) は、ひらがな・カタカナ・全角・半角・大文字小文字を区別せずに探し、今の画面に関係するもの・最近使ったものを上に出す。
- `FeatureInfo`: ホームのタイルの元 (名前・1 行の説明・アイコン・入っているか・大きさ・状態・開く・閉じる・入れる)。
- 拡張機能は独自の窓や絵を描かず、コマンド・機能の情報・設定を渡し、**GetText が自分の部品で描く** (拡張機能が増えても見た目がそろう)。

```csharp
AppCommands.Registry.Register(new AppCommand
{
    Id = "example.hello", Title = "見本のコマンド", Keywords = ["Example"],
    Category = CommandCategory.Extension, Icon = IconSource.Svg("M4 12h16") ?? AppIcon.Extensions,
    Execute = () => { /* ... */ },
});
```

## 6. 状態

| 状態 | 見せ方 |
| --- | --- |
| 空 | アイコン + 短い説明 + 次にすること (例: ホームの「まだ議事録はありません」+「議事録を開く」) |
| 読み込み中 | その場の帯で何をしているか (「AI OCR を準備しています… その間は Windows OCR で読み取ります」)。操作は止めない |
| エラー | 続けられるなら帯 (「AI OCR を使えません · Windows OCR で読み取っています [詳細]」)。止める必要があるときだけダイアログ |
| 見つからない | 「見つかりませんでした」+ 別の探し方 |

## 7. 動き

マウスを乗せた・開いた・パネルの切り替えだけ。読み取りの結果が変わるたびの動き (fade・slide) は付けない。

## 8. 確認 (CI)

- `.github/workflows/windows.yml`: 単体テスト・画面の画像 (ライト / ダーク、拡大率 100・125・150・200%、状態ごと、最小の大きさ)・画面の操作の確認 → Actions の成果物 `GetText-windows-screens`
- `.github/workflows/mac.yml`: Mac の画面の画像 (ライト / ダーク) と操作の確認 → `ci/mac-results`
- 画像の比べ方: OS・字体の描き方の違いで揺れるので、ピクセルの完全一致では落とさない。画像は人が見て確かめ、意味の確認 (タイルの数・検索の件数・まとまりの見出しなど) は操作の確認で行う

## 9. まだのこと

- 英語の画面 (今は日本語だけ。文字を資源に分ける作業が要る)
- 拡張機能の配布の画面 (Extensions / Discover / Updates)・プレゼンの取り込み・読み取りの履歴 (機能そのものが無い。登録先と画面の部品は用意済み)
- Windows の「文字のサイズ」200% (WPF は OS の文字の拡大に従わない。拡大率の画像は描いて確かめている)
- 議事録の画面の色の直書き (話者の色など一部) の置き換え
