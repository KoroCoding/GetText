# 拡張機能の作り方 (development)

いちばん小さな例は [plugins/GetText.Plugin.Sample](../../plugins/GetText.Plugin.Sample) です (ホームのタイル・コマンド・設定 1 つ・知らせ)。

## 1. プロジェクト

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <!-- Plugin API は GetText のものを使うので、パッケージには入れない -->
    <ProjectReference Include="..\..\sdk\GetText.Plugin.Abstractions\GetText.Plugin.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

- Windows・Mac の両方で動かすなら `net9.0` (OS に依存しない) にし、`plugin.json` の `platforms` を `["any"]` にします。
- WPF・Avalonia などの UI の部品は参照しません (画面は GetText が描く)。

## 2. 入口 (IGetTextPlugin)

```csharp
using GetText.Plugins;

public sealed class HelloPlugin : IGetTextPlugin
{
    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.AddCommand(new PluginCommand
        {
            Id = "hello",
            Title = new LocalizedText(new Dictionary<string, string> { ["ja"] = "あいさつを出す", ["en"] = "Say hello" }),
            Keywords = ["hello"],
            Icon = "Info",
            Execute = _ => { context.Notify(new PluginNotification("こんにちは")); return Task.CompletedTask; },
        });
    }

    public void Shutdown() { }
}
```

`Initialize` は UI のスレッドで呼ばれます。**すぐに返してください** (重い準備は `Task.Run` で)。例外を投げると、その拡張機能が登録したものはすべて外れ、設定 → 拡張機能 に理由が出ます。GetText は止まりません。
`Shutdown` は終了時に呼ばれ、2 秒で待つのをやめます。

## 3. GetText に渡せるもの (IPluginContext)

| メソッド | 出る場所 |
| --- | --- |
| `AddFeature(PluginFeature)` | ホームのタイル。`Status` で「開いています」「記録中」などを返す |
| `AddCommand(PluginCommand)` | コマンドの一覧 (Ctrl+K / ⌘K) |
| `AddSettings(PluginSettingsPage)` | 設定 → 拡張機能 の「設定」。種類は Toggle・Text・MultilineText・Number・Choice、`Advanced` で「詳細設定」に入れる |
| `Notify(PluginNotification)` | 右下の知らせ (操作のボタン・音も付けられる) |
| `AddSearchProvider`・`AddOcrProvider`・`AddExportProvider` | 検索・読み取りの方式・書き出しの提供 (順に GetText 側の画面につなぐ) |
| `event OcrFrameRead` | 読み取りの画面が読んだ 1 回分 (行と画面の座標・文字。`GetImage()` で画像)。別のスレッドで届き、前の分を処理中なら次の分は飛ばされる |
| `Settings` | 拡張機能の設定の値 (`DataDirectory/settings.json` に保存) |
| `DataDirectory` | 拡張機能だけのフォルダ (消すときに一緒に消える) |
| `GetService<T>()` | GetText の部品を使う (下の表) |
| `HasCapability(name)` | GetText (`ocr`・`ocr.local`・`meeting.transcript`・`screen.record`) やほかの拡張機能の機能があるか |
| `Log` | `plugins.log` に書く。**読み取った文字・訳・キーは書かないこと** |

名前に絵文字は使えません (アイコンは `AppIcon` の名前か、`PluginIcon.SvgPath` に線の SVG パス)。

### GetText の部品 (`GetService<T>()`)

| 部品 | できること |
| --- | --- |
| `IPluginUi` | ファイル・フォルダ・保存先を選ぶ、一覧から選ぶ (`ChooseAsync`)、画面の範囲を選んで画像を受け取る (`SelectScreenRegionAsync`)、進み具合 (中止つき)、クリップボード、URL を開く (http・https だけ)、エクスプローラー / Finder で表示 |
| `IOcrService` | 画像 (BGRA) の文字を読む。方式を指定しなければ利用者が選んだ方式。PC の外に送る方式は指定したときだけ |
| `ITranslationService` | 日本語に訳す (この PC の中の翻訳だけ。オンラインには送らない) |
| `IScreenCaptureService` | 画面 (モニター)・窓の一覧と、1 枚の取り込み (保護された窓は黒いまま) |
| `IDocumentService` | 画像のファイル・PDF のページを画像にする (OS の標準の部品) |
| `IOverlayService` | 画面の上に文字を重ねる (描くのは GetText。クリックは下に通り、読み取りには写らない) |

使えない環境では `null` が返るので、確かめてから使ってください。公式の拡張機能 ([plugins/](../../plugins)) が使い方の例です。

## 4. plugin.json

[package-format.md](package-format.md) を見てください。`permissions` には、使うものを正直に書きます (入れる前に利用者に見せます)。
`OcrFrameRead` を使うなら `screen-text`、`Notify` を使うなら `notifications`、インターネットにつなぐなら `network` です。

## 5. ビルドとパッケージ

```bash
powershell -ExecutionPolicy Bypass -File tools/make_plugins.ps1 -Out out/plugins
```

`plugins/` の下の各プロジェクトを `dotnet publish` し、`<id>-<版>.gtplugin` と `plugins-index.json` を作ります。
GitHub Releases に置くときは `-ReleaseTag v1.1.0` を付けると、一覧に `url` が入ります。

## 6. 入れて試す

設定 → 拡張機能 → 「ファイルから入れる…」で `.gtplugin` を選び、GetText を再起動します。
うまく動かないときは、設定 → 拡張機能 → 詳細 の「診断をコピー」と `%LOCALAPPDATA%\GetText\logs\plugins.log` を見てください。
読み込みの途中で GetText が落ちたときは、次の起動でその拡張機能を止めます。`--safe-mode` で起動すると拡張機能を読み込みません。

## 7. テスト

- 拡張機能の中の処理は、ふつうの単体テストで確かめられます (`IPluginContext` を自分で用意する)。
- GetText 側の受け止め方 (例外・止まる・遅い処理) は `tests/TestPlugins/GetText.Plugin.Faulty` と `tests/GetText.Tests/PluginRuntimeTests.cs` を参考にしてください。

## 守ること

- 自分の窓を出さない (どうしても必要なら、まず GetText に部品を足す相談を)。
- 既定でインターネットに送らない。送るなら `network` を申告し、説明に何を送るかを書く。
- 読み取った文字・訳・議事録の文・キーをログに書かない。
- DRM・HDCP・画面のキャプチャの保護を回避しない (保護された画面は黒く写る。そのときは知らせるだけ)。
