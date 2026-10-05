using System.IO;

namespace GetText.Tests;

public class MinutesNoteAudioTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 14, 0, 0);

    private static MinutesDocument Sample()
    {
        var doc = new MinutesDocument { Source = "定例ミーティング" };
        doc.Add(Start, new TranscriptSegment("win", 5, 8, 1, "本日の会議を始めます。", 1));
        doc.Add(Start, new TranscriptSegment("win", 9, 12, 1, "資料を共有します。", 2));
        doc.Speaker(1).Name = "田中";
        doc.AddNote(Start.AddSeconds(8.5), "はい");   // 相づちと同じ言葉でもメモは消さない
        return doc;
    }

    [Fact]
    public void メモは時刻順に入り話者の一覧には出ない()
    {
        var doc = Sample();
        Assert.Equal(["本日の会議を始めます。", "はい", "資料を共有します。"], doc.Entries.Select(e => e.Text));
        Assert.True(doc.Entries[1].IsNote);
        Assert.False(doc.Entries[1].IsFiller);
        Assert.DoesNotContain(doc.Speakers, s => s.Name == MinutesDocument.NoteName);
        Assert.Equal(TimeSpan.FromSeconds(8.5), doc.Entries[1].Offset);
    }

    [Fact]
    public void メモは段落をまとめずに印を付けて書き出す()
    {
        var md = Sample().ToMarkdown(new MinutesDocument.ExportOptions(ExcludeFillers: true));
        Assert.Contains("**[14:00:05] 田中**: 本日の会議を始めます。", md);
        Assert.Contains("**📝 [14:00:08] メモ**: はい", md);
        Assert.Contains("**[14:00:09] 田中**: 資料を共有します。", md);
        Assert.Contains("📝 [14:00:08] メモ: はい", Sample().ToPlainText());
        Assert.Contains("📝 [14:00:08] メモ: はい", Sample().SummaryTranscript());
        Assert.DoesNotContain("メモ", Sample().ToSubtitles(vtt: false)); // 字幕には入れない
    }

    [Theory]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData("json")]
    public void メモは読み戻せる(string kind)
    {
        var doc = Sample();
        doc.Entries[1].IsMarked = true;
        var file = kind switch
        {
            "md" => MinutesFile.FromText(doc.ToMarkdown()),
            "txt" => MinutesFile.FromText(doc.ToPlainText()),
            _ => MinutesFile.FromJson(doc.ToFile().ToJson()),
        };
        var loaded = new MinutesDocument();
        loaded.Load(file);
        Assert.Equal([false, true, false], loaded.Entries.Select(e => e.IsNote));
        Assert.True(loaded.Entries[1].IsMarked);
        Assert.Equal("はい", loaded.Entries[1].Text);
        Assert.Equal(["田中"], loaded.Speakers.Select(s => s.Name));
    }

    [Fact]
    public void 保存した音声から発言の位置を決める()
    {
        var doc = Sample();
        doc.AudioParts.Add(new AudioPart(@"C:\m\音声1.m4a", Start, 30));
        doc.AudioParts.Add(new AudioPart(@"C:\m\音声2.m4a", Start.AddMinutes(10), 60));
        doc.Add(Start, new TranscriptSegment("win", 615, 618, 1, "再開します。", 3));
        Assert.Equal((@"C:\m\音声1.m4a", TimeSpan.FromSeconds(9)), doc.AudioAt(doc.Entries[2]));
        Assert.Equal((@"C:\m\音声2.m4a", TimeSpan.FromSeconds(15)), doc.AudioAt(doc.Entries[3]));
        doc.Add(Start, new TranscriptSegment("win", 300, 302, 1, "音声を保存していない間の発言", 4));
        Assert.Null(doc.AudioAt(doc.Entries.First(e => e.Text.StartsWith("音声を保存していない"))));
        // 議事録データに残る
        var loaded = new MinutesDocument();
        loaded.Load(MinutesFile.FromJson(doc.ToFile().ToJson()));
        Assert.Equal(2, loaded.AudioParts.Count);
        Assert.Equal(@"C:\m\音声2.m4a", loaded.AudioAt(loaded.Entries.First(e => e.Text == "再開します。"))!.Value.Path);
    }

    [Fact]
    public void 録音はウィンドウとマイクを同じ位置で混ぜて_WAV_にする()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gettext_rec_{Guid.NewGuid():N}.wav");
        try
        {
            using (var rec = new SessionRecorder(path, Start, ["win", "mic"]))
            {
                rec.Add("win", Pcm(1600, 1000));   // 0.1 秒
                rec.Add("mic", Pcm(800, 500));     // 0.05 秒 (先に届いた分だけ混ぜる)
                Assert.Equal(TimeSpan.FromSeconds(0.05), rec.Duration);
                rec.Add("mic", Pcm(400, 32000));   // 混ぜると上限を超える → 32767 に抑える
            }
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(16000, BitConverter.ToInt32(bytes, 24));
            int dataBytes = BitConverter.ToInt32(bytes, 40);
            Assert.Equal(1600 * 2, dataBytes);                    // 閉じるときに残りも書く
            Assert.Equal(44 + dataBytes, bytes.Length);
            Assert.Equal(1500, BitConverter.ToInt16(bytes, 44));                 // 1000 + 500
            Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 44 + 900 * 2)); // 1000 + 32000 → 上限
            Assert.Equal(1000, BitConverter.ToInt16(bytes, 44 + 1500 * 2));      // マイクが無い分はウィンドウだけ
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Pcm(int samples, short value)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            b[i * 2] = (byte)value;
            b[i * 2 + 1] = (byte)(value >> 8);
        }
        return b;
    }
}
