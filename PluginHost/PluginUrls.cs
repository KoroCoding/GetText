namespace GetText;

/// <summary>拡張機能が開いてよい URL (https・http だけ。file:・独自のスキーム・ユーザー名入りの URL は開かない)。</summary>
public static class PluginUrls
{
    public static bool IsAllowed(Uri url) =>
        url.IsAbsoluteUri
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(url.UserInfo)
        && !string.IsNullOrEmpty(url.Host);
}
