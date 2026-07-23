namespace Recodio;

public class SpotDlForm : Form
{
    private record QueueItem(string Label, string Query);

    private readonly string _spotdlPath;
    private readonly string _ffmpegPath;
    private readonly AppConfig _config;
    private readonly Action _onSettingsChanged;
    private readonly Action _saveConfig;
    private readonly Action<HistoryEntry>? _onHistory;

    private readonly TextBox _txtQuery;
    private readonly Button _btnAnalyze;
    private readonly Label _lblInfo;
    private readonly CheckedListBox _clbTracks;
    private readonly ListBox _lstQueue;
    private readonly List<QueueItem> _queue = [];
    private readonly ComboBox _cmbFormat;
    private readonly ComboBox _cmbBitrate;
    private readonly CheckBox _chkLyrics;
    private readonly NumericUpDown _numThreads;
    private readonly CheckBox _chkSkipExisting;
    private readonly CheckBox _chkOrganizeFolders;
    private readonly CheckBox _chkSponsorBlock;
    private readonly CheckBox _chkYtm;
    private readonly CheckBox _chkYt;
    private readonly CheckBox _chkSc;
    private readonly CheckBox _chkBc;
    private readonly ComboBox _cmbCookies;
    private readonly TextBox _txtDest;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgress;
    private readonly TextBox _txtLog;
    private readonly Button _btnDownload;
    private readonly Button _btnCancel;
    private readonly ToolTip _tip = new();

    private List<SpotDlTrack> _tracks = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _analyzeCts;
    private bool _closeRequested;

    public SpotDlForm(string spotdlPath, string ffmpegPath, AppConfig config,
        Action onSettingsChanged, Action saveConfig,
        Action<HistoryEntry>? onHistory = null)
    {
        _spotdlPath = spotdlPath;
        _ffmpegPath = ffmpegPath;
        _config = config;
        _onSettingsChanged = onSettingsChanged;
        _saveConfig = saveConfig;
        _onHistory = onHistory;

        Text = "Descargar con spotDL";
        Size = new Size(700, 920);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 780);

        var y = 10;

        // --- Query ---
        Controls.Add(new Label
        {
            Text = "URL de Spotify (cancion/album/playlist/artista) o busqueda:",
            Location = new Point(10, y),
            AutoSize = true,
        });
        y += 20;

        _txtQuery = new TextBox
        {
            Location = new Point(10, y),
            Size = new Size(560, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtQuery);

        _btnAnalyze = new Button
        {
            Text = "Analizar",
            Location = new Point(580, y - 1),
            Size = new Size(100, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _btnAnalyze.Click += async (_, _) => await AnalyzeAsync();
        Controls.Add(_btnAnalyze);
        _tip.SetToolTip(_btnAnalyze, "Lista las canciones sin descargar audio (spotdl save).");
        y += 30;

        _lblInfo = new Label
        {
            Text = "Analiza para elegir tracks, descarga la URL directo, o usa la cola.",
            Location = new Point(10, y),
            Size = new Size(670, 18),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lblInfo);
        y += 22;

        _clbTracks = new CheckedListBox
        {
            Location = new Point(10, y),
            Size = new Size(670, 130),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
            HorizontalScrollbar = true,
        };
        Controls.Add(_clbTracks);
        y += 136;

        var btnAll = new Button { Text = "Todos", Location = new Point(10, y), Size = new Size(70, 24) };
        btnAll.Click += (_, _) => SetAllChecked(true);
        Controls.Add(btnAll);
        var btnNone = new Button { Text = "Ninguno", Location = new Point(86, y), Size = new Size(70, 24) };
        btnNone.Click += (_, _) => SetAllChecked(false);
        Controls.Add(btnNone);

        Controls.Add(new Label
        {
            Text = "Cola (otras URLs en orden; ignora la lista de arriba):",
            Location = new Point(170, y + 4),
            AutoSize = true,
        });
        y += 28;

        _lstQueue = new ListBox
        {
            Location = new Point(10, y),
            Size = new Size(560, 56),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lstQueue);

        var btnAddQ = new Button
        {
            Text = "+ Cola",
            Location = new Point(580, y),
            Size = new Size(100, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnAddQ.Click += (_, _) =>
        {
            var query = _txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show(this, "Escribi una URL o busqueda.", "Recodio");
                return;
            }
            _queue.Add(new QueueItem(query, query));
            _lstQueue.Items.Add(query);
            _txtQuery.Clear();
            _tracks.Clear();
            _clbTracks.Items.Clear();
            _lblInfo.Text = "Agregado a la cola.";
        };
        Controls.Add(btnAddQ);

        var btnRemQ = new Button
        {
            Text = "Quitar",
            Location = new Point(580, y + 28),
            Size = new Size(100, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnRemQ.Click += (_, _) =>
        {
            var idx = _lstQueue.SelectedIndex;
            if (idx < 0) return;
            _queue.RemoveAt(idx);
            _lstQueue.Items.RemoveAt(idx);
        };
        Controls.Add(btnRemQ);
        y += 66;

        // --- Options ---
        var grp = new GroupBox
        {
            Text = "Opciones",
            Location = new Point(10, y),
            Size = new Size(670, 230),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(grp);

        grp.Controls.Add(new Label { Text = "Formato:", Location = new Point(10, 22), AutoSize = true });
        _cmbFormat = new ComboBox
        {
            Location = new Point(10, 40),
            Size = new Size(120, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var f in SpotDlDownloader.Formats) _cmbFormat.Items.Add(f);
        _cmbFormat.SelectedIndex = Math.Max(0, Array.IndexOf(SpotDlDownloader.Formats, config.SpotDlFormat));
        grp.Controls.Add(_cmbFormat);
        _tip.SetToolTip(_cmbFormat, "opus = mas rapido (sin recodificar).");

        grp.Controls.Add(new Label { Text = "Bitrate:", Location = new Point(140, 22), AutoSize = true });
        _cmbBitrate = new ComboBox
        {
            Location = new Point(140, 40),
            Size = new Size(110, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var b in SpotDlDownloader.Bitrates) _cmbBitrate.Items.Add(b);
        _cmbBitrate.SelectedIndex = Math.Max(0, Array.IndexOf(SpotDlDownloader.Bitrates, config.SpotDlBitrate));
        grp.Controls.Add(_cmbBitrate);

        _chkLyrics = new CheckBox
        {
            Text = "Letras (Genius + Musixmatch)",
            Location = new Point(270, 42),
            AutoSize = true,
            Checked = config.SpotDlLyrics,
        };
        grp.Controls.Add(_chkLyrics);

        grp.Controls.Add(new Label { Text = "Hilos:", Location = new Point(10, 74), AutoSize = true });
        _numThreads = new NumericUpDown
        {
            Location = new Point(55, 72),
            Size = new Size(50, 22),
            Minimum = 1,
            Maximum = SpotDlDownloader.MaxThreads,
            Value = Math.Clamp(config.SpotDlThreads, 1, SpotDlDownloader.MaxThreads),
        };
        grp.Controls.Add(_numThreads);

        _chkSkipExisting = new CheckBox
        {
            Text = "Solo nuevas (archive)",
            Location = new Point(120, 74),
            AutoSize = true,
            Checked = config.SpotDlSkipExisting,
        };
        grp.Controls.Add(_chkSkipExisting);

        _chkOrganizeFolders = new CheckBox
        {
            Text = "Subcarpetas playlist/album",
            Location = new Point(280, 74),
            AutoSize = true,
            Checked = config.SpotDlOrganizeInFolders,
        };
        grp.Controls.Add(_chkOrganizeFolders);

        _chkSponsorBlock = new CheckBox
        {
            Text = "SponsorBlock (YouTube intros/sponsors)",
            Location = new Point(10, 102),
            AutoSize = true,
            Checked = config.SpotDlSponsorBlock,
        };
        grp.Controls.Add(_chkSponsorBlock);

        grp.Controls.Add(new Label
        {
            Text = "Proveedores de audio (fallback en orden):",
            Location = new Point(10, 128),
            AutoSize = true,
        });

        var providers = SpotDlDownloader.ParseProvidersCsv(config.SpotDlAudioProviders);
        _chkYtm = new CheckBox
        {
            Text = "YouTube Music",
            Location = new Point(10, 148),
            AutoSize = true,
            Checked = providers.Contains("youtube-music", StringComparer.OrdinalIgnoreCase),
        };
        grp.Controls.Add(_chkYtm);
        _chkYt = new CheckBox
        {
            Text = "YouTube",
            Location = new Point(140, 148),
            AutoSize = true,
            Checked = providers.Contains("youtube", StringComparer.OrdinalIgnoreCase),
        };
        grp.Controls.Add(_chkYt);
        _chkSc = new CheckBox
        {
            Text = "SoundCloud",
            Location = new Point(230, 148),
            AutoSize = true,
            Checked = providers.Contains("soundcloud", StringComparer.OrdinalIgnoreCase),
        };
        grp.Controls.Add(_chkSc);
        _chkBc = new CheckBox
        {
            Text = "Bandcamp",
            Location = new Point(340, 148),
            AutoSize = true,
            Checked = providers.Contains("bandcamp", StringComparer.OrdinalIgnoreCase),
        };
        grp.Controls.Add(_chkBc);

        grp.Controls.Add(new Label
        {
            Text = "Cookies navegador (anti bot-check):",
            Location = new Point(10, 176),
            AutoSize = true,
        });
        _cmbCookies = new ComboBox
        {
            Location = new Point(10, 194),
            Size = new Size(180, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbCookies.Items.AddRange(["No usar", "Chrome", "Edge", "Firefox", "Brave", "Opera", "Chromium"]);
        _cmbCookies.SelectedIndex = (config.SpotDlCookiesBrowser ?? "").ToLowerInvariant() switch
        {
            "chrome" => 1, "edge" => 2, "firefox" => 3, "brave" => 4, "opera" => 5, "chromium" => 6, _ => 0,
        };
        grp.Controls.Add(_cmbCookies);

        // Create dest box early so archive-reset handler can read it safely.
        _txtDest = new TextBox
        {
            Text = config.SpotDlDownloadDir,
            Location = new Point(10, 10), // temporary; repositioned below
            Size = new Size(600, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var btnReset = new LinkLabel
        {
            Text = "reiniciar archive de esta carpeta",
            Location = new Point(210, 198),
            AutoSize = true,
        };
        btnReset.Click += (_, _) =>
        {
            var path = SpotDlDownloader.ArchivePathFor(_txtDest.Text.Trim());
            if (File.Exists(path))
            {
                File.Delete(path);
                MessageBox.Show(this, "Archive reiniciado.", "Recodio");
                RefreshArchiveMarks();
            }
            else MessageBox.Show(this, "No hay archive en esta carpeta.", "Recodio");
        };
        grp.Controls.Add(btnReset);

        y += 240;

        Controls.Add(new Label { Text = "Carpeta de destino:", Location = new Point(10, y), AutoSize = true });
        y += 18;
        _txtDest.Location = new Point(10, y);
        _txtDest.Size = new Size(600, 22);
        Controls.Add(_txtDest);
        var btnDest = new Button
        {
            Text = "...",
            Location = new Point(620, y - 1),
            Size = new Size(60, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnDest.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { InitialDirectory = _txtDest.Text.Trim() };
            if (fbd.ShowDialog(this) != DialogResult.OK) return;
            _txtDest.Text = fbd.SelectedPath;
            PersistSettings();
        };
        Controls.Add(btnDest);
        _txtDest.Leave += (_, _) => PersistSettings();
        y += 32;

        _progressBar = new ProgressBar
        {
            Location = new Point(10, y),
            Size = new Size(670, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_progressBar);
        y += 24;

        _lblProgress = new Label { Text = "", Location = new Point(10, y), AutoSize = true };
        Controls.Add(_lblProgress);
        y += 22;

        _txtLog = new TextBox
        {
            Location = new Point(10, y),
            Size = new Size(670, 200),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtLog);

        _btnDownload = new Button
        {
            Text = "Descargar",
            Location = new Point(504, 850),
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        _btnDownload.Click += async (_, _) => await StartDownloadAsync();
        Controls.Add(_btnDownload);

        _btnCancel = new Button
        {
            Text = "Cancelar",
            Location = new Point(600, 850),
            Size = new Size(80, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Enabled = false,
        };
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _analyzeCts?.Cancel();
        };
        Controls.Add(_btnCancel);

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
            if (_config.ClipboardAutoFill && string.IsNullOrWhiteSpace(_txtQuery.Text))
            {
                var clip = ClipboardHelper.TryGetSpotifyUrl();
                if (clip != null)
                {
                    _txtQuery.Text = clip;
                    AppendLog(">> URL de Spotify detectada en el portapapeles.");
                }
            }
            _txtQuery.Focus();
            _txtQuery.SelectAll();
        };
    }

    private void SetAllChecked(bool value)
    {
        for (var i = 0; i < _clbTracks.Items.Count; i++)
            _clbTracks.SetItemChecked(i, value);
    }

    private void CloseIfPending()
    {
        if (_closeRequested && _cts == null && _analyzeCts == null) Close();
    }

    private List<string> SelectedProviders()
    {
        var list = new List<string>();
        if (_chkYtm.Checked) list.Add("youtube-music");
        if (_chkYt.Checked) list.Add("youtube");
        if (_chkSc.Checked) list.Add("soundcloud");
        if (_chkBc.Checked) list.Add("bandcamp");
        if (list.Count == 0)
            list.AddRange(SpotDlDownloader.DefaultAudioProviders);
        return list;
    }

    private string SelectedCookies() => _cmbCookies.SelectedIndex switch
    {
        1 => "chrome", 2 => "edge", 3 => "firefox", 4 => "brave", 5 => "opera", 6 => "chromium", _ => "",
    };

    private void PersistSettings()
    {
        _config.SpotDlDownloadDir = _txtDest.Text.Trim();
        _config.SpotDlFormat = SpotDlDownloader.Formats[_cmbFormat.SelectedIndex];
        _config.SpotDlBitrate = SpotDlDownloader.Bitrates[_cmbBitrate.SelectedIndex];
        _config.SpotDlLyrics = _chkLyrics.Checked;
        _config.SpotDlThreads = (int)_numThreads.Value;
        _config.SpotDlSkipExisting = _chkSkipExisting.Checked;
        _config.SpotDlOrganizeInFolders = _chkOrganizeFolders.Checked;
        _config.SpotDlSponsorBlock = _chkSponsorBlock.Checked;
        _config.SpotDlAudioProviders = string.Join(",", SelectedProviders());
        _config.SpotDlCookiesBrowser = SelectedCookies();
        _onSettingsChanged();
        _saveConfig();
    }

    private void RefreshArchiveMarks()
    {
        if (_tracks.Count == 0) return;
        var dest = _txtDest.Text.Trim();
        for (var i = 0; i < _tracks.Count && i < _clbTracks.Items.Count; i++)
        {
            var t = _tracks[i];
            var inArch = !string.IsNullOrEmpty(t.Url) && SpotDlDownloader.IsInArchive(dest, t.Url);
            _clbTracks.Items[i] = FormatTrackLabel(t, inArch);
            _clbTracks.SetItemChecked(i, !inArch);
        }
    }

    private static string FormatTrackLabel(SpotDlTrack t, bool inArchive)
    {
        var dur = t.DurationSec > 0 ? $" [{t.DurationSec / 60}:{t.DurationSec % 60:D2}]" : "";
        return $"{t.Artist} - {t.Name}{dur}" + (inArchive ? "  ✓" : "");
    }

    private async Task AnalyzeAsync()
    {
        var query = _txtQuery.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            MessageBox.Show(this, "Pega una URL de Spotify o una busqueda.", "Recodio");
            return;
        }

        _btnAnalyze.Enabled = false;
        _btnDownload.Enabled = false;
        _lblInfo.Text = "Analizando metadata de Spotify (sin descargar audio)...";
        _clbTracks.Items.Clear();
        _tracks = [];
        _analyzeCts = new CancellationTokenSource();
        try
        {
            var result = await SpotDlDownloader.AnalyzeAsync(_spotdlPath, query, _analyzeCts.Token);
            _tracks = result.Tracks;
            var dest = _txtDest.Text.Trim();
            foreach (var t in _tracks)
            {
                var inArch = !string.IsNullOrEmpty(t.Url) && SpotDlDownloader.IsInArchive(dest, t.Url);
                _clbTracks.Items.Add(FormatTrackLabel(t, inArch), !inArch);
            }

            var listLabel = string.IsNullOrEmpty(result.ListName) ? "lista" : result.ListName;
            var already = _tracks.Count(t => !string.IsNullOrEmpty(t.Url) && SpotDlDownloader.IsInArchive(dest, t.Url));
            _lblInfo.Text = $"{listLabel}: {_tracks.Count} cancion(es)"
                + (already > 0 ? $", {already} ya en archive (desmarcadas)" : "")
                + ". Desmarca las que no quieras.";
            AppendLog($">> Preview: {_tracks.Count} tracks ({listLabel}).");
        }
        catch (OperationCanceledException)
        {
            _lblInfo.Text = "Analisis cancelado.";
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "No se pudo analizar.";
            MessageBox.Show(this, ex.Message, "Error al analizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnAnalyze.Enabled = true;
            _btnDownload.Enabled = true;
            _analyzeCts.Dispose();
            _analyzeCts = null;
            CloseIfPending();
        }
    }

    private async Task StartDownloadAsync()
    {
        List<(string Label, string Query, List<string>? TrackUrls)> jobs = [];

        if (_queue.Count > 0)
        {
            foreach (var q in _queue)
                jobs.Add((q.Label, q.Query, null));
        }
        else if (_tracks.Count > 0)
        {
            var selected = new List<string>();
            for (var i = 0; i < _clbTracks.Items.Count; i++)
            {
                if (!_clbTracks.GetItemChecked(i)) continue;
                var url = _tracks[i].Url;
                if (!string.IsNullOrWhiteSpace(url)) selected.Add(url);
            }
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Selecciona al menos una cancion, o vacia el preview (reanaliza) y descarga la URL/cola.", "Recodio");
                return;
            }
            jobs.Add(($"preview ({selected.Count} tracks)", _txtQuery.Text.Trim(), selected));
        }
        else
        {
            var query = _txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show(this, "Pega una URL, analiza una playlist, o agrega items a la cola.", "Recodio");
                return;
            }
            jobs.Add((query, query, null));
        }

        var destDir = _txtDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(destDir))
        {
            MessageBox.Show(this, "Elegi una carpeta de destino.", "Recodio");
            return;
        }

        PersistSettings();

        var providers = SelectedProviders();
        var cookies = SelectedCookies();

        _btnDownload.Enabled = false;
        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true;
        _progressBar.Value = 0;
        _txtLog.Clear();
        AppendLog($">> Carpeta: {destDir}");
        AppendLog($">> Proveedores: {string.Join(" → ", providers)}");
        if (!string.IsNullOrEmpty(cookies))
            AppendLog($">> Cookies: {cookies}");

        _cts = new CancellationTokenSource();
        var totalOk = 0;
        var totalFail = 0;
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                _lblProgress.Text = jobs.Count > 1
                    ? $"Cola {i + 1} de {jobs.Count}: {job.Label}"
                    : "Descargando...";
                AppendLog($">> {job.Label}");

                var lastTotal = job.TrackUrls?.Count ?? 0;
                var failCount = await SpotDlDownloader.DownloadAsync(
                    _spotdlPath, _ffmpegPath, job.Query, job.TrackUrls,
                    _config.SpotDlFormat, _config.SpotDlBitrate, _config.SpotDlLyrics,
                    _config.SpotDlThreads, _config.SpotDlSkipExisting,
                    _config.SpotDlOrganizeInFolders, _config.SpotDlSponsorBlock, destDir,
                    providers, cookies,
                    onLine: AppendLog,
                    onProgress: (done, total) =>
                    {
                        lastTotal = Math.Max(lastTotal, total);
                        SetProgress(done, total);
                    },
                    _cts.Token);

                totalFail += failCount;
                totalOk += Math.Max(lastTotal - failCount, 0);

                var status = failCount == 0 ? "ok" : lastTotal > 0 && failCount < lastTotal ? "partial" : "fail";
                _onHistory?.Invoke(new HistoryEntry
                {
                    Name = job.Label.Length > 80 ? job.Label[..77] + "..." : job.Label,
                    Path = destDir,
                    Kind = "spotdl",
                    Status = status,
                    Detail = job.Query,
                });
            }

            var summary = totalFail > 0
                ? $"Descarga completa: {totalOk} ok, {totalFail} con error (revisa el log)."
                : totalOk > 0
                    ? $"Descarga completa: {totalOk} cancion(es) OK."
                    : "Descarga completa.";
            AppendLog($">> {summary}");
            MessageBox.Show(this, summary, "Recodio");

            if (_queue.Count > 0)
            {
                _queue.Clear();
                _lstQueue.Items.Clear();
            }
            RefreshArchiveMarks();
        }
        catch (OperationCanceledException)
        {
            AppendLog(">> Descarga cancelada.");
        }
        catch (Exception ex)
        {
            AppendLog($">> ERROR: {ex.Message}");
            _onHistory?.Invoke(new HistoryEntry
            {
                Name = jobs.FirstOrDefault().Label ?? "spotDL",
                Path = destDir,
                Kind = "spotdl",
                Status = "fail",
                Detail = ex.Message,
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

    private void SetProgress(int done, int total)
    {
        if (InvokeRequired) { Invoke(() => SetProgress(done, total)); return; }
        _progressBar.Maximum = Math.Max(total, 1);
        _progressBar.Value = Math.Clamp(done, 0, _progressBar.Maximum);
        _lblProgress.Text = $"{done} de {total} completadas";
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired) { Invoke(() => AppendLog(line)); return; }
        _txtLog.AppendText(line + Environment.NewLine);
    }
}
