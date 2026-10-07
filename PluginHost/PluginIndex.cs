using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using GetText.Plugins;

namespace GetText;

/// <summary>索引の 1 件 (入れられる拡張機能)。</summary>
public sealed class PluginIndexEntry
{
    public required string Id { get; init; }
    public required LocalizedText Name { get; init; }
    public required string Publisher { get; init; }
    public required LocalizedText Description { get; init; }
    public required SemVersion Version { get; init; }
    /// <summary>ダウンロードする場所 (https)。同梱のパッケージなら null。</summary>
    public Uri? Url { get; init; }
    /// <summary>同梱のパッケージのファイル (GetText のフォルダの bundled-plugins)。</summary>
    public string? BundledFile { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public long InstalledSize { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = ["any"];
    public SemVersion MinHostVersion { get; init; } = new(1, 0, 0);
    public int ApiVersion { get; init; } = PluginApi.Version;
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Models { get; init; } = [];
    public string? License { get; init; }
    public bool Verified { get; init; }
    public PluginIcon Icon { get; init; } = "Extensions";
    /// <summary>PC の中だけで動くか (false ならインターネットを使う)。</summary>
    public bool Local { get; init; } = true;

    public bool IsOfficial => Id.StartsWith("gettext.", StringComparison.Ordinal) && Publisher == "KoroCoding";

    /// <summary>この GetText で使えるか (使えなければ理由)。</summary>
    public string? Incompatibility(SemVersion host, string platform)
    {
        if (ApiVersion != PluginApi.Version) return $"Plugin API v{ApiVersion} 用です (この GetText は v{PluginApi.Version})";
        if (host < MinHostVersion) return $"GetText {MinHostVersion} 以上が必要です";
        if (!Platforms.Contains("any") && !Platforms.Contains(platform)) return $"この環境 ({platform}) には対応していません";
        return null;
    }
}

/// <summary>
/// 拡張機能の索引 (plugins-index.json)。GitHub Releases などに置いたものを読む (専用のサーバーは無い)。
/// 取れなければ前に保存した索引を使う。同梱のパッケージ (bundled-plugins/plugins-index.json) はネットワークが無くても使える。
/// 読み込みは起動を止めない (Extensions の画面を開いたとき・起動の後に裏で)。
/// </summary>
public static class PluginIndex
{
    public const string DefaultUrl = "https://github.com/KoroCoding/GetText/releases/latest/download/plugins-index.json";

    /// <summary>索引の文字列を読む。壊れた項目は飛ばす (1 件のせいで全部を捨てない)。</summary>
    public static List<PluginIndexEntry> Parse(string json, string? bundledFolder = null)
    {
        var list = new List<PluginIndexEntry>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return list;
        }
        if (root?["plugins"] is not JsonArray items) return list;
        foreach (var item in items)
        {
            try
            {
                var id = item!["id"]!.GetValue<string>();
                if (!PluginManifests.IsValidId(id)) continue;
                if (!SemVersion.TryParse(item["version"]?.GetValue<string>(), out var version)) continue;
                var sha = item["sha256"]?.GetValue<string>() ?? "";
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) continue;
                Uri? url = null;
                if (item["url"]?.GetValue<string>() is { } u)
                {
                    if (!Uri.TryCreate(u, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps) continue;
                }
                string? file = null;
                if (item["file"]?.GetValue<string>() is { } f && bundledFolder != null)
                {
                    if (!PluginPackages.IsSafeRelativePath(f)) continue;
                    file = Path.Combine(bundledFolder, f);
                }
                if (url == null && file == null) continue;
                SemVersion.TryParse(item["minHostVersion"]?.GetValue<string>(), out var minHost);
                var permissions = Strings(item["permissions"]);
                list.Add(new PluginIndexEntry
                {
                    Id = id,
                    Name = PluginManifests.Text(item["name"]) ?? new LocalizedText(id),
                    Publisher = item["publisher"]?.GetValue<string>() ?? "",
                    Description = PluginManifests.Text(item["description"]) ?? new LocalizedText(""),
                    Version = version,
                    Url = url,
                    BundledFile = file,
                    Sha256 = sha.ToLowerInvariant(),
                    Size = item["size"]?.GetValue<long>() ?? 0,
                    InstalledSize = item["installedSize"]?.GetValue<long>() ?? 0,
                    Platforms = Strings(item["platforms"]) is { Count: > 0 } p ? p : ["any"],
                    MinHostVersion = minHost,
                    ApiVersion = item["apiVersion"]?.GetValue<int>() ?? PluginApi.Version,
                    Permissions = permissions,
                    Models = Strings(item["models"]),
                    License = item["license"]?.GetValue<string>(),
                    Verified = item["verified"]?.GetValue<bool>() ?? false,
                    Icon = new PluginIcon(item["icon"]?.GetValue<string>() ?? "Extensions"),
                    // 書いていなければ、インターネットを要求しているかで決める (書いてあっても network を要求していれば PC 内とは出さない)
                    Local = (item["local"]?.GetValue<bool>() ?? true) && !permissions.Contains("network"),
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
            {
                // 形の違う項目は飛ばす
            }
        }
        return list;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() : [];

    /// <summary>
    /// 索引を取ってくる (時間切れ 10 秒)。取れたら保存し、取れなければ保存してある索引を返す。
    /// fromCache は保存してある索引を使ったか、error は取れなかった理由。
    /// </summary>
    public static async Task<(List<PluginIndexEntry> Entries, bool FromCache, string? Error)> FetchAsync(HttpClient http, Uri url, string cachePath, CancellationToken ct)
    {
        string? error;
        try
        {
            if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("索引は https からだけ読みます");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidOperationException("索引が大きすぎます");
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > 4 * 1024 * 1024) throw new InvalidOperationException("索引が大きすぎます");
            var entries = Parse(json);
            try { AtomicFile.WriteAllText(cachePath, json); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (entries, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            error = ex is OperationCanceledException ? "時間切れ" : ex.Message;
        }
        try
        {
            return (File.Exists(cachePath) ? Parse(await File.ReadAllTextAsync(cachePath, ct)) : [], true, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], true, error);
        }
    }

    /// <summary>同梱のパッケージの索引 (GetText のフォルダの bundled-plugins/plugins-index.json)。</summary>
    public static List<PluginIndexEntry> ReadBundled(string folder)
    {
        var file = Path.Combine(folder, "plugins-index.json");
        try
        {
            return File.Exists(file) ? Parse(File.ReadAllText(file), folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>同じ id は新しい版を残す (同じ版なら同梱を優先: ネットワークが要らない)。</summary>
    public static List<PluginIndexEntry> Merge(IEnumerable<PluginIndexEntry> bundled, IEnumerable<PluginIndexEntry> online) =>
        bundled.Concat(online)
            .GroupBy(e => e.Id)
            .Select(g => g.OrderByDescending(e => e.Version).ThenBy(e => e.BundledFile == null ? 1 : 0).First())
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
}
