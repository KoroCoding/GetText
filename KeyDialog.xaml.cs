using System.Windows;

namespace GetText;

public partial class KeyDialog : Window
{
    public string? Key { get; private set; }

    public KeyDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Key = KeyBox.Password.Trim();
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "保存してある DeepL のキーを削除しますか？", "GetText", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Key = null;
        DialogResult = true;
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // ブラウザーを開けなくても続ける
        }
        e.Handled = true;
    }
}
