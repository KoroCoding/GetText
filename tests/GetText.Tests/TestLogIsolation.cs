using System.IO;
using System.Runtime.CompilerServices;
using GetText.Plugins;

namespace GetText.Tests;

/// <summary>
/// テストが利用者の本物のログ (%LOCALAPPDATA%\GetText\logs) に書かないよう、どのテストよりも前に
/// PluginLog を一時フォルダへ向ける。製品の既定の場所は変えない (テストの中だけ)。
/// </summary>
internal static class TestLogIsolation
{
    public static readonly string RealLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs");

    public static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "gettext-tests-" + Guid.NewGuid().ToString("N"));

    public static string LogPath => Path.Combine(TempRoot, "logs", "plugins.log");

    /// <summary>テストを始める前の本物のログ (名前 → 大きさ・更新日時)。</summary>
    public static IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> RealLogsBefore { get; private set; } =
        new Dictionary<string, (long, DateTime)>();

#pragma warning disable CA2255 // テストのアセンブリなので、最初に一度だけ動かす
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        RealLogsBefore = SnapshotRealLogs();
        PluginLog.Path = LogPath;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(TempRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    public static Dictionary<string, (long Length, DateTime LastWriteUtc)> SnapshotRealLogs()
    {
        var result = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(RealLogDir)) return result;
        foreach (var file in new DirectoryInfo(RealLogDir).EnumerateFiles("*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(RealLogDir, file.FullName)] = (file.Length, file.LastWriteTimeUtc);
        return result;
    }
}

/// <summary>ほかのテストと並べずに (並べて動くテストが終わってから) 本物のログが変わっていないことを確かめる。</summary>
[CollectionDefinition("LogIsolation", DisableParallelization = true)]
public sealed class LogIsolationCollection;

[Collection("LogIsolation")]
public class TestLogIsolationTests
{
    [Fact]
    public void PluginLog_PointsToTempDirectory_NotRealLogs()
    {
        var path = Path.GetFullPath(PluginLog.Path);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
        Assert.False(path.StartsWith(Path.GetFullPath(TestLogIsolation.RealLogDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"PluginLog.Path が本物のログを指しています: {path}");
    }

    [Fact]
    public void PluginLogWrite_GoesToTempFile_NotRealLog()
    {
        var marker = "log-isolation-" + Guid.NewGuid().ToString("N");
        PluginLog.Write("info", "gettext.test.isolation", marker);

        Assert.Contains(marker, File.ReadAllText(PluginLog.Path));
        var real = Path.Combine(TestLogIsolation.RealLogDir, "plugins.log");
        if (File.Exists(real))
            Assert.DoesNotContain(marker, File.ReadAllText(real));
    }

    [Fact]
    public void Tests_DoNotTouchRealLogDirectory()
    {
        var after = TestLogIsolation.SnapshotRealLogs();
        var changed = after.Where(kv => !TestLogIsolation.RealLogsBefore.TryGetValue(kv.Key, out var before) || before != kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(changed.Count == 0,
            $"テスト中に {TestLogIsolation.RealLogDir} が変わりました: {string.Join(", ", changed)} (GetText 本体を動かしていると、それが書くこともあります)");
    }
}
