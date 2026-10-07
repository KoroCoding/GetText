using Avalonia.Interactivity;

namespace GetText;

/// <summary>設定 → 診断 の「開発者向けの API」(127.0.0.1 だけ・トークン必須・既定でオフ)。</summary>
public partial class SettingsWindow
{
    private void LoadDeveloperApi()
    {
        DeveloperApiCheck.IsChecked = _settings.DeveloperApi;
        DeveloperApiPortBox.Text = _settings.DeveloperApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateDeveloperApiStatus();
    }

    private void UpdateDeveloperApiStatus()
    {
        DeveloperApiStatus.Text = DeveloperApiControl.Error
            ?? (DeveloperApiControl.RunningPort is { } port ? $"動いています: http://127.0.0.1:{port}/v1/status" : "オフ");
    }

    private void DeveloperApi_Click(object? sender, RoutedEventArgs e)
    {
        _settings.DeveloperApi = DeveloperApiCheck.IsChecked == true;
        _settings.Save();
        if (!App.DemoMode) DeveloperApiControl.Apply(_settings);
        UpdateDeveloperApiStatus();
    }

    private void DeveloperApiPort_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(DeveloperApiPortBox.Text, out var port) && port is >= 1024 and <= 65535)
        {
            if (port == _settings.DeveloperApiPort) return;
            _settings.DeveloperApiPort = port;
            _settings.Save();
            if (!App.DemoMode) DeveloperApiControl.Apply(_settings);
        }
        DeveloperApiPortBox.Text = _settings.DeveloperApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateDeveloperApiStatus();
    }

    private async void DeveloperApiCopyToken_Click(object? sender, RoutedEventArgs e)
    {
        if (App.DemoMode) return;
        try
        {
            DeveloperApiStatus.Text = await Dialogs.CopyAsync(this, DeveloperApiServer.LoadOrCreateToken())
                ? "トークンをコピーしました (人に見せないでください)"
                : "トークンをコピーできませんでした";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            DeveloperApiStatus.Text = "トークンを読めませんでした: " + ex.Message;
        }
    }

    private async void DeveloperApiRenewToken_Click(object? sender, RoutedEventArgs e)
    {
        if (App.DemoMode) return;
        if (!await Dialogs.ConfirmAsync(this, "トークンを作り直しますか？\n\n前のトークンを使っているスクリプトは使えなくなります。", "開発者向けの API", "作り直す")) return;
        try
        {
            DeveloperApiControl.RenewToken(_settings);
            DeveloperApiStatus.Text = "トークンを作り直しました";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            DeveloperApiStatus.Text = "トークンを作り直せませんでした: " + ex.Message;
        }
    }
}
