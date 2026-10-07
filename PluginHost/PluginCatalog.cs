using System.IO;
using System.Net.Http;
using GetText.Plugins;

namespace GetText;

/// <summary>拡張機能の一覧の 1 行の状態 (画面の印の文字と色を決める)。</summary>
public enum PluginItemState
{
    /// <summary>入れてあり、動いている。</summary>
    Running,
    /// <summary>入れてあるが止めている。</summary>
    Disabled,
    /// <summary>読み込めなかった (理由は Error)。</summary>
    Error,
    /// <summary>この GetText・この環境では使えない。</summary>
    Incompatible,
    /// <summary>Safe Mode なので読み込んでいない。</summary>
    SafeMode,
    /// <summary>入れた・有効にした・止めた・更新した・消した: 再起動で反映する。</summary>
    RestartRequired,
    /// <summary>まだ入れていない (入れられる)。</summary>
    Available,
}

/// <summary>拡張機能の一覧の 1 行 (入れたもの・入れられるものをまとめたもの。Windows 版・Mac 版で同じものを描く)。</summary>
public sealed class PluginItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    /// <summary>入れられる新しい版 (無ければ null)。</summary>
    public string? UpdateVersion { get; init; }
    public required PluginTrust Trust { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Models { get; init; } = [];
    public string? License { get; init; }
    public bool Local { get; init; } = true;
    public long Size { get; init; }
    public long InstalledSize { get; init; }
    public PluginIcon Icon { get; init; } = "Extensions";
    public required PluginItemState State { get; init; }
    /// <summary>状態の説明 (印の横・詳細に出す)。</summary>
    public string? Detail { get; init; }
    public bool Installed { get; init; }
    public bool Enabled { get; init; }
    public bool PendingRemoval { get; init; }
    public PluginIndexEntry? Entry { get; init; }
    public LoadedPlugin? Loaded { get; init; }

    public bool HasUpdate => UpdateVersion != null;

    public string TrustLabel => PluginCatalog.TrustLabel(Trust);

    public string StateLabel => State switch
    {
        PluginItemState.Running => "動いています",
        PluginItemState.Disabled => "止めています",
        PluginItemState.Error => "問題があります",
        PluginItemState.Incompatible => "使えません",
        PluginItemState.SafeMode => "Safe Mode",
        PluginItemState.RestartRequired => "再起動で反映",
        _ => HasUpdate ? "更新があります" : "",
    };

    public string SizeLabel => PluginCatalog.FormatSize(Installed ? InstalledSize : Math.Max(Size, InstalledSize));

    /// <summary>検索に使う文字 (名前・説明・発行元・id・要求されるアクセス)。</summary>
    public string SearchText => string.Join(" ", new[] { Name, Description, Publisher, Id }.Concat(Permissions.Select(p => PluginPermissions.Label(p))));
}

/// <summary>要求されるアクセスの 1 つ (画面の小さな札)。</summary>
public sealed record PermissionChip(string Label, string Description);

/// <summary>拡張機能が要求するアクセス (マニフェストの permissions) の、画面に出す名前と説明。</summary>
public static class PluginPermissions
{
    public static string Label(string permission) => permission switch
    {
        "screen-text" => "読み取った画面の文字",
        "screen-capture" => "画面の画像",
        "clipboard" => "クリップボード",
        "files" => "選んだファイル",
        "storage" => "自分のデータの保存",
        "notifications" => "知らせ",
        "network" => "インターネット",
        _ => permission,
    };

    public static string Description(string permission) => permission switch
    {
        "screen-text" => "読み取りの画面が読んだ文字を受け取ります。",
        "screen-capture" => "画面・窓の画像を撮ります。",
        "clipboard" => "クリップボードに文字を入れます。",
        "files" => "利用者が選んだファイル・フォルダを読み書きします。",
        "storage" => "設定や記録を、この PC の拡張機能のフォルダに保存します。",
        "notifications" => "GetText の画面の右下に知らせを出します。",
        "network" => "インターネットに接続します。何を送るかは拡張機能の説明を確認してください。",
        _ => "",
    };

    /// <summary>プライバシーのページの行: 動いている拡張機能が申告しているインターネットの利用と、一覧の読み込み。</summary>
    public static (string Text, bool Online) Privacy(IReadOnlyList<LoadedPlugin> plugins)
    {
        var running = plugins.Where(p => p.Loaded && p.Manifest != null).ToList();
        var online = running.Where(p => p.Manifest!.Permissions.Contains("network")).Select(p => p.Manifest!.Name.For("ja")).ToList();
        string list = "拡張機能の一覧は「オンラインで探す」「更新を確認」を押したときだけ GitHub から読み込みます。";
        if (running.Count == 0) return ("入れた拡張機能は動いていません。" + list, false);
        if (online.Count == 0) return ($"動いている拡張機能 {running.Count} 個は、インターネットを使うと申告していません。" + list, false);
        return ($"インターネットを使うと申告している拡張機能: {string.Join("・", online)}。" + list, true);
    }

    /// <summary>拡張機能は GetText と同じ権限で動く。申告されたものを見せるだけで、制限ではない (画面にもそう書く)。</summary>
    public const string Notice = "拡張機能は GetText と同じ権限で動きます。ここに出すのは、拡張機能が使うと申告しているもので、GetText が制限するものではありません。信頼できる発行元のものだけを入れてください。";
}

/// <summary>
/// 拡張機能の一覧 (入れたもの・見つける・更新)。入れてある記録・読み込みの結果・索引 (同梱・保存したもの・オンライン) をまとめる。
/// オンラインの索引は、利用者が「オンラインで探す」「更新を確認」を押したときだけ取りに行く (既定で通信しない)。
/// 入れる・更新・有効/無効・消すは、GetText を再起動すると反映する。
/// </summary>
public sealed class PluginCatalog(PluginStore store, Func<IReadOnlyList<LoadedPlugin>> loaded, string bundledFolder, bool safeMode)
{
    private List<PluginIndexEntry> _bundled = [];
    private List<PluginIndexEntry> _online = [];

    public PluginStore Store { get; } = store;

    /// <summary>今使える索引 (同梱 + 保存してあるオンラインの索引)。</summary>
    public IReadOnlyList<PluginIndexEntry> Index { get; private set; } = [];

    /// <summary>このセッションでオンラインの索引を取りに行ったか。</summary>
    public bool CheckedOnline { get; private set; }

    /// <summary>オンラインの索引を取れなかった理由 (保存してある索引を使っている)。</summary>
    public string? OnlineError { get; private set; }

    public DateTime? OnlineCheckedAt { get; private set; }

    public string CachePath => Path.Combine(Store.Root, ".index-cache.json");

    /// <summary>同梱の拡張機能の場所。Windows は exe と同じフォルダー、Mac のアプリ (GetText.app) では Contents/Resources/bundled-plugins。</summary>
    public static string DefaultBundledFolder
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "bundled-plugins");
            var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bundled-plugins"));
            return Directory.Exists(beside) || !Directory.Exists(resources) ? beside : resources;
        }
    }

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GetText/{PluginCompatibility.HostVersion}");
        return http;
    });

    /// <summary>一覧・パッケージを取りに行く (利用者が押したときだけ使う)。</summary>
    public static HttpClient Http => SharedHttp.Value;

    /// <summary>設定の画面が使う一覧 (見本の画面では、利用者の拡張機能のフォルダを読まない)。</summary>
    public static PluginCatalog ForApp(bool demo)
    {
        var store = PluginRuntime.Store ?? new PluginStore(demo
            ? Path.Combine(Path.GetTempPath(), $"gettext-demo-plugins-{Environment.ProcessId}")
            : PluginStore.DefaultRoot);
        var catalog = new PluginCatalog(store, () => PluginRuntime.Plugins, DefaultBundledFolder, PluginRuntime.SafeMode);
        catalog.LoadLocal();
        return catalog;
    }

    /// <summary>入れる・一覧を読むときに起こりうる問題 (画面に理由を出して続ける。それ以外は不具合として上に投げる)。</summary>
    public static bool IsExpectedFailure(Exception ex) =>
        ex is PluginPackageException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
            or InvalidOperationException or InvalidDataException;

    /// <summary>「診断をコピー」の文 (読み取った文字・キー・ユーザー名は入れない)。</summary>
    public string? Diagnostics(PluginItem item)
    {
        var loaded = item.Loaded ?? (Store.Find(item.Id) is { } record ? new LoadedPlugin { Record = record, Error = record.LastError } : null);
        return loaded != null ? PluginRuntime.Diagnostics(loaded) : null;
    }

    /// <summary>同梱と保存してある索引を読む (通信しない)。</summary>
    public void LoadLocal()
    {
        try
        {
            Store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Write("error", null, "拡張機能の記録を読めませんでした: " + ex.Message);
        }
        _bundled = PluginIndex.ReadBundled(bundledFolder);
        try
        {
            _online = File.Exists(CachePath) ? PluginIndex.Parse(File.ReadAllText(CachePath)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _online = [];
        }
        Index = PluginIndex.Merge(_bundled, _online);
    }

    /// <summary>オンラインの索引を取りに行く (利用者が押したときだけ)。取れなければ保存してあるものを使い、理由を OnlineError に入れる。</summary>
    public async Task RefreshOnlineAsync(HttpClient http, CancellationToken ct, Uri? url = null)
    {
        var (entries, fromCache, error) = await PluginIndex.FetchAsync(http, url ?? new Uri(PluginIndex.DefaultUrl), CachePath, ct);
        _online = entries;
        CheckedOnline = true;
        OnlineError = fromCache ? error ?? "取れませんでした" : null;
        OnlineCheckedAt = DateTime.Now;
        Index = PluginIndex.Merge(_bundled, _online);
    }

    /// <summary>入れたもの (消す予定のものも含む)。</summary>
    public List<PluginItem> Installed()
    {
        var running = loaded();
        var items = new List<PluginItem>();
        foreach (var record in Store.Records)
        {
            var l = running.FirstOrDefault(p => p.Record.Id == record.Id);
            PluginManifest? manifest = l?.Manifest ?? ReadManifest(record);
            var entry = Index.FirstOrDefault(e => e.Id == record.Id);
            SemVersion.TryParse(record.PendingVersion ?? record.Version, out var current);
            bool update = entry != null && entry.Version > current && entry.Incompatibility(PluginCompatibility.HostVersion, PluginCompatibility.CurrentPlatform) == null;
            string? incompatible = manifest != null ? PluginCompatibility.Check(manifest, PluginCompatibility.HostVersion, PluginCompatibility.CurrentPlatform) : null;

            var (state, detail) = StateOf(record, l, incompatible);
            items.Add(new PluginItem
            {
                Id = record.Id,
                Name = manifest?.Name.For("ja") ?? entry?.Name.For("ja") ?? record.Id,
                Publisher = manifest?.Publisher ?? entry?.Publisher ?? "",
                Description = manifest?.Description.For("ja") ?? entry?.Description.For("ja") ?? "",
                Version = record.PendingVersion ?? record.Version,
                UpdateVersion = update ? entry!.Version.ToString() : null,
                Trust = record.Trust,
                Permissions = manifest?.Permissions ?? entry?.Permissions ?? [],
                Models = manifest?.Models ?? entry?.Models ?? [],
                License = manifest?.License ?? entry?.License,
                Local = !(manifest?.Permissions ?? entry?.Permissions ?? []).Contains("network"),
                InstalledSize = DirectorySize(Store.VersionDirectory(record.Id, record.Version)),
                Icon = manifest?.Icon ?? entry?.Icon ?? "Extensions",
                State = state,
                Detail = detail,
                Installed = true,
                Enabled = record.Enabled,
                PendingRemoval = record.PendingRemoval,
                Entry = entry,
                Loaded = l,
            });
        }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>まだ入れていないもの (使えないものも、理由を付けて出す)。</summary>
    public List<PluginItem> Available()
    {
        var installed = Store.Records.Select(r => r.Id).ToHashSet();
        return Index.Where(e => !installed.Contains(e.Id))
            .Select(e =>
            {
                var why = e.Incompatibility(PluginCompatibility.HostVersion, PluginCompatibility.CurrentPlatform);
                return new PluginItem
                {
                    Id = e.Id,
                    Name = e.Name.For("ja"),
                    Publisher = e.Publisher,
                    Description = e.Description.For("ja"),
                    Version = e.Version.ToString(),
                    Trust = TrustOf(e),
                    Permissions = e.Permissions,
                    Models = e.Models,
                    License = e.License,
                    Local = e.Local,
                    Size = e.Size,
                    InstalledSize = e.InstalledSize,
                    Icon = e.Icon,
                    State = why != null ? PluginItemState.Incompatible : PluginItemState.Available,
                    Detail = why,
                    Entry = e,
                };
            })
            .OrderBy(i => i.State == PluginItemState.Incompatible ? 1 : 0)
            .ThenBy(i => i.Trust)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public List<PluginItem> Updates() => Installed().Where(i => i.HasUpdate && !i.PendingRemoval).ToList();

    /// <summary>名前・説明・発行元・要求されるアクセスで絞る (空なら全部)。</summary>
    public static List<PluginItem> Filter(IEnumerable<PluginItem> items, string query)
    {
        var tokens = CommandSearch.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items.Where(i =>
        {
            var hay = CommandSearch.Normalize(i.SearchText);
            return tokens.All(t => hay.Contains(t, StringComparison.Ordinal));
        }).ToList();
    }

    /// <summary>再起動すると変わるものがあるか (画面の上の帯で知らせる)。</summary>
    public bool RestartRequired => Installed().Any(i => i.State == PluginItemState.RestartRequired);

    // ───────── 操作 ─────────

    /// <summary>索引の拡張機能を入れる・更新する (同梱ならファイルから、無ければダウンロード)。再起動で反映する。</summary>
    public async Task<PluginManifest> InstallAsync(PluginIndexEntry entry, HttpClient? http, IProgress<double>? progress, CancellationToken ct)
    {
        if (entry.Incompatibility(PluginCompatibility.HostVersion, PluginCompatibility.CurrentPlatform) is { } why) throw new PluginPackageException(why);
        var trust = TrustOf(entry);
        PluginManifest manifest;
        if (entry.BundledFile is { } file && File.Exists(file))
        {
            manifest = await Task.Run(() => Store.InstallFromFile(file, entry.Sha256, trust, PluginSource.Bundled, entry.Id, entry.Version), ct);
        }
        else if (entry.Url is { } url && http != null)
        {
            manifest = await Store.InstallFromUrlAsync(http, url, entry.Sha256, entry.Size, trust, entry.Id, entry.Version, progress, ct);
        }
        else
        {
            throw new PluginPackageException("この拡張機能のパッケージが見つかりません (オンラインの一覧を読み直してください)");
        }
        PluginLog.Write("info", entry.Id, $"{entry.Version} を入れました (次の起動で反映・{trust})");
        return manifest;
    }

    /// <summary>利用者が選んだ .gtplugin を入れる (コミュニティの扱い)。</summary>
    public Task<PluginManifest> InstallFileAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var manifest = Store.InstallFromFile(path, null, PluginTrust.Community, PluginSource.LocalFile);
        PluginLog.Write("info", manifest.Id, $"{manifest.Version} をファイルから入れました (次の起動で反映)");
        return manifest;
    }, ct);

    public void SetEnabled(string id, bool enabled)
    {
        Store.SetEnabled(id, enabled);
        PluginLog.Write("info", id, enabled ? "有効にしました (次の起動で反映)" : "止めました (次の起動で反映)");
    }

    public void Uninstall(string id)
    {
        Store.Uninstall(id);
        PluginLog.Write("info", id, "消す予定にしました (次の起動で反映)");
    }

    public void CancelUninstall(string id)
    {
        Store.CancelUninstall(id);
        PluginLog.Write("info", id, "消すのを取り消しました");
    }

    // ───────── 補助 ─────────

    /// <summary>索引の項目の信頼 (公式は GetText の開発元の id と発行元のもの。確かめたものは索引の verified)。</summary>
    public static PluginTrust TrustOf(PluginIndexEntry e) => e.IsOfficial ? PluginTrust.Official : e.Verified ? PluginTrust.Verified : PluginTrust.Community;

    public static string TrustLabel(PluginTrust trust) => trust switch
    {
        PluginTrust.Official => "公式",
        PluginTrust.Verified => "確認済み",
        _ => "コミュニティ",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };

    private (PluginItemState, string?) StateOf(PluginRecord record, LoadedPlugin? l, string? incompatible)
    {
        if (record.PendingRemoval) return (PluginItemState.RestartRequired, "再起動すると消えます");
        if (record.PendingVersion != null)
            return (PluginItemState.RestartRequired,
                SemVersion.TryParse(record.PendingVersion, out var next) && SemVersion.TryParse(record.Version, out var now) && next < now
                    ? $"再起動すると前の版 {record.PendingVersion} に戻します"
                    : $"再起動すると {record.PendingVersion} に更新します");
        if (incompatible != null) return (PluginItemState.Incompatible, incompatible);
        if (l == null) return record.Enabled ? (PluginItemState.RestartRequired, "再起動すると使えます") : (PluginItemState.Disabled, null);
        if (safeMode) return (PluginItemState.SafeMode, "Safe Mode なので読み込んでいません");
        if (l.Loaded && !record.Enabled) return (PluginItemState.RestartRequired, "再起動すると止まります");
        if (!l.Loaded && record.Enabled && l.Error == null && record.LastError == null) return (PluginItemState.RestartRequired, "再起動すると使えます");
        if (!record.Enabled) return (PluginItemState.Disabled, record.LastError);
        if (!l.Loaded) return (PluginItemState.Error, l.Error ?? record.LastError);
        return (PluginItemState.Running, null);
    }

    private PluginManifest? ReadManifest(PluginRecord record)
    {
        try
        {
            var path = Path.Combine(Store.VersionDirectory(record.Id, record.PendingVersion ?? record.Version), "plugin.json");
            return File.Exists(path) ? PluginManifests.Parse(File.ReadAllText(path), out _) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>拡張機能のページの文 (Windows 版・Mac 版で同じものを出す)。</summary>
public static class PluginTexts
{
    public const string FileInstallWarning = "ファイルから入れた拡張機能は「コミュニティ」の扱いになり、GetText の開発元は確かめていません。";

    /// <summary>一覧が空のときの見出しと説明。</summary>
    public static (string Title, string Detail) Empty(string tab, PluginCatalog catalog, bool searching, int unfiltered) =>
        searching && unfiltered > 0
            ? ("見つかりませんでした", "別の言葉で探してください。")
            : tab switch
            {
                "Discover" when catalog.Index.Count > 0 => ("入れられるものはすべて入れてあります", "「入れたもの」で、止める・消すを選べます。"),
                "Discover" => ("入れられる拡張機能はまだありません", "「オンラインで探す」を押すと、GitHub の拡張機能の一覧を読み込みます (押したときだけ接続します)。"),
                "Updates" => ("更新はありません", catalog.CheckedOnline ? "入れた拡張機能はすべて新しい版です。" : "「更新を確認」を押すと、オンラインの一覧と比べます (押したときだけ接続します)。"),
                _ => ("まだ拡張機能を入れていません", "GetText は拡張機能が無くても使えます。必要な機能だけを「見つける」から入れられます。"),
            };

    /// <summary>一覧の出どころ (同梱・オンラインを確かめたか)。</summary>
    public static string Source(PluginCatalog catalog, bool safeMode)
    {
        int bundled = catalog.Index.Count(e => e.BundledFile != null);
        string online = catalog.CheckedOnline
            ? catalog.OnlineError != null
                ? "オンラインの一覧は取れませんでした (前に保存した一覧を使っています)"
                : $"オンラインの一覧: {catalog.OnlineCheckedAt:HH:mm} に確認"
            : "オンラインの一覧はまだ確認していません (押したときだけ接続します)";
        return $"{(safeMode ? "Safe Mode で起動しています (拡張機能を読み込んでいません) ・ " : "")}同梱 {bundled} 件 ・ {online}";
    }

    public static string OnlineError(string error) => $"オンラインの一覧を取れませんでした ({error})。前に保存した一覧を出しています。";

    /// <summary>入れる前に見せる文 (要求されるアクセスと信頼)。</summary>
    public static string ConfirmInstall(PluginItem item)
    {
        var access = item.Permissions.Count == 0 ? "なし"
            : string.Join("\n", item.Permissions.Select(p => $"・{PluginPermissions.Label(p)}: {PluginPermissions.Description(p)}"));
        return $"「{item.Name}」({item.Publisher} ・ {item.TrustLabel}) を入れます。\n\n要求されるアクセス:\n{access}\n\n"
               + (item.Local ? "" : "この拡張機能はインターネットに接続します。\n\n")
               + PluginPermissions.Notice + "\n\n再起動すると使えるようになります。入れますか？";
    }

    public static string ConfirmRemove(PluginItem item) => $"「{item.Name}」を消しますか？\n\n再起動すると消えます。拡張機能の設定・記録も消えます。";

    public static string Meta(PluginItem item) => string.Join(" ・ ", new[]
    {
        item.Publisher,
        item.HasUpdate ? $"{item.Version} → {item.UpdateVersion}" : item.Version,
        item.SizeLabel,
        item.Local ? "この PC の中" : "オンライン",
    }.Where(s => !string.IsNullOrEmpty(s)));

    public static IReadOnlyList<PermissionChip> Chips(PluginItem item) =>
        item.Permissions.Select(p => new PermissionChip(PluginPermissions.Label(p), PluginPermissions.Description(p))).ToList();

    public static string Details(PluginItem item)
    {
        var lines = new List<string> { $"ID: {item.Id}" };
        if (item.License != null) lines.Add($"ライセンス: {item.License}");
        if (item.Models.Count > 0)
            lines.Add("使うモデル: " + string.Join(", ", item.Models.Select(id =>
                ModelCatalog.Find(id) is { } m ? $"{m.Name} ({ModelCatalog.Status(m).StateLabel})" : id)));
        lines.AddRange(Chips(item).Select(p => $"・{p.Label}: {p.Description}"));
        lines.Add(item.Trust switch
        {
            PluginTrust.Official => "公式: GetText の開発元が作り、配布の一覧の SHA-256 と一致したものです。",
            PluginTrust.Verified => "確認済み: 拡張機能の一覧で確認済みとされたものです。",
            _ => "コミュニティ: GetText の開発元は確かめていません。",
        });
        return string.Join("\n", lines);
    }

    public static string PrimaryLabel(PluginItem item) => item.Installed ? $"{item.UpdateVersion} に更新" : "入れる";

    public static bool HasPrimary(PluginItem item) =>
        item.Entry != null && (item.Installed ? item.HasUpdate && !item.PendingRemoval : item.State == PluginItemState.Available);

    public static string ToggleLabel(PluginItem item) => item.Enabled ? "止める" : "有効にする";

    public static string RemoveLabel(PluginItem item) => item.PendingRemoval ? "消すのを取り消す" : "消す";

    public static AppIcon Icon(PluginItem item) =>
        Enum.TryParse<AppIcon>(item.Icon.Name, true, out var icon) && icon != AppIcon.None ? icon : AppIcon.Extensions;

    public static AppIcon TrustIcon(PluginItem item) => item.Trust == PluginTrust.Community ? AppIcon.Info : AppIcon.Success;

    public static AppIcon StateIcon(PluginItem item) => item.State switch
    {
        PluginItemState.Running => AppIcon.Success,
        PluginItemState.Disabled => AppIcon.Pause,
        PluginItemState.Error => AppIcon.Error,
        PluginItemState.Incompatible or PluginItemState.SafeMode => AppIcon.Warning,
        PluginItemState.RestartRequired => AppIcon.Refresh,
        _ => AppIcon.Download,
    };

    public static uint TrustBackground(PluginItem item, Palette p) => item.Trust switch { PluginTrust.Official => p.AccentSubtle, PluginTrust.Verified => p.SuccessSubtle, _ => p.SubtleHover };
    public static uint TrustForeground(PluginItem item, Palette p) => item.Trust switch { PluginTrust.Official => p.AccentText, PluginTrust.Verified => p.Success, _ => p.TextSecondary };

    public static uint StateBackground(PluginItem item, Palette p) => item.State switch
    {
        PluginItemState.Running => p.SuccessSubtle,
        PluginItemState.Error => p.CriticalSubtle,
        PluginItemState.Incompatible or PluginItemState.SafeMode => p.WarningSubtle,
        PluginItemState.Disabled => p.SubtleHover,
        _ => p.AccentSubtle,
    };

    public static uint StateForeground(PluginItem item, Palette p) => item.State switch
    {
        PluginItemState.Running => p.Success,
        PluginItemState.Error => p.Critical,
        PluginItemState.Incompatible or PluginItemState.SafeMode => p.Warning,
        PluginItemState.Disabled => p.TextSecondary,
        _ => p.AccentText,
    };

    public static uint DetailForeground(PluginItem item, Palette p) => item.State switch
    {
        PluginItemState.Error => p.Critical,
        PluginItemState.Incompatible or PluginItemState.SafeMode => p.Warning,
        _ => p.TextSecondary,
    };

    public static string AccessibleName(PluginItem item) => $"{item.Name}、{item.TrustLabel}{(item.StateLabel.Length > 0 ? "、" + item.StateLabel : "")}";
}