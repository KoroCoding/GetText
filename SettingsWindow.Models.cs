using System.Windows;
using System.Windows.Media;

namespace GetText;

/// <summary>設定 → モデルとセットアップ の「モデルの一覧」(1 つずつ入れる・消す)。一覧と文は ModelCatalog・ModelTexts (Mac 版と共有)。</summary>
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
        ModelList.ItemsSource = models.Select(s => new ModelRow(s, runtime && _modelCancel == null)).ToList(); // (見本では押しても何もしない)
    }

    private async void ModelInstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelRow { Status: var status } || _modelCancel != null || _demoModels != null) return;
        if (MessageBox.Show(this, ModelTexts.ConfirmInstall(status), "モデルを入れる", MessageBoxButton.YesNo,
                status.Model.NonCommercial ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        _modelCancel = new CancellationTokenSource();
        Closing += OnClosingDuringInstall;
        ModelErrorBar.Visibility = Visibility.Collapsed;
        ModelProgressPanel.Visibility = Visibility.Visible;
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
            ModelErrorBar.Background = Theme.Brush(p => p.CriticalSubtle);
            ModelErrorBar.Visibility = Visibility.Visible;
        }
        finally
        {
            Closing -= OnClosingDuringInstall;
            _modelCancel.Dispose();
            _modelCancel = null;
            ModelProgressPanel.Visibility = Visibility.Collapsed;
            UpdateStatus();
            RefreshModels();
        }
    }

    private void ModelCancel_Click(object sender, RoutedEventArgs e) => _modelCancel?.Cancel();

    private void OnClosingDuringInstall(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_modelCancel == null) return;
        if (MessageBox.Show(this, "モデルを入れている途中です。中止して閉じますか？\n(続きは、あとで「続きを入れる」から入れられます)", "モデルを入れる",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _modelCancel.Cancel();
    }

    private void ModelRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelRow { Status: var status } || _modelCancel != null || _demoModels != null) return;
        if (MessageBox.Show(this, ModelTexts.ConfirmRemove(status), "モデルを消す", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            ModelCatalog.Remove(status.Model);
            ModelErrorBar.Visibility = Visibility.Collapsed;
        }
        catch (System.IO.IOException ex)
        {
            ModelErrorText.Text = ex.Message;
            ModelErrorBar.Background = Theme.Brush(p => p.WarningSubtle);
            ModelErrorBar.Visibility = Visibility.Visible;
        }
        UpdateStatus();
        RefreshModels();
    }

    private void ModelErrorClose_Click(object sender, RoutedEventArgs e) => ModelErrorBar.Visibility = Visibility.Collapsed;
}

/// <summary>モデルの一覧の 1 行の表示。</summary>
public sealed class ModelRow(ModelStatus status, bool canAct)
{
    public ModelStatus Status { get; } = status;
    public string Name => Status.Model.Name;
    public string Purpose => Status.Model.Purpose;
    public string Meta => ModelTexts.Meta(Status);
    public Brush MetaForeground => Theme.Brush(p => ModelTexts.MetaForeground(Status.Model, p));
    public string StateLabel => Status.StateLabel;
    public Brush StateBackground => Theme.Brush(p => ModelTexts.StateBackground(Status.State, p));
    public Brush StateForeground => Theme.Brush(p => ModelTexts.StateForeground(Status.State, p));
    public string InstallLabel => ModelTexts.InstallLabel(Status);
    public Visibility InstallVisibility => Status.State != ModelState.Installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => Status.State != ModelState.Missing ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAct => canAct;
    public string AccessibleName => ModelTexts.AccessibleName(Status);
}
