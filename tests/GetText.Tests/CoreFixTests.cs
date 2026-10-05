namespace GetText.Tests;

/// <summary>見直しで見つけた不具合の再発を防ぐ確認 (議事録の共通の処理)。</summary>
public class CoreFixTests
{
    private static readonly DateTime Start = new(2026, 10, 3, 14, 0, 0);

    [Fact]
    public void 発言の音声は前後の発言の声を入れずに流す()
    {
        var doc = new MinutesDocument();
        doc.AudioParts.Add(new AudioPart("rec.m4a", Start, 120));
        doc.Add(Start, new TranscriptSegment("win", 10, 14, 1, "前の発言", 1));
        var middle = doc.Add(Start, new TranscriptSegment("win", 14.2, 18, 2, "聞きたい発言", 2));
        doc.Add(Start, new TranscriptSegment("win", 18.1, 25, 1, "次の発言", 3));
        var range = doc.PlayRange(middle)!.Value;
        Assert.Equal("rec.m4a", range.Path);
        Assert.True(range.From.TotalSeconds >= 14.0 && range.From.TotalSeconds <= 14.2, $"始まり {range.From.TotalSeconds}");
        Assert.True(range.Until.TotalSeconds >= 18.0 && range.Until.TotalSeconds <= 18.1, $"終わり {range.Until.TotalSeconds}");
        // 後に発言が無くても、発言の終わりの後はほとんど流さない (すぐ後の返事や、文字にならなかった声が入らないように)
        var last = doc.PlayRange(doc.Entries[^1])!.Value;
        Assert.True(last.Until.TotalSeconds >= 25.0 && last.Until.TotalSeconds <= 25.1, $"最後の発言の終わり {last.Until.TotalSeconds}");
    }

    [Fact]
    public void まとめた発言は始まりから終わりまで流す()
    {
        var doc = new MinutesDocument();
        doc.AudioParts.Add(new AudioPart("rec.m4a", Start, 120));
        doc.Add(Start, new TranscriptSegment("win", 10, 12, 1, "前半", 1));
        doc.Add(Start, new TranscriptSegment("win", 13, 16, 1, "後半", 2));
        var merged = doc.Revise(new TranscriptRevision([1, 2], "前半と後半", "", ""))!;
        Assert.Equal(TimeSpan.FromSeconds(5), merged.Duration);   // 話していた時間 (2 + 3 秒)
        Assert.Equal(TimeSpan.FromSeconds(6), merged.Span);       // 10 秒から 16 秒まで
        var range = doc.PlayRange(merged)!.Value;
        Assert.True(range.Until.TotalSeconds >= 16, $"終わり {range.Until.TotalSeconds}");
        // 保存して開き直しても範囲が残る
        var reloaded = new MinutesDocument();
        reloaded.Load(MinutesFile.FromJson(doc.ToFile().ToJson()));
        Assert.Equal(TimeSpan.FromSeconds(6), reloaded.Entries[0].Span);
    }

    [Fact]
    public void 録画に付ける字幕は録画を始めた時刻からの時間にする()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "録画の前の発言", 1));
        doc.Add(Start, new TranscriptSegment("win", 12, 15, 1, "録画の中の発言", 2));
        doc.Add(Start, new TranscriptSegment("win", 40, 42, 1, "録画の後の発言", 3));
        var video = new AudioPart("meeting.mp4", Start.AddSeconds(10), 20);
        var cues = doc.SubtitleCues(null, video.Start, TimeSpan.FromSeconds(video.Seconds));
        var cue = Assert.Single(cues);
        Assert.Equal(TimeSpan.FromSeconds(2), cue.Start);
        Assert.Equal("録画の中の発言", cue.Text);
        Assert.StartsWith(cue.Speaker + ": ", cue.Display);
        Assert.Contains("00:00:02,000 --> ", doc.ToSubtitles(false, null, video.Start, TimeSpan.FromSeconds(video.Seconds)));
    }

    [Fact]
    public void 録画は保存して開き直しても残り_新しい議事録では消える()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "発言", 1));
        doc.VideoParts.Add(new AudioPart("meeting.mp4", Start, 30));
        var reloaded = new MinutesDocument();
        reloaded.Load(MinutesFile.FromJson(doc.ToFile().ToJson()));
        var part = Assert.Single(reloaded.VideoParts);
        Assert.Equal("meeting.mp4", part.Path);
        Assert.Equal(30, part.Seconds);
        reloaded.Clear();
        Assert.Empty(reloaded.VideoParts);
        // 録画の項目が無い前の版のファイルも開ける
        var old = new MinutesDocument();
        old.Load(MinutesFile.FromJson(doc.ToFile().ToJson().Replace("\"Video\"", "\"Unknown\"")));
        Assert.Empty(old.VideoParts);
    }

    [Fact]
    public void 終わりの時刻は遅れて届いた発言で戻らない()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 100, 120, 1, "後の発言", 1));
        doc.Add(Start, new TranscriptSegment("mic", 50, 60, 0, "先の発言 (マイクの結果が遅れて届いた)", 2));
        Assert.Equal(Start.AddSeconds(120), doc.EndedAt);
    }

    [Fact]
    public void WebVTT_の字幕では書式の記号を文字にする()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "x < y & z の場合", 1));
        var vtt = doc.ToSubtitles(vtt: true);
        Assert.Contains("x &lt; y &amp; z の場合", vtt);
        Assert.Contains("x < y & z の場合", doc.ToSubtitles(vtt: false)); // SRT はそのまま
    }

    [Fact]
    public void テキストで保存した名前に記号があっても読み戻せる()
    {
        var doc = new MinutesDocument { Source = "Zoom" };
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "始めます。", 1));
        doc.Add(Start, new TranscriptSegment("win", 5, 7, 2, "よろしくお願いします。", 2));
        doc.Speaker(1).Name = "田中 [営業]";
        doc.Speaker(2).Name = "佐藤: 開発";
        var file = MinutesFile.FromText(doc.ToPlainText());
        Assert.Equal(2, file.Entries.Count);
        Assert.Contains(file.Speakers, s => s.Name == "田中 [営業]");
        Assert.Equal("始めます。", file.Entries[0].Text);
    }

    [Fact]
    public void 漢数字の後の〇は句点にしない()
    {
        Assert.Equal("定員は三〇", JapaneseCorrector.Correct("定員は三〇"));
        Assert.Equal("終わりました。", JapaneseCorrector.Correct("終わりました〇"));
    }

    [Fact]
    public void 処理が落ちた後は前の発言と新しい処理を切り離す()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "前の処理の発言", 1));
        doc.Speaker(1).Name = "田中";
        doc.Detach();
        // 新しい処理は番号を 1 から数え直す
        var entry = doc.Add(Start, new TranscriptSegment("win", 10, 12, 1, "新しい処理の発言", 1));
        Assert.NotEqual("田中", entry.Speaker.Name);            // 新しい声が前の「田中」に入らない
        Assert.Contains(doc.Speakers, s => s.Name == "田中" && s.Id >= 1000);
        doc.Revise(new TranscriptRevision([1], "新しい処理の発言の補正", "", "")); // 新しい処理の 1 番への補正
        Assert.Equal("前の処理の発言", doc.Entries[0].Text);
    }

    [Fact]
    public void 元に戻した発言は時刻の順の位置に入る()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 8, 9, 1, "A", 1));
        doc.Add(Start, new TranscriptSegment("win", 9, 10, 1, "B", 2));
        doc.Add(Start, new TranscriptSegment("win", 10, 11, 1, "C", 3));
        doc.Add(Start, new TranscriptSegment("win", 11, 12, 1, "D", 4));
        var removed = doc.Remove([doc.Entries[2]]);   // C を消す
        doc.Remove([doc.Entries[0]]);                 // その後に前の発言が減る (まとまったときと同じ)
        doc.Restore(removed);
        Assert.Equal(["B", "C", "D"], doc.Entries.Select(e => e.Text));
    }
}
