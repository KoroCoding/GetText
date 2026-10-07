using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GetText.Plugins;

namespace GetText;

/// <summary>版 (major.minor.patch[-pre])。比べられるようにする。</summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? Pre = null) : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new(@"^(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:-([0-9A-Za-z.\-]{1,40}))?$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0);
        if (text == null) return false;
        var m = Pattern.Match(text.Trim());
        if (!m.Success) return false;
        version = new SemVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    public static SemVersion From(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // 正式版 > 先行版
        if (Pre == null) return other.Pre == null ? 0 : 1;
        if (other.Pre == null) return -1;
        return string.CompareOrdinal(Pre, other.Pre);
    }

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre != null ? "-" + Pre : "");
}

/// <summary>依存の版の条件 (">=1.0.0"・"1.2.0" (ちょうど)・"*" (何でも))。</summary>
public sealed record VersionRange(SemVersion? Minimum, SemVersion? Exact)
{
    public static readonly VersionRange Any = new(null, null);

    public static bool TryParse(string? text, out VersionRange range)
    {
        range = Any;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*") return true;
        text = text.Trim();
        if (text.StartsWith(">=", StringComparison.Ordinal))
        {
            if (!SemVersion.TryParse(text[2..], out var min)) return false;
            range = new VersionRange(min, null);
            return true;
        }
        if (!SemVersion.TryParse(text, out var exact)) return false;
        range = new VersionRange(null, exact);
        return true;
    }

    public bool Allows(SemVersion v) => (Minimum == null || v >= Minimum) && (Exact == null || v.CompareTo(Exact) == 0);

    public override string ToString() => Exact != null ? Exact.ToString() : Minimum != null ? ">=" + Minimum : "*";
}

public sealed record PluginDependency(string Id, VersionRange Version);

/// <summary>拡張機能の入口 (読み込むアセンブリと型)。無ければ情報だけの拡張機能 (モデルの組など)。</summary>
public sealed record PluginEntryPoint(string Assembly, string Type);

/// <summary>拡張機能のマニフェスト (plugin.json)。docs/plugins/package-format.md。</summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required LocalizedText Name { get; init; }
    public required SemVersion Version { get; init; }
    public required string Publisher { get; init; }
    public required LocalizedText Description { get; init; }
    public required int ApiVersion { get; init; }
    public required SemVersion MinHostVersion { get; init; }
    /// <summary>win-x64・osx-arm64・osx-x64、または any。</summary>
    public required IReadOnlyList<string> Platforms { get; init; }
    /// <summary>この拡張機能が加える機能の名前 (ほかの拡張機能が HasCapability で調べる)。</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>使うと宣言したアクセス (OS が制限するものではない)。</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<PluginDependency> OptionalDependencies { get; init; } = [];
    public PluginEntryPoint? EntryPoint { get; init; }
    public bool RequiresRestart { get; init; } = true;
    /// <summary>使うモデルの id (モデルの管理で入れる。パッケージには入れない)。</summary>
    public IReadOnlyList<string> Models { get; init; } = [];
    public string? License { get; init; }
    public string? Homepage { get; init; }
    public PluginIcon Icon { get; init; } = "Extensions";
    /// <summary>知らない項目 (新しい版のマニフェスト)。読み込みは続け、診断に出す。</summary>
    public IReadOnlyList<string> UnknownFields { get; init; } = [];
}

/// <summary>マニフェストを読んで確かめる。間違いは利用者に見せる文で返す。</summary>
public static class PluginManifests
{
    public const string FileName = "plugin.json";

    /// <summary>許されるアクセスの名前と、利用者に見せる説明。</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownPermissions = new Dictionary<string, string>
    {
        ["screen-text"] = "読み取りの画面が読み取った文字を受け取る",
        ["screen-capture"] = "画面・ウィンドウを取り込む (OS の正式な方法だけ)",
        ["clipboard"] = "クリップボードに書き込む",
        ["files"] = "利用者が選んだファイル・フォルダを読み書きする",
        ["storage"] = "自分のフォルダにデータを保存する",
        ["notifications"] = "GetText の画面に知らせを出す",
        ["network"] = "インターネットにつなぐ",
    };

    public static readonly IReadOnlyList<string> KnownPlatforms = ["any", "win-x64", "osx-arm64", "osx-x64"];

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:[.-][a-z0-9]+){1,7}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownFields =
    [
        "id", "name", "version", "publisher", "description", "apiVersion", "minHostVersion", "platforms", "capabilities", "permissions",
        "dependencies", "optionalDependencies", "entryPoint", "requiresRestart", "models", "license", "homepage", "icon", "$schema",
    ];

    public static bool IsValidId(string? id) => id != null && id.Length <= 80 && IdPattern.IsMatch(id);

    /// <summary>JSON の文字列から読む。間違いがあれば null と理由 (最初の 1 つ)。</summary>
    public static PluginManifest? Parse(string json, out string? error)
    {
        error = null;
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                   ?? throw new JsonException("オブジェクトではありません");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            error = "plugin.json を読めません (JSON の書き方が正しくありません): " + ex.Message;
            return null;
        }
        try
        {
            string Req(string key) => root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s.Trim() : throw new FormatException($"「{key}」がありません");

            var id = Req("id");
            if (!IsValidId(id)) throw new FormatException($"id「{id}」は使えません (小文字・数字を . か - でつなぐ。例: gettext.keyword-monitor)");
            if (!SemVersion.TryParse(Req("version"), out var version)) throw new FormatException("version は 1.2.3 の形で書いてください");
            if (root["apiVersion"] is not JsonValue av || !av.TryGetValue<int>(out int api)) throw new FormatException("apiVersion (数) がありません");
            if (!SemVersion.TryParse(Req("minHostVersion"), out var minHost)) throw new FormatException("minHostVersion は 1.2.3 の形で書いてください");
            var platforms = Strings(root["platforms"]);
            if (platforms.Count == 0) throw new FormatException("platforms がありません");
            foreach (var p in platforms)
                if (!KnownPlatforms.Contains(p)) throw new FormatException($"platforms の「{p}」は知らない環境です");
            var permissions = Strings(root["permissions"]);
            foreach (var p in permissions)
                if (!KnownPermissions.ContainsKey(p)) throw new FormatException($"permissions の「{p}」は知らないアクセスです");
            PluginEntryPoint? entry = null;
            if (root["entryPoint"] is JsonObject e)
            {
                string assembly = e["assembly"]?.GetValue<string>() ?? throw new FormatException("entryPoint.assembly がありません");
                string type = e["type"]?.GetValue<string>() ?? throw new FormatException("entryPoint.type がありません");
                if (!PluginPackages.IsSafeRelativePath(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("entryPoint.assembly はパッケージの中の .dll の相対パスにしてください");
                entry = new PluginEntryPoint(assembly.Replace('\\', '/'), type);
            }
            var icon = root["icon"] is JsonValue iv && iv.TryGetValue<string>(out var iconText) ? new PluginIcon(iconText) : new PluginIcon("Extensions");
            return new PluginManifest
            {
                Id = id,
                Name = Text(root["name"]) ?? throw new FormatException("「name」がありません"),
                Version = version,
                Publisher = Req("publisher"),
                Description = Text(root["description"]) ?? throw new FormatException("「description」がありません"),
                ApiVersion = api,
                MinHostVersion = minHost,
                Platforms = platforms,
                Capabilities = Strings(root["capabilities"]),
                Permissions = permissions,
                Dependencies = Dependencies(root["dependencies"]),
                OptionalDependencies = Dependencies(root["optionalDependencies"]),
                EntryPoint = entry,
                RequiresRestart = root["requiresRestart"] is not JsonValue rv || !rv.TryGetValue<bool>(out bool r) || r,
                Models = Strings(root["models"]),
                License = root["license"]?.GetValue<string>(),
                Homepage = root["homepage"]?.GetValue<string>(),
                Icon = icon,
                UnknownFields = root.Select(kv => kv.Key).Where(k => !KnownFields.Contains(k)).ToList(),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            error = "plugin.json の内容が正しくありません: " + ex.Message;
            return null;
        }
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(s => s.Length > 0).Distinct().ToList() : [];

    private static List<PluginDependency> Dependencies(JsonNode? node)
    {
        var list = new List<PluginDependency>();
        if (node is not JsonArray a) return list;
        foreach (var item in a)
        {
            string id = item?["id"]?.GetValue<string>() ?? throw new FormatException("依存に id がありません");
            if (!IsValidId(id)) throw new FormatException($"依存の id「{id}」は使えません");
            if (!VersionRange.TryParse(item["version"]?.GetValue<string>(), out var range)) throw new FormatException($"依存「{id}」の版の条件が読めません");
            list.Add(new PluginDependency(id, range));
        }
        return list;
    }

    /// <summary>文字列、または {"ja": "...", "en": "..."}。</summary>
    internal static LocalizedText? Text(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return new LocalizedText(s.Trim());
        if (node is JsonObject o)
        {
            var map = o.Where(kv => kv.Value is JsonValue jv && jv.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t))
                .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>().Trim());
            return map.Count > 0 ? new LocalizedText(map) : null;
        }
        return null;
    }
}

/// <summary>この GetText で使えるか (Plugin API の版・GetText の版・環境)。</summary>
public static class PluginCompatibility
{
    /// <summary>今の環境の名前 (win-x64・osx-arm64・osx-x64)。</summary>
    public static string CurrentPlatform =>
        (OperatingSystem.IsMacOS() ? "osx-" : OperatingSystem.IsWindows() ? "win-" : "linux-")
        + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static SemVersion HostVersion =>
        SemVersion.From(typeof(PluginCompatibility).Assembly.GetName().Version ?? new Version(1, 0, 0));

    /// <summary>使えなければ、その理由 (利用者に見せる文)。使えれば null。</summary>
    public static string? Check(PluginManifest m, SemVersion host, string platform, int apiVersion = PluginApi.Version)
    {
        if (m.ApiVersion != apiVersion)
            return m.ApiVersion > apiVersion
                ? $"この拡張機能は新しい Plugin API (v{m.ApiVersion}) 用です。GetText を新しくしてください"
                : $"この拡張機能は古い Plugin API (v{m.ApiVersion}) 用で、この GetText (v{apiVersion}) では使えません";
        if (host < m.MinHostVersion) return $"この拡張機能は GetText {m.MinHostVersion} 以上が必要です (今は {host})";
        if (!m.Platforms.Contains("any") && !m.Platforms.Contains(platform))
            return $"この拡張機能は {string.Join("・", m.Platforms)} 用で、この環境 ({platform}) では使えません";
        return null;
    }
}

/// <summary>依存関係を調べて、読み込む順を決める。</summary>
public static class PluginDependencies
{
    public sealed record Resolution(IReadOnlyList<PluginManifest> Order, IReadOnlyDictionary<string, string> Problems);

    /// <summary>
    /// 読み込む順 (依存されるものが先) を決める。足りない依存・版が合わない依存・循環がある拡張機能と、
    /// それに依存する拡張機能は Problems に理由を入れて除く (GetText 本体は起動する)。
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<PluginManifest> plugins)
    {
        var problems = new Dictionary<string, string>();
        var byId = new Dictionary<string, PluginManifest>();
        foreach (var p in plugins)
        {
            if (byId.ContainsKey(p.Id)) problems[p.Id] = "同じ id の拡張機能が 2 つあります";
            else byId[p.Id] = p;
        }
        foreach (var id in problems.Keys) byId.Remove(id);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in byId.Values.ToList())
            {
                foreach (var d in p.Dependencies)
                {
                    string? why = !byId.TryGetValue(d.Id, out var dep)
                        ? problems.ContainsKey(d.Id) ? $"依存する「{d.Id}」が使えません" : $"必要な拡張機能「{d.Id}」が入っていません"
                        : !d.Version.Allows(dep.Version) ? $"必要な拡張機能「{d.Id}」の版 ({dep.Version}) が条件 ({d.Version}) に合いません" : null;
                    if (why == null) continue;
                    problems[p.Id] = why;
                    byId.Remove(p.Id);
                    changed = true;
                    break;
                }
            }
        }

        // 依存されるものが先になるように並べる (深さ優先。循環は除く)
        var order = new List<PluginManifest>();
        var state = new Dictionary<string, int>(); // 1 = 調べている途中、2 = 済み
        var cyclic = new HashSet<string>();
        bool Visit(PluginManifest p, Stack<string> path)
        {
            if (state.TryGetValue(p.Id, out int s)) return s == 2 || MarkCycle(p.Id, path);
            state[p.Id] = 1;
            path.Push(p.Id);
            bool ok = true;
            foreach (var d in p.Dependencies.Concat(p.OptionalDependencies))
                if (byId.TryGetValue(d.Id, out var dep) && !Visit(dep, path) && p.Dependencies.Contains(d)) ok = false;
            path.Pop();
            state[p.Id] = 2;
            if (!ok || cyclic.Contains(p.Id))
            {
                problems.TryAdd(p.Id, "拡張機能どうしの依存が循環しています");
                return false;
            }
            order.Add(p);
            return true;
        }
        bool MarkCycle(string id, Stack<string> path)
        {
            foreach (var x in path)
            {
                cyclic.Add(x);
                if (x == id) break;
            }
            return false;
        }
        foreach (var p in byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal)) Visit(p, new Stack<string>());
        // 循環の一部と分かった拡張機能は、順番に入っていても除く
        order.RemoveAll(p => cyclic.Contains(p.Id));
        foreach (var id in cyclic) problems.TryAdd(id, "拡張機能どうしの依存が循環しています");
        return new Resolution(order, problems);
    }
}
