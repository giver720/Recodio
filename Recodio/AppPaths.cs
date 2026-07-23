namespace Recodio;

// Config, history and logs live under the user's profile (not next to the exe).
// That avoids OneDrive/file-lock issues when the install folder is synced, and keeps
// portable/install updates from wiping user settings.
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Recodio");

    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recodio");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string LogFile => Path.Combine(LogDir, "watcher.log");
    /// <summary>Optional Netscape cookies.txt — preferred over live browser DPAPI reads.</summary>
    public static string CookiesFile => Path.Combine(DataDir, "cookies.txt");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }

    // One-time move of config/history that lived beside the exe (v1 layout).
    public static void MigrateFromInstallDir(string installDir)
    {
        EnsureDirs();
        TryMigrate(Path.Combine(installDir, "config.json"), ConfigFile);
        TryMigrate(Path.Combine(installDir, "history.json"), HistoryFile);

        var oldLog = Path.Combine(installDir, "watcher.log");
        if (File.Exists(oldLog) && !File.Exists(LogFile))
        {
            try { File.Copy(oldLog, LogFile); } catch { /* ignore */ }
        }
    }

    private static void TryMigrate(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        try { File.Copy(from, to); } catch { /* ignore lock/permission */ }
    }
}
