using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GetText;

/// <summary>設定 → モデルとセットアップ の「モデルの一覧」(1 つずつ入れる・消す)。一覧と文は ModelCatalog・ModelTexts (Windows 版と共有)。</summary>
public partial class SettingsWindow
{
    private CancellationTokenSource? _modelCancel;
    private IReadOnlyList<ModelStatus>? _demoModels;

    /// <summary>見本の画面: 見本の状態でモデルの一覧を描く。</summary>
    internal void ShowDemoModels(IReadOnlyList<ModelStatus> models)
    {
        _demoModels = models;
        RefreshModels();
    }

    private void RefreshModels()
    {
        bool runtime = _demoModels != null || ModelCatalog.RuntimeInstalled;
        var plugins = PluginRuntime.Plugins.Where(p => p.Manifest != null).Select(p => p.Manifest!);
        var models = _demoModels ?? ModelCatalog.List(plugins: plugins);
        ModelListNote.Text = ModelTexts.Note(runtime);
        ModelList.ItemsSource = models.Select(s => new MacModelRow(s, runtime && _modelCancel == null)).ToList(); // (見本では押しても何もしない)
    }

    private async void ModelInstall_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MacModelRow { Status: var status } || _modelCancel != null || _demoModels != null) return;
        if (!await Dialogs.ConfirmAsync(this, ModelTexts.ConfirmInstall(status), "モデルを入れる", "入れる")) return;
        _modelCancel = new CancellationTokenSource();
        ModelErrorBar.IsVisible = false;
        ModelProgressPanel.IsVisible = true;
        ModelProgressText.Text = $"「{status.Model.Name}」を入れています";
        ModelProgress.IsIndeterminate = true;
        RefreshModels();
        var progress = new Progress<double>(v =>
        {
            ModelProgress.IsIndeterminate = false;
            ModelProgress.Value = v;
        });
        try
        {
            await ModelCatalog.InstallAsync(status.Model, progress, _modelCancel.Token);
        }
        catch (OperationCanceledException)
        {
            ModelProgressText.Text = "中止しました";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            App.Log("ModelInstall", ex);
            ModelErrorText.Text = ex.Message;
            ModelErrorBar.Background = MacTheme.BrushOf(p => p.CriticalSubtle);
            ModelErrorBar.IsVisible = true;
        }
        finally
        {
            _modelCancel.Dispose();
            _modelCancel = null;
            ModelProgressPanel.IsVisible = false;
            _ = UpdateStatusAsync();
            RefreshModels();
        }
    }

    private void ModelCancel_Click(object? sender, RoutedEventArgs e) => _modelCancel?.Cancel();

    private async void ModelRemove_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MacModelRow { Status: var status } || _modelCancel != null || _demoModels != null) return;
        if (!await Dialogs.ConfirmAsync(this, ModelTexts.ConfirmRemove(status), "モデルを消す", "消す")) return;
        try
        {
            ModelCatalog.Remove(status.Model);
            ModelErrorBar.IsVisible = false;
        }
        catch (System.IO.IOException ex)
        {
            ModelErrorText.Text = ex.Message;
            ModelErrorBar.Background = MacTheme.BrushOf(p => p.WarningSubtle);
            ModelErrorBar.IsVisible = true;
        }
        _ = UpdateStatusAsync();
        RefreshModels();
    }

    private void ModelErrorClose_Click(object? sender, RoutedEventArgs e) => ModelErrorBar.IsVisible = false;
}

/// <summary>モデルの一覧の 1 行の表示。</summary>
public sealed class MacModelRow(ModelStatus status, bool canAct)
{
    public ModelStatus Status { get; } = status;
    public string Name => Status.Model.Name;
    public string Purpose => Status.Model.Purpose;
    public string Meta => ModelTexts.Meta(Status);
    public IBrush MetaForeground => MacTheme.BrushOf(p => ModelTexts.MetaForeground(Status.Model, p));
    public string StateLabel => Status.StateLabel;
    public IBrush StateBackground => MacTheme.BrushOf(p => ModelTexts.StateBackground(Status.State, p));
    public IBrush StateForeground => MacTheme.BrushOf(p => ModelTexts.StateForeground(Status.State, p));
    public string InstallLabel => ModelTexts.InstallLabel(Status);
    public bool CanInstall => Status.State != ModelState.Installed;
    public bool CanRemove => Status.State != ModelState.Missing;
    public bool CanAct => canAct;
    public string AccessibleName => ModelTexts.AccessibleName(Status);
}
