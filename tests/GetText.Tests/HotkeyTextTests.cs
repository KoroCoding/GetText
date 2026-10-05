using System.Windows.Input;

namespace GetText.Tests;

public class HotkeyTextTests
{
    [Theory]
    [InlineData("Ctrl+Alt+C", ModifierKeys.Control | ModifierKeys.Alt, Key.C)]
    [InlineData("ctrl + shift + F9", ModifierKeys.Control | ModifierKeys.Shift, Key.F9)]
    [InlineData("Alt+1", ModifierKeys.Alt, Key.D1)]
    public void 読める(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal(modifiers, m);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("C")]            // Ctrl も Alt も無い (普段の入力とぶつかる)
    [InlineData("Shift+C")]
    [InlineData("Ctrl+Alt")]     // キーが無い
    [InlineData("Ctrl+C+V")]     // キーが 2 つ
    [InlineData("Ctrl+Nothing")]
    public void 使えないキーは読まない(string text) => Assert.False(HotkeyText.TryParse(text, out _, out _));

    [Fact]
    public void 文字にして読み戻すと同じ()
    {
        var text = HotkeyText.Format(ModifierKeys.Control | ModifierKeys.Alt, Key.D7);
        Assert.Equal("Ctrl+Alt+7", text);
        Assert.True(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal((ModifierKeys.Control | ModifierKeys.Alt, Key.D7), (m, k));
        Assert.Equal("Ctrl + Alt + 7", HotkeyText.Display(text));
    }

    [Fact]
    public void 設定が無い_読めないときは既定のキー()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl+Alt+C", HotkeyText.For(settings, "copy-ocr"));
        settings.Hotkeys["copy-ocr"] = "壊れた値";
        Assert.Equal("Ctrl+Alt+C", HotkeyText.For(settings, "copy-ocr"));
        settings.Hotkeys["copy-ocr"] = "Ctrl+Shift+F1";
        Assert.Equal("Ctrl+Shift+F1", HotkeyText.For(settings, "copy-ocr"));
    }
}
