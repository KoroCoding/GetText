using System.Globalization;
using System.Text;

namespace GetText.Plugins.KeywordMonitor;

/// <summary>見張る言葉の 1 つが見つかった。Line はその言葉を含む行 (画面に出すだけ。保存・ログはしない)。</summary>
public sealed record KeywordHit(string Keyword, string Line);

/// <summary>
/// 読み取った文字から、決めた言葉を探す。
/// 全角・半角 (NFKC)・大文字・小文字・行の中の空白の違いは無視する (OCR は「締め 切り」のように空白を入れることがある)。
/// 同じ言葉は、画面から消えてまた出たときか、待つ時間を過ぎたときだけもう一度知らせる (出ている間は何度も知らせない)。
/// </summary>
public sealed class KeywordMatcher
{
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = [];
    private HashSet<string> _present = [];

    public IReadOnlyList<string> Keywords { get; private set; } = [];

    /// <summary>同じ言葉をもう一度知らせるまでの時間。</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>1 行に 1 つ (空の行と、# で始まる行は飛ばす。同じ言葉は 1 つに)。</summary>
    public void SetKeywords(string? text)
    {
        Keywords = (text ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .DistinctBy(Normalize)
            .Take(200)
            .ToList();
        _present.RemoveWhere(k => !Keywords.Contains(k));
    }

    /// <summary>比べるための形 (NFKC・小文字・空白を除く)。</summary>
    public static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>今の画面に出ている言葉 (知らせるかは関係なく)。</summary>
    public List<KeywordHit> Find(IEnumerable<string> lines)
    {
        var hits = new List<KeywordHit>();
        var normalizedLines = lines.Select(l => (Line: l, Key: Normalize(l))).Where(l => l.Key.Length > 0).ToList();
        foreach (var keyword in Keywords)
        {
            var key = Normalize(keyword);
            if (key.Length == 0) continue;
            if (normalizedLines.FirstOrDefault(l => l.Key.Contains(key, StringComparison.Ordinal)) is { Line: { } line })
                hits.Add(new KeywordHit(keyword, line));
        }
        return hits;
    }

    /// <summary>
    /// 1 回分の読み取りを見て、知らせる言葉を返す: 前の回に出ていなかった言葉 (新しく出た) で、待つ時間を過ぎたもの。
    /// </summary>
    public List<KeywordHit> Observe(IEnumerable<string> lines, DateTimeOffset now)
    {
        var hits = Find(lines);
        var notify = new List<KeywordHit>();
        foreach (var hit in hits)
        {
            bool appeared = !_present.Contains(hit.Keyword);
            bool rested = !_lastNotified.TryGetValue(hit.Keyword, out var last) || now - last >= Cooldown;
            if (appeared && rested)
            {
                notify.Add(hit);
                _lastNotified[hit.Keyword] = now;
            }
        }
        _present = hits.Select(h => h.Keyword).ToHashSet();
        return notify;
    }

    /// <summary>見張りを止めたとき (次に始めたら、出ている言葉をまた知らせる)。</summary>
    public void Reset()
    {
        _present = [];
        _lastNotified.Clear();
    }
}
