namespace GetText.Tests;

public class MinutesDocumentTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 14, 0, 0);

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "Zoom ミーティング" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。"));
        doc.Add(Start, new TranscriptSegment("win", 9, 12, 1, "まず進捗を確認します。"));
        doc.Add(Start, new TranscriptSegment("mic", 14, 16, 0, "はい、完成しました。"));
        doc.Add(Start, new TranscriptSegment("win", 20, 23, 2, "次回は十月六日です。"));
        return doc;
    }

    [Fact]
    public void 遅れて届いた発言も時刻順に並ぶ()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 10, 12, 1, "二番目"));
        doc.Add(Start, new TranscriptSegment("mic", 3, 5, 0, "一番目")); // マイクの結果があとから届いた
        doc.Add(Start, new TranscriptSegment("win", 20, 22, 1, "三番目"));
        Assert.Equal(["一番目", "二番目", "三番目"], doc.Entries.Select(e => e.Text));
        Assert.Equal("14:00:03", doc.Entries[0].TimeText);
    }

    [Fact]
    public void 手で足した話者と後から聞き分けた話者が同じ名前にならない()
    {
        var doc = Sample();                 // 自分・話者1・話者2
        var added = doc.NewSpeaker();       // 手で足した「話者3」
        Assert.Equal("話者3", added.Name);
        doc.Add(Start, new TranscriptSegment("win", 30, 32, 3, "三人目の声です。")); // 声の聞き分けで 3 番目の人
        var third = doc.Entries.Last().Speaker;
        Assert.NotSame(added, third);
        Assert.NotEqual(added.Name, third.Name);
        Assert.Equal(doc.Speakers.Count, doc.Speakers.Select(s => s.Name).Distinct().Count());
        Assert.True(third.IsUnnamed);        // 自動の名前のまま (覚えた声で名前を付けられる)
        third.Name = "";                     // 名前を消すと自動の名前に戻る
        Assert.True(third.IsUnnamed);
    }

    [Fact]
    public void 消した発言を戻しても_まとめて消えた話者は戻らない()
    {
        var doc = Sample();
        var second = doc.Speakers.First(s => s.Id == 2);
        var removed = doc.Remove([doc.Entries.First(e => e.Speaker == second)]); // 話者2 の発言を消す
        doc.MergeSpeakers(second, doc.Speakers.First(s => s.Id == 1));        // その後、話者2 を話者1 にまとめる
        doc.Restore(removed);                                                  // 消した発言を元に戻す
        Assert.DoesNotContain(second, doc.Speakers);
        Assert.Equal(1, doc.Entries.First(e => e.Text == "次回は十月六日です。").Speaker.Id);
    }

    [Fact]
    public void 話者は番号順で自分が先頭()
    {
        var doc = Sample();
        Assert.Equal(["自分", "話者1", "話者2"], doc.Speakers.Select(s => s.Name));
    }

    [Fact]
    public void Markdown_同じ話者の連続発言はまとめる()
    {
        var md = Sample().ToMarkdown();
        Assert.Contains("# 議事録", md);
        Assert.Contains("- 日時: 2026年9月29日", md);
        Assert.Contains("- 音声: Zoom ミーティング", md);
        Assert.Contains("- 参加者 (声から推定): 自分、話者1、話者2", md);
        Assert.Contains("**[14:00:05] 話者1**: 本日の会議を始めます。まず進捗を確認します。", md);
        Assert.Contains("**[14:00:14] 自分**: はい、完成しました。", md);
        Assert.Contains("**[14:00:20] 話者2**: 次回は十月六日です。", md);
    }

    [Fact]
    public void 名前を変えると書き出しに反映される()
    {
        var doc = Sample();
        doc.Speakers.First(s => s.Id == 1).Name = "田中";
        doc.Entries[2].Text = "はい、完成しています。"; // 画面で書き直した
        var text = doc.ToPlainText();
        Assert.Contains("[14:00:05] 田中: 本日の会議を始めます。まず進捗を確認します。", text);
        Assert.Contains("[14:00:14] 自分: はい、完成しています。", text);
        Assert.Contains("参加者 (声から推定): 自分、田中、話者2", text);
    }

    [Fact]
    public void 空の名前は既定の名前に戻る()
    {
        var doc = Sample();
        var speaker = doc.Speakers.First(s => s.Id == 2);
        speaker.Name = "  ";
        Assert.Equal("話者2", speaker.Name);
    }

    [Fact]
    public void 句点のない和文の連続発言は句点を補ってつなぐ()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "本日の会議を始めます"));
        doc.Add(Start, new TranscriptSegment("win", 3, 4, 1, "まず進捗を確認します"));
        doc.Add(Start, new TranscriptSegment("win", 5, 6, 1, "よろしいですか？"));
        doc.Add(Start, new TranscriptSegment("win", 7, 8, 1, "では始めます"));
        Assert.Contains("話者1: 本日の会議を始めます。まず進捗を確認します。よろしいですか？では始めます", doc.ToPlainText());
    }

    [Fact]
    public void 補正で複数の発言を1つにまとめて置き換える()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "ありがとうございます", 5));
        doc.Add(Start, new TranscriptSegment("win", 3, 6, 1, "次に音声から疑似録を作る機能", 6));
        doc.Add(Start, new TranscriptSegment("win", 8, 9, 2, "はい", 7));

        var entry = doc.Revise(new TranscriptRevision([5, 6], "ありがとうございます。次に音声から議事録を作る機能", "asr", ""));
        Assert.NotNull(entry);
        Assert.Equal(["ありがとうございます。次に音声から議事録を作る機能", "はい"], doc.Entries.Select(e => e.Text));
        Assert.Equal([5, 6], entry!.SegmentIds);
        Assert.False(entry.IsCorrected); // まとめて認識し直しただけなら印は付けない
        Assert.Equal("14:00:01", entry.TimeText); // 最初の発言の時刻を使う
    }

    [Fact]
    public void 文脈補正には印を付け元の文を残す()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "翻訳の制度が高い", 1));
        var entry = doc.Revise(new TranscriptRevision([1], "翻訳の精度が高い", "llm", ""))!;
        Assert.True(entry.IsCorrected);
        Assert.Contains("元の文: 翻訳の制度が高い", entry.CorrectionNote);
        Assert.False(entry.UserEdited);
    }

    [Fact]
    public void 書き直した発言は補正しない()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "翻訳の制度が高い", 1));
        doc.Entries[0].Text = "翻訳の精度がとても高い"; // 画面で書き直した
        Assert.Null(doc.Revise(new TranscriptRevision([1], "翻訳の精度が高い", "llm", "")));
        Assert.Equal("翻訳の精度がとても高い", doc.Entries[0].Text);
    }

    [Fact]
    public void 話者の付け直しを反映し使われない話者は消す()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "一つ目", 1));
        doc.Add(Start, new TranscriptSegment("win", 3, 4, 1, "二つ目", 2));
        doc.Add(Start, new TranscriptSegment("win", 5, 6, 2, "三つ目", 3));
        doc.Speakers.First(s => s.Id == 1).Name = "田中"; // 名前を付けた話者は残す

        // 全体を分け直したら、2 つ目は話者 3、3 つ目は話者 1 だった
        int changed = doc.ApplySpeakerChanges(new Dictionary<int, int> { [2] = 3, [3] = 1 });
        Assert.Equal(2, changed);
        Assert.Equal(["田中", "話者3", "田中"], doc.Entries.Select(e => e.Speaker.Name));
        Assert.Equal(["田中", "話者3"], doc.Speakers.Select(s => s.Name)); // 使われなくなった話者2 は消える
    }

    [Fact]
    public void 別の話者になった発言はまとめない()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "はい", 1));
        doc.Add(Start, new TranscriptSegment("win", 3, 4, 1, "わかりました", 2));
        doc.ApplySpeakerChanges(new Dictionary<int, int> { [2] = 2 });
        Assert.Null(doc.Revise(new TranscriptRevision([1, 2], "はい、わかりました", "asr", "")));
        Assert.Equal(2, doc.Entries.Count);
    }

    [Fact]
    public void 英語の連続発言は空白でつなぐ()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "Hello everyone"));
        doc.Add(Start, new TranscriptSegment("win", 3, 4, 1, "let's start"));
        Assert.Contains("話者1: Hello everyone let's start", doc.ToPlainText());
    }
}
