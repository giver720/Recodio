using System.Diagnostics;

namespace Recodio;

// Keeps the two downloader backends current. YouTube breaks yt-dlp's extractors all the
// time, and spotDL rides on yt-dlp underneath - an outdated pair is the #1 cause of
// "everything suddenly fails" reports, so the app refreshes both on a daily background
// check plus a manual button.
public static class ToolUpdater
{
    public record UpdateResult(bool Updated, string Summary);

    // yt-dlp's standalone exe self-updates in place with -U.
    public static async Task<UpdateResult> UpdateYtDlpAsync(string ytDlpPath, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        psi.ArgumentList.Add("-U");

        var updated = false;
        var upToDate = false;
        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);
            if (line.Contains("Updated yt-dlp to", StringComparison.OrdinalIgnoreCase)) updated = true;
            if (line.Contains("is up to date", StringComparison.OrdinalIgnoreCase)) upToDate = true;
        }, code => $"yt-dlp -U termino con codigo {code}", ct);

        return new UpdateResult(updated, updated ? "yt-dlp actualizado" : upToDate ? "yt-dlp ya estaba al dia" : "yt-dlp: sin cambios");
    }

    // spotDL is a pip package; its own docs point at pip for upgrades. The python.exe is
    // derived from where spotdl.exe lives (...\PythonXYZ\Scripts\spotdl.exe -> ...\PythonXYZ\python.exe).
    public static async Task<UpdateResult> UpdateSpotDlAsync(string spotdlPath, Action<string> onLine, CancellationToken ct)
    {
        var pythonPath = ResolvePythonFor(spotdlPath);

        var psi = new ProcessStartInfo { FileName = pythonPath };
        foreach (var a in new[] { "-m", "pip", "install", "--upgrade", "--progress-bar", "off", "--disable-pip-version-check", "spotdl" })
            psi.ArgumentList.Add(a);

        var updated = false;
        await ProcessRunner.RunAsync(psi, line =>
        {
            // pip is chatty; only surface the lines that say something happened.
            if (line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
            {
                updated = true;
                onLine(line);
            }
            else if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                onLine(line);
            }
        }, code => $"pip install --upgrade spotdl termino con codigo {code}", ct);

        return new UpdateResult(updated, updated ? "spotDL actualizado" : "spotDL ya estaba al dia");
    }

    private static string ResolvePythonFor(string spotdlPath)
    {
        // ...\PythonXYZ\Scripts\spotdl.exe → ...\PythonXYZ\python.exe
        if (!string.IsNullOrWhiteSpace(spotdlPath)
            && !string.Equals(spotdlPath, "spotdl", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(spotdlPath, "spotdl.exe", StringComparison.OrdinalIgnoreCase))
        {
            var scriptsDir = Path.GetDirectoryName(spotdlPath);
            var pythonDir = scriptsDir != null ? Path.GetDirectoryName(scriptsDir) : null;
            if (pythonDir != null)
            {
                var candidate = Path.Combine(pythonDir, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Resolve bare "spotdl" via PATH first, then derive python.
        if (DependencyChecker.TryFindOnPath("spotdl.exe", out var spotdlAbs) && spotdlAbs != null)
        {
            var scriptsDir = Path.GetDirectoryName(spotdlAbs);
            var pythonDir = scriptsDir != null ? Path.GetDirectoryName(scriptsDir) : null;
            if (pythonDir != null)
            {
                var candidate = Path.Combine(pythonDir, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (DependencyChecker.TryFindOnPath("python.exe", out var py) && py != null)
            return py;
        return "python";
    }
}
