using System.Text.RegularExpressions;

namespace Recodio;

// Scans a download folder (and subfolders) so playlist re-runs skip anything already on disk,
// even if the download-archive file was deleted or never written.
public sealed class ExistingMediaIndex
{
    private static readonly string[] MediaExts =
        [".mp4", ".mkv", ".webm", ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav", ".avi", ".mov", ".aac"];

    // Incomplete / junk: never treat as "already downloaded".
    private const long MinPlausibleBytes = 32 * 1024; // 32 KB

    // Filename stem without extension, lower-invariant, punctuation collapsed.
    private readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);
    // Ids found as " [id]" in names (yt-dlp / our template) — primary skip key.
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fileNames = [];

    private static readonly Regex BracketIdRegex = new(
        @"\[([A-Za-z0-9_-]{3,})\]", RegexOptions.Compiled);

    public int FileCount => _fileNames.Count;

    public static ExistingMediaIndex Build(params string?[] roots)
    {
        var idx = new ExistingMediaIndex();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (IsIncompletePath(f)) continue;
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!MediaExts.Contains(ext)) continue;
                    try
                    {
                        var len = new FileInfo(f).Length;
                        if (len < MinPlausibleBytes) continue; // partial / empty
                    }
                    catch { continue; }

                    var name = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    idx._fileNames.Add(name);
                    idx._stems.Add(Normalize(name));
                    foreach (Match m in BracketIdRegex.Matches(name))
                        idx._ids.Add(m.Groups[1].Value);
                }
            }
            catch { /* locked / permission */ }
        }
        return idx;
    }

    /// <summary>.part / .ytdl / names that look like in-progress downloads.</summary>
    public static bool IsIncompletePath(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return true;
        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains(".part.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".part" or ".ytdl" or ".tmp" or ".crdownload" or ".download") return true;
        return false;
    }

    public bool HasId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // Strip synthetic prefixes
        var bare = id.StartsWith("url:", StringComparison.OrdinalIgnoreCase) ? ""
            : id.StartsWith("idx:", StringComparison.OrdinalIgnoreCase) ? ""
            : id;
        if (!string.IsNullOrEmpty(bare) && _ids.Contains(bare)) return true;
        if (_ids.Contains(id)) return true;
        return false;
    }

    /// <summary>
    /// Strict title match for synthetic ids only: exact normalized stem, or stem that is
    /// "Title [id]" with long title (>= 16 chars). Avoids short false positives.
    /// </summary>
    public bool HasTitleStrict(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var n = Normalize(title);
        if (n.Length < 16) return false; // short titles never skip by title alone
        if (_stems.Contains(n)) return true;
        foreach (var stem in _stems)
        {
            // Exact title before trailing " [id]" segment
            var stemNoId = Regex.Replace(stem, @"\s*\[[^\]]+\]\s*$", "").Trim();
            if (stemNoId.Equals(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Legacy loose match — prefer HasId / HasTitleStrict for yt-dlp seed.</summary>
    public bool HasTitle(string? title) => HasTitleStrict(title);

    public bool HasArtistTitle(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (HasTitleStrict(title)) return true;
        if (string.IsNullOrWhiteSpace(artist)) return false;
        var combined = Normalize($"{artist} - {title}");
        if (combined.Length < 16) return false;
        if (_stems.Contains(combined)) return true;
        foreach (var stem in _stems)
        {
            var stemNoId = Regex.Replace(stem, @"\s*\[[^\]]+\]\s*$", "").Trim();
            if (stemNoId.Equals(combined, StringComparison.OrdinalIgnoreCase)) return true;
            var alt = Normalize($"{title} - {artist}");
            if (stemNoId.Equals(alt, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Trim().ToLowerInvariant().Select(c =>
            char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : ' ').ToArray();
        var collapsed = Regex.Replace(new string(chars), @"\s+", " ").Trim();
        // Drop trailing " [id]" style for comparison of pure titles
        collapsed = Regex.Replace(collapsed, @"\s*\[[^\]]+\]\s*$", "").Trim();
        return collapsed;
    }
}
