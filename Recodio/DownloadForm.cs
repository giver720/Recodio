using System.Text.RegularExpressions;

namespace Recodio;

public class DownloadForm : Form
{
    private static readonly Regex ItemDeRegex = new(@"Item\s+(\d+)\s+de\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DestRegex = new(@"\[download\]\s+Destination:\s+(.+)", RegexOptions.Compiled);
    private static readonly Regex AlreadyRegex = new(@"\[download\]\s+(.+?)\s+has already been downloaded", RegexOptions.Compiled);
    private static readonly Regex MergingRegex = new(@"\[Merger\]\s+Merging formats into\s+""?(.+?)""?\s*$", RegexOptions.Compiled);

    private readonly string _ytDlpPath;
    private readonly string _ffmpegPath;
    private readonly Action<string> _onDestDirChanged;
    private readonly Action<HistoryEntry>? _onHistory;
    private readonly Action<string>? _onCookiesChanged;

    private readonly TextBox _txtUrl;
    private readonly Button _btnAnalyze;
    private readonly Label _lblInfo;
    private readonly CheckedListBox _clbEntries;
    private readonly Button _btnAll;
    private readonly Button _btnNone;
    private readonly ComboBox _cmbFormat;
    private readonly ComboBox _cmbVideoQuality;
    private readonly ComboBox _cmbAudioQuality;
    private readonly ComboBox _cmbCookies;
    private readonly CheckBox _chkOrganizeFolders;
    private readonly CheckBox _chkRemoveSponsors;
    private readonly TextBox _txtDest;
    private readonly DownloadProgressPanel _progress;
    private readonly Button _btnDownload;
    private readonly Button _btnCancel;
    private readonly Button _btnClose;

    private List<PlaylistEntry> _entries = [];
    private string _detectedExtractor = "";
    private string? _playlistTitle;
    private string _analyzedUrl = "";
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _analyzeCts;
    private bool _closeRequested;

    // Live progress tracking during download
    private int _dlTotal;
    private int _dlCompleted;
    private int _dlCurrentItem; // 1-based when known
    private int _dlFilePct;

    public DownloadForm(
        string ytDlpPath,
        string ffmpegPath,
        string initialDestDir,
        Action<string> onDestDirChanged,
        Action<HistoryEntry>? onHistory = null,
        bool clipboardAutoFill = true,
        string cookiesBrowser = "",
        Action<string>? onCookiesChanged = null)
    {
        _ytDlpPath = ytDlpPath;
        _ffmpegPath = ffmpegPath;
        _onDestDirChanged = onDestDirChanged;
        _onHistory = onHistory;
        _onCookiesChanged = onCookiesChanged;

        Text = "Descargar con yt-dlp (cualquier sitio)";
        Size = new Size(660, 600);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(580, 540);

        var lblUrl = new Label
        {
            Text = "URL (YouTube, X/Twitter, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit…):",
            Location = new Point(10, 12),
            AutoSize = true,
        };
        Controls.Add(lblUrl);

        _txtUrl = new TextBox { Location = new Point(10, 32), Size = new Size(520, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_txtUrl);

        _btnAnalyze = new Button { Text = "Analizar", Location = new Point(540, 31), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnAnalyze.Click += async (_, _) => await AnalyzeAsync();
        Controls.Add(_btnAnalyze);

        _lblInfo = new Label
        {
            Text = "Pega cualquier URL que yt-dlp soporte y presiona Analizar.",
            Location = new Point(10, 62),
            Size = new Size(620, 20),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lblInfo);

        _clbEntries = new CheckedListBox
        {
            Location = new Point(10, 85),
            Size = new Size(620, 140),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
        };
        Controls.Add(_clbEntries);

        _btnAll = new Button { Text = "Todos", Location = new Point(10, 230), Size = new Size(80, 22) };
        _btnAll.Click += (_, _) => SetAllChecked(true);
        Controls.Add(_btnAll);

        _btnNone = new Button { Text = "Ninguno", Location = new Point(95, 230), Size = new Size(80, 22) };
        _btnNone.Click += (_, _) => SetAllChecked(false);
        Controls.Add(_btnNone);

        var lblFormat = new Label { Text = "Formato de salida:", Location = new Point(10, 262), AutoSize = true };
        Controls.Add(lblFormat);
        _cmbFormat = new ComboBox
        {
            Location = new Point(10, 280),
            Size = new Size(240, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbFormat.Items.AddRange([
            "MP4 (video, remux)",
            "MKV (video, remux)",
            "Mejor original (sin forzar contenedor)",
            "MP3 (solo audio)",
            "M4A / AAC (solo audio)",
        ]);
        _cmbFormat.SelectedIndex = 0;
        _cmbFormat.SelectedIndexChanged += (_, _) => UpdateQualityEnabled();
        Controls.Add(_cmbFormat);

        var lblVideoQ = new Label { Text = "Calidad de video:", Location = new Point(270, 262), AutoSize = true };
        Controls.Add(lblVideoQ);
        _cmbVideoQuality = new ComboBox { Location = new Point(270, 280), Size = new Size(160, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbVideoQuality.Items.AddRange(["Mejor disponible", "2160p (4K)", "1440p (2K)", "1080p", "720p", "480p", "360p"]);
        _cmbVideoQuality.SelectedIndex = 0;
        Controls.Add(_cmbVideoQuality);

        var lblAudioQ = new Label { Text = "Calidad de audio:", Location = new Point(450, 262), AutoSize = true };
        Controls.Add(lblAudioQ);
        _cmbAudioQuality = new ComboBox { Location = new Point(450, 280), Size = new Size(180, 22), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _cmbAudioQuality.Items.AddRange(["Alta", "Media (192k)", "Baja (128k)"]);
        _cmbAudioQuality.SelectedIndex = 0;
        Controls.Add(_cmbAudioQuality);

        var lblCookies = new Label
        {
            Text = "Cookies (automatico; se guarda en Configuracion):",
            Location = new Point(10, 312),
            AutoSize = true,
        };
        Controls.Add(lblCookies);
        _cmbCookies = new ComboBox
        {
            Location = new Point(10, 330),
            Size = new Size(280, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbCookies.Items.AddRange(BrowserCookies.Labels());
        _cmbCookies.SelectedIndex = BrowserCookies.IndexOfKey(cookiesBrowser);
        _cmbCookies.SelectedIndexChanged += (_, _) =>
        {
            _onCookiesChanged?.Invoke(SelectedCookiesBrowser());
        };
        Controls.Add(_cmbCookies);
        var tipCookies = new ToolTip();
        tipCookies.SetToolTip(_cmbCookies, BrowserCookies.HintFor(cookiesBrowser));

        _chkOrganizeFolders = new CheckBox
        {
            Text = "Subcarpeta + archivo .m3u por playlist (items sueltos quedan sueltos)",
            Location = new Point(10, 362),
            AutoSize = true,
            Checked = true,
        };
        Controls.Add(_chkOrganizeFolders);

        _chkRemoveSponsors = new CheckBox
        {
            Text = "SponsorBlock (solo YouTube: recortar auspicios)",
            Location = new Point(10, 386),
            AutoSize = true,
            Checked = false,
        };
        Controls.Add(_chkRemoveSponsors);

        var lblDest = new Label { Text = "Carpeta de destino:", Location = new Point(10, 414), AutoSize = true };
        Controls.Add(lblDest);
        _txtDest = new TextBox { Text = initialDestDir, Location = new Point(10, 432), Size = new Size(520, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_txtDest);
        var btnDest = new Button { Text = "...", Location = new Point(540, 431), Size = new Size(40, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnDest.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { InitialDirectory = _txtDest.Text.Trim() };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            _txtDest.Text = fbd.SelectedPath;
            _onDestDirChanged(fbd.SelectedPath);
        };
        Controls.Add(btnDest);

        _txtDest.Leave += (_, _) =>
        {
            var typed = _txtDest.Text.Trim();
            if (typed.Length > 0) _onDestDirChanged(typed);
        };

        _progress = new DownloadProgressPanel(10, 466, 620, this);

        _btnDownload = new Button { Text = "Descargar", Location = new Point(365, 530), Size = new Size(90, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnDownload.Click += async (_, _) => await StartDownloadAsync();
        Controls.Add(_btnDownload);

        _btnCancel = new Button { Text = "Cancelar", Location = new Point(460, 530), Size = new Size(80, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _analyzeCts?.Cancel();
        };
        Controls.Add(_btnCancel);

        _btnClose = new Button { Text = "Cerrar", Location = new Point(545, 530), Size = new Size(85, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnClose.Click += (_, _) => Close();
        Controls.Add(_btnClose);

        FormClosing += (_, e) =>
        {
            if (_cts == null && _analyzeCts == null) return;
            e.Cancel = true;
            _closeRequested = true;
            _cts?.Cancel();
            _analyzeCts?.Cancel();
        };

        Shown += (_, _) =>
        {
            if (clipboardAutoFill && string.IsNullOrWhiteSpace(_txtUrl.Text))
            {
                var clip = ClipboardHelper.TryGetMediaUrl();
                if (clip != null)
                {
                    _txtUrl.Text = clip;
                    _lblInfo.Text = "URL detectada en el portapapeles (cualquier sitio).";
                }
            }
            _txtUrl.Focus();
            _txtUrl.SelectAll();
        };

        UpdateQualityEnabled();
    }

    private void UpdateQualityEnabled()
    {
        // Audio-only formats: video quality N/A
        var audioOnly = _cmbFormat.SelectedIndex is 3 or 4;
        _cmbVideoQuality.Enabled = !audioOnly;
    }

    private void CloseIfPending()
    {
        if (_closeRequested && _cts == null && _analyzeCts == null) Close();
    }

    private void SetAllChecked(bool value)
    {
        for (var i = 0; i < _clbEntries.Items.Count; i++) _clbEntries.SetItemChecked(i, value);
    }

    private string SelectedFormatKey() => _cmbFormat.SelectedIndex switch
    {
        1 => "mkv",
        2 => "best",
        3 => "mp3",
        4 => "m4a",
        _ => "mp4",
    };

    private string SelectedCookiesBrowser() => BrowserCookies.KeyAt(_cmbCookies.SelectedIndex);

    private async Task AnalyzeAsync()
    {
        var url = _txtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Pega una URL primero.", "Recodio");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "La URL tiene que empezar con http:// o https://.", "Recodio");
            return;
        }

        // Spotify is the other pipeline — only look at the textbox URL, not the clipboard.
        if (url.Contains("spotify.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("spotify.link", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "Las URLs de Spotify se descargan con spotDL (boton \"Descargar con spotDL...\").\n\n" +
                "yt-dlp aca es para video/audio de otros sitios.",
                "Recodio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true; // allow cancelling a long analyze
        _lblInfo.Text = "Analizando con yt-dlp (puede tardar en playlists grandes)...";
        _progress.SetStatus("Analizando URL...");
        _clbEntries.Items.Clear();
        _entries = [];
        _detectedExtractor = "";
        _playlistTitle = null;
        _analyzedUrl = "";
        _analyzeCts = new CancellationTokenSource();
        try
        {
            // Pass the same cookie choice used for download — Instagram/X often need it to list items.
            var result = await YtDlpDownloader.AnalyzeAsync(
                _ytDlpPath, url, _analyzeCts.Token, SelectedCookiesBrowser());
            _entries = result.Entries;
            _detectedExtractor = result.Extractor;
            _playlistTitle = result.IsPlaylist ? result.PlaylistTitle : null;
            _analyzedUrl = url;
            foreach (var e in _entries)
            {
                var label = string.IsNullOrEmpty(e.Extractor)
                    ? e.Title
                    : $"{e.Title}  ({e.Extractor})";
                _clbEntries.Items.Add(label, true);
            }

            var site = string.IsNullOrEmpty(result.Extractor) ? "sitio detectado" : result.Extractor;
            _lblInfo.Text = result.IsPlaylist
                ? $"{site}: {result.PlaylistTitle} ({_entries.Count} items) — desmarca los que no quieras"
                : $"{site}: item individual listo para descargar.";
            _progress.SetStatus(result.IsPlaylist
                ? $"Lista lista: {_entries.Count} items."
                : "Item listo para descargar.");

            // Hint cookies for sites that often need a logged-in browser session.
            var mayNeedCookies =
                site.Contains("instagram", StringComparison.OrdinalIgnoreCase)
                || site.Contains("twitter", StringComparison.OrdinalIgnoreCase)
                || site.Contains("facebook", StringComparison.OrdinalIgnoreCase)
                || site.Contains("tiktok", StringComparison.OrdinalIgnoreCase);
            if (_cmbCookies.SelectedIndex == 0 && mayNeedCookies)
                _lblInfo.Text += "  Tip: activa cookies (Brave/Chrome) en el combo o en Configuracion.";
        }
        catch (OperationCanceledException)
        {
            _lblInfo.Text = "Analisis cancelado.";
            _progress.SetStatus("Analisis cancelado.");
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "No se pudo analizar la URL.";
            _progress.SetStatus("Error al analizar.", isError: true);
            var msg = CookieManager.IsCookieFailureText(ex.Message)
                ? CookieManager.FriendlyCookieError(ex.Message)
                : ex.Message + "\n\nSitios soportados: todo lo que entiende yt-dlp "
                  + "(YouTube, X, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, …).\n"
                  + "Si el sitio pide login: cierra Brave, actualiza yt-dlp, o usa cookies.txt en:\n"
                  + CookieManager.CookiesFilePath;
            MessageBox.Show(this, msg, "Error al analizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnAnalyze.Enabled = true;
            if (_cts == null) _btnCancel.Enabled = false;
            _analyzeCts?.Dispose();
            _analyzeCts = null;
            CloseIfPending();
        }
    }

    private async Task StartDownloadAsync()
    {
        if (_entries.Count == 0 || string.IsNullOrWhiteSpace(_analyzedUrl))
        {
            MessageBox.Show(this, "Primero analiza una URL.", "Recodio");
            return;
        }

        var currentUrl = _txtUrl.Text.Trim();
        if (!string.Equals(currentUrl, _analyzedUrl, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "La URL del cuadro no coincide con la que se analizo.\n\n" +
                "Volve a presionar Analizar, o restaura la URL original.",
                "Recodio");
            return;
        }

        var selectedEntries = new List<PlaylistEntry>();
        for (var i = 0; i < _clbEntries.Items.Count; i++)
        {
            if (_clbEntries.GetItemChecked(i)) selectedEntries.Add(_entries[i]);
        }
        if (selectedEntries.Count == 0)
        {
            MessageBox.Show(this, "Selecciona al menos un item.", "Recodio");
            return;
        }

        var destDir = _txtDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(destDir))
        {
            MessageBox.Show(this, "Elegi una carpeta de destino.", "Recodio");
            return;
        }
        _onDestDirChanged(destDir);

        var format = SelectedFormatKey();
        var videoQuality = _cmbVideoQuality.SelectedIndex switch
        {
            1 => "2160", 2 => "1440", 3 => "1080", 4 => "720", 5 => "480", 6 => "360", _ => "best",
        };
        var audioQuality = _cmbAudioQuality.SelectedIndex switch { 1 => "medium", 2 => "low", _ => "high" };
        var cookies = SelectedCookiesBrowser();

        // Always download the URL that was analyzed (indices/ids belong to that page).
        var url = _analyzedUrl;
        var label = selectedEntries.Count == 1
            ? selectedEntries[0].Title
            : $"{selectedEntries.Count} items"
              + (string.IsNullOrEmpty(_detectedExtractor) ? "" : $" ({_detectedExtractor})");

        _btnDownload.Enabled = false;
        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true;

        _dlTotal = selectedEntries.Count;
        _dlCompleted = 0;
        _dlCurrentItem = 0;
        _dlFilePct = 0;
        _progress.Reset(
            queueText: $"Cola: 0 / {_dlTotal} items",
            status: "Iniciando descarga...");
        if (_dlTotal == 1)
            _progress.SetCurrent(selectedEntries[0].Title);

        _cts = new CancellationTokenSource();
        try
        {
            // Prefer analyzed playlist title so the folder name is stable even on direct-URL retries.
            var playlistTitle = _playlistTitle;
            if (string.IsNullOrWhiteSpace(playlistTitle) && selectedEntries.Count > 1)
                playlistTitle = null; // downloader will guess from URL

            var failCount = await YtDlpDownloader.DownloadAsync(
                _ytDlpPath, _ffmpegPath, url, selectedEntries, format, videoQuality, audioQuality,
                _chkOrganizeFolders.Checked, _chkRemoveSponsors.Checked, destDir,
                onLine: HandleDownloadLine,
                onProgress: pct =>
                {
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(() => ApplyFilePercent(pct));
                    }
                    catch { /* dispose race */ }
                },
                _cts.Token,
                cookiesFromBrowser: cookies,
                playlistTitle: playlistTitle);

            var status = failCount == 0 ? "ok" : failCount >= selectedEntries.Count ? "fail" : "partial";
            var summary = failCount > 0
                ? $"Descarga completa: {selectedEntries.Count - failCount} ok, {failCount} no se pudieron bajar."
                : $"Descarga completa: {selectedEntries.Count} item(s) OK.";

            Ui(() =>
            {
                _progress.SetPercent(100);
                _progress.SetQueue($"Cola: {_dlTotal - failCount} / {_dlTotal} ok"
                    + (failCount > 0 ? $", {failCount} error" : ""));
                _progress.SetStatus(summary, isError: failCount > 0);
                _progress.SetCurrent("");
            });

            var historyPath = destDir;
            if (_chkOrganizeFolders.Checked && !string.IsNullOrWhiteSpace(playlistTitle))
            {
                var folder = YtDlpDownloader.SanitizeFolderName(playlistTitle!);
                var candidate = Path.Combine(destDir, folder);
                if (Directory.Exists(candidate)) historyPath = candidate;
            }

            _onHistory?.Invoke(new HistoryEntry
            {
                Name = label,
                Path = historyPath,
                Kind = "ytdlp",
                Status = status,
                Detail = url,
                SizeKB = 0,
            });
            MessageBox.Show(this, summary, "Recodio");
        }
        catch (OperationCanceledException)
        {
            Ui(() => _progress.SetStatus("Descarga cancelada."));
        }
        catch (Exception ex)
        {
            Ui(() => _progress.SetStatus(ex.Message, isError: true));
            _onHistory?.Invoke(new HistoryEntry
            {
                Name = label,
                Path = destDir,
                Kind = "ytdlp",
                Status = "fail",
                Detail = url + " — " + ex.Message,
            });
            MessageBox.Show(this, ex.Message, "Error al descargar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnDownload.Enabled = true;
            _btnAnalyze.Enabled = true;
            _btnCancel.Enabled = false;
            _cts.Dispose();
            _cts = null;
            CloseIfPending();
        }
    }

    private void ApplyFilePercent(int pct)
    {
        try
        {
            if (IsDisposed || _progress.Bar.IsDisposed) return;
            _dlFilePct = Math.Clamp(pct, 0, 100);

            // When downloader reports overall-style jumps (retry phase uses 0..100 of missing batch)
            // and we don't know current item yet, use raw percent.
            if (_dlTotal <= 1)
            {
                _progress.SetPercent(_dlFilePct);
                _progress.SetQueue(_dlTotal == 1 ? "Cola: 1 item" : "Cola: —");
                if (_dlFilePct >= 100)
                    _progress.SetStatus("Finalizando...");
                else if (_dlFilePct > 0)
                    _progress.SetStatus($"Descargando… {_dlFilePct}%");
                return;
            }

            // Prefer item-based overall if we saw "Item N de M"
            if (_dlCurrentItem > 0)
            {
                var completed = Math.Max(0, _dlCurrentItem - 1);
                _dlCompleted = completed;
                _progress.SetOverallProgress(completed, _dlTotal, _dlFilePct);
                _progress.SetQueue($"Cola: {completed} / {_dlTotal} · item {_dlCurrentItem} de {_dlTotal}");
                _progress.SetStatus($"Descargando… {_dlFilePct}% del archivo actual");
            }
            else
            {
                // File % alone: show as-is but keep queue totals
                _progress.SetPercent(_dlFilePct);
                _progress.SetQueue($"Cola: {_dlCompleted} / {_dlTotal} · archivo {_dlFilePct}%");
                _progress.SetStatus($"Descargando… {_dlFilePct}%");
            }

            if (_dlFilePct >= 100 && _dlCurrentItem > 0 && _dlCurrentItem > _dlCompleted)
            {
                _dlCompleted = _dlCurrentItem;
                _progress.SetQueue($"Cola: {_dlCompleted} / {_dlTotal}");
            }
        }
        catch { /* dispose race */ }
    }

    private void HandleDownloadLine(string line)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => HandleDownloadLine(line)); return; }
            if (string.IsNullOrWhiteSpace(line)) return;

            // >> status messages from our wrapper
            if (line.StartsWith(">> ", StringComparison.Ordinal))
            {
                var msg = line[3..].Trim();
                if (msg.StartsWith("Omitidas", StringComparison.OrdinalIgnoreCase)
                    || msg.StartsWith("Todos los items", StringComparison.OrdinalIgnoreCase))
                {
                    _progress.SetStatus(msg);
                    // If everything skipped, mark complete
                    if (msg.StartsWith("Todos los items", StringComparison.OrdinalIgnoreCase))
                    {
                        _dlCompleted = _dlTotal;
                        _progress.SetPercent(100);
                        _progress.SetQueue($"Cola: {_dlTotal} / {_dlTotal} (ya en carpeta)");
                    }
                }
                else if (msg.StartsWith("Reintento", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("Descargando", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("Carpeta", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("Cookies", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("SponsorBlock", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("pasada", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("faltan", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("playlist", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("reintenta", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("no disponible", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("error", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("Item ", StringComparison.OrdinalIgnoreCase))
                {
                    _progress.SetStatus(msg, isError: msg.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("no disponible", StringComparison.OrdinalIgnoreCase));
                }

                var im = ItemDeRegex.Match(msg);
                if (im.Success
                    && int.TryParse(im.Groups[1].Value, out var n)
                    && int.TryParse(im.Groups[2].Value, out var m)
                    && m > 0)
                {
                    _dlCurrentItem = n;
                    _dlTotal = Math.Max(_dlTotal, m);
                    _dlCompleted = Math.Max(_dlCompleted, n - 1);
                    _dlFilePct = 0;
                    _progress.SetQueue($"Cola: {_dlCompleted} / {_dlTotal} · item {n} de {m}");
                    _progress.SetOverallProgress(_dlCompleted, _dlTotal, 0);
                    _progress.SetStatus($"Procesando item {n} de {m}...");
                }
                return;
            }

            var dest = DestRegex.Match(line);
            if (dest.Success)
            {
                var name = Path.GetFileName(dest.Groups[1].Value.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(name))
                    _progress.SetCurrent(name);
                return;
            }

            var already = AlreadyRegex.Match(line);
            if (already.Success)
            {
                var name = Path.GetFileName(already.Groups[1].Value.Trim());
                if (!string.IsNullOrEmpty(name))
                    _progress.SetCurrent(name + " (ya descargado)");
                _progress.SetStatus("Omitido: ya estaba descargado.");
                return;
            }

            var merge = MergingRegex.Match(line);
            if (merge.Success)
            {
                var name = Path.GetFileName(merge.Groups[1].Value.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(name))
                    _progress.SetCurrent(name);
                _progress.SetStatus("Uniendo video + audio...");
                return;
            }

            if (line.Contains("[ExtractAudio]", StringComparison.Ordinal)
                || line.Contains("Extracting audio", StringComparison.OrdinalIgnoreCase))
            {
                _progress.SetStatus("Extrayendo audio...");
                return;
            }

            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                var shortErr = line.Length > 120 ? line[..117] + "..." : line;
                _progress.SetStatus(shortErr, isError: true);
            }
        }
        catch (ObjectDisposedException) { /* form closed mid-download */ }
    }

    private void Ui(Action action)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(action); return; }
            action();
        }
        catch (ObjectDisposedException) { /* form closed */ }
    }
}
