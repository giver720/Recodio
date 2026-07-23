using System.Diagnostics;

namespace Recodio;

// Robust cookie strategy for yt-dlp / spotDL on Windows:
// 1) Prefer %AppData%\Recodio\cookies.txt if present (stable; no DPAPI mid-download)
// 2) Else try --cookies-from-browser KEY
// 3) On DPAPI / decrypt / lock errors: fall back to no cookies for the rest of the session
//    and optionally retry the current operation without cookies.
public static class CookieManager
{
    private static readonly object Gate = new();
    private static bool _browserCookiesBroken;
    private static string? _brokenReason;

    public static string CookiesFilePath
    {
        get
        {
            AppPaths.EnsureDirs();
            return Path.Combine(AppPaths.DataDir, "cookies.txt");
        }
    }

    public static bool BrowserCookiesBroken
    {
        get { lock (Gate) return _browserCookiesBroken; }
    }

    public static string? BrokenReason
    {
        get { lock (Gate) return _brokenReason; }
    }

    public static void ResetSession()
    {
        lock (Gate)
        {
            _browserCookiesBroken = false;
            _brokenReason = null;
        }
    }

    public static void MarkBrowserBroken(string reason)
    {
        lock (Gate)
        {
            _browserCookiesBroken = true;
            _brokenReason = reason;
        }
    }

    public static bool IsCookieFailureText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        ReadOnlySpan<string> markers =
        [
            "Failed to decrypt with DPAPI",
            "failed to decrypt",
            "DPAPI",
            "Could not copy Chrome cookie database",
            "Could not find COOKIE",
            "unable to load cookies",
            "Unable to load cookies",
            "could not find cookies",
            "Failed to load cookies",
            "cookie database is locked",
            "Error loading cookies",
            "Cookies load failure",
            "no such table: cookies",
            "Permission denied", // often when browser holds the DB
        ];
        foreach (var m in markers)
        {
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Chromium app-bound encryption messages
        if (text.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            && text.Contains("decrypt", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static string FriendlyCookieError(string? raw)
    {
        return
            "No se pudieron leer las cookies del navegador (DPAPI / base bloqueada).\n\n" +
            "Proba:\n" +
            "1) Cerra Brave/Chrome del todo (tambien la bandeja) y reintenta\n" +
            "2) No ejecutes Recodio como Administrador\n" +
            "3) Actualiza yt-dlp (boton Actualizar descargadores)\n" +
            "4) O pone cookies en \"No usar\" y usa solo contenido publico\n\n" +
            "Tip: podes exportar cookies a:\n" + CookiesFilePath + "\n" +
            "(formato Netscape cookies.txt) y Recodio las usara sin abrir el navegador.\n\n" +
            (string.IsNullOrWhiteSpace(raw) ? "" : "Detalle tecnico:\n" + Truncate(raw, 400));
    }

    /// <summary>
    /// Resolves how to pass cookies to yt-dlp for this call.
    /// Prefers cookies.txt; else browser key unless marked broken this session.
    /// </summary>
    public static CookieArgs Resolve(string? browserKey)
    {
        var file = CookiesFilePath;
        if (File.Exists(file) && new FileInfo(file).Length > 32)
            return CookieArgs.FromFile(file);

        if (BrowserCookiesBroken)
            return CookieArgs.None;

        var key = (browserKey ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            return CookieArgs.None;

        return CookieArgs.FromBrowser(key);
    }

    public static void ApplyToArgs(List<string> args, CookieArgs cookies)
    {
        if (cookies.Mode == CookieMode.File && !string.IsNullOrEmpty(cookies.PathOrKey))
        {
            args.Add("--cookies");
            args.Add(cookies.PathOrKey);
        }
        else if (cookies.Mode == CookieMode.Browser && !string.IsNullOrEmpty(cookies.PathOrKey))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookies.PathOrKey);
        }
    }

    public static void ApplyToSpotDl(ProcessStartInfo psi, CookieArgs cookies)
    {
        if (cookies.Mode == CookieMode.File && !string.IsNullOrEmpty(cookies.PathOrKey))
        {
            // spotDL: --cookie-file PATH and also yt-dlp-args for some backends
            psi.ArgumentList.Add("--cookie-file");
            psi.ArgumentList.Add(cookies.PathOrKey);
            psi.ArgumentList.Add("--yt-dlp-args");
            psi.ArgumentList.Add($"--cookies {QuoteForArg(cookies.PathOrKey)}");
        }
        else if (cookies.Mode == CookieMode.Browser && !string.IsNullOrEmpty(cookies.PathOrKey))
        {
            psi.ArgumentList.Add("--yt-dlp-args");
            psi.ArgumentList.Add($"--cookies-from-browser {cookies.PathOrKey}");
        }
    }

    private static string QuoteForArg(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// Tries once to export browser cookies into AppData cookies.txt via yt-dlp.
    /// Success means later downloads avoid re-touching the live browser DB.
    /// </summary>
    public static async Task<bool> TryExportBrowserCookiesAsync(
        string ytDlpPath, string browserKey, Action<string>? onLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(browserKey) || BrowserCookiesBroken)
            return File.Exists(CookiesFilePath);

        if (File.Exists(CookiesFilePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(CookiesFilePath)) < TimeSpan.FromHours(12)
            && new FileInfo(CookiesFilePath).Length > 32)
        {
            onLine?.Invoke(">> Usando cookies.txt cacheado (sin reabrir el navegador).");
            return true;
        }

        var outFile = CookiesFilePath;
        var tmp = outFile + ".tmp";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }

        onLine?.Invoke($">> Exportando cookies de {browserKey} a cookies.txt...");

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Minimal request: just load cookies and write Netscape file; skip download.
        foreach (var a in new[]
        {
            "--skip-download",
            "--no-warnings",
            "--cookies-from-browser", browserKey.Trim().ToLowerInvariant(),
            "--cookies", tmp,
            "ytsearch1:test",
        })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            await ProcessRunner.WaitForExitAsync(proc, ct);
            var stderr = await stderrTask;
            _ = await stdoutTask;

            if (IsCookieFailureText(stderr) || proc.ExitCode != 0)
            {
                if (IsCookieFailureText(stderr))
                {
                    MarkBrowserBroken(stderr.Trim());
                    onLine?.Invoke(">> No se pudieron exportar cookies (DPAPI/navegador abierto). Se reintenta sin cookies.");
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                return false;
            }

            if (File.Exists(tmp) && new FileInfo(tmp).Length > 32)
            {
                try
                {
                    if (File.Exists(outFile)) File.Delete(outFile);
                    File.Move(tmp, outFile);
                }
                catch
                {
                    try { File.Copy(tmp, outFile, true); File.Delete(tmp); } catch { /* ignore */ }
                }
                onLine?.Invoke($">> Cookies exportadas OK → {outFile}");
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onLine?.Invoke($">> Export de cookies falló: {ex.Message}");
            if (IsCookieFailureText(ex.Message))
                MarkBrowserBroken(ex.Message);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
        return File.Exists(CookiesFilePath) && new FileInfo(CookiesFilePath).Length > 32;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

public enum CookieMode { None, Browser, File }

public readonly record struct CookieArgs(CookieMode Mode, string? PathOrKey)
{
    public static CookieArgs None => new(CookieMode.None, null);
    public static CookieArgs FromBrowser(string key) => new(CookieMode.Browser, key);
    public static CookieArgs FromFile(string path) => new(CookieMode.File, path);
    public bool Enabled => Mode != CookieMode.None && !string.IsNullOrEmpty(PathOrKey);
}
