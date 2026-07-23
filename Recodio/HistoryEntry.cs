namespace Recodio;

public class HistoryEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Date { get; set; } = "";
    public long SizeKB { get; set; }

    // convert | ytdlp | spotdl
    public string Kind { get; set; } = "convert";

    // ok | fail | partial
    public string Status { get; set; } = "ok";

    // URL, source path, or short message
    public string Detail { get; set; } = "";

    public static string KindLabel(string kind) => kind switch
    {
        "ytdlp" => "Video",
        "spotdl" => "Spotify",
        _ => "Convertir",
    };

    public static string StatusLabel(string status) => status switch
    {
        "fail" => "Error",
        "partial" => "Parcial",
        _ => "OK",
    };
}
