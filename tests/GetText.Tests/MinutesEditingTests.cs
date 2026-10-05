namespace GetText.Tests;

public class MinutesEditingTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 14, 0, 0);

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "Zoom ミーティング" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。", 1));
        doc.Add(Start, new TranscriptSegment("mic", 9, 10, 0, "はい。", 2));
        doc.Add(Start, new TranscriptSegment("win", 11, 14, 1, "まず進捗を確認します。", 3));
        doc.Add(Start, new TranscriptSegment("win", 15, 18, 2, "資料は明日共有します。", 4));
        return doc;
    }

    [Theory]
    [InlineData("はい。")]
    [InlineData("はいはい")]
    [InlineData("うーん、なるほど。")]
    [InlineData("えーっと")]
    [InlineData("そうですね。")]
    [InlineData("うんうんうん")]
    [InlineData("へー、そうなんですね")]
    [InlineData("Yeah.")]
    [InlineData("I see.")]
    [InlineData("Uh-huh.")]
    [InlineData("Got it!")]
    [InlineData("Okay, okay.")]
    public void 相づちだけの発言(string text) => Assert.True(Fillers.IsFiller(text));

    [Theory]
    [InlineData("はい、完成しました。")]
    [InlineData("なるほど、では来週にしましょう。")]
    [InlineData("わかりました。")]
    [InlineData("えーと、資料は明日送ります。")]
    [InlineData("いいえ")]
    [InlineData("OK, let's start.")]
    [InlineData("I see the problem in the second chart.")]
    [InlineData("Yes, we shipped it last week.")]
    public void 内容のある発言は相づちにしない(string text) => Assert.False(Fillers.IsFiller(text));

    [Fact]
    public void 相づちを除いて書き出すと間の相づちを飛ばして段落をつなぐ()
    {
        var doc = Sample();
        Assert.True(doc.Entries[1].IsFiller);
        var text = doc.ToPlainText(new MinutesDocument.ExportOptions(ExcludeFillers: true));
        Assert.DoesNotContain("はい。", text);
        Assert.Contains("[14:00:05] 話者1: 本日の会議を始めます。まず進捗を確認します。", text);
        // 除かなければ残る
        Assert.Contains("自分: はい。", Sample().ToPlainText());
    }

    [Fact]
    public void 書き直して相づちでなくなれば表示に戻る()
    {
        var doc = Sample();
        doc.Entries[1].Text = "はい、了解しました。来週までに対応します。";
        Assert.False(doc.Entries[1].IsFiller);
    }

    [Fact]
    public void 時刻なしで書き出せる()
    {
        var doc = Sample();
        var options = new MinutesDocument.ExportOptions(IncludeTime: false);
        var text = doc.ToPlainText(options);
        Assert.Contains("話者1: 本日の会議を始めます。", text);
        Assert.DoesNotContain("[14:00:05]", text);
        Assert.Contains("**話者2**: 資料は明日共有します。", doc.ToMarkdown(options));
    }

    [Fact]
    public void 削除して元に戻すと同じ位置に戻る()
    {
        var doc = Sample();
        var removed = doc.Remove([doc.Entries[1], doc.Entries[3]]);
        Assert.Equal(["本日の会議を始めます。", "まず進捗を確認します。"], doc.Entries.Select(e => e.Text));
        // 使われなくなった話者2 が一覧から消えても、戻すと話者ごと戻る
        doc.ApplySpeakerChanges(new Dictionary<int, int>());
        Assert.DoesNotContain(doc.Speakers, s => s.Id == 2);
        doc.Restore(removed);
        Assert.Equal(["本日の会議を始めます。", "はい。", "まず進捗を確認します。", "資料は明日共有します。"], doc.Entries.Select(e => e.Text));
        Assert.Contains(doc.Speakers, s => s.Id == 2);
    }

    [Fact]
    public void 削除した発言を含む補正は反映しない()
    {
        var doc = Sample();
        doc.Remove([doc.Entries[2]]); // 発言 3
        Assert.Null(doc.Revise(new TranscriptRevision([1, 3], "本日の会議を始めます。まず進捗を確認します。", "asr", "")));
        Assert.Equal("本日の会議を始めます。", doc.Entries[0].Text);
    }

    [Fact]
    public void 手で選んだ話者は自動の付け直しで変わらない()
    {
        var doc = Sample();
        var entry = doc.Entries[3];
        doc.SetSpeaker([entry], doc.Speaker(1));
        Assert.Equal(1, entry.Speaker.Id);
        doc.ApplySpeakerChanges(new Dictionary<int, int> { [4] = 3 });
        Assert.Equal(1, entry.Speaker.Id);
        Assert.DoesNotContain(doc.Speakers, s => s.Id == 2); // 使われなくなった
    }

    [Fact]
    public void 同じ名前の話者は1人にまとめる()
    {
        var doc = Sample();
        var a = doc.Speaker(1);
        var b = doc.Speaker(2);
        a.Name = "田中";
        Assert.Null(doc.SameNameAs(a));
        b.Name = "田中";
        Assert.Equal(a, doc.SameNameAs(b));
        var (kept, removed) = doc.MergeSpeakers(b, a);
        Assert.Equal(a, kept);
        Assert.Equal(b, removed);
        Assert.DoesNotContain(b, doc.Speakers);
        Assert.All(doc.Entries.Where(e => e.Speaker.Id != 0), e => Assert.Equal(a, e.Speaker));
        // 1 つの段落にまとまって書き出される
        Assert.Contains("[14:00:05] 田中: 本日の会議を始めます。", doc.ToPlainText());
    }

    [Fact]
    public void 手で追加した話者とまとめると声の番号を持つ方に残す()
    {
        var doc = Sample();
        var manual = doc.NewSpeaker();
        doc.SetSpeaker([doc.Entries[0]], manual);
        manual.Name = "佐藤";
        var voice = doc.Speaker(2);
        voice.Name = "佐藤";
        var (kept, _) = doc.MergeSpeakers(voice, manual);
        Assert.Equal(2, kept.Id);
        Assert.Equal("佐藤", kept.Name);
        Assert.Equal(kept, doc.Entries[0].Speaker);
    }

    [Fact]
    public void 話者ごとの発言時間と割合()
    {
        var doc = Sample(); // 話者1: 3+3 秒、自分: 1 秒、話者2: 3 秒
        var talk = doc.TalkTimes();
        Assert.Equal(1, talk[0].Speaker.Id);
        Assert.Equal(TimeSpan.FromSeconds(6), talk[0].Time);
        Assert.Equal(0.6, talk[0].Share, 3);
        doc.UpdateTalkTimes();
        Assert.Equal("0:06 ・ 60%", doc.Speaker(1).Share);
        Assert.Contains("- 発言時間: 話者1 0:06 (60%)、話者2 0:03 (30%)、自分 0:01 (10%)", doc.ToMarkdown());
        // まとめて認識し直しても合計は変わらない
        doc.Revise(new TranscriptRevision([1, 3], "本日の会議を始めます。まず進捗を確認します。", "asr", ""));
        Assert.Equal(TimeSpan.FromSeconds(6), doc.TalkTimes()[0].Time);
    }

    [Fact]
    public void 新しい話者は聞き分けの番号と重ならない()
    {
        var doc = Sample();
        var added = doc.NewSpeaker();
        Assert.True(added.Id >= 1000);
        Assert.Equal("話者3", added.Name);
    }

    [Fact]
    public void 日本語以外の発言は日本語訳を原文とセットで書き出す()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "Let's get started.", 1, "en"));
        doc.Add(Start, new TranscriptSegment("win", 4, 6, 1, "First, the schedule.", 2, "en"));
        doc.Add(Start, new TranscriptSegment("mic", 7, 9, 0, "了解しました。", 3, "ja"));
        Assert.True(doc.Entries[0].NeedsTranslation);
        Assert.False(doc.Entries[2].NeedsTranslation);
        doc.Entries[0].Translation = "始めましょう。";
        doc.Entries[1].Translation = "まず、日程です。";

        var text = doc.ToPlainText();
        Assert.Contains("[14:00:01] 話者1: Let's get started. First, the schedule.", text);
        Assert.Contains("    (訳) 始めましょう。まず、日程です。", text);
        Assert.DoesNotContain("(訳) 了解", text);
        Assert.Contains("> 訳: 始めましょう。まず、日程です。", doc.ToMarkdown());
        // 訳を含めない
        Assert.DoesNotContain("(訳)", doc.ToPlainText(new MinutesDocument.ExportOptions(IncludeTranslation: false)));
    }

    [Fact]
    public void 本文が変わると訳し直す()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 3, 1, "Hello.", 1, "en"));
        var entry = doc.Entries[0];
        entry.Translation = "こんにちは。";
        Assert.True(entry.HasTranslation);
        entry.Text = "Hello, everyone.";
        Assert.Null(entry.Translation);
        entry.Translation = "皆さん、こんにちは。";
        entry.ShowTranslation = false;
        Assert.False(entry.HasTranslation);
    }

    [Fact]
    public void ファイルの文字起こしは先頭からの時間で示す()
    {
        var doc = new MinutesDocument { FromFile = true, Source = "会議.mp4", Duration = TimeSpan.FromSeconds(3725) };
        doc.Add(Start, new TranscriptSegment("win", 65.4, 70, 1, "始めます。", 1));
        doc.Add(Start, new TranscriptSegment("win", 3700, 3710, 1, "終わります。", 2));
        Assert.Equal("01:05", doc.Entries[0].TimeText);
        Assert.Equal("1:01:40", doc.Entries[1].TimeText);
        var md = doc.ToMarkdown();
        Assert.Contains("- ファイル: 会議.mp4", md);
        Assert.Contains("- 長さ: 1:02:05", md);
        Assert.DoesNotContain("日時", md);
    }
}
