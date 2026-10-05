using System.IO;

namespace GetText.Tests;

/// <summary>見直しで直した不具合が戻らないことを確かめる。</summary>
public class MinutesFixTests
{
    private static readonly DateTime Start = new(2026, 10, 2, 10, 0, 0);

    [Fact]
    public void 新しい話者の名前はほかの話者と重ならない()
    {
        var doc = new MinutesDocument();
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "一", 1));
        doc.Add(Start, new TranscriptSegment("win", 3, 4, 3, "三", 2)); // 話者1 と 話者3 (話者2 はいない)
        var added = doc.NewSpeaker();
        Assert.Equal(1, doc.Speakers.Count(s => s.Name == added.Name));
        Assert.Null(doc.SameNameAs(added));
    }

    [Fact]
    public void テキストの見出しでウィンドウ名の中のハイフンを消さない()
    {
        var doc = new MinutesDocument { Source = "講演の動画 - ブラウザ" };
        doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "こんにちは", 1));
        var text = doc.ToPlainText();
        Assert.Contains("音声: 講演の動画 - ブラウザ", text);
        Assert.DoesNotContain("- 音声:", text);
        Assert.Contains("- 音声: 講演の動画 - ブラウザ", doc.ToMarkdown()); // Markdown は箇条書きのまま
    }

    [Fact]
    public void 音声の_WAV_が無く_m4a_があればそちらを使う()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gettext_fix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "議事録_音声1.wav");
            File.WriteAllBytes(Path.ChangeExtension(wav, ".m4a"), [0]); // 変換だけ終わって WAV は消えた
            var doc = new MinutesDocument();
            doc.Add(Start, new TranscriptSegment("win", 1, 2, 1, "こんにちは", 1));
            doc.AudioParts.Add(new AudioPart(wav, Start, 10));
            var loaded = new MinutesDocument();
            loaded.Load(MinutesFile.FromJson(doc.ToFile().ToJson()));
            Assert.Equal(Path.ChangeExtension(wav, ".m4a"), loaded.AudioParts[0].Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 録音を外した音があっても残りの音は保存し続ける()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gettext_rec_{Guid.NewGuid():N}.wav");
        try
        {
            using (var rec = new SessionRecorder(path, Start, ["win", "mic"]))
            {
                rec.Add("win", new byte[3200]);  // 0.1 秒
                rec.Add("mic", new byte[1600]);  // 0.05 秒
                rec.RemoveSource("mic");         // マイクを抜いた
                rec.Add("win", new byte[3200]);  // ウィンドウの音だけで続ける
                Assert.Equal(TimeSpan.FromSeconds(0.2), rec.Duration);
            }
            Assert.Equal(44 + 6400, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
