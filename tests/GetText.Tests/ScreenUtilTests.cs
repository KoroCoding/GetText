using System.Windows;

namespace GetText.Tests;

public class ScreenUtilTests
{
    private static readonly Rect Work = new(0, 0, 1920, 1040); // タスクバーを除いた画面
    private static readonly Size Text = new(480, 600);

    [Fact]
    public void 余裕があれば枠の右に置く()
    {
        var p = ScreenUtil.Beside(new Rect(100, 100, 520, 320), Text, 8, Work);
        Assert.Equal(new Point(628, 100), p);
    }

    [Fact]
    public void 右端では枠の左に回る()
    {
        var p = ScreenUtil.Beside(new Rect(1300, 100, 520, 320), Text, 8, Work);
        Assert.Equal(new Point(1300 - 8 - 480, 100), p);
    }

    [Fact]
    public void 下にはみ出す分は上にずらす()
    {
        var p = ScreenUtil.Beside(new Rect(100, 800, 520, 200), Text, 8, Work);
        Assert.Equal(628, p.X);
        Assert.Equal(1040 - 600, p.Y);
    }

    [Fact]
    public void 左右に入らなければ下に置く()
    {
        // 枠が横に広く、左右どちらにも入らない
        var p = ScreenUtil.Beside(new Rect(200, 50, 1600, 300), Text, 8, Work);
        Assert.Equal(new Point(200, 358), p);
    }

    [Fact]
    public void どこにも入らなければ画面の中に収める()
    {
        var p = ScreenUtil.Beside(new Rect(0, 0, 1900, 1000), Text, 8, Work);
        Assert.True(p.X >= 0 && p.X + Text.Width <= Work.Right);
        Assert.True(p.Y >= 0 && p.Y + Text.Height <= Work.Bottom);
    }

    [Fact]
    public void 画面の外の位置は中に戻す()
    {
        Assert.Equal(new Point(1920 - 480, 0), ScreenUtil.Clamp(new Point(5000, -300), Text, Work));
        Assert.Equal(new Point(0, 1040 - 600), ScreenUtil.Clamp(new Point(-9000, 2000), Text, Work));
    }
}
