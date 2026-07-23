using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Recodio;

public enum MediaKind { Audio, Video }

public record FormatSpec(string Key, string Label, string Extension, MediaKind Kind);

public static class Formats
{
    public static readonly FormatSpec[] All =
    [
        new("mp3", "MP3 (audio)", ".mp3", MediaKind.Audio),
        new("wav", "WAV (audio)", ".wav", MediaKind.Audio),
        new("flac", "FLAC (audio)", ".flac", MediaKind.Audio),
        new("m4a", "AAC / M4A (audio)", ".m4a", MediaKind.Audio),
        new("ogg", "OGG Vorbis (audio)", ".ogg", MediaKind.Audio),
        new("opus", "Opus (audio)", ".opus", MediaKind.Audio),
        new("mp4", "MP4 (video)", ".mp4", MediaKind.Video),
        new("mkv", "MKV (video)", ".mkv", MediaKind.Video),
        new("webm", "WebM (video)", ".webm", MediaKind.Video),
        new("avi", "AVI (video)", ".avi", MediaKind.Video),
        new("mov", "MOV (video)", ".mov", MediaKind.Video),
    ];

    public static FormatSpec Get(string key) => All.FirstOrDefault(f => f.Key == key) ?? All[0];
}

public static class FormatConverter
{
    // Incomplete / temp downloads — never convert these.
    public static readonly string[] TempExtensions =
        [".part", ".tmp", ".ytdl", ".crdownload", ".ffmpeg", ".download"];

    // Legacy alias: previously always skipped .mp3 (wrong when watch target is e.g. flac).
    // Prefer TempExtensions + target-extension checks at the call site.
    public static readonly string[] SkipExtensions = TempExtensions;

    // Extensions the Windows context menu / manual picker offers to convert FROM.
    public static readonly string[] ConvertibleExtensions =
        [".mp3", ".mp4", ".wav", ".flac", ".m4a", ".ogg", ".opus", ".aac", ".wma",
         ".mkv", ".webm", ".avi", ".mov", ".m4v", ".flv", ".3gp"];

    public record ConversionResult(bool Success, bool Skipped, string OutputPath, string Log);

    // Expands dropped/dragged paths: files pass through, folders get scanned recursively
    // for anything with a convertible extension.
    public static string[] ExpandDroppedPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                result.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                    .Where(f => ConvertibleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            }
            else if (File.Exists(p))
            {
                result.Add(p);
            }
        }
        return result.ToArray();
    }

    private static readonly Regex TimeRegex = new(@"time=(\d\d:\d\d:\d\d\.\d\d)", RegexOptions.Compiled);

    // onFileExists: skip | overwrite | rename
    public static async Task<ConversionResult> ConvertAsync(
        string ffmpegPath, string inputPath, string outputDir, string formatKey, string quality,
        Action<int>? onProgress = null, string onFileExists = "skip",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (ready, notReadyReason) = await WaitUntilReadyAsync(inputPath, ct);
        if (!ready)
            return new ConversionResult(false, true, "", notReadyReason ?? "archivo no disponible para convertir");

        var spec = Formats.Get(formatKey);
        var baseName = Path.GetFileNameWithoutExtension(inputPath) + spec.Extension;
        var destDir = string.IsNullOrWhiteSpace(outputDir)
            ? (Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory)
            : outputDir;
        if (string.IsNullOrWhiteSpace(destDir))
            destDir = Environment.CurrentDirectory;
        Directory.CreateDirectory(destDir);
        var outPath = Path.Combine(destDir, baseName);

        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
            outPath = Path.Combine(destDir, Path.GetFileNameWithoutExtension(inputPath) + " (convertido)" + spec.Extension);

        if (File.Exists(outPath))
        {
            var policy = (onFileExists ?? "skip").ToLowerInvariant();
            if (policy == "overwrite")
            {
                // ffmpeg -y already overwrites; keep path as-is
            }
            else if (policy == "rename")
            {
                outPath = NextAvailablePath(outPath);
            }
            else
            {
                return new ConversionResult(false, true, outPath, $"ya existe un archivo {spec.Extension} con ese nombre");
            }
        }

        var durationSeconds = await GetDurationSecondsAsync(ffmpegPath, inputPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        foreach (var a in BuildCodecArgs(formatKey, quality)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(outPath);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var logBuilder = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logBuilder.AppendLine(e.Data);
            if (onProgress == null || durationSeconds <= 0) return;
            var m = TimeRegex.Match(e.Data);
            if (!m.Success) return;
            var elapsed = ParseTimecode(m.Groups[1].Value);
            onProgress((int)Math.Clamp(elapsed / durationSeconds * 100, 0, 100));
        };

        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ConversionResult(false, false, "",
                $"No se encontro ffmpeg ({ffmpegPath}): {ex.Message}");
        }

        proc.BeginErrorReadLine();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await ProcessRunner.WaitForExitAsync(proc, ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { /* already gone */ }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* partial */ }
            throw;
        }

        var stdout = await stdoutTask;
        if (!string.IsNullOrEmpty(stdout)) logBuilder.Insert(0, stdout + Environment.NewLine);

        var ok = proc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0;
        if (ok) onProgress?.Invoke(100);
        return new ConversionResult(ok, false, outPath, logBuilder.ToString());
    }

    private static string NextAvailablePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static double ParseTimecode(string tc)
    {
        var parts = tc.Split(':');
        return double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600
             + double.Parse(parts[1], CultureInfo.InvariantCulture) * 60
             + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    private static async Task<double> GetDurationSecondsAsync(string ffmpegPath, string inputPath)
    {
        var ffprobePath = "ffprobe";
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(candidate)) ffprobePath = candidate;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", inputPath })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (double.TryParse(stdout.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        catch
        {
            // ffprobe missing or failed; progress bar falls back to per-file indeterminate steps
        }
        return 0;
    }

    private static List<string> BuildCodecArgs(string formatKey, string quality) => formatKey switch
    {
        "mp3" => ["-vn", "-map", "0:a", "-c:a", "libmp3lame", .. AudioQualityArgs(quality, "libmp3lame")],
        "wav" => ["-vn", "-map", "0:a", "-c:a", "pcm_s16le"],
        "flac" => ["-vn", "-map", "0:a", "-c:a", "flac"],
        "m4a" => ["-vn", "-map", "0:a", "-c:a", "aac", .. AudioQualityArgs(quality, "aac")],
        "ogg" => ["-vn", "-map", "0:a", "-c:a", "libvorbis", .. AudioQualityArgs(quality, "libvorbis")],
        "opus" => ["-vn", "-map", "0:a", "-c:a", "libopus", .. AudioQualityArgs(quality, "libopus")],
        // Optional maps (?): audio-only sources still produce a usable file when converting "to video"
        // containers is avoided by the UI; for real video, pick first video + first audio if present.
        "mp4" => ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
        "mkv" => ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k"],
        "webm" => ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus"],
        "avi" => ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "mpeg4", "-vtag", "xvid", "-qscale:v", "4", "-c:a", "libmp3lame"],
        "mov" => ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k"],
        _ => ["-vn", "-map", "0:a", "-c:a", "libmp3lame", .. AudioQualityArgs(quality, "libmp3lame")],
    };

    private static List<string> AudioQualityArgs(string quality, string codec) => codec switch
    {
        "libmp3lame" => quality switch { "medium" => ["-b:a", "192k"], "low" => ["-b:a", "128k"], _ => ["-q:a", "0"] },
        "aac" => quality switch { "medium" => ["-b:a", "160k"], "low" => ["-b:a", "96k"], _ => ["-b:a", "256k"] },
        "libvorbis" => quality switch { "medium" => ["-q:a", "5"], "low" => ["-q:a", "2"], _ => ["-q:a", "8"] },
        "libopus" => quality switch { "medium" => ["-b:a", "128k"], "low" => ["-b:a", "64k"], _ => ["-b:a", "192k"] },
        _ => [],
    };

    // Require several identical size samples so a downloader that pauses ≥1s between writes
    // does not look "done". Then require exclusive open.
    private static async Task<(bool Ready, string? Reason)> WaitUntilReadyAsync(string path, CancellationToken ct = default)
    {
        const int sampleDelayMs = 1000;
        const int stableSamplesNeeded = 4; // ~4s of unchanged size
        const int maxWaitRounds = 90; // ~90s total for size stabilization

        long prevSize = -1;
        var stableCount = 0;
        for (var i = 0; i < maxWaitRounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return (false, "archivo desaparecio antes de convertir");
            long size;
            try { size = new FileInfo(path).Length; }
            catch (IOException) { await Task.Delay(sampleDelayMs, ct); continue; }

            if (size > 0 && size == prevSize) stableCount++;
            else stableCount = 0;
            prevSize = size;
            if (stableCount >= stableSamplesNeeded) break;
            await Task.Delay(sampleDelayMs, ct);
        }

        if (prevSize <= 0)
            return (false, "archivo vacio o no se estabilizo el tamano");

        // Reject files that are still "too new" (last write < 2s ago) even if size stable once.
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.FromSeconds(2))
                await Task.Delay(TimeSpan.FromSeconds(2) - age + TimeSpan.FromMilliseconds(200), ct);
        }
        catch { /* ignore */ }

        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return (true, null);
            }
            catch (IOException)
            {
                await Task.Delay(sampleDelayMs, ct);
            }
        }
        return (false, "el archivo sigue en uso por otro proceso, no se pudo bloquear para convertir");
    }

    /// <summary>
    /// After a successful convert, true if the output looks plausibly complete vs the source
    /// (guards against truncating a still-growing download).
    /// </summary>
    public static bool OutputLooksPlausible(string inputPath, string outputPath)
    {
        try
        {
            var inLen = new FileInfo(inputPath).Length;
            var outLen = new FileInfo(outputPath).Length;
            if (outLen <= 0) return false;
            // Pure re-mux / same container can be similar size; re-encode audio is often smaller.
            // Only reject when output is absurdly tiny relative to a large source.
            if (inLen > 512 * 1024 && outLen < Math.Max(8 * 1024, inLen / 100))
                return false;
            return true;
        }
        catch { return File.Exists(outputPath); }
    }
}
