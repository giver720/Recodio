using System.Diagnostics;

namespace Recodio;

public record ToolStatus(string Name, bool Found, string PathOrHint);

public static class DependencyChecker
{
    public static ToolStatus CheckFfmpeg(string resolvedPath) =>
        CheckTool("ffmpeg", resolvedPath, "winget install Gyan.FFmpeg  (o yt-dlp.FFmpeg)");

    public static ToolStatus CheckYtDlp(string resolvedPath) =>
        CheckTool("yt-dlp", resolvedPath, "winget install yt-dlp.yt-dlp");

    public static ToolStatus CheckSpotDl(string resolvedPath) =>
        CheckTool("spotDL", resolvedPath, "pip install spotdl");

    public static IReadOnlyList<ToolStatus> CheckAll(string ffmpeg, string ytDlp, string spotdl) =>
        [CheckFfmpeg(ffmpeg), CheckYtDlp(ytDlp), CheckSpotDl(spotdl)];

    public static string FormatSummary(IReadOnlyList<ToolStatus> tools)
    {
        var missing = tools.Where(t => !t.Found).Select(t => t.Name).ToList();
        if (missing.Count == 0) return "Dependencias OK: ffmpeg, yt-dlp, spotDL.";
        return "Falta: " + string.Join(", ", missing) + ". Revisá Configuración o el log.";
    }

    private static ToolStatus CheckTool(string name, string path, string installHint)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ToolStatus(name, false, installHint);

        // Absolute path that exists on disk.
        if (Path.IsPathRooted(path) && File.Exists(path))
            return new ToolStatus(name, true, path);

        // Bare command name: resolve via where.exe (PATH).
        if (TryFindOnPath(path, out var found) || TryFindOnPath(path + ".exe", out found))
            return new ToolStatus(name, true, found!);

        return new ToolStatus(name, false, $"{path} — {installHint}");
    }

    public static bool TryFindOnPath(string command, out string? fullPath)
    {
        fullPath = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (proc.ExitCode != 0) return false;
            var first = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first) || !File.Exists(first)) return false;
            fullPath = first.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
