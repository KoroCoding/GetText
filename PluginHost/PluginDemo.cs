using System.IO;
using System.IO.Compression;
using System.Text;
using GetText.Plugins;

namespace GetText;

/// <summary>
/// 画面の画像 (CI の見本) 用の、拡張機能の一覧の見本。一時フォルダに見本のパッケージと索引を作る (通信しない・利用者のフォルダに書かない)。
/// 見本の拡張機能は読み込まない (DLL は空)。
/// </summary>
public static class PluginDemo
{
    private sealed record Sample(string Id, string Ja, string Publisher, string Description, string Version, string Permissions,
        string Icon, bool Verified = true, string MinHost = "1.0.0", string? Models = null);

    private static readonly Sample[] Samples =
    [
        new("gettext.keyword-monitor", "キーワードの見張り", "KoroCoding", "読み取った文字に決めた言葉が出たら知らせます", "1.0.0", "[\"screen-text\",\"notifications\",\"storage\"]", "Search"),
        new("gettext.presentation", "スライドの記録", "KoroCoding", "発表のスライドが変わるたびに保存し、PPTX にまとめます", "1.0.0", "[\"screen-capture\",\"files\",\"storage\"]", "Presentation"),
        new("gettext.history", "読み取りの履歴", "KoroCoding", "読み取った文字をこの PC に保存し、あとで検索できます (既定は保存しない)", "1.0.0", "[\"screen-text\",\"storage\"]", "History"),
        new("gettext.document-ocr", "文書の文字の読み取り", "KoroCoding", "PDF や画像のファイルの文字を読み、テキストにします", "1.1.0", "[\"files\",\"notifications\"]", "Document"),
        new("example.webhook", "Webhook に送る", "Example Labs", "読み取った文字を、指定した URL に送ります", "0.3.0", "[\"screen-text\",\"network\"]", "Cloud", Verified: false),
        new("gettext.future", "次の版の機能", "KoroCoding", "この GetText より新しい版が必要な拡張機能の見本です", "2.0.0", "[]", "Extensions", MinHost: "9.0.0"),
    ];

    /// <summary>
    /// scenario: empty・installed・discover・updates。loaded は「起動したときに読み込んだ結果」(一覧の状態を決める)。
    /// </summary>
    public static PluginCatalog Create(string dir, string scenario)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var root = Path.Combine(dir, "plugins");
        var bundled = Path.Combine(dir, "bundled-plugins");
        Directory.CreateDirectory(bundled);
        var loaded = new List<LoadedPlugin>();
        var store = new PluginStore(root);
        if (scenario == "empty") return Catalog(store, loaded, bundled);

        // 入れてある: 動いている・止めている・問題がある
        if (scenario is "installed" or "updates")
        {
            Package(bundled, Samples[0] with { Version = "1.0.0" });
            Package(bundled, Samples[2]);
            Package(bundled, Samples[3] with { Version = "1.0.0" });
            WriteIndex(bundled, [Samples[0] with { Version = "1.0.0" }, Samples[2], Samples[3] with { Version = "1.0.0" }]);
            var catalog = Catalog(store, loaded, bundled);
            // (見本は UI のスレッドで作るので、非同期の InstallAsync を待つと止まる。同梱のファイルから同期で入れる)
            foreach (var entry in catalog.Index.ToList())
                store.InstallFromFile(entry.BundledFile!, entry.Sha256, PluginCatalog.TrustOf(entry), PluginSource.Bundled, entry.Id, entry.Version);
            store.ApplyPending();
            store.SetEnabled(Samples[2].Id, false);
            store.SetError(Samples[3].Id, "読み込めませんでした: FileNotFoundException: 必要なファイルがありません");
            foreach (var record in store.Records)
            {
                var manifest = ReadManifest(store, record);
                var item = new LoadedPlugin
                {
                    Record = record,
                    Manifest = manifest,
                    Loaded = record.Id == Samples[0].Id,
                    Error = record.LastError,
                };
                if (item.Loaded && manifest != null)
                {
                    // 拡張機能の設定 (GetText が自分の部品で描く) の見本
                    item.Context = new PluginContext(manifest, store.DataDirectory(record.Id), null);
                    item.Context.AddSettings(new PluginSettingsPage
                    {
                        Title = "キーワードの見張り",
                        Items =
                        [
                            new PluginSetting { Key = "words", Label = "見張る言葉", Description = "1 行に 1 つ", Kind = PluginSettingKind.MultilineText, Default = "締め切り\n至急" },
                            new PluginSetting { Key = "sound", Label = "音を鳴らす", Kind = PluginSettingKind.Toggle, Default = "true" },
                        ],
                    });
                }
                loaded.Add(item);
            }
            if (scenario == "updates")
            {
                // 新しい版が一覧に載った
                foreach (var f in Directory.GetFiles(bundled)) File.Delete(f);
                Package(bundled, Samples[0] with { Version = "1.2.0" });
                Package(bundled, Samples[3]);
                WriteIndex(bundled, [Samples[0] with { Version = "1.2.0" }, Samples[3]]);
            }
            return Catalog(store, loaded, bundled);
        }

        // 見つける: 公式・確認済みでない (インターネットを使う)・使えない
        foreach (var s in Samples) Package(bundled, s);
        WriteIndex(bundled, Samples);
        return Catalog(store, loaded, bundled);
    }

    private static PluginCatalog Catalog(PluginStore store, List<LoadedPlugin> loaded, string bundled)
    {
        var catalog = new PluginCatalog(store, () => loaded, bundled, safeMode: false);
        catalog.LoadLocal();
        return catalog;
    }

    private static PluginManifest? ReadManifest(PluginStore store, PluginRecord record)
    {
        var path = Path.Combine(store.VersionDirectory(record.Id, record.Version), "plugin.json");
        return File.Exists(path) ? PluginManifests.Parse(File.ReadAllText(path), out _) : null;
    }

    private static string Manifest(Sample s) => $$"""
        {
          "id": "{{s.Id}}", "name": {"ja": "{{s.Ja}}", "en": "{{s.Id}}"}, "version": "{{s.Version}}", "publisher": "{{s.Publisher}}",
          "description": "{{s.Description}}", "apiVersion": 1, "minHostVersion": "{{s.MinHost}}", "platforms": ["any"],
          "capabilities": [], "permissions": {{s.Permissions}}, "dependencies": [], "optionalDependencies": [],
          "entryPoint": { "assembly": "bin/Demo.dll", "type": "Demo.Plugin" }, "requiresRestart": true,
          "license": "GPL-3.0-or-later", "icon": "{{s.Icon}}"
        }
        """;

    private static void Package(string folder, Sample s)
    {
        var path = Path.Combine(folder, $"{s.Id}-{s.Version}.gtplugin");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open(), new UTF8Encoding(false))) w.Write(Manifest(s));
        // (0 で埋めると圧縮率が高すぎて、ZIP 爆弾の検査で正しく拒まれる。見本の中身は縮まない乱数にする)
        var bytes = new byte[48 * 1024];
        new Random(s.Id.GetHashCode()).NextBytes(bytes);
        using var dll = zip.CreateEntry("bin/Demo.dll").Open();
        dll.Write(bytes);
    }

    private static void WriteIndex(string folder, IEnumerable<Sample> samples)
    {
        var items = samples.Select(s =>
        {
            var file = $"{s.Id}-{s.Version}.gtplugin";
            var path = Path.Combine(folder, file);
            return $$"""
                { "id": "{{s.Id}}", "name": {"ja": "{{s.Ja}}"}, "publisher": "{{s.Publisher}}", "description": "{{s.Description}}",
                  "version": "{{s.Version}}", "file": "{{file}}", "sha256": "{{PluginPackages.Sha256(path)}}", "size": {{new FileInfo(path).Length}},
                  "installedSize": {{new FileInfo(path).Length * 3}}, "minHostVersion": "{{s.MinHost}}", "apiVersion": 1, "platforms": ["any"],
                  "permissions": {{s.Permissions}}, "verified": {{(s.Verified ? "true" : "false")}}, "icon": "{{s.Icon}}",
                  "local": {{(s.Permissions.Contains("network") ? "false" : "true")}}, "license": "GPL-3.0-or-later" }
                """;
        });
        File.WriteAllText(Path.Combine(folder, "plugins-index.json"), $$"""{ "schema": 1, "plugins": [{{string.Join(",", items)}}] }""");
    }
}
