namespace Recodio;

// Shared browser list for yt-dlp / spotDL --cookies-from-browser.
// yt-dlp reads the browser profile on disk (Brave = Chromium cookie DB).
public static class BrowserCookies
{
    // Display label + yt-dlp key (empty key = disabled).
    public static readonly (string Label, string Key)[] Options =
    [
        ("No usar (publico)", ""),
        ("Brave", "brave"),
        ("Chrome", "chrome"),
        ("Edge", "edge"),
        ("Firefox", "firefox"),
        ("Opera", "opera"),
        ("Chromium", "chromium"),
    ];

    public static int IndexOfKey(string? key)
    {
        key = (key ?? "").Trim().ToLowerInvariant();
        for (var i = 0; i < Options.Length; i++)
        {
            if (Options[i].Key == key) return i;
        }
        return 0;
    }

    public static string KeyAt(int index) =>
        index >= 0 && index < Options.Length ? Options[index].Key : "";

    public static string[] Labels() => Options.Select(o => o.Label).ToArray();

    /// <summary>
    /// True if a Brave user-data folder exists on this machine (profile may still be locked).
    /// </summary>
    public static bool IsBraveInstalled()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "User Data");
        return Directory.Exists(path);
    }

    /// <summary>
    /// Prefer Brave when installed and no preference was saved yet.
    /// </summary>
    public static string ResolveDefault(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim().ToLowerInvariant();
        return IsBraveInstalled() ? "brave" : "";
    }

    public static string HintFor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Sin cookies: solo contenido publico.";
        return $"Usa cookies de {key}. Si falla DPAPI, Recodio reintenta sin cookies y puede "
             + $"cachear en:\n{CookieManager.CookiesFilePath}";
    }
}
