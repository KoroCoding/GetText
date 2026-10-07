using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GetText.Plugins.History;

/// <summary>履歴の 1 件 (読み取った 1 回分)。</summary>
public sealed record HistoryEntry(
    [property: JsonPropertyName("t")] DateTimeOffset Time,
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("text")] string Text);

/// <summary>見つかった 1 件 (合った行と、その回の文字全体)。</summary>
public sealed record HistoryHit(HistoryEntry Entry, string Line);

/// <summary>
/// 読み取りの履歴 (この PC の拡張機能のフォルダに、日ごとの JSON Lines で保存する)。
/// 前と同じ文字は保存しない。除くアプリ・除く言葉を含む回は保存しない。保存期間を過ぎた日のファイルは消す。
/// 検索は全角・半角・大文字小文字・空白の違いを無視する。読み取った文字をログには書かない。
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _folder;
    private readonly object _lock = new();
    private List<HistoryEntry>? _entries;
    private string? _lastNormalized;
    private DateTimeOffset _lastSaved;

    public HistoryStore(string folder)
    {
        _folder = folder;
    }

    public string Folder => _folder;

    /// <summary>保存しておく日数 (この日数より前の日のファイルは消す)。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>保存しないアプリ (プロセス名。大文字小文字は無視)。</summary>
    public IReadOnlyList<string> ExcludedApps { get; set; } = [];

    /// <summary>この言葉を含む回は保存しない。</summary>
    public IReadOnlyList<string> ExcludedWords { get; set; } = [];

    /// <summary>続けて保存するときの最短の間隔 (読み取りは 1 秒ごとなので、短すぎる間隔では保存しない)。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int Count
    {
        get { lock (_lock) return Load().Count; }
    }

    /// <summary>もう読んであれば件数 (まだなら null。画面の表示のために、ファイルを読み込ませない)。</summary>
    public int? CountIfLoaded
    {
        get { lock (_lock) return _entries?.Count; }
    }

    public static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    public static IReadOnlyList<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>保存するか確かめて保存する。保存したら true (保存しなかった理由は返さない)。</summary>
    public bool Add(DateTimeOffset time, string? app, string text)
    {
        text = text.Trim();
        var normalized = Normalize(text);
        if (normalized.Length < 2) return false;
        if (app != null && ExcludedApps.Any(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase))) return false;
        if (ExcludedWords.Any(w => Normalize(w) is { Length: > 0 } nw && normalized.Contains(nw, StringComparison.Ordinal))) return false;
        lock (_lock)
        {
            var entries = Load();
            if (normalized == _lastNormalized) return false;
            if (time - _lastSaved < MinInterval) return false;
            var entry = new HistoryEntry(time, app, text);
            Directory.CreateDirectory(_folder);
            File.AppendAllText(DayFile(time), JsonSerializer.Serialize(entry, Json) + "\n", new UTF8Encoding(false));
            entries.Add(entry);
            _lastNormalized = normalized;
            _lastSaved = time;
            return true;
        }
    }

    /// <summary>新しい順に探す。query の言葉 (空白区切り) をすべて含む回。</summary>
    public List<HistoryHit> Search(string query, int max = 50)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return [];
        List<HistoryEntry> snapshot;
        lock (_lock) snapshot = [.. Load()];
        var hits = new List<HistoryHit>();
        for (int i = snapshot.Count - 1; i >= 0 && hits.Count < max; i--)
        {
            var e = snapshot[i];
            var n = Normalize(e.Text);
            if (!tokens.All(t => n.Contains(t, StringComparison.Ordinal))) continue;
            var line = e.Text.Split('\n').FirstOrDefault(l => Normalize(l).Contains(tokens[0], StringComparison.Ordinal)) ?? e.Text;
            hits.Add(new HistoryHit(e, line.Trim()));
        }
        return hits;
    }

    /// <summary>保存期間を過ぎた日のファイルを消す。</summary>
    public int Prune(DateTimeOffset now)
    {
        if (!Directory.Exists(_folder)) return 0;
        var oldest = now.LocalDateTime.Date.AddDays(-(Math.Max(1, RetentionDays) - 1));
        int removed = 0;
        foreach (var file in Directory.GetFiles(_folder, "*.jsonl"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day >= oldest) continue;
            File.Delete(file);
            removed++;
        }
        if (removed > 0)
            lock (_lock) _entries = null; // 読み直す
        return removed;
    }

    /// <summary>すべて消す。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (Directory.Exists(_folder))
                foreach (var file in Directory.GetFiles(_folder, "*.jsonl")) File.Delete(file);
            _entries = [];
            _lastNormalized = null;
            _lastSaved = default;
        }
    }

    private string DayFile(DateTimeOffset time) => Path.Combine(_folder, time.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    // 保存してある履歴を読む (初めてのときだけ。壊れた行は飛ばす)
    private List<HistoryEntry> Load()
    {
        if (_entries != null) return _entries;
        var list = new List<HistoryEntry>();
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.GetFiles(_folder, "*.jsonl").Order(StringComparer.Ordinal))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { Text: { } } e) list.Add(e);
                    }
                    catch (JsonException)
                    {
                        // 途中で切れた行 (書いている途中に終わったなど) は飛ばす
                    }
                }
            }
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        _entries = list;
        return list;
    }
}
