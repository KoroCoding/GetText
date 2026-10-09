using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GetText.Plugins;

namespace GetText;

/// <summary>Windows 版・Mac 版で違う、拡張機能のための画面の部品 (知らせ・ファイルを選ぶ・進み具合など)。</summary>
public interface IPluginHostUi : IPluginUi
{
    /// <summary>GetText の画面の右下に知らせを出す。</summary>
    void ShowNotification(string pluginName, PluginNotification notification);

    /// <summary>拡張機能のコマンド・機能・設定が変わった (ホーム・設定を描き直す)。</summary>
    void ContributionsChanged();

    /// <summary>UI のスレッドで行う。</summary>
    void Post(Action action);
}

/// <summary>読み込んだ (または読み込めなかった) 拡張機能の今の状態 (Extensions の画面・診断に出す)。</summary>
public sealed class LoadedPlugin
{
    public required PluginRecord Record { get; init; }
    public PluginManifest? Manifest { get; init; }
    public bool Loaded { get; set; }
    public string? Error { get; set; }
    /// <summary>読み込んだ (起動した) ときは止めていた (そのときの問題の表示は、有効に戻した後は古い)。</summary>
    public bool DisabledAtLoad { get; init; }
    public TimeSpan LoadTime { get; set; }
    public IGetTextPlugin? Instance { get; set; }
    public PluginContext? Context { get; set; }
}

/// <summary>
/// 拡張機能を読み込んで動かす。起動の後 (ホームを出した後) に呼ぶので、起動は遅くならない。
/// 読み込みの前に「読み込み中」の印を記録し、落ちたら次の起動でその拡張機能を止める (または前の版に戻す)。
/// Safe Mode では何も読み込まない (GetText の基本の機能だけで起動する)。
/// </summary>
public static class PluginRuntime
{
    private static readonly List<LoadedPlugin> _plugins = [];
    private static readonly Dictionary<Type, object> _services = [];
    private static readonly HashSet<string> _builtInCapabilities = [];
    private static IPluginHostUi? _ui;
    /// <summary>ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION (アプリの制御のポリシーでファイルが止められた)。止められたことを伝えるだけで、回避はしない。</summary>
    private const int AppControlBlocked = unchecked((int)0x800711C7);

    public static PluginStore? Store { get; private set; }
    public static bool SafeMode { get; private set; }
    public static IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    /// <summary>前回、読み込みの途中で落ちた拡張機能 (起動したときに知らせる)。</summary>
    public static string? CrashedPlugin { get; private set; }
    public static TimeSpan DiscoveryTime { get; private set; }

    public static IPluginHostUi? Ui => _ui;

    public static event Action? Changed;

    public static void RegisterService<T>(T service) where T : class => _services[typeof(T)] = service;

    public static T? GetService<T>() where T : class => _services.TryGetValue(typeof(T), out var s) ? (T)s : null;

    /// <summary>GetText 本体が持つ機能の名前 (例: "ocr"・"ocr.ai"・"meeting.transcript")。</summary>
    public static void AddBuiltInCapability(string capability) => _builtInCapabilities.Add(capability);

    public static bool HasCapability(string capability) =>
        _builtInCapabilities.Contains(capability)
        || _plugins.Any(p => p.Loaded && p.Manifest!.Capabilities.Contains(capability));

    /// <summary>記録を読み、前回の続きを片付ける (読み込みはしない)。起動の早い段階で呼ぶ (速い)。</summary>
    public static void Prepare(IPluginHostUi ui, bool safeMode, string? root = null)
    {
        _ui = ui;
        SafeMode = safeMode;
        Store = new PluginStore(root ?? PluginStore.DefaultRoot);
        try
        {
            Store.Load();
            Store.ApplyPending();
            CrashedPlugin = Store.CrashedWhileLoading;
            if (Store.RecoveredFrom != null) PluginLog.Write("warn", null, "plugin-state.json が壊れていたので作り直しました");
            if (CrashedPlugin != null) PluginLog.Write("warn", CrashedPlugin, "前回の起動で読み込みの途中に落ちました");
        }
        catch (Exception ex)
        {
            PluginLog.Write("error", null, "拡張機能の記録を準備できませんでした: " + ex.Message);
        }
    }

    /// <summary>入れてある拡張機能を読み込む (有効なものだけ。依存の順)。</summary>
    public static void LoadAll()
    {
        if (Store == null) return;
        var sw = Stopwatch.StartNew();
        _plugins.Clear();
        List<(PluginRecord Record, PluginManifest? Manifest, string? Error)> installed;
        try
        {
            installed = Store.ReadInstalled();
        }
        catch (Exception ex)
        {
            PluginLog.Write("error", null, "拡張機能の一覧を読めませんでした: " + ex.Message);
            return;
        }
        var candidates = new List<PluginManifest>();
        foreach (var (record, manifest, error) in installed)
        {
            var item = new LoadedPlugin { Record = record, Manifest = manifest, Error = error ?? record.LastError, DisabledAtLoad = !record.Enabled };
            _plugins.Add(item);
            if (manifest == null || !record.Enabled || SafeMode) continue;
            if (PluginCompatibility.Check(manifest, PluginCompatibility.HostVersion, PluginCompatibility.CurrentPlatform) is { } why)
            {
                item.Error = why;
                continue;
            }
            candidates.Add(manifest);
        }
        var resolution = PluginDependencies.Resolve(candidates);
        foreach (var (id, why) in resolution.Problems)
            if (_plugins.FirstOrDefault(p => p.Record.Id == id) is { } p) p.Error = why;
        DiscoveryTime = sw.Elapsed;
        foreach (var manifest in resolution.Order)
        {
            var item = _plugins.First(p => p.Record.Id == manifest.Id);
            // 必要な依存が読み込めなかったら、これも読み込まない
            if (manifest.Dependencies.FirstOrDefault(d => !_plugins.Any(p => p.Record.Id == d.Id && p.Loaded)) is { } missing)
            {
                item.Error = $"必要な拡張機能「{missing.Id}」を読み込めませんでした";
                continue;
            }
            Load(item);
        }
        PluginLog.Write("info", null, $"拡張機能 {_plugins.Count(p => p.Loaded)}/{_plugins.Count} 個を読み込みました ({sw.ElapsedMilliseconds} ms{(SafeMode ? "・Safe Mode" : "")})");
        Changed?.Invoke();
        _ui?.ContributionsChanged();
    }

    private static void Load(LoadedPlugin item)
    {
        var manifest = item.Manifest!;
        var dir = Store!.VersionDirectory(manifest.Id, manifest.Version.ToString());
        var sw = Stopwatch.StartNew();
        var context = new PluginContext(manifest, Store.DataDirectory(manifest.Id), _ui);
        item.Context = context;
        Store.BeginLoading(manifest.Id);
        try
        {
            if (manifest.EntryPoint is { } entry)
            {
                var path = Path.GetFullPath(Path.Combine(dir, entry.Assembly));
                if (!path.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("入口のアセンブリがパッケージの外にあります");
                var alc = new PluginLoadContext(manifest.Id, path);
                var assembly = alc.LoadFromAssemblyPath(path);
                var type = assembly.GetType(entry.Type, throwOnError: true)!;
                if (!typeof(IGetTextPlugin).IsAssignableFrom(type)) throw new InvalidOperationException($"{entry.Type} は IGetTextPlugin ではありません");
                var plugin = (IGetTextPlugin)Activator.CreateInstance(type)!;
                plugin.Initialize(context);
                item.Instance = plugin;
            }
            context.Commit();
            item.Loaded = true;
            item.Error = null;
            Store.SetError(manifest.Id, null);
            PluginLog.Write("info", manifest.Id, $"{manifest.Version} を読み込みました ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            context.Rollback();
            item.Loaded = false;
            item.Error = inner is FileLoadException { HResult: AppControlBlocked }
                ? "Windows のアプリの制御 (Smart App Control など) が、この拡張機能のファイルを止めました。署名のある版を入れてください"
                : "読み込めませんでした: " + inner.GetType().Name + ": " + inner.Message;
            Store.SetError(manifest.Id, item.Error);
            PluginLog.Write("error", manifest.Id, item.Error);
        }
        finally
        {
            item.LoadTime = sw.Elapsed;
            Store.EndLoading(manifest.Id, item.Loaded); // (新しい版を読み込めなければ、次の起動で前の版に戻す)
        }
    }

    /// <summary>GetText が終わるとき。各拡張機能の Shutdown を呼ぶ (2 秒で待つのをやめる)。</summary>
    public static void ShutdownAll()
    {
        foreach (var p in _plugins.Where(p => p.Loaded).Reverse())
        {
            var plugin = p.Instance;
            p.Context?.Close();
            if (plugin == null) continue;
            try
            {
                if (!Task.Run(plugin.Shutdown).Wait(TimeSpan.FromSeconds(2)))
                    PluginLog.Write("warn", p.Record.Id, "終わるのに 2 秒より長くかかったので、待たずに終えました");
            }
            catch (Exception ex)
            {
                PluginLog.Write("error", p.Record.Id, "終えるときに問題がありました: " + (ex.InnerException ?? ex).Message);
            }
        }
    }

    // ───────── 読み取りの結果を拡張機能に渡す ─────────

    /// <summary>
    /// 読み取りの画面が新しく読み取ったとき。受け取る拡張機能ごとに、別のスレッドで渡す
    /// (前の分をまだ処理していればこの分は飛ばす。読み取りを遅くしない)。
    /// </summary>
    public static void PublishOcrFrame(OcrFrameEventArgs frame)
    {
        foreach (var p in _plugins)
            if (p.Loaded && p.Context is { HasOcrSubscribers: true } c) c.Deliver(frame);
    }

    public static bool HasOcrSubscribers => _plugins.Any(p => p.Loaded && p.Context is { HasOcrSubscribers: true });

    /// <summary>診断の文 (Extensions の画面の「診断をコピー」。個人のデータ・キーは入れない)。</summary>
    public static string Diagnostics(LoadedPlugin p)
    {
        var m = p.Manifest;
        var lines = new List<string>
        {
            $"拡張機能: {p.Record.Id} {p.Record.Version}{(p.Record.PendingVersion != null ? $" (次の起動で {p.Record.PendingVersion})" : "")}",
            $"GetText: {PluginCompatibility.HostVersion} ・ Plugin API v{PluginApi.Version} ・ {PluginCompatibility.CurrentPlatform} ・ .NET {Environment.Version}",
            $"状態: {(p.Loaded ? "読み込み済み" : p.Record.Enabled ? "読み込んでいない" : "無効")}{(SafeMode ? " (Safe Mode)" : "")} ・ 信頼: {p.Record.Trust} ・ 入手元: {p.Record.Source}",
            $"読み込みの時間: {p.LoadTime.TotalMilliseconds:0} ms",
        };
        if (m != null)
        {
            lines.Add($"必要: Plugin API v{m.ApiVersion} ・ GetText {m.MinHostVersion} 以上 ・ {string.Join(", ", m.Platforms)}");
            if (m.Dependencies.Count > 0)
                lines.Add("依存: " + string.Join(", ", m.Dependencies.Select(d => $"{d.Id} {d.Version} ({(_plugins.Any(x => x.Record.Id == d.Id && x.Loaded) ? "OK" : "無し")})")));
            if (m.Models.Count > 0) lines.Add("モデル: " + string.Join(", ", m.Models));
            if (m.UnknownFields.Count > 0) lines.Add("知らない項目: " + string.Join(", ", m.UnknownFields));
        }
        if (p.Error != null) lines.Add("最後の問題: " + PluginLog.Redact(p.Error));
        return string.Join(Environment.NewLine, lines);
    }

    // ───────── 読み込み (拡張機能ごとのアセンブリの文脈) ─────────

    /// <summary>
    /// 拡張機能のアセンブリを、その拡張機能のフォルダからだけ読む (ほかの場所の同じ名前の DLL は読まない)。
    /// Plugin API (GetText.Plugin.Abstractions) は GetText のものを共有する。外すこと (unload) はしない (再起動で反映)。
    /// </summary>
    private sealed class PluginLoadContext(string name, string mainAssembly) : AssemblyLoadContext("GetText.Plugin:" + name, isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == "GetText.Plugin.Abstractions") return null; // GetText と同じものを使う
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}

/// <summary>拡張機能 1 つ分の、GetText とのつなぎ (IPluginContext)。</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly PluginManifest _manifest;
    private readonly IPluginHostUi? _ui;
    private readonly List<string> _commandIds = [];
    private readonly List<string> _featureIds = [];
    private readonly List<AppCommand> _pendingCommands = [];
    private readonly List<FeatureInfo> _pendingFeatures = [];
    private int _delivering;
    private bool _closed;

    public PluginContext(PluginManifest manifest, string dataDirectory, IPluginHostUi? ui)
    {
        _manifest = manifest;
        _ui = ui;
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        Log = new Logger(manifest.Id);
        Settings = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
    }

    public string PluginId => _manifest.Id;
    public string PluginName => _manifest.Name.For(Language);
    public string DataDirectory { get; }
    public HostInfo Host
    {
        get
        {
            var v = PluginCompatibility.HostVersion;
            return new HostInfo(new Version(v.Major, v.Minor, v.Patch), PluginApi.Version, PluginCompatibility.CurrentPlatform, Language);
        }
    }
    public IPluginLogger Log { get; }
    public IPluginSettings Settings { get; }
    public List<PluginSettingsPage> SettingsPages { get; } = [];
    public List<IPluginSearchProvider> SearchProviders { get; } = [];
    public List<IOcrProvider> OcrProviders { get; } = [];
    public List<IExportProvider> ExportProviders { get; } = [];

    private static string Language => "ja";

    public event EventHandler<OcrFrameEventArgs>? OcrFrameRead;

    public bool HasOcrSubscribers => OcrFrameRead != null && !_closed;

    public void AddCommand(PluginCommand command)
    {
        if (!PluginManifests.IsValidId(_manifest.Id)) return;
        var title = command.Title.For(Language);
        if (IconValidator.HasPictograph(title)) throw new ArgumentException("コマンドの名前に絵文字は使えません: " + title);
        var id = $"plugin:{_manifest.Id}:{command.Id}";
        _pendingCommands.Add(new AppCommand
        {
            Id = id,
            Title = title,
            Subtitle = command.Subtitle?.For(Language),
            Keywords = command.Keywords,
            Category = CommandCategory.Extension,
            Icon = ToIcon(command.Icon),
            IsAvailable = () => !_closed && Safe(command.IsAvailable, false),
            Execute = () => Run(command.Execute, title),
            Publisher = _manifest.Publisher,
        });
    }

    public void AddFeature(PluginFeature feature)
    {
        var name = feature.Name.For(Language);
        if (IconValidator.HasPictograph(name)) throw new ArgumentException("機能の名前に絵文字は使えません: " + name);
        _pendingFeatures.Add(new FeatureInfo
        {
            Id = $"plugin:{_manifest.Id}:{feature.Id}",
            Name = name,
            Description = feature.Description.For(Language),
            Icon = ToIcon(feature.Icon),
            Status = () =>
            {
                var s = Safe(feature.Status, new PluginFeatureStatus(PluginFeatureState.Closed));
                return new FeatureStatus(s.State switch { PluginFeatureState.Active => FeatureState.Active, PluginFeatureState.Open => FeatureState.Open, _ => FeatureState.Closed },
                    s.Label?.For(Language));
            },
            Open = () => Run(feature.Open, name),
            Close = feature.Close is { } close ? () => Run(close, name) : null,
            Local = !_manifest.Permissions.Contains("network"),
            Publisher = _manifest.Publisher,
            // (読み取りの画面の切り替えは、切る (Close) が無いと戻せないので、無ければホームに出す)
            InReadingWindow = feature.Placement == PluginFeaturePlacement.ReadingWindow && feature.Close != null,
        });
    }

    public void AddSettings(PluginSettingsPage page) => SettingsPages.Add(page);

    public void AddSearchProvider(IPluginSearchProvider provider) => SearchProviders.Add(provider);

    public void AddOcrProvider(IOcrProvider provider) => OcrProviders.Add(provider);

    public void AddExportProvider(IExportProvider provider) => ExportProviders.Add(provider);

    public void Notify(PluginNotification notification)
    {
        if (_closed) return;
        _ui?.Post(() => _ui.ShowNotification(PluginName, notification));
    }

    public T? GetService<T>() where T : class => PluginRuntime.GetService<T>();

    public bool HasCapability(string capability) => PluginRuntime.HasCapability(capability);

    /// <summary>読み込めたので、コマンド・機能を GetText に登録する。</summary>
    internal void Commit()
    {
        foreach (var c in _pendingCommands)
        {
            AppCommands.Registry.Register(c);
            _commandIds.Add(c.Id);
        }
        foreach (var f in _pendingFeatures)
        {
            AppCommands.Features.RemoveAll(x => x.Id == f.Id);
            AppCommands.Features.Add(f);
            _featureIds.Add(f.Id);
        }
        _pendingCommands.Clear();
        _pendingFeatures.Clear();
    }

    /// <summary>読み込めなかった: 途中まで登録したものを外す。</summary>
    internal void Rollback()
    {
        _closed = true;
        _pendingCommands.Clear();
        _pendingFeatures.Clear();
        foreach (var id in _commandIds) AppCommands.Registry.Unregister(id);
        AppCommands.Features.RemoveAll(f => _featureIds.Contains(f.Id));
        SettingsPages.Clear();
        SearchProviders.Clear();
        OcrProviders.Clear();
        ExportProviders.Clear();
        OcrFrameRead = null;
    }

    internal void Close()
    {
        _closed = true;
        OcrFrameRead = null;
    }

    internal void Deliver(OcrFrameEventArgs frame)
    {
        var handler = OcrFrameRead;
        if (handler == null || _closed) return;
        if (Interlocked.CompareExchange(ref _delivering, 1, 0) != 0) return; // 前の分を処理中: 飛ばす
        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                handler(this, frame);
            }
            catch (Exception ex)
            {
                Log.Error("読み取りの結果を受け取るときに問題がありました", ex);
            }
            finally
            {
                if (sw.ElapsedMilliseconds > 500) Log.Warn($"読み取りの結果の処理に {sw.ElapsedMilliseconds} ms かかりました");
                Interlocked.Exchange(ref _delivering, 0);
            }
        });
    }

    private void Run(Func<CancellationToken, Task> action, string what)
    {
        if (_closed) return;
        _ = RunAsync();
        async Task RunAsync()
        {
            try
            {
                await action(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"「{what}」の実行中に問題がありました", ex);
                _ui?.Post(() => _ui.ShowNotification(PluginName, new PluginNotification(
                    new LocalizedText($"「{what}」を実行できませんでした"), new LocalizedText(ex.Message), PluginNotificationKind.Error)));
            }
        }
    }

    private T Safe<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            Log.Error("状態を調べるときに問題がありました", ex);
            return fallback;
        }
    }

    private static IconSource ToIcon(PluginIcon icon)
    {
        if (icon.SvgPath is { } svg && IconSource.Svg(svg) is { } custom) return custom;
        return Enum.TryParse<AppIcon>(icon.Name, ignoreCase: true, out var a) && a != AppIcon.None ? a : AppIcon.Extensions;
    }

    private sealed class Logger(string id) : IPluginLogger
    {
        public void Info(string message) => PluginLog.Write("info", id, message);
        public void Warn(string message) => PluginLog.Write("warn", id, message);
        public void Error(string message, Exception? exception = null) =>
            PluginLog.Write("error", id, message + (exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : ""));
    }

    /// <summary>拡張機能の設定の値 (DataDirectory/settings.json。壊れていたら空から)。</summary>
    private sealed class SettingsStore : IPluginSettings
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, string?> _values = [];

        public SettingsStore(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path)) _values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path)) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _values = [];
            }
        }

        public event Action<string>? Changed;

        public string? Get(string key)
        {
            lock (_lock) return _values.GetValueOrDefault(key);
        }

        public void Set(string key, string? value)
        {
            string json;
            lock (_lock)
            {
                if (_values.GetValueOrDefault(key) == value) return;
                _values[key] = value;
                json = JsonSerializer.Serialize(_values);
            }
            try { AtomicFile.WriteAllText(_path, json); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Changed?.Invoke(key);
        }

        public bool GetBool(string key, bool fallback) => bool.TryParse(Get(key), out var b) ? b : fallback;

        public double GetNumber(string key, double fallback) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }
}

/// <summary>
/// 拡張機能のログ (logs/plugins.log)。入れた・更新・止めた・問題などだけを書き、読み取った文字・訳・議事録の文・キーは書かない。
/// </summary>
public static class PluginLog
{
    private static readonly object Lock = new();

    /// <summary>ログのファイル (単体テストでは一時フォルダに変える)。</summary>
    public static string Path { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GetText", "logs", "plugins.log");

    public static void Write(string level, string? pluginId, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
                File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {pluginId ?? "-"}: {Redact(message)}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログが書けなくても続ける
        }
    }

    /// <summary>キー・トークンらしい文字列と、利用者のフォルダ名を伏せる。</summary>
    public static string Redact(string text)
    {
        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length >= 2) text = text.Replace(user, "(ユーザー名)", StringComparison.OrdinalIgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\bbearer\s+\S+", "Bearer (伏せました)");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(key|token|secret|password|authorization)\s*[=:]\s*(?!Bearer \(伏せました\))\S+", "$1=(伏せました)");
        // ファイルのパス (選んだファイルの名前は個人の情報になりうる): Windows の C:\…・\\server\…、Mac の /Users/… など。
        // 例外の文は 'C:\My Files\a.pptx' のように引用符で囲むことが多いので、空白を含む形も伏せる
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(['""])(?:[A-Za-z]:\\|\\\\|/)[^'""]*\1", "$1(パス)$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|]+", "(パス)");
        return System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\w.])/(?:Users|home|private|var|Volumes|tmp)/[^\s""'<>]+", "(パス)");
    }
}
