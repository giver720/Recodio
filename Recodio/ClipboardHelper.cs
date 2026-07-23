namespace Recodio;

public static class ClipboardHelper
{
    public static string? TryGetUrl(params string[] hostHints)
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            var text = Clipboard.GetText()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Allow bare URLs and a single line that is just a URL.
            var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? text;
            if (!Uri.TryCreate(firstLine, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme is not ("http" or "https")) return null;

            if (hostHints.Length == 0) return firstLine;

            var host = uri.Host.ToLowerInvariant();
            foreach (var hint in hostHints)
            {
                if (host.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return firstLine;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetYouTubeUrl() =>
        TryGetUrl("youtube.com", "youtu.be", "music.youtube.com", "www.youtube.com");

    public static string? TryGetSpotifyUrl() =>
        TryGetUrl("open.spotify.com", "spotify.com", "spotify.link");

    /// <summary>
    /// Any http(s) media URL for yt-dlp — excludes Spotify (that pipeline is spotDL).
    /// </summary>
    public static string? TryGetMediaUrl()
    {
        var any = TryGetUrl();
        if (any == null) return null;
        if (TryGetSpotifyUrl() != null) return null;
        return any;
    }

    public static string? TryGetSpotifyOrYouTubeUrl() =>
        TryGetSpotifyUrl() ?? TryGetYouTubeUrl();
}
