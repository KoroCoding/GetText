using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace GetText;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    protected void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>議事録の話者。名前を変えるとすべての発言に反映される。</summary>
public sealed class MinutesSpeaker(int id, string name, string color, string? autoName = null) : Observable
{
    private string _name = name;

    /// <summary>0 = 自分 (マイク)、1 以降 = 声で聞き分けた話者。</summary>
    public int Id { get; } = id;
    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? AutoName : value.Trim()); }

    /// <summary>自動で付けた名前 (「話者3」など)。名前を消したときはこれに戻る。</summary>
    public string AutoName { get; } = autoName ?? DefaultName(id);

    /// <summary>まだ名前を付けていない (自動の名前のまま)。</summary>
    public bool IsUnnamed => Name == AutoName;

    private string _share = "";

    /// <summary>発言時間と全体に占める割合 (例: "5:12 ・ 45%")。</summary>
    public string Share { get => _share; internal set => Set(ref _share, value); }

    private double _shareValue;

    /// <summary>全体に占める発言時間の割合 (0〜1。話者の一覧の棒の長さ)。</summary>
    public double ShareValue { get => _shareValue; internal set => Set(ref _shareValue, value); }
    /// <summary>画面で話者を見分けるための色 (#RRGGBB)。</summary>
    public string Color { get; } = color;

    public static string DefaultName(int id) => id == 0 ? "自分" : $"話者{id}";
}

public sealed class MinutesEntry(DateTime time, TimeSpan offset, MinutesSpeaker speaker, string text, bool showOffset = false,
    string language = "ja", bool isNote = false) : Observable
{
    private string? _translation;
    private bool _showTranslation = true;
    private string _text = text;
    private string? _original;
    private bool _isFiller = !isNote && Fillers.IsFiller(text);

    /// <summary>手で書き込んだメモ (発言ではない。話者の聞き分け・相づち・字幕の対象にしない)。</summary>
    public bool IsNote { get; } = isNote;

    /// <summary>発言した時刻。</summary>
    public DateTime Time { get; } = time;
    /// <summary>記録開始からの経過時間。</summary>
    public TimeSpan Offset { get; } = offset;
    private MinutesSpeaker _speaker = speaker;

    /// <summary>話者。声の聞き分けを全体でやり直したときに付け直される。</summary>
    public MinutesSpeaker Speaker { get => _speaker; internal set => Set(ref _speaker, value); }

    /// <summary>話者を手で選び直した (自動の付け直しをしない)。</summary>
    public bool SpeakerFixed { get; internal set; }

    /// <summary>話された言語 (ja, en など)。</summary>
    public string Language { get; } = language;

    /// <summary>日本語以外の発言で、日本語訳を付ける対象か。</summary>
    public bool NeedsTranslation => Language != "ja" && !string.IsNullOrWhiteSpace(_text);

    /// <summary>日本語訳 (まだ訳していない・本文が変わって訳し直す前は null)。</summary>
    public string? Translation
    {
        get => _translation;
        set
        {
            Set(ref _translation, value);
            OnChanged(nameof(HasTranslation));
        }
    }

    /// <summary>日本語訳を画面に出すか (画面の「日本語訳」のチェック)。</summary>
    public bool ShowTranslation
    {
        get => _showTranslation;
        set
        {
            Set(ref _showTranslation, value);
            OnChanged(nameof(HasTranslation));
        }
    }

    public bool HasTranslation => _showTranslation && !string.IsNullOrEmpty(_translation);

    /// <summary>相づち・言いよどみだけの発言 (「はい」「なるほど」など)。</summary>
    public bool IsFiller { get => _isFiller; private set => Set(ref _isFiller, value); }

    private bool _isMarked;

    /// <summary>重要な発言の印 (★)。保存にも入り、要約で重視される。</summary>
    public bool IsMarked { get => _isMarked; set => Set(ref _isMarked, value); }
    /// <summary>画面で書き直せる (書き直した発言は自動の補正をしない)。書き出しにも反映される。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            UserEdited = true;
            Set(ref _text, value);
            IsFiller = !IsNote && Fillers.IsFiller(value);
            Translation = null; // 訳し直す
        }
    }

    /// <summary>表示する時刻。ファイルの文字起こしではファイルの先頭からの時間、録音では時刻。</summary>
    public string TimeText => showOffset
        ? (Offset.TotalHours >= 1 ? Offset.ToString(@"h\:mm\:ss") : Offset.ToString(@"mm\:ss"))
        : Time.ToString("HH:mm:ss");

    /// <summary>話していた時間 (発言時間の集計用)。</summary>
    public TimeSpan Duration { get; internal set; }

    private TimeSpan _span;

    /// <summary>
    /// 発言の始まりから終わりまでの長さ (音声を聞く範囲)。いくつかの区切りをまとめた発言は、間の無音を含むので
    /// 話していた時間 (Duration) より長い。
    /// </summary>
    public TimeSpan Span { get => _span > Duration ? _span : Duration; internal set => _span = value; }

    /// <summary>この発言にまとめられた文字起こしの番号。</summary>
    public List<int> SegmentIds { get; } = [];

    /// <summary>ユーザーが書き直した。</summary>
    public bool UserEdited { get; private set; }

    /// <summary>前後の文脈で聞き間違いを直した (印を表示する)。</summary>
    public bool IsCorrected => _original != null && _original != _text;

    /// <summary>補正する前の文 (画面の印のツールチップに出す)。</summary>
    public string CorrectionNote => _original == null ? "" : $"前後の文脈で聞き間違いを直しました\n元の文: {_original}";

    /// <summary>自動の補正を反映する。まとめて認識し直した結果 (asr) は元の文として扱い、文脈補正 (llm) は印を付ける。</summary>
    /// <summary>読み込んだ議事録の状態を戻す (書き直した印・補正前の文)。</summary>
    internal void RestoreState(bool userEdited, string? original)
    {
        UserEdited = userEdited;
        _original = original;
        OnChanged(nameof(IsCorrected));
        OnChanged(nameof(CorrectionNote));
    }

    /// <summary>補正する前の文 (無ければ null)。</summary>
    internal string? Original => _original;

    internal void ApplyRevision(string text, bool markCorrected)
    {
        if (markCorrected) _original ??= _text;
        else _original = null;
        Set(ref _text, text, nameof(Text));
        IsFiller = Fillers.IsFiller(text);
        Translation = null;
        OnChanged(nameof(IsCorrected));
        OnChanged(nameof(CorrectionNote));
    }
}

/// <summary>議事録 (発言の並びと話者)。</summary>
public sealed partial class MinutesDocument
{
    // 話者の色 (点・棒にはこのまま、名前の文字にはテーマの背景で読める明るさに直して使う: SpeakerColors.ForText)
    internal static readonly string[] Palette =
        ["#3A86E0", "#D65A2E", "#2E9E5B", "#A35FDB", "#B97F1F", "#1F9FA8", "#CC4A87", "#7C8C2F"];

    public ObservableCollection<MinutesEntry> Entries { get; } = [];
    public ObservableCollection<MinutesSpeaker> Speakers { get; } = [];
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Source { get; set; } = "";
    /// <summary>動画・音声ファイルから作った (時刻はファイルの先頭からの時間で示す)。</summary>
    public bool FromFile { get; set; }
    /// <summary>新しく加わる発言に日本語訳を出すか。</summary>
    public bool ShowTranslations { get; set; } = true;
    /// <summary>ファイルの長さ。</summary>
    public TimeSpan? Duration { get; set; }
    /// <summary>記録した会議の音声 (記録を止めるたびに 1 つ。発言の時刻から聞く位置を決める)。</summary>
    public List<AudioPart> AudioParts { get; } = [];

    /// <summary>議事録を取りながら録画した会議の動画 (場所・録画を始めた時刻・長さ)。字幕つきの動画を作るのに使う。</summary>
    public List<AudioPart> VideoParts { get; } = [];
    /// <summary>手で書き込むメモの「話者」(話者の一覧には出さない)。</summary>
    public MinutesSpeaker NoteSpeaker { get; } = new(-1, NoteName, "#8A8A8A");
    public const string NoteName = "メモ";
    /// <summary>ファイルから作ったときの、そのファイルの場所 (発言の音声を聞くため)。</summary>
    public string? SourcePath { get; set; }
    /// <summary>AI で作った要約 (Markdown。概要・決定事項・やること・主な論点)。無ければ null。</summary>
    public string? Summary { get; set; }

    // 消した発言の文字起こしの番号 (あとから届く補正で消した内容が戻らないようにする)
    private readonly HashSet<int> _deletedIds = [];

    public MinutesSpeaker Speaker(int id)
    {
        var speaker = Speakers.FirstOrDefault(s => s.Id == id);
        if (speaker != null) return speaker;
        // 手で足した話者・読み込んだ話者がもうその名前を使っていたら、空いている番号の名前にする (同じ名前の 2 人にしない)
        var auto = MinutesSpeaker.DefaultName(id);
        for (int n = id; id != 0 && Speakers.Any(s => s.Name == auto);) auto = $"話者{++n}";
        speaker = new MinutesSpeaker(id, auto, id == 0 ? "#8A8A8A" : Palette[(id - 1) % Palette.Length], auto);
        InsertSpeaker(speaker);
        return speaker;
    }

    // 番号順 (自分 = 0 が先頭)
    private void InsertSpeaker(MinutesSpeaker speaker)
    {
        int index = 0;
        while (index < Speakers.Count && Speakers[index].Id < speaker.Id) index++;
        Speakers.Insert(index, speaker);
    }

    /// <summary>手で追加する話者 (まだ使われていない番号)。</summary>
    /// 声の聞き分けの番号と重ならないよう 1000 番以降を使う。
    public MinutesSpeaker NewSpeaker()
    {
        var speaker = Speaker(Math.Max(1000, Speakers.Count == 0 ? 0 : Speakers.Max(s => s.Id) + 1));
        int n = Speakers.Count(s => s.Id != 0);
        while (Speakers.Any(s => s != speaker && s.Name == $"話者{n}")) n++;
        speaker.Name = $"話者{n}";
        return speaker;
    }

    /// <summary>名前が同じ別の話者 (いなければ null)。同じ名前を付けた 2 人は 1 人にまとめるため。</summary>
    public MinutesSpeaker? SameNameAs(MinutesSpeaker speaker) =>
        Speakers.FirstOrDefault(s => s != speaker && s.Name == speaker.Name);

    /// <summary>
    /// 2 人の話者を 1 人にまとめる (from の発言をすべて into にする)。
    /// 手で追加した話者 (1000 番以降) は声の聞き分けの番号を持たないので、声の番号を持つ方に残す。
    /// 実際に残った話者と消えた話者を返す。
    /// </summary>
    public (MinutesSpeaker Kept, MinutesSpeaker Removed) MergeSpeakers(MinutesSpeaker from, MinutesSpeaker into)
    {
        if (into.Id >= 1000 && from.Id is > 0 and < 1000)
        {
            from.Name = into.Name;
            (from, into) = (into, from);
        }
        foreach (var e in Entries.Where(e => e.Speaker == from)) e.Speaker = into;
        Speakers.Remove(from);
        _mergedInto[from] = into;
        return (into, from);
    }

    // まとめて消えた話者 → 残った話者 (消した発言を元に戻したとき、消えた話者を復活させない)
    private readonly Dictionary<MinutesSpeaker, MinutesSpeaker> _mergedInto = [];

    /// <summary>発言の話者を手で選び直す。以後、声による自動の付け直しをしない。</summary>
    public void SetSpeaker(IEnumerable<MinutesEntry> entries, MinutesSpeaker speaker)
    {
        if (!Speakers.Contains(speaker)) InsertSpeaker(speaker);
        foreach (var e in entries)
        {
            e.Speaker = speaker;
            e.SpeakerFixed = true;
        }
        RemoveUnusedSpeakers();
    }

    /// <summary>発言を消す。元に戻すための記録 (元の位置と発言) を返す。</summary>
    public IReadOnlyList<(int Index, MinutesEntry Entry)> Remove(IEnumerable<MinutesEntry> entries)
    {
        var removed = entries.Distinct().Select(e => (Index: Entries.IndexOf(e), Entry: e))
            .Where(x => x.Index >= 0).OrderBy(x => x.Index).ToList();
        for (int i = removed.Count - 1; i >= 0; i--) Entries.RemoveAt(removed[i].Index);
        foreach (var (_, e) in removed) _deletedIds.UnionWith(e.SegmentIds);
        return removed;
    }

    /// <summary><see cref="Remove"/> で消した発言を元の位置に戻す。</summary>
    public void Restore(IReadOnlyList<(int Index, MinutesEntry Entry)> removed)
    {
        foreach (var (index, e) in removed.OrderBy(x => x.Index))
        {
            if (!e.IsNote && !Speakers.Contains(e.Speaker))
            {
                // ほかの話者にまとめた後なら、まとめた先の話者にする
                var kept = e.Speaker;
                for (int i = 0; i < 100 && _mergedInto.TryGetValue(kept, out var next); i++) kept = next;
                if (Speakers.Contains(kept))
                {
                    e.Speaker = kept;
                    InsertByTime(e, index);
                    _deletedIds.ExceptWith(e.SegmentIds);
                    continue;
                }
                // 話者の一覧から消えていたら戻す (同じ番号の話者が新しくできていればそちらにする)
                var same = Speakers.FirstOrDefault(sp => sp.Id == e.Speaker.Id);
                if (same != null) e.Speaker = same;
                else InsertSpeaker(e.Speaker);
            }
            InsertByTime(e, index);
            _deletedIds.ExceptWith(e.SegmentIds);
        }
    }

    // 消したときの位置に戻す。その後に発言がまとまったり増えたりして時刻の順が崩れるなら、時刻の順の位置にする
    private void InsertByTime(MinutesEntry e, int index)
    {
        int i = Math.Min(index, Entries.Count);
        bool fits = (i == 0 || Entries[i - 1].Time <= e.Time) && (i == Entries.Count || Entries[i].Time >= e.Time);
        if (!fits)
        {
            i = Entries.Count;
            while (i > 0 && Entries[i - 1].Time > e.Time) i--;
        }
        Entries.Insert(i, e);
    }

    /// <summary>
    /// 文字起こしの処理が落ちて起動し直すとき、今の発言を新しい処理と切り離す。新しい処理は発言の番号も話者の番号も
    /// 1 から数え直すので、そのままだと補正や話者の付け直しが前の発言に当たり、新しい声が前の話者 (「田中」など) に入る。
    /// (保存した議事録を読み込んだときと同じ扱いにする)
    /// </summary>
    public void Detach()
    {
        _deletedIds.Clear();
        int next = Math.Max(1000, Speakers.Count == 0 ? 0 : Speakers.Max(s => s.Id) + 1);
        foreach (var old in Speakers.Where(s => s.Id is > 0 and < 1000).ToList())
        {
            var moved = new MinutesSpeaker(next++, old.Name, old.Color) { Share = old.Share, ShareValue = old.ShareValue };
            Speakers.Remove(old);
            InsertSpeaker(moved);
            foreach (var e in Entries.Where(e => e.Speaker == old)) e.Speaker = moved;
        }
        foreach (var e in Entries)
        {
            e.SegmentIds.Clear();
            e.SpeakerFixed = true;
        }
    }

    /// <summary>発言を時刻順の位置に加える (マイクとウィンドウの結果は前後して届くことがある)。</summary>
    public MinutesEntry Add(DateTime sessionStart, TranscriptSegment segment)
    {
        var time = sessionStart + TimeSpan.FromSeconds(segment.Start);
        StartedAt ??= sessionStart;
        var offset = time - StartedAt.Value;
        var entry = new MinutesEntry(time, offset < TimeSpan.Zero ? TimeSpan.Zero : offset, Speaker(segment.Speaker), segment.Text, FromFile, segment.Lang)
        {
            ShowTranslation = ShowTranslations,
        };
        entry.SegmentIds.Add(segment.Id);
        entry.Duration = TimeSpan.FromSeconds(Math.Max(0, segment.End - segment.Start));
        entry.Span = entry.Duration;
        int i = Entries.Count;
        while (i > 0 && Entries[i - 1].Time > entry.Time) i--;
        Entries.Insert(i, entry);
        // マイクとウィンドウの結果は前後して届くので、終わりの時刻は遅い方を残す
        var end = sessionStart + TimeSpan.FromSeconds(segment.End);
        if (EndedAt == null || end > EndedAt) EndedAt = end;
        return entry;
    }

    /// <summary>手で書いたメモを、時刻順の位置に加える。</summary>
    public MinutesEntry AddNote(DateTime time, string text)
    {
        StartedAt ??= time;
        var offset = time - StartedAt.Value;
        var entry = new MinutesEntry(time, offset < TimeSpan.Zero ? TimeSpan.Zero : offset, NoteSpeaker, text.Trim(), FromFile, isNote: true)
        {
            ShowTranslation = ShowTranslations,
        };
        int i = Entries.Count;
        while (i > 0 && Entries[i - 1].Time > entry.Time) i--;
        Entries.Insert(i, entry);
        return entry;
    }

    /// <summary>発言の音声のある場所と位置 (ファイルなら元のファイル、記録なら保存した音声)。無ければ null。</summary>
    /// <summary>
    /// 発言の音声を聞く範囲 (音声のファイルと、その中の位置)。発言の始まりから終わりまでを流し、
    /// 同じファイルの前後の発言の声は入れない (前の発言の終わりから、次の発言の始まりまで)。
    /// 発言の始まりと終わりは、声の区切りを見つける処理が前後に 0.2 秒ずつ余白を足してあるので、ほとんど足さない
    /// (足すと、すぐ後に続く返事や次の言葉が入る)。
    /// </summary>
    public (string Path, TimeSpan From, TimeSpan Until)? PlayRange(MinutesEntry entry)
    {
        if (AudioAt(entry) is not { } audio) return null;
        var start = audio.Position;
        var end = start + entry.Span;
        var from = start - TimeSpan.FromSeconds(0.05);
        var until = end + TimeSpan.FromSeconds(0.05);
        var margin = TimeSpan.FromSeconds(0.1);
        foreach (var other in Entries)
        {
            if (other == entry || other.IsNote || AudioAt(other) is not { } o || o.Path != audio.Path) continue;
            var otherEnd = o.Position + other.Span;
            // 次の発言 (この発言の終わりより後に始まる) の手前で止める
            if (o.Position >= end - margin && o.Position < until) until = o.Position - TimeSpan.FromSeconds(0.05) > end ? o.Position - TimeSpan.FromSeconds(0.05) : end;
            // 前の発言 (この発言の始まりより前に終わる) の終わりから流す
            if (otherEnd <= start + margin && otherEnd > from) from = otherEnd < start ? otherEnd : start;
        }
        if (from < TimeSpan.Zero) from = TimeSpan.Zero;
        return (audio.Path, from, until);
    }

    public (string Path, TimeSpan Position)? AudioAt(MinutesEntry entry)
    {
        if (FromFile) return SourcePath is { } path ? (path, entry.Offset) : null;
        foreach (var part in AudioParts)
            if (entry.Time >= part.Start.AddSeconds(-1) && entry.Time <= part.Start.AddSeconds(part.Seconds + 1))
                return (part.Path, entry.Time - part.Start < TimeSpan.Zero ? TimeSpan.Zero : entry.Time - part.Start);
        return null;
    }

    /// <summary>
    /// 補正を反映する: 該当する発言を 1 つにまとめて text に置き換える。
    /// ユーザーが書き直した発言が含まれていれば何もしない (手で直した内容を優先する)。
    /// </summary>
    public MinutesEntry? Revise(TranscriptRevision revision)
    {
        if (revision.Ids.Any(_deletedIds.Contains)) return null; // 消した発言を含む
        var targets = Entries.Where(e => e.SegmentIds.Any(revision.Ids.Contains)).ToList();
        if (targets.Count == 0 || targets.Any(e => e.UserEdited || e.SpeakerFixed)) return null;
        // あとから話者を付け直した結果、別の人の発言になったものはまとめない
        if (targets.Select(e => e.Speaker).Distinct().Count() > 1) return null;
        var first = targets[0];
        foreach (var other in targets.Skip(1))
        {
            first.SegmentIds.AddRange(other.SegmentIds);
            var end = Math.Max((first.Time + first.Span).Ticks, (other.Time + other.Span).Ticks);
            first.Duration += other.Duration;
            first.Span = new DateTime(end) - first.Time;
            Entries.Remove(other);
        }
        first.ApplyRevision(revision.Text, markCorrected: revision.Stage == "llm");
        return first;
    }

    /// <summary>
    /// 話者の付け直しを反映する (発言が増えて全体を分け直した結果)。
    /// まとめた発言は最初の文字起こしの話者に従う。使われなくなった話者は、名前を付けていなければ一覧から消す。
    /// </summary>
    public int ApplySpeakerChanges(IReadOnlyDictionary<int, int> changes)
    {
        int changed = 0;
        foreach (var entry in Entries)
        {
            if (entry.SpeakerFixed || entry.SegmentIds.Count == 0 || !changes.TryGetValue(entry.SegmentIds[0], out int id)) continue;
            if (entry.Speaker.Id == id) continue;
            entry.Speaker = Speaker(id);
            changed++;
        }
        RemoveUnusedSpeakers();
        return changed;
    }

    private void RemoveUnusedSpeakers()
    {
        foreach (var unused in Speakers.Where(s => s.Id != 0 && s.IsUnnamed
                                                   && Entries.All(e => e.Speaker != s)).ToList())
            Speakers.Remove(unused);
    }

    /// <summary>話者ごとの発言時間と割合 (長い順)。</summary>
    public List<(MinutesSpeaker Speaker, TimeSpan Time, double Share)> TalkTimes()
    {
        var totals = Entries.GroupBy(e => e.Speaker)
            .Select(g => (Speaker: g.Key, Time: TimeSpan.FromSeconds(g.Sum(e => e.Duration.TotalSeconds))))
            .Where(t => t.Time > TimeSpan.Zero).ToList();
        double all = totals.Sum(t => t.Time.TotalSeconds);
        return totals.OrderByDescending(t => t.Time).Select(t => (t.Speaker, t.Time, all > 0 ? t.Time.TotalSeconds / all : 0)).ToList();
    }

    private static string FormatTalk(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>話者の一覧に出す発言時間を更新する。</summary>
    public void UpdateTalkTimes()
    {
        var talk = TalkTimes().ToDictionary(t => t.Speaker);
        foreach (var sp in Speakers)
        {
            bool has = talk.TryGetValue(sp, out var t);
            sp.Share = has ? $"{FormatTalk(t.Time)} ・ {t.Share:P0}" : "";
            sp.ShareValue = has ? t.Share : 0;
        }
    }

    public void Clear()
    {
        Entries.Clear();
        Speakers.Clear();
        _deletedIds.Clear();
        _mergedInto.Clear();
        StartedAt = EndedAt = null;
        FromFile = false;
        Duration = null;
        Summary = null;
        SourcePath = null;
        AudioParts.Clear();
        VideoParts.Clear();
    }

    /// <summary>書き出しの設定。</summary>
    public sealed record ExportOptions(bool IncludeTime = true, bool ExcludeFillers = false, bool IncludeTranslation = true);

    /// <summary>
    /// 同じ話者が続く発言は 1 つの段落にまとめる (相づちを除くときは、間の相づちも飛ばしてつなぐ)。
    /// ★ の付いた発言はまとめず 1 段落にする (★ がどの発言のものか分かるように)。
    /// </summary>
    private IEnumerable<(MinutesEntry First, string Text, string Translation)> Paragraphs(ExportOptions options)
    {
        MinutesEntry? first = null;
        var text = new StringBuilder();
        var translation = new StringBuilder();
        foreach (var e in Entries)
        {
            if (options.ExcludeFillers && e.IsFiller && !e.IsMarked) continue;
            var tr = options.IncludeTranslation && e.NeedsTranslation ? e.Translation ?? "" : "";
            if (first != null && e.Speaker == first.Speaker && (e.Time - first.Time).TotalMinutes < 2 && !e.IsMarked && !first.IsMarked && !e.IsNote && !first.IsNote)
            {
                text.Append(Separator(text, e.Text)).Append(e.Text);
                if (tr.Length > 0) translation.Append(Separator(translation, tr)).Append(tr);
                continue;
            }
            if (first != null) yield return (first, text.ToString(), translation.ToString());
            first = e;
            text.Clear().Append(e.Text);
            translation.Clear().Append(tr);
        }
        if (first != null) yield return (first, text.ToString(), translation.ToString());
    }

    /// <summary>
    /// 発言どうしのつなぎ目。発言は 0.6 秒以上の間で区切っているのでほぼ文の終わりに当たる。
    /// 音声認識が句点を付けなかった和文には「。」を補い、欧文は空白でつなぐ。
    /// </summary>
    private static string Separator(StringBuilder sb, string next)
    {
        if (sb.Length == 0 || next.Length == 0) return "";
        char last = sb[^1];
        if ("。、！？!?」』）)…".Contains(last)) return "";
        if (TextScript.IsCjk(last)) return "。";
        return TextScript.IsCjk(next[0]) ? "" : " ";
    }

    private string Header(string title, string bullet = "- ")
    {
        var sb = new StringBuilder();
        sb.AppendLine(title).AppendLine();
        if (FromFile)
        {
            if (Duration is { } d) sb.AppendLine($"{bullet}長さ: {d:h\\:mm\\:ss}");
        }
        else if (StartedAt is { } s)
        {
            var end = EndedAt is { } e ? $"〜{e:HH:mm}" : "";
            sb.AppendLine($"{bullet}日時: {s:yyyy年M月d日 (ddd) HH:mm}{end}");
        }
        if (Source.Length > 0) sb.AppendLine($"{bullet}{(FromFile ? "ファイル" : "音声")}: {Source}");
        var names = Speakers.Where(sp => Entries.Any(en => en.Speaker == sp)).Select(sp => sp.Name);
        sb.AppendLine($"{bullet}参加者 (声から推定): {string.Join("、", names)}");
        var talk = TalkTimes();
        if (talk.Count > 0)
            sb.AppendLine(bullet + "発言時間: " + string.Join("、", talk.Select(t => $"{t.Speaker.Name} {FormatTalk(t.Time)} ({t.Share:P0})")));
        return sb.ToString();
    }

    public string ToMarkdown(ExportOptions? options = null)
    {
        options ??= new();
        var sb = new StringBuilder(Header("# 議事録"));
        if (!string.IsNullOrWhiteSpace(Summary))
            sb.AppendLine().AppendLine("## 要約").AppendLine().AppendLine(Summary.Trim());
        sb.AppendLine().AppendLine("## 発言記録").AppendLine();
        foreach (var (first, text, translation) in Paragraphs(options))
        {
            var time = options.IncludeTime ? $"[{first.TimeText}] " : "";
            sb.AppendLine($"**{Marks(first)}{time}{first.Speaker.Name}**: {text}");
            if (translation.Length > 0) sb.AppendLine($"> 訳: {translation}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public string ToPlainText(ExportOptions? options = null)
    {
        options ??= new();
        var sb = new StringBuilder(Header("議事録", bullet: ""));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(Summary))
        {
            // 見出し (### 概要) はテキストでは【概要】にする
            sb.AppendLine("【要約】");
            foreach (var raw in Summary.Trim().Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                sb.AppendLine(line.StartsWith("### ") ? $"【{line[4..].Trim()}】" : line);
            }
            sb.AppendLine().AppendLine("【発言記録】");
        }
        foreach (var (first, text, translation) in Paragraphs(options))
        {
            sb.AppendLine($"{Marks(first)}{(options.IncludeTime ? $"[{first.TimeText}] " : "")}{first.Speaker.Name}: {text}");
            if (translation.Length > 0) sb.AppendLine($"    (訳) {translation}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 字幕ファイル (SRT または WebVTT)。発言ごとに「話者: 本文」を出す (日本語訳を含めるなら 2 行目に訳)。
    /// 時刻は、ファイルならファイルの先頭から、録音なら記録を始めてから。
    /// </summary>
    /// <summary>字幕 1 枚 (話者の名前は発言の最初の 1 枚だけ。訳があれば次の行)。</summary>
    public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Speaker, string Text, string? Translation)
    {
        /// <summary>画面に出す文字 (「名前: 本文」と、訳があれば次の行に訳)。</summary>
        public string Display => (Speaker.Length > 0 ? Speaker + ": " : "") + Text + (string.IsNullOrEmpty(Translation) ? "" : "\n" + Translation);
    }

    /// <summary>
    /// 字幕にする。origin を渡すとその時刻からの時間 (録画した動画に付けるとき)、渡さなければ議事録の先頭からの時間。
    /// length を渡すと、その長さの中に入る字幕だけにする。
    /// </summary>
    public List<SubtitleCue> SubtitleCues(ExportOptions? options = null, DateTime? origin = null, TimeSpan? length = null)
    {
        options ??= new();
        var list = Entries.Where(e => !e.IsNote && !(options.ExcludeFillers && e.IsFiller) && !string.IsNullOrWhiteSpace(e.Text)).ToList();
        TimeSpan StartOf(MinutesEntry e) => origin is { } o ? e.Time - o : e.Offset;
        var cues = new List<SubtitleCue>();
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var text = e.Text.Trim();
            bool latin = TextScript.IsMostlyLatin(text);
            var start = StartOf(e);
            // 終わり: 話していた時間。ただし読み切れるだけの時間 (短くても 1.5 秒) は出す。次の発言には重ならないようにする
            double seconds = Math.Max(Math.Max(1.5, e.Span.TotalSeconds), text.Length / (latin ? ReadingRateLatin : ReadingRateCjk));
            var end = start + TimeSpan.FromSeconds(seconds);
            if (i + 1 < list.Count && StartOf(list[i + 1]) > start && end > StartOf(list[i + 1])) end = StartOf(list[i + 1]);
            if (length is { } len && (end <= TimeSpan.Zero || start >= len)) continue; // 動画の外
            // 長い発言 (まとめた発言など) は、1 枚に収まる長さに区切って時間を文字数で割り振る
            var parts = SplitForSubtitles(text, latin ? CueLimitLatin : CueLimitCjk);
            string? translation = options.IncludeTranslation && e.NeedsTranslation && !string.IsNullOrWhiteSpace(e.Translation) ? e.Translation.Trim() : null;
            // 訳は原文の字幕の枚数に近い数に区切って、順に割り振る
            var translations = translation == null ? null
                : Distribute(SplitForSubtitles(translation, Math.Min(CueLimitCjk, (int)Math.Ceiling(translation.Length / (double)parts.Count) + 6)), parts.Count);
            double total = parts.Sum(t => t.Length), done = 0;
            for (int k = 0; k < parts.Count; k++)
            {
                var from = start + (end - start) * (done / total);
                done += parts[k].Length;
                var to = k == parts.Count - 1 ? end : start + (end - start) * (done / total);
                if (from < TimeSpan.Zero) from = TimeSpan.Zero;
                if (to <= from) continue;
                cues.Add(new SubtitleCue(from, to, k == 0 ? e.Speaker.Name : "", parts[k], translations?[k]));
            }
        }
        return cues;
    }

    public string ToSubtitles(bool vtt, ExportOptions? options = null, DateTime? origin = null, TimeSpan? length = null)
    {
        var sb = new StringBuilder();
        if (vtt) sb.Append("WEBVTT\n\n");
        int number = 0;
        foreach (var c in SubtitleCues(options, origin, length))
        {
            if (!vtt) sb.Append(++number).Append('\n');
            sb.Append(SubtitleTime(c.Start, vtt)).Append(" --> ").Append(SubtitleTime(c.End, vtt)).Append('\n');
            sb.Append(Cue(c.Speaker.Length > 0 ? c.Speaker + ": " : "", vtt)).Append(Cue(c.Text, vtt)).Append('\n');
            if (!string.IsNullOrEmpty(c.Translation)) sb.Append(Cue(c.Translation, vtt)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // WebVTT では < と & が書式の記号になる (そのままだと「x < y」の後ろが消える)
    private static string Cue(string text, bool vtt) =>
        vtt ? text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") : text;

    // 字幕 1 枚の文字数の目安 (日本語などは 1 行 20 字 × 2 行、英語などは 1 行 42 字 × 2 行) と、読む速さ (1 秒あたりの文字数)
    private const int CueLimitCjk = 40, CueLimitLatin = 84;
    private const double ReadingRateCjk = 8, ReadingRateLatin = 17;

    /// <summary>
    /// 字幕 1 枚に収まる長さ (limit 文字) に区切る。文の終わり → 読点・カンマ → 空白の順に区切りやすい所を探し、
    /// どこにも無ければ limit 文字で切る。
    /// </summary>
    internal static List<string> SplitForSubtitles(string text, int limit)
    {
        text = text.Trim();
        var result = new List<string>();
        while (text.Length > limit)
        {
            int? sentence = BreakBefore(text, limit, ".。！？!?");
            int? soft = BreakBefore(text, limit, "、，,;；：: ");
            // 文の終わりが前すぎる (1 枚がとても短くなる) なら読点・空白で区切る。どちらも無ければ limit 文字で切る
            int cut = sentence is int a && a >= limit / 3 ? a : soft is int b && b >= limit / 3 ? b : sentence ?? soft ?? limit;
            result.Add(text[..cut].Trim());
            text = text[cut..].Trim();
        }
        if (text.Length > 0) result.Add(text);
        // 最後の 1 枚がとても短ければ、前の 1 枚の後半と合わせて長さをならす
        if (result.Count >= 2 && result[^1].Length < limit / 4)
        {
            var merged = result[^2] + (TextScript.IsMostlyLatin(result[^2]) ? " " : "") + result[^1];
            int half = BreakBefore(merged, (merged.Length + 1) / 2 + limit / 4, "、，,;；：: ") is int h && h >= merged.Length / 3 ? h : -1;
            if (half > 0 && merged.Length - half <= limit)
            {
                result[^2] = merged[..half].Trim();
                result[^1] = merged[half..].Trim();
            }
        }
        return result;
    }

    // text の先頭 limit 文字以内で、marks のどれかの直後 (区切る位置) のうち最も後ろ。英語の「.」は後ろが空白のときだけ文の終わりとみなす
    private static int? BreakBefore(string text, int limit, string marks)
    {
        for (int i = Math.Min(limit, text.Length) - 1; i > 0; i--)
        {
            char c = text[i];
            if (marks.IndexOf(c) < 0) continue;
            if (c == '.' && i + 1 < text.Length && text[i + 1] != ' ') continue; // 3.5 や URL の「.」では切らない
            return c == ' ' ? i : i + 1;
        }
        return null;
    }

    // 訳の断片 (parts) を、原文の字幕の枚数 (count) に順番どおり割り振る
    private static string[] Distribute(List<string> parts, int count)
    {
        var buckets = new List<string>[count];
        for (int k = 0; k < count; k++) buckets[k] = [];
        for (int j = 0; j < parts.Count; j++) buckets[Math.Min(count - 1, j * count / parts.Count)].Add(parts[j]);
        return buckets.Select(b => b.Aggregate("", TextScript.Join)).ToArray();
    }

    /// <summary>行頭の印 (★ = 重要な発言、📝 = 手で書いたメモ)。</summary>
    private static string Marks(MinutesEntry e) => (e.IsMarked ? "★ " : "") + (e.IsNote ? "📝 " : "");

    private static string SubtitleTime(TimeSpan t, bool vtt) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{(vtt ? '.' : ',')}{t.Milliseconds:000}";

    /// <summary>
    /// 要約する AI に渡す文字起こし (相づちを除き、同じ話者が続く発言は 1 行にまとめる)。
    /// ★ の付いた発言は行頭に ★ を付ける (AI に重要な発言として扱わせる)。
    /// </summary>
    public string SummaryTranscript()
    {
        var sb = new StringBuilder();
        foreach (var (first, text, _) in Paragraphs(new ExportOptions(IncludeTime: true, ExcludeFillers: true, IncludeTranslation: false)))
            if (!string.IsNullOrWhiteSpace(text)) sb.Append($"{Marks(first)}[{first.TimeText}] {first.Speaker.Name}: {text}\n");
        return sb.ToString();
    }
}

/// <summary>記録した会議の音声 1 つ分 (Start = 記録を始めた時刻、Seconds = 長さ)。</summary>
public sealed record AudioPart(string Path, DateTime Start, double Seconds);
