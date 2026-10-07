using GetText.Plugins;

namespace GetText.Tests;

/// <summary>拡張機能・Quick OCR から使う読み取り: 方式の選び方 (PC の外に送る方式は指定したときだけ) と、結果の形。</summary>
public class HostOcrServiceTests
{
    private sealed class FakeProvider(string id, bool local = true, bool available = true) : IOcrProvider
    {
        public int Calls;
        public string Id => id;
        public LocalizedText Name => id;
        public bool IsLocal => local;
        public bool IsAvailable => available;

        public Task<OcrResult> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new OcrResult([new OcrResultLine(id, 0, 0, 1, 1)], id));
        }
    }

    private static readonly byte[] Image = new byte[4 * 4 * 4];

    [Fact]
    public async Task UsesPreferredThenFallsBackToAvailableLocal()
    {
        var ai = new FakeProvider("ai", available: false);
        var os = new FakeProvider("windows");
        var service = new HostOcrService([ai, os], () => "ai");
        Assert.Equal("windows", (await service.RecognizeAsync(Image, 4, 4, null, CancellationToken.None)).ProviderId);

        var ai2 = new FakeProvider("ai");
        var service2 = new HostOcrService([ai2, os], () => "ai");
        Assert.Equal("ai", (await service2.RecognizeAsync(Image, 4, 4, null, CancellationToken.None)).ProviderId);
    }

    [Fact]
    public async Task NeverPicksOnlineProviderUnlessAsked()
    {
        var cloud = new FakeProvider("cloud", local: false);
        var os = new FakeProvider("windows");
        var service = new HostOcrService([cloud, os], () => "cloud");
        Assert.Equal("windows", (await service.RecognizeAsync(Image, 4, 4, null, CancellationToken.None)).ProviderId);
        Assert.Equal(0, cloud.Calls);
        Assert.Equal("cloud", (await service.RecognizeAsync(Image, 4, 4, "cloud", CancellationToken.None)).ProviderId);
    }

    [Fact]
    public async Task ExplicitUnavailableOrUnknownProviderFails()
    {
        var service = new HostOcrService([new FakeProvider("ai", available: false)], () => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(Image, 4, 4, "ai", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(Image, 4, 4, "nothing", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(Image, 4, 4, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeAsync(new byte[3], 4, 4, null, CancellationToken.None));
    }

    [Fact]
    public void ResultsJoinWordsLikeTheReadingWindowAndKeepSpans()
    {
        var spans = new List<OcrSpan> { new("日本", 0, 0, 20, 10), new("語", 20, 0, 30, 10) };
        var result = OcrResults.From([new OcrLineData(["日本", "語"], 0, 0, 30, 10, spans)], "test");
        var line = Assert.Single(result.Lines);
        Assert.Equal("日本語", line.Text);
        Assert.Equal(2, line.Spans!.Count);
        Assert.Equal("日本語", result.Text);
    }
}
