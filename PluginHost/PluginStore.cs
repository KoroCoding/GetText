using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GetText;

/// <summary>拡張機能をどこまで信頼できるか (画面に出す)。同じプロセスで動くので、どれも GetText と同じ権限を持つ。</summary>
public enum PluginTrust
{
    /// <summary>GetText の開発元が作り、配布の索引の SHA-256 と一致したもの。</summary>
    Official,
    /// <summary>索引で確かめた (verified) と書かれたもの。</summary>
    Verified,
    /// <summary>それ以外 (ファイルから入れたものなど)。</summary>
    Community,
}

public enum PluginSource
{
    /// <summary>GetText の配布物に同梱されたパッケージ。</summary>
    Bundled,
    /// <summary>拡張機能の索引 (plugins-index.json) からダウンロード。</summary>
    Index,
    /// <summary>利用者が選んだファイル。</summary>
    LocalFile,
}

/// <summary>入れた拡張機能の記録 (plugin-state.json)。</summary>
public sealed class PluginRecord
{
    public string Id { get; set; } = "";
    /// <summary>今使う版 (plugins/&lt;id&gt;/&lt;版&gt;)。</summary>
    public string Version { get; set; } = "";
    /// <summary>次に起動したときに切り替える版 (更新)。</summary>
    public string? PendingVersion { get; set; }
    /// <summary>更新の前の版 (新しい版の読み込みで落ちたら戻す。読み込めたら消す)。</summary>
    public string? PreviousVersion { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>次に起動したときに消す。</summary>
    public bool PendingRemoval { get; set; }
    public PluginTrust Trust { get; set; } = PluginTrust.Community;
    public PluginSource Source { get; set; } = PluginSource.LocalFile;
    public string? Sha256 { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    /// <summary>最後の問題 (利用者に見せる。個人のデータは入れない)。</summary>
    public string? LastError { get; set; }
}

public sealed class PluginStateFile
{
    public int Schema { get; set; } = 1;
    public List<PluginRecord> Plugins { get; set; } = [];
    /// <summary>読み込みの途中の拡張機能 (読み込めたら消す。残っていれば前回はその途中で落ちた)。</summary>
    public string? Loading { get; set; }
}

/// <summary>
/// 拡張機能の置き場所と記録。置き場所は %LOCALAPPDATA%\GetText\plugins (Mac は ~/Library/Application Support/GetText/plugins)。
/// GetText のフォルダには書かない。入れる・更新・消すは、次に起動したときに反映する (読み込み中のファイルは消せないため)。
/// </summary>
public sealed class PluginStore
{
    private readonly object _lock = new();
    private PluginStateFile _state = new();
    private bool _loaded;

    /// <summary>記録 (まだ読んでいなければ読む。読まずに書くと、ほかの拡張機能の記録を消してしまうため)。</summary>
    private PluginStateFile State
    {
        get
        {
            if (!_loaded) Load();
            return _state;
        }
    }

    public PluginStore(string root)
    {
        Root = root;
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "plugins");

    public string Root { get; }
    public string StatePath => Path.Combine(Root, "plugin-state.json");
    private string Staging => Path.Combine(Root, ".staging");

    /// <summary>記録が壊れていて作り直した (理由)。</summary>
    public string? RecoveredFrom { get; private set; }

    public string VersionDirectory(string id, string version) => Path.Combine(Root, id, version);

    public string DataDirectory(string id) => Path.Combine(Root, ".data", id);

    public IReadOnlyList<PluginRecord> Records
    {
        get { lock (_lock) return State.Plugins.Select(Clone).ToList(); }
    }

    public PluginRecord? Find(string id)
    {
        lock (_lock) return State.Plugins.FirstOrDefault(p => p.Id == id) is { } r ? Clone(r) : null;
    }

    // 記録の写し (呼び出し元が書き換えても、保存している記録は変わらない)
    private static PluginRecord Clone(PluginRecord r) => new()
    {
        Id = r.Id, Version = r.Version, PendingVersion = r.PendingVersion, PreviousVersion = r.PreviousVersion, Enabled = r.Enabled,
        PendingRemoval = r.PendingRemoval, Trust = r.Trust, Source = r.Source, Sha256 = r.Sha256, InstalledAt = r.InstalledAt, LastError = r.LastError,
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>記録を読む。壊れていたら .bad に残して空から始める (GetText は起動する)。</summary>
    public void Load()
    {
        lock (_lock)
        {
            _loaded = true;
            _state = new PluginStateFile();
            if (!File.Exists(StatePath)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<PluginStateFile>(File.ReadAllText(StatePath), Json);
                if (loaded?.Plugins == null) throw new JsonException("Plugins がありません");
                loaded.Plugins.RemoveAll(p => !PluginManifests.IsValidId(p.Id) || !SemVersion.TryParse(p.Version, out _));
                _state = loaded;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                RecoveredFrom = ex.Message;
                try { File.Copy(StatePath, StatePath + ".bad", overwrite: true); } catch { }
                _state = new PluginStateFile();
            }
        }
    }

    private readonly object _fileLock = new();

    public void Save()
    {
        string text;
        lock (_lock) text = JsonSerializer.Serialize(State, Json);
        // (一時ファイルの名前は決まっているので、別のスレッドから同時に書かない)
        lock (_fileLock) AtomicFile.WriteAllText(StatePath, text);
    }

    private void Update(string id, Action<PluginRecord> change)
    {
        lock (_lock)
        {
            var r = State.Plugins.FirstOrDefault(p => p.Id == id) ?? throw new PluginPackageException($"拡張機能「{id}」は入っていません");
            change(r);
        }
        Save();
    }

    public void SetEnabled(string id, bool enabled) => Update(id, r => { r.Enabled = enabled; r.LastError = enabled ? null : r.LastError; });

    /// <summary>消す (次に起動したときにファイルを消す)。</summary>
    public void Uninstall(string id) => Update(id, r => { r.PendingRemoval = true; r.Enabled = false; });

    /// <summary>消すのを取り消す (再起動の前)。</summary>
    public void CancelUninstall(string id) => Update(id, r => { r.PendingRemoval = false; r.Enabled = true; });

    public void SetError(string id, string? message)
    {
        lock (_lock)
        {
            if (State.Plugins.FirstOrDefault(p => p.Id == id) is not { } r) return;
            r.LastError = message;
        }
        SaveQuietly();
    }

    // ───────── 読み込みの印 (落ちたら次の起動で分かる) ─────────

    public string? CrashedWhileLoading { get; private set; }

    public void BeginLoading(string id)
    {
        lock (_lock) State.Loading = id;
        SaveQuietly();
    }

    /// <param name="loaded">読み込めたか。新しい版を読み込めたら前の版を消し、読み込めなければ次の起動で前の版に戻す。</param>
    public void EndLoading(string id, bool loaded = true)
    {
        lock (_lock)
        {
            if (State.Loading == id) State.Loading = null;
            if (State.Plugins.FirstOrDefault(p => p.Id == id) is { PreviousVersion: { } prev } r)
            {
                if (loaded)
                {
                    TryDelete(VersionDirectory(id, prev));
                    r.PreviousVersion = null;
                }
                else if (IsOlder(prev, r.Version) && Directory.Exists(VersionDirectory(id, prev)))
                {
                    // 新しい版を読み込めなかった: 前の版は消さずに、次の起動で戻す (読みかけのファイルは今は消せないことがある)
                    r.PendingVersion = prev;
                    r.PreviousVersion = null;
                    r.LastError = (r.LastError is { Length: > 0 } e ? e + " ・ " : "") + "次の起動で前の版に戻します";
                }
                else
                {
                    // 戻した前の版も読めなかった: もう行き来しない (入れ直すか、消すかを選んでもらう)
                    r.PreviousVersion = null;
                    TryDelete(VersionDirectory(id, prev));
                    r.LastError = (r.LastError is { Length: > 0 } e ? e + " ・ " : "") + "前の版でも読み込めませんでした。入れ直すか、消してください";
                }
            }
        }
        SaveQuietly();
    }

    private static bool IsOlder(string a, string b) =>
        SemVersion.TryParse(a, out var va) && SemVersion.TryParse(b, out var vb) && va < vb;

    /// <summary>読み込みの印・問題の記録を保存する。保存できなくても (ウイルス対策ソフトが使用中など) 読み込みは続ける。</summary>
    private void SaveQuietly()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Write("error", null, "拡張機能の記録を保存できませんでした: " + ex.Message);
        }
    }

    /// <summary>
    /// 起動したとき (読み込む前) に、前回の続きを片付ける:
    /// 前回読み込みの途中で落ちた拡張機能は、前の版があれば戻し、無ければ止める。消す予定・更新の予定を反映する。作業途中のフォルダを消す。
    /// </summary>
    public void ApplyPending()
    {
        lock (_lock)
        {
            CrashedWhileLoading = State.Loading;
            if (State.Loading is { } crashed && State.Plugins.FirstOrDefault(p => p.Id == crashed) is { } bad)
            {
                if (bad.PreviousVersion is { } prev && Directory.Exists(VersionDirectory(bad.Id, prev)))
                {
                    TryDelete(VersionDirectory(bad.Id, bad.Version));
                    bad.Version = prev;
                    bad.PreviousVersion = null;
                    bad.LastError = "前回この拡張機能の新しい版の読み込み中に問題が発生したため、前の版に戻しました";
                }
                else
                {
                    bad.Enabled = false;
                    bad.LastError = "前回この拡張機能の読み込み中に問題が発生したため、止めました";
                }
            }
            State.Loading = null;
            foreach (var r in State.Plugins.ToList())
            {
                if (r.PendingRemoval)
                {
                    // 拡張機能のファイルと、その設定・記録 (.data) を消す。消せなければ記録を残し、次の起動でまた試す
                    bool removed = TryDelete(Path.Combine(Root, r.Id)) & TryDelete(DataDirectory(r.Id));
                    if (removed) State.Plugins.Remove(r);
                    continue;
                }
                if (r.PendingVersion is { } next && Directory.Exists(VersionDirectory(r.Id, next)))
                {
                    r.PreviousVersion = r.Version;
                    r.Version = next;
                    r.PendingVersion = null;
                }
            }
        }
        TryDelete(Staging);
        Save();
    }

    /// <summary>フォルダを消す (無ければ消せたことにする)。使用中 (ウイルス対策ソフトなど) で消せなければ false。</summary>
    private static bool TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>入れてある拡張機能のマニフェスト (今使う版)。読めないものは理由つき。</summary>
    public List<(PluginRecord Record, PluginManifest? Manifest, string? Error)> ReadInstalled()
    {
        var list = new List<(PluginRecord, PluginManifest?, string?)>();
        foreach (var r in Records)
        {
            if (r.PendingRemoval) continue;
            var file = Path.Combine(VersionDirectory(r.Id, r.Version), PluginManifests.FileName);
            if (!File.Exists(file))
            {
                list.Add((r, null, "plugin.json が見つかりません (フォルダが消されたか、壊れています)"));
                continue;
            }
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                list.Add((r, null, "plugin.json を読めません: " + ex.Message));
                continue;
            }
            var m = PluginManifests.Parse(json, out var error);
            if (m != null && (m.Id != r.Id || m.Version.ToString() != r.Version)) (m, error) = (null, "plugin.json の id・版が、入れたときと違います");
            list.Add((r, m, error));
        }
        return list;
    }

    // ───────── 入れる ─────────

    /// <summary>
    /// パッケージのファイルから入れる: (SHA-256 の確認) → 作業用のフォルダに安全に展開 → マニフェストと互換性の確認 → 置き場所へ移す。
    /// 途中で失敗したら、作業用のフォルダを消して何も入れない。反映は次に起動したとき。
    /// </summary>
    public PluginManifest InstallFromFile(string package, string? expectedSha256, PluginTrust trust, PluginSource source,
        string? expectedId = null, SemVersion? expectedVersion = null, SemVersion? hostVersion = null, string? platform = null)
    {
        var sha = PluginPackages.Sha256(package);
        if (expectedSha256 != null && !string.Equals(sha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException("パッケージの SHA-256 が一致しません (壊れているか、すり替えられています)。入れませんでした");
        Directory.CreateDirectory(Staging);
        var work = Path.Combine(Staging, Guid.NewGuid().ToString("N"));
        try
        {
            PluginPackages.ExtractSafely(package, work);
            var manifestPath = Path.Combine(work, PluginManifests.FileName);
            if (!File.Exists(manifestPath)) throw new PluginPackageException("パッケージに plugin.json がありません");
            var manifest = PluginManifests.Parse(File.ReadAllText(manifestPath), out var error)
                           ?? throw new PluginPackageException(error ?? "plugin.json が正しくありません");
            if (expectedId != null && manifest.Id != expectedId) throw new PluginPackageException($"パッケージの id ({manifest.Id}) が索引 ({expectedId}) と違います");
            if (expectedVersion != null && manifest.Version.CompareTo(expectedVersion) != 0)
                throw new PluginPackageException($"パッケージの版 ({manifest.Version}) が索引 ({expectedVersion}) と違います");
            if (PluginCompatibility.Check(manifest, hostVersion ?? PluginCompatibility.HostVersion, platform ?? PluginCompatibility.CurrentPlatform) is { } why)
                throw new PluginPackageException(why);
            if (manifest.EntryPoint is { } entry && !File.Exists(Path.Combine(work, entry.Assembly)))
                throw new PluginPackageException($"パッケージに {entry.Assembly} がありません");
            // 公式を名乗れるのは、GetText の開発元の id で、公式の索引・同梱のもので SHA-256 を確かめたものだけ
            if (trust == PluginTrust.Official && !(manifest.Id.StartsWith("gettext.", StringComparison.Ordinal) && expectedSha256 != null))
                trust = PluginTrust.Community;

            var target = VersionDirectory(manifest.Id, manifest.Version.ToString());
            lock (_lock)
            {
                var existing = State.Plugins.FirstOrDefault(p => p.Id == manifest.Id);
                if (existing is { PendingRemoval: true } && existing.Version == manifest.Version.ToString() && Directory.Exists(target))
                {
                    // 消す予定にした同じ版を入れ直す: 今のフォルダはそのまま使い、消すのを取り消す (今のフォルダを消さない)
                    existing.PendingRemoval = false;
                    existing.Enabled = true;
                    existing.PendingVersion = null;
                    existing.Trust = trust;
                    existing.Source = source;
                    existing.Sha256 = sha;
                    existing.LastError = null;
                }
                else
                {
                    if (Directory.Exists(target))
                    {
                        if (existing != null && !existing.PendingRemoval && (existing.Version == manifest.Version.ToString() || existing.PendingVersion == manifest.Version.ToString()))
                            throw new PluginPackageException($"「{manifest.Name}」{manifest.Version} はもう入っています");
                        TryDelete(target); // 前に消した版の残り
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(work, target); // 同じドライブの中なので、一度に入れ替わる
                    if (existing == null)
                    {
                        State.Plugins.Add(new PluginRecord
                        {
                            Id = manifest.Id, Version = manifest.Version.ToString(), Enabled = true, Trust = trust, Source = source, Sha256 = sha,
                            InstalledAt = DateTimeOffset.Now,
                        });
                    }
                    else if (existing.PendingRemoval)
                    {
                        // 消す予定だったものを入れ直す: 古い版は次の起動で消す
                        existing.PendingRemoval = false;
                        existing.Enabled = true;
                        existing.PendingVersion = manifest.Version.ToString();
                        existing.Trust = trust;
                        existing.Source = source;
                        existing.Sha256 = sha;
                        existing.LastError = null;
                    }
                    else
                    {
                        // 前に入れた更新待ちの版 (今の版でないもの) は要らない
                        if (existing.PendingVersion is { } oldPending && oldPending != existing.Version && oldPending != manifest.Version.ToString())
                            TryDelete(VersionDirectory(existing.Id, oldPending));
                        existing.PendingVersion = manifest.Version.ToString(); // 更新: 次に起動したとき切り替える
                        existing.Trust = trust;
                        existing.Source = source;
                        existing.Sha256 = sha;
                        existing.LastError = null;
                    }
                }
            }
            Save();
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginPackageException("拡張機能を書き込めませんでした (空き容量・書き込みの権限・ほかのアプリが使用中か確かめてください): " + ex.Message, ex);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// 索引の URL からダウンロードして入れる。https だけ。大きさの上限を超えたら止める。SHA-256 は必ず確かめる。
    /// </summary>
    public async Task<PluginManifest> InstallFromUrlAsync(HttpClient http, Uri url, string sha256, long expectedSize, PluginTrust trust,
        string expectedId, SemVersion expectedVersion, IProgress<double>? progress, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new PluginPackageException("拡張機能は https からだけダウンロードします");
        if (string.IsNullOrWhiteSpace(sha256)) throw new PluginPackageException("索引に SHA-256 がないため、入れられません");
        Directory.CreateDirectory(Staging);
        var file = Path.Combine(Staging, Guid.NewGuid().ToString("N") + PluginPackages.Extension);
        try
        {
            long limit = expectedSize > 0 ? expectedSize : PackageLimits.Default.MaxTotalBytes;
            // (続きは UI のスレッドに戻さない: 最後の展開・SHA-256 は重いので、画面を止めない)
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new PluginPackageException($"ダウンロードできませんでした (HTTP {(int)response.StatusCode})");
                if (response.Content.Headers.ContentLength is long length && length > limit)
                    throw new PluginPackageException("ダウンロードの大きさが索引と違います");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > limit) throw new PluginPackageException("ダウンロードが索引の大きさを超えました");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (expectedSize > 0) progress?.Report(Math.Min(1, total / (double)expectedSize));
                }
                if (expectedSize > 0 && total != expectedSize) throw new PluginPackageException("ダウンロードが途中で切れました (大きさが索引と違います)");
            }
            return InstallFromFile(file, sha256, trust, PluginSource.Index, expectedId, expectedVersion);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginPackageException("ダウンロードできませんでした (ネットワークを確かめてください): " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginPackageException("ダウンロードが時間切れになりました", ex);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}
