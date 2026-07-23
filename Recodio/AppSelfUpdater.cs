using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Recodio;

// Checks this app's own GitHub repo for a newer release and, if found, downloads the zip
// asset and applies it in place. The repo is public specifically so this works without any
// embedded token - a token baked into a distributed exe is trivially extractable.
public static class AppSelfUpdater
{
    private const string Repo = "giver720/Recodio";

    public record UpdateInfo(string Version, string DownloadUrl);

    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Recodio-App-Updater");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var remoteVersion = tag.TrimStart('v');

        string? zipUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }

        if (zipUrl == null || string.IsNullOrWhiteSpace(remoteVersion)) return null;
        return IsNewer(remoteVersion, CurrentVersion) ? new UpdateInfo(remoteVersion, zipUrl) : null;
    }

    // Plain dotted-integer comparison (1.0.10 > 1.0.9) - fine since we control both the release
    // tags and the csproj Version ourselves and always keep them in that shape.
    private static bool IsNewer(string remote, string current)
    {
        var r = remote.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        var c = current.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        for (var i = 0; i < Math.Max(r.Length, c.Length); i++)
        {
            var rv = i < r.Length ? r[i] : 0;
            var cv = i < c.Length ? c[i] : 0;
            if (rv != cv) return rv > cv;
        }
        return false;
    }

    // Downloads the release zip, extracts it, then hands off to a detached PowerShell script
    // that waits for THIS process to exit, overwrites the install folder, and relaunches the
    // app. A running exe/dll can't overwrite itself, so the actual file swap has to happen
    // after this process is gone - this method ends by exiting the process to make that happen.
    public static async Task DownloadAndApplyAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exePath = Path.Combine(installDir, "Recodio.exe");

        var tempRoot = Path.Combine(Path.GetTempPath(), "RecodioUpdate_" + info.Version);
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, "update.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Recodio-App-Updater");
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await response.Content.CopyToAsync(fs, ct);
        }

        var extractDir = Path.Combine(tempRoot, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // Some zips nest a single top-level folder; copy from that folder if so.
        var copyFrom = extractDir;
        try
        {
            var top = Directory.GetFileSystemEntries(extractDir);
            if (top.Length == 1 && Directory.Exists(top[0])
                && File.Exists(Path.Combine(top[0], "Recodio.exe")))
                copyFrom = top[0];
        }
        catch { /* use extractDir */ }

        // Escape for PowerShell single-quoted strings (double any embedded ').
        static string Ps(string s) => s.Replace("'", "''");
        var scriptPath = Path.Combine(tempRoot, "apply_update.ps1");
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $targetPid = {{Environment.ProcessId}}
            try { Wait-Process -Id $targetPid -Timeout 30 } catch {}
            Start-Sleep -Milliseconds 500
            Copy-Item -Path '{{Ps(copyFrom)}}\*' -Destination '{{Ps(installDir)}}' -Recurse -Force
            Start-Process -FilePath '{{Ps(exePath)}}'
            Remove-Item -Path '{{Ps(tempRoot)}}' -Recurse -Force
            """;
        await File.WriteAllTextAsync(scriptPath, script, ct);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            psi.ArgumentList.Add(a);
        Process.Start(psi);

        Environment.Exit(0);
    }
}
