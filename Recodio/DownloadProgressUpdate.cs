namespace Recodio;

public enum QueueItemState
{
    Pending,
    Downloading,
    Done,
    Skipped,
    Failed,
}

/// <summary>Structured progress event from yt-dlp / spotDL / convert.</summary>
public sealed record DownloadProgressUpdate
{
    /// <summary>Overall 0–100. Use null to leave the overall bar unchanged.</summary>
    public int? OverallPercent { get; init; }

    /// <summary>Current file 0–100. Use null to leave the file bar unchanged.</summary>
    public int? FilePercent { get; init; }

    public int Done { get; init; }
    public int Total { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }

    /// <summary>1-based position within the full selection (not the batch).</summary>
    public int CurrentIndex { get; init; }

    /// <summary>Optional yt-dlp/spotDL batch counters (pending subset).</summary>
    public int BatchIndex { get; init; }
    public int BatchTotal { get; init; }

    public string? CurrentTitle { get; init; }
    public string Phase { get; init; } = "";
    public double? SpeedBytesPerSec { get; init; }
    public TimeSpan? Eta { get; init; }
    public string? SizeInfo { get; init; }
    public string? Status { get; init; }
    public bool IsError { get; init; }

    /// <summary>When true, clear speed/ETA/size detail (e.g. session end).</summary>
    public bool ClearDetail { get; init; }

    /// <summary>Optional key matching a row in the progress queue list.</summary>
    public string? ItemKey { get; init; }
    public QueueItemState? ItemState { get; init; }

    public static DownloadProgressUpdate FromCounts(
        int done, int total, int skipped = 0, int failed = 0,
        int filePercent = 0, string? currentTitle = null, string? status = null,
        string phase = "", int currentIndex = 0)
    {
        total = Math.Max(total, 1);
        done = Math.Clamp(done, 0, total);
        var overall = (int)Math.Round((done + skipped + filePercent / 100.0) * 100.0 / total);
        return new DownloadProgressUpdate
        {
            OverallPercent = Math.Clamp(overall, 0, 100),
            FilePercent = Math.Clamp(filePercent, 0, 100),
            Done = done,
            Total = total,
            Skipped = skipped,
            Failed = failed,
            CurrentTitle = currentTitle,
            Status = status,
            Phase = phase,
            CurrentIndex = currentIndex,
        };
    }

    public static string FormatSpeed(double? bytesPerSec)
    {
        if (bytesPerSec is null or <= 0) return "";
        var b = bytesPerSec.Value;
        if (b >= 1024 * 1024) return $"{b / (1024 * 1024):0.0} MB/s";
        if (b >= 1024) return $"{b / 1024:0.0} KB/s";
        return $"{b:0} B/s";
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta.Value.TotalSeconds < 0) return "";
        var t = eta.Value;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes}:{t.Seconds:D2}";
    }

    public static double? ParseRateToBytes(string rate)
    {
        // e.g. 2.10MiB/s, 850KiB/s, 1.2MB/s
        rate = rate.Trim().TrimEnd('/').Replace("/s", "", StringComparison.OrdinalIgnoreCase);
        if (rate.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            rate, @"^([\d.]+)\s*([KMG]i?B)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = m.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "KIB" => n * 1024,
            "KB" => n * 1000,
            "MIB" => n * 1024 * 1024,
            "MB" => n * 1000 * 1000,
            "GIB" => n * 1024 * 1024 * 1024,
            "GB" => n * 1000 * 1000 * 1000,
            "B" or "" => n,
            _ => n,
        };
    }

    public static TimeSpan? ParseEta(string eta)
    {
        // 00:03 or 1:02:03
        var parts = eta.Trim().Split(':');
        try
        {
            if (parts.Length == 2
                && int.TryParse(parts[0], out var m)
                && int.TryParse(parts[1], out var s))
                return new TimeSpan(0, m, s);
            if (parts.Length == 3
                && int.TryParse(parts[0], out var h)
                && int.TryParse(parts[1], out var m2)
                && int.TryParse(parts[2], out var s2))
                return new TimeSpan(h, m2, s2);
        }
        catch { /* ignore */ }
        return null;
    }
}

public sealed class ProgressQueueItem
{
    public string Key { get; }
    public string Title { get; set; }
    public QueueItemState State { get; set; }

    public ProgressQueueItem(string key, string title, QueueItemState state = QueueItemState.Pending)
    {
        Key = key;
        Title = title;
        State = state;
    }

    public string DisplayText => State switch
    {
        QueueItemState.Downloading => $"▶  {Title}",
        QueueItemState.Done => $"✓  {Title}",
        QueueItemState.Skipped => $"⏭  {Title}",
        QueueItemState.Failed => $"✗  {Title}",
        _ => $"·  {Title}",
    };
}
