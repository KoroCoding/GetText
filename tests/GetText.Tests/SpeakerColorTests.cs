using System.Windows.Media;

namespace GetText.Tests;

/// <summary>話者の名前の文字の色が、ライト・ダークどちらの背景でも読めることを確かめる。</summary>
public class SpeakerColorTests
{
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 名前の文字は背景との明るさの比が_4_5_以上(bool dark)
    {
        foreach (var hex in MinutesDocument.Palette.Append("#8A8A8A"))
        {
            var text = SpeakerColors.ForText(Parse(hex), dark);
            Assert.True(SpeakerColors.ContrastOnTheme(text, dark) >= 4.5, $"{hex} → {text} ({(dark ? "ダーク" : "ライト")})");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 直した色どうしも見分けられる(bool dark)
    {
        var colors = MinutesDocument.Palette.Select(h => SpeakerColors.ForText(Parse(h), dark)).ToList();
        for (int i = 0; i < colors.Count; i++)
            for (int j = i + 1; j < colors.Count; j++)
            {
                double distance = DeltaE(colors[i], colors[j]); // 見た目の色の差 (CIELAB)。25 以上ならはっきり見分けられる
                Assert.True(distance >= 25, $"{MinutesDocument.Palette[i]} と {MinutesDocument.Palette[j]} が近すぎる ({colors[i]} / {colors[j]} ・ 差 {distance:0.0})");
            }
    }

    private static double DeltaE(Color a, Color b)
    {
        static (double L, double A, double B) Lab(Color c)
        {
            static double Lin(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
            double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
            double x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * bl, z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }
        var (p, q) = (Lab(a), Lab(b));
        return Math.Sqrt(Math.Pow(p.L - q.L, 2) + Math.Pow(p.A - q.A, 2) + Math.Pow(p.B - q.B, 2));
    }

    [Fact]
    public void 足りている色は変えない()
    {
        var black = Color.FromRgb(0x20, 0x20, 0x20);
        Assert.Equal(black, SpeakerColors.ForText(black, dark: false));
    }
}
