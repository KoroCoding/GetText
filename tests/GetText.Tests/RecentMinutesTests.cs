using System.IO;

namespace GetText.Tests;

public class RecentMinutesTests
{
    [Fact]
    public void 名前から日時と元のファイルを読む()
    {
        var any = new DateTime(2000, 1, 1);
        Assert.Equal("10月3日 (土) 14:00 ・ 記録", RecentMinutes.Label("議事録_20261003_140012.json", any));
        Assert.Equal("10月3日 (土) 9:05 ・ 定例_会議", RecentMinutes.Label("議事録_定例_会議_20261003_090501.md", any));
        Assert.Equal("1月1日 (土) 0:00 ・ メモ", RecentMinutes.Label("議事録_メモ.json", any)); // 日時の無い名前は更新日時
    }

    [Fact]
    public void 新しい順に_議事録データを優先して並べる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gettext_recent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            void Make(string name, int minutesAgo)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, "x");
                File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-minutesAgo));
            }
            Make("議事録_20261001_100000.md", 30);
            Make("議事録_20261001_100000.json", 30); // 同じ名前の .json があれば .md は出さない
            Make("議事録_20261002_100000.md", 20);   // 古い版 (Markdown だけ)
            Make("議事録_20261003_100000.json", 10);
            Make("メモ.txt", 1);                     // 議事録ではない
            var list = RecentMinutes.List(dir, 8);
            Assert.Equal(["議事録_20261003_100000.json", "議事録_20261002_100000.md", "議事録_20261001_100000.json"],
                list.Select(x => Path.GetFileName(x.Path)));
            Assert.Single(RecentMinutes.List(dir, 1));
            Assert.Empty(RecentMinutes.List(Path.Combine(dir, "無い"), 8));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
