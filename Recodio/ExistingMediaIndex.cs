using System.Text.RegularExpressions;

namespace Recodio;

// Scans a download folder (and subfolders) so playlist re-runs skip anything already on disk,
// even if the download-archive file was deleted or never written.
public sealed class ExistingMediaIndex
{
    private static readonly string[] MediaExts =
        [".mp4", ".mkv", ".webm", ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav", ".avi", ".mov", ".aac"];

    // Filename stem without extension, lower-invariant, punctuation collapsed.
    private readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);
    // Ids found as " [id]" or " [id]." in names (yt-dlp / our template).
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
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!MediaExts.Contains(ext)) continue;
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

    public bool HasId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // Strip synthetic prefixes
        var bare = id.StartsWith("url:", StringComparison.OrdinalIgnoreCase) ? ""
            : id.StartsWith("idx:", StringComparison.OrdinalIgnoreCase) ? id["idx:".Length..]
            : id;
        if (!string.IsNullOrEmpty(bare) && _ids.Contains(bare)) return true;
        if (_ids.Contains(id)) return true;
        // Also check id appears as substring in any stem (conservative: require bracket form preferred)
        return false;
    }

    public bool HasTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var n = Normalize(title);
        if (n.Length < 4) return false;
        if (_stems.Contains(n)) return true;
        // Filename is often "Title [id]" — stem normalize still contains title tokens.
        // Match if any file stem starts with normalized title or equals title before " ["
        foreach (var stem in _stems)
        {
            if (stem.StartsWith(n, StringComparison.OrdinalIgnoreCase) && stem.Length <= n.Length + 50)
                return true;
            // "Artist - Title" style: ends with title
            if (stem.EndsWith(n, StringComparison.OrdinalIgnoreCase) && stem.Length <= n.Length + 80)
                return true;
            if (stem.Contains(n, StringComparison.OrdinalIgnoreCase) && n.Length >= 12)
                return true;
        }
        return false;
    }

    public bool HasArtistTitle(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (HasTitle(title)) return true;
        if (string.IsNullOrWhiteSpace(artist)) return false;
        var combined = Normalize($"{artist} - {title}");
        if (combined.Length < 6) return false;
        if (_stems.Contains(combined)) return true;
        foreach (var stem in _stems)
        {
            if (stem.Contains(combined, StringComparison.OrdinalIgnoreCase)) return true;
            // Sometimes "Title - Artist"
            var alt = Normalize($"{title} - {artist}");
            if (stem.Contains(alt, StringComparison.OrdinalIgnoreCase)) return true;
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
