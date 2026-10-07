using System.Text.Json;

namespace GetText.Tests;

/// <summary>設定の引き継ぎ (前の版の Topmost → 読み取りの画面の TextTopmost)・窓ごとの常に手前・開発者向けの API の既定。保護された画面の見分け。</summary>
public class SettingsMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldTopmostMovesToTextTopmost(bool old)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>($$"""{ "Topmost": {{(old ? "true" : "false")}} }""")!;
        settings.Normalize();
        Assert.Equal(old, settings.TextTopmost);
        Assert.Null(settings.LegacyTopmost);
        // 書くときは新しい名前だけ
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("\"Topmost\"", json);
        Assert.Contains("\"TextTopmost\"", json);
    }

    [Fact]
    public void NewSettingsHaveSafeDefaults()
    {
        var settings = new AppSettings();
        settings.Normalize();
        Assert.True(settings.TextTopmost);
        Assert.False(settings.MinutesTopmost);
        Assert.False(settings.RecorderTopmost);
        Assert.False(settings.DeveloperApi); // 既定でオフ
        Assert.Equal(47390, settings.DeveloperApiPort);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(70000)]
    public void BadApiPortFallsBack(int port)
    {
        var settings = new AppSettings { DeveloperApiPort = port };
        settings.Normalize();
        Assert.Equal(47390, settings.DeveloperApiPort);
    }

    [Fact]
    public void UniformFramesAreRecognizedAsProtected()
    {
        var black = new byte[200 * 100 * 4];
        Assert.True(ProtectedContent.IsUniform(black, 200, 100));
        var text = (byte[])black.Clone();
        for (int x = 20; x < 180; x++)
            for (int y = 40; y < 50; y++)
            {
                int i = (y * 200 + x) * 4;
                text[i] = text[i + 1] = text[i + 2] = 255;
            }
        Assert.False(ProtectedContent.IsUniform(text, 200, 100));
        Assert.False(ProtectedContent.IsUniform(new byte[3], 200, 100));

        // 保護された画面とみなすのは暗い一色だけ (白い一色は白紙)
        Assert.True(ProtectedContent.LooksProtected(black, 200, 100));
        var white = new byte[200 * 100 * 4];
        Array.Fill(white, (byte)255);
        Assert.True(ProtectedContent.IsUniform(white, 200, 100));
        Assert.False(ProtectedContent.LooksProtected(white, 200, 100));
        Assert.False(ProtectedContent.LooksProtected(text, 200, 100));
    }
}
