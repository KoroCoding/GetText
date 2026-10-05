using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GetText;

/// <summary>
/// 議事録の保存データ (GetText の議事録データ .json)。別の PC で作った議事録を読み込み、話者の名前などを直して保存し直すため。
/// </summary>
public sealed record MinutesFile(
    int Version,
    string Source,
    bool FromFile,
    DateTime? StartedAt,
    DateTime? EndedAt,
    double? DurationSeconds,
    List<MinutesFile.SpeakerData> Speakers,
    List<MinutesFile.EntryData> Entries,
    string? Summary = null,
    string? SourcePath = null,
    List<AudioPart>? Audio = null,
    List<AudioPart>? Video = null)
{
    public sealed record SpeakerData(int Id, string Name, string Color);

    public sealed record EntryData(
        DateTime Time, double OffsetSeconds, int Speaker, string Text, string Language,
        string? Translation, double DurationSeconds, bool UserEdited, string? Original, bool Marked = false, bool Note = false,
        double? SpanSeconds = null);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MinutesFile FromJson(string json) =>
        JsonSerializer.Deserialize<MinutesFile>(json, Json) ?? throw new InvalidDataException("議事録データを読めませんでした");

    /// <summary>ファイル (.json / .md / .txt) を読む (読めなければ例外。今の議事録を消す前に読めるか確かめるため)。</summary>
    public static MinutesFile Read(string path)
    {
        var text = File.ReadAllText(path);
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? FromJson(text) : FromText(text);
    }

    /// <summary>
    /// GetText が書き出した Markdown (.md) / テキスト (.txt) の議事録を読む。
    /// 同じ話者が続く発言は 1 段落にまとめて書き出しているので、段落ごとに 1 つの発言として読む。
    /// </summary>
    public static MinutesFile FromText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string source = "";
        bool fromFile = false;
        DateTime? date = null;
        double? duration = null;
        var entries = new List<(string? Time, string Name, string Text, string? Translation, bool Marked, bool Note)>();
        bool started = false;
        StringBuilder? summary = null;
        bool inSummary = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            // 要約の欄 (Markdown「## 要約」〜「## 発言記録」、テキスト「【要約】」〜「【発言記録】」)
            if (!started && line is "## 要約" or "【要約】")
            {
                inSummary = true;
                summary = new StringBuilder();
                continue;
            }
            if (inSummary)
            {
                if (line is "## 発言記録" or "【発言記録】")
                {
                    inSummary = false;
                    continue;
                }
                // テキストの見出し【概要】は Markdown の ### 概要 に戻す
                var h = Regex.Match(line, @"^【(.+)】$");
                summary!.AppendLine(h.Success ? "### " + h.Groups[1].Value : line);
                continue;
            }
            // 見出しの項目 (Markdown は「- 」付き)
            var head = Regex.Match(line, @"^(?:- )?(日時|音声|ファイル|長さ|参加者 \(声から推定\)|発言時間): (.*)$");
            if (!started && head.Success)
            {
                var value = head.Groups[2].Value;
                switch (head.Groups[1].Value)
                {
                    case "日時":
                        var d = Regex.Match(value, @"(\d{4})年(\d{1,2})月(\d{1,2})日");
                        if (d.Success) date = new DateTime(int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), int.Parse(d.Groups[3].Value));
                        break;
                    case "音声":
                        source = value;
                        break;
                    case "ファイル":
                        source = value;
                        fromFile = true;
                        break;
                    case "長さ":
                        fromFile = true;
                        if (TryParseClock(value, out var len)) duration = len.TotalSeconds;
                        break;
                }
                continue;
            }
            // 日本語訳の行 (Markdown「> 訳: …」/ テキスト「    (訳) …」)
            var tr = Regex.Match(line, @"^(?:> 訳: |\s+\(訳\) )(.*)$");
            if (tr.Success && entries.Count > 0)
            {
                var last = entries[^1];
                entries[^1] = last with { Translation = tr.Groups[1].Value };
                continue;
            }
            // 発言 (Markdown「**[時刻] 名前**: 本文」/ テキスト「[時刻] 名前: 本文」。時刻は無いこともある)
            bool marked = false;
            var body = line;
            if (body.StartsWith("**★ ")) { marked = true; body = "**" + body[4..]; }
            else if (body.StartsWith("★ ")) { marked = true; body = body[2..]; }
            bool note = false;
            if (body.StartsWith("**📝 ")) { note = true; body = "**" + body[5..]; }
            else if (body.StartsWith("📝 ")) { note = true; body = body[3..]; }
            var md = Regex.Match(body, @"^\*\*(?:\[([^\]]+)\] )?(.+?)\*\*: (.*)$");
            var txt = md.Success ? md : Regex.Match(body, @"^(?:\[(\d{1,2}:\d{2}(?::\d{2})?)\] )?(.{1,60}?): (.*)$");
            if (txt.Success) // 見出しの項目は上で読み飛ばしているので、残りは発言
            {
                started = true;
                entries.Add((txt.Groups[1].Success ? txt.Groups[1].Value : null, txt.Groups[2].Value.Trim(), txt.Groups[3].Value.Trim(), null, marked, note));
            }
        }
        if (entries.Count == 0) throw new InvalidDataException("議事録の発言が見つかりませんでした (GetText で保存した .md / .txt / .json を選んでください)");

        // 話者: 自分 = 0、それ以外は声の聞き分けの番号と重ならないよう 1000 番以降
        var names = entries.Where(e => !e.Note).Select(e => e.Name).Distinct().ToList();
        string[] palette = ["#2B7BD6", "#D6532B", "#2E9E5B", "#9B4FD1", "#C4892B", "#1F9FA8", "#C43E7A", "#6B7A2B"];
        int next = 0;
        var speakers = names.Select(n => n == MinutesSpeaker.DefaultName(0)
            ? new SpeakerData(0, n, "#8A8A8A")
            : new SpeakerData(1000 + next, n, palette[next++ % palette.Length])).ToList();
        var idOf = speakers.ToDictionary(s => s.Name, s => s.Id);
        idOf.TryAdd(MinutesDocument.NoteName, -1);

        // 時刻: ファイルなら先頭からの時間、録音なら時刻 (日付は見出しの日時)
        var day = date ?? DateTime.Today;
        var starts = entries.Select(e => e.Time != null && TryParseClock(e.Time, out var t) ? t : (TimeSpan?)null).ToList();
        if (!fromFile)
        {
            // 日付をまたいだ会議 (23:50〜0:20 など) は、時刻が大きく戻ったところから翌日にする (並びが崩れないように)
            var add = TimeSpan.Zero;
            TimeSpan? previous = null;
            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] is not { } t) continue;
                if (previous is { } p && t + add < p - TimeSpan.FromHours(12)) add += TimeSpan.FromDays(1);
                starts[i] = t + add;
                previous = starts[i];
            }
        }
        var origin = fromFile ? TimeSpan.Zero : starts.FirstOrDefault(t => t != null) ?? TimeSpan.Zero;
        var result = new List<EntryData>();
        for (int i = 0; i < entries.Count; i++)
        {
            var at = starts[i] ?? (i > 0 ? starts.Take(i).LastOrDefault(t => t != null) ?? origin : origin);
            // 発言時間は次の段落までの時間から見積もる (段落には長さが書かれていないため。上限 60 秒)
            var nextStart = starts.Skip(i + 1).FirstOrDefault(t => t != null);
            double dur = nextStart is { } ns && ns > at ? Math.Min(60, (ns - at).TotalSeconds) : 10;
            var e = entries[i];
            result.Add(new EntryData(day + at, (at - origin).TotalSeconds, e.Note ? -1 : idOf[e.Name], e.Text,
                e.Note ? "ja" : GuessLanguage(e.Text), e.Translation, e.Note ? 0 : dur, false, null, e.Marked, e.Note));
        }
        var first = result[0].Time;
        var summaryText = summary?.ToString().Trim();
        // ファイルの議事録は、発言の時刻を「その日の 0 時 + 先頭からの時間」で持つ (メモの差し込みにも使う)
        return new MinutesFile(1, source, fromFile, fromFile ? day : first, fromFile ? null : result[^1].Time,
            duration, speakers, result, string.IsNullOrEmpty(summaryText) ? null : summaryText);
    }

    private static bool TryParseClock(string s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s.Trim(), [@"h\:mm\:ss", @"hh\:mm\:ss", @"mm\:ss", @"m\:ss"], CultureInfo.InvariantCulture, out t);

    // ひらがな・カタカナがあれば日本語、英字だけなら英語、それ以外 (漢字だけ・ハングルなど) は日本語以外として扱う
    private static string GuessLanguage(string text)
    {
        if (text.Any(c => c is >= '぀' and <= 'ヿ')) return "ja";
        if (text.Any(c => c is >= '가' and <= '힯')) return "ko";
        if (text.Any(char.IsAsciiLetter) && !text.Any(c => c > '⿿')) return "en";
        return text.Any(c => c is >= '一' and <= '鿿') ? "zh" : "ja";
    }
}

public sealed partial class MinutesDocument
{
    /// <summary>保存データにする。</summary>
    public MinutesFile ToFile() => new(
        1, Source, FromFile, StartedAt, EndedAt, Duration?.TotalSeconds,
        Speakers.Select(s => new MinutesFile.SpeakerData(s.Id, s.Name, s.Color)).ToList(),
        Entries.Select(e => new MinutesFile.EntryData(e.Time, e.Offset.TotalSeconds, e.Speaker.Id, e.Text, e.Language,
            e.Translation, e.Duration.TotalSeconds, e.UserEdited, e.Original, e.IsMarked, e.IsNote,
            e.Span > e.Duration ? e.Span.TotalSeconds : null)).ToList(),
        Summary, SourcePath, AudioParts.Count > 0 ? [.. AudioParts] : null, VideoParts.Count > 0 ? [.. VideoParts] : null);

    /// <summary>
    /// 保存データを読み込む (今の内容は消える)。話者は、これから記録したときの声の聞き分けの番号と重ならないよう
    /// 1000 番以降に付け直す。読み込んだ発言は手で直した扱いにし、声による付け直しの対象にしない。
    /// </summary>
    public void Load(MinutesFile file)
    {
        Clear();
        Source = file.Source;
        FromFile = file.FromFile;
        StartedAt = file.StartedAt;
        EndedAt = file.EndedAt;
        Duration = file.DurationSeconds is { } d ? TimeSpan.FromSeconds(d) : null;
        Summary = file.Summary;
        SourcePath = file.SourcePath;
        if (file.Audio != null)
            AudioParts.AddRange(file.Audio.Select(a => !File.Exists(a.Path) && File.Exists(Path.ChangeExtension(a.Path, ".m4a"))
                ? a with { Path = Path.ChangeExtension(a.Path, ".m4a") } : a));
        if (file.Video != null) VideoParts.AddRange(file.Video);
        var map = new Dictionary<int, MinutesSpeaker>();
        int next = 1000;
        foreach (var s in file.Speakers)
        {
            int id = s.Id == 0 ? 0 : next++;
            var speaker = new MinutesSpeaker(id, s.Name, s.Color);
            InsertSpeaker(speaker);
            map[s.Id] = speaker;
        }
        foreach (var e in file.Entries.OrderBy(e => e.Time))
        {
            if (e.Note)
            {
                var note = new MinutesEntry(e.Time, TimeSpan.FromSeconds(e.OffsetSeconds), NoteSpeaker, e.Text, FromFile, isNote: true)
                {
                    ShowTranslation = ShowTranslations,
                    IsMarked = e.Marked,
                };
                note.RestoreState(e.UserEdited, null);
                Entries.Add(note);
                continue;
            }
            if (!map.TryGetValue(e.Speaker, out var speaker)) map[e.Speaker] = speaker = NewSpeaker();
            var entry = new MinutesEntry(e.Time, TimeSpan.FromSeconds(e.OffsetSeconds), speaker, e.Text, FromFile, e.Language)
            {
                ShowTranslation = ShowTranslations,
                Translation = e.Translation,
                Duration = TimeSpan.FromSeconds(e.DurationSeconds),
                Span = TimeSpan.FromSeconds(e.SpanSeconds ?? e.DurationSeconds),
                SpeakerFixed = true,
                IsMarked = e.Marked,
            };
            entry.RestoreState(e.UserEdited, e.Original);
            Entries.Add(entry);
        }
        UpdateTalkTimes();
    }

    /// <summary>ファイル (.json / .md / .txt) から読み込む。</summary>
    public void LoadFromPath(string path) => Load(MinutesFile.Read(path));
}
