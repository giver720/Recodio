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
    private readonly DownloadProgressPanel _progress;
    private readonly Button _btnDownload;
    private readonly Button _btnCancel;
    private readonly ToolTip _tip = new();

    private List<SpotDlTrack> _tracks = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _analyzeCts;
    private bool _closeRequested;

    private int _jobIndex;
    private int _jobCount;
    private string _jobLabel = "";

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
        Size = new Size(720, 880);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(660, 780);

        _progress = new DownloadProgressPanel(10, 580, 690, this);

        var y = 10;

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
            Size = new Size(580, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtQuery);

        _btnAnalyze = new Button
        {
            Text = "Analizar",
            Location = new Point(600, y - 1),
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
            Size = new Size(690, 18),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lblInfo);
        y += 22;

        _clbTracks = new CheckedListBox
        {
            Location = new Point(10, y),
            Size = new Size(690, 110),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
            HorizontalScrollbar = true,
        };
        Controls.Add(_clbTracks);
        y += 116;

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
            Size = new Size(580, 52),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lstQueue);

        var btnAddQ = new Button
        {
            Text = "+ Cola",
            Location = new Point(600, y),
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
            _progress.SetStats($"Cola: {_queue.Count} URL(s) pendientes");
            _progress.SetStatus("Item agregado a la cola.");
        };
        Controls.Add(btnAddQ);

        var btnRemQ = new Button
        {
            Text = "Quitar",
            Location = new Point(600, y + 26),
            Size = new Size(100, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnRemQ.Click += (_, _) =>
        {
            var idx = _lstQueue.SelectedIndex;
            if (idx < 0) return;
            _queue.RemoveAt(idx);
            _lstQueue.Items.RemoveAt(idx);
            _progress.SetStats(_queue.Count > 0
                ? $"Cola: {_queue.Count} URL(s) pendientes"
                : "Cola: —");
        };
        Controls.Add(btnRemQ);
        y += 60;

        var grp = new GroupBox
        {
            Text = "Opciones",
            Location = new Point(10, y),
            Size = new Size(690, 220),
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
            Text = "Omitir ya descargadas (carpeta + archive)",
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
            Text = "Cookies (auto desde Configuracion):",
            Location = new Point(10, 176),
            AutoSize = true,
        });
        _cmbCookies = new ComboBox
        {
            Location = new Point(10, 194),
            Size = new Size(200, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbCookies.Items.AddRange(BrowserCookies.Labels());
        _cmbCookies.SelectedIndex = BrowserCookies.IndexOfKey(config.EffectiveCookiesBrowser());
        _cmbCookies.SelectedIndexChanged += (_, _) => PersistSettings();
        grp.Controls.Add(_cmbCookies);
        _tip.SetToolTip(_cmbCookies, BrowserCookies.HintFor(config.EffectiveCookiesBrowser()));

        _txtDest = new TextBox
        {
            Text = config.SpotDlDownloadDir,
            Location = new Point(10, 10),
            Size = new Size(620, 22),
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

        y += 230;

        Controls.Add(new Label { Text = "Carpeta de destino:", Location = new Point(10, y), AutoSize = true });
        y += 18;
        _txtDest.Location = new Point(10, y);
        _txtDest.Size = new Size(620, 22);
        Controls.Add(_txtDest);
        var btnDest = new Button
        {
            Text = "...",
            Location = new Point(640, y - 1),
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
        y += 30;

        _progress.Host.Location = new Point(10, y);
        y += DownloadProgressPanel.PreferredHeight + 8;

        _btnDownload = new Button
        {
            Text = "Descargar",
            Location = new Point(520, 820),
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        _btnDownload.Click += async (_, _) => await StartDownloadAsync();
        Controls.Add(_btnDownload);

        _btnCancel = new Button
        {
            Text = "Cancelar",
            Location = new Point(620, 820),
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
            if (!IsBusy)
            {
                _progress.StopTimer();
                return;
            }
            if (!_closeRequested)
            {
                var r = MessageBox.Show(this,
                    "Hay un analisis o descarga en curso.\n\n¿Cancelar la operacion y cerrar?",
                    "Recodio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            e.Cancel = true;
            _closeRequested = true;
            _btnCancel.Enabled = false;
            _progress.SetStatus("Cancelando...");
            _cts?.Cancel();
            _analyzeCts?.Cancel();
        };
        FormClosed += (_, _) => _progress.StopTimer();

        Shown += (_, _) =>
        {
            if (_config.ClipboardAutoFill && string.IsNullOrWhiteSpace(_txtQuery.Text))
            {
                var clip = ClipboardHelper.TryGetSpotifyUrl();
                if (clip != null)
                {
                    _txtQuery.Text = clip;
                    _progress.SetStatus("URL de Spotify detectada en el portapapeles.");
                }
            }
            _txtQuery.Focus();
            _txtQuery.SelectAll();
        };
    }

    public bool IsBusy => _cts != null || _analyzeCts != null;

    /// <summary>Refresh dest/cookies/options from main config while this form is open.</summary>
    public void ApplyExternalSettings(AppConfig config)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ApplyExternalSettings(config)); return; }
        if (IsBusy)
        {
            _progress.SetStatus("Config cambio; se aplicara en la proxima descarga.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(config.SpotDlDownloadDir))
            _txtDest.Text = config.SpotDlDownloadDir;
        var idx = BrowserCookies.IndexOfKey(config.EffectiveCookiesBrowser());
        if (idx >= 0 && idx < _cmbCookies.Items.Count)
            _cmbCookies.SelectedIndex = idx;
        // Keep form checkboxes in sync with config when not mid-download.
        _chkLyrics.Checked = config.SpotDlLyrics;
        _chkSkipExisting.Checked = config.SpotDlSkipExisting;
        _chkOrganizeFolders.Checked = config.SpotDlOrganizeInFolders;
        _chkSponsorBlock.Checked = config.SpotDlSponsorBlock;
        _numThreads.Value = Math.Clamp(config.SpotDlThreads, 1, SpotDlDownloader.MaxThreads);
        var fi = Array.IndexOf(SpotDlDownloader.Formats, config.SpotDlFormat);
        if (fi >= 0) _cmbFormat.SelectedIndex = fi;
        var bi = Array.IndexOf(SpotDlDownloader.Bitrates, config.SpotDlBitrate);
        if (bi >= 0) _cmbBitrate.SelectedIndex = bi;
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

    private string SelectedCookies() => BrowserCookies.KeyAt(_cmbCookies.SelectedIndex);

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
        var cookies = SelectedCookies();
        _config.CookiesBrowser = cookies;
        _config.SpotDlCookiesBrowser = cookies;
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
        _progress.SetStatus("Analizando playlist/cancion...");
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
            _progress.SetStats($"Preview: {_tracks.Count} cancion(es)"
                + (already > 0 ? $", {already} ya bajadas" : ""));
            _progress.SetStatus($"Listo: {listLabel}");
        }
        catch (OperationCanceledException)
        {
            _lblInfo.Text = "Analisis cancelado.";
            _progress.SetStatus("Analisis cancelado.");
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "No se pudo analizar.";
            _progress.SetStatus("Error al analizar.", isError: true);
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
        List<(string Label, string Query, List<string>? TrackUrls, List<SpotDlTrack>? Meta)> jobs = [];

        if (_queue.Count > 0)
        {
            foreach (var q in _queue)
                jobs.Add((q.Label, q.Query, null, null));
        }
        else if (_tracks.Count > 0)
        {
            var selected = new List<string>();
            var meta = new List<SpotDlTrack>();
            for (var i = 0; i < _clbTracks.Items.Count; i++)
            {
                if (!_clbTracks.GetItemChecked(i)) continue;
                var t = _tracks[i];
                if (string.IsNullOrWhiteSpace(t.Url)) continue;
                selected.Add(t.Url);
                meta.Add(t);
            }
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Selecciona al menos una cancion, o vacia el preview (reanaliza) y descarga la URL/cola.", "Recodio");
                return;
            }
            jobs.Add(($"preview ({selected.Count} tracks)", _txtQuery.Text.Trim(), selected, meta));
        }
        else
        {
            var query = _txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show(this, "Pega una URL, analiza una playlist, o agrega items a la cola.", "Recodio");
                return;
            }
            jobs.Add((query, query, null, null));
        }

        var destDir = _txtDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(destDir))
        {
            MessageBox.Show(this, "Elegi una carpeta de destino.", "Recodio");
            return;
        }

        PersistSettings();

        var providersList = SelectedProviders();
        var cookies = SelectedCookies();

        _btnDownload.Enabled = false;
        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true;

        _jobCount = jobs.Count;
        _jobIndex = 0;
        _progress.Reset(
            statsText: _jobCount > 1 ? $"Cola URL 0 / {_jobCount}" : "Cola: 1 trabajo",
            status: "Iniciando descarga...");
        _progress.StartSession(destDir);

        // Multi-URL: show all jobs in checklist; single job: show tracks.
        if (_jobCount > 1)
        {
            _progress.SetQueueItems(jobs.Select((j, idx) =>
                new ProgressQueueItem($"job:{idx}", j.Label)));
        }

        _cts = new CancellationTokenSource();
        var totalOk = 0;
        var totalFail = 0;
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                _jobIndex = i;
                _jobLabel = job.Label;

                // Single-job checklist = tracks; multi-job keeps URL list and marks current.
                if (_jobCount == 1)
                {
                    if (job.Meta is { Count: > 0 })
                    {
                        _progress.SetQueueItems(job.Meta.Select(t =>
                            new ProgressQueueItem(t.Url, $"{t.Artist} - {t.Name}")));
                    }
                    else if (job.TrackUrls is { Count: > 0 })
                    {
                        _progress.SetQueueItems(job.TrackUrls.Select(u =>
                            new ProgressQueueItem(u, u)));
                    }
                    else
                    {
                        _progress.SetQueueItems([new ProgressQueueItem(job.Query, job.Label)]);
                    }
                }
                else
                {
                    _progress.SetItemState($"job:{i}", QueueItemState.Downloading, job.Label);
                }

                Ui(() =>
                {
                    _progress.SetStats(_jobCount > 1
                        ? $"Cola URL {i + 1} / {_jobCount}: {job.Label}"
                        : $"Trabajo: {job.Label}");
                    _progress.SetStatus($"Iniciando: {job.Label}");
                    _progress.SetCurrent("");
                });

                IReadOnlyList<SpotDlTrack>? meta = job.Meta;
                var lastDone = 0;
                var lastSkipped = 0;
                var lastTotal = job.TrackUrls?.Count ?? 0;

                var failCount = await SpotDlDownloader.DownloadAsync(
                    _spotdlPath, _ffmpegPath, job.Query, job.TrackUrls,
                    _config.SpotDlFormat, _config.SpotDlBitrate, _config.SpotDlLyrics,
                    _config.SpotDlThreads, _config.SpotDlSkipExisting,
                    _config.SpotDlOrganizeInFolders, _config.SpotDlSponsorBlock, destDir,
                    providersList, cookies,
                    onLine: HandleDownloadLine,
                    onProgress: u =>
                    {
                        // Capture on worker thread (before UI marshal) for post-job totals.
                        if (u.Total > 0)
                        {
                            lastDone = u.Done;
                            lastSkipped = u.Skipped;
                            lastTotal = u.Total;
                        }
                        try
                        {
                            if (IsDisposed || !IsHandleCreated) return;
                            BeginInvoke(() =>
                            {
                                try
                                {
                                    if (IsDisposed) return;
                                    // Blend multi-URL jobs into overall %
                                    if (_jobCount > 1 && u.Total > 0)
                                    {
                                        var finished = u.Done + u.Skipped;
                                        var jobFrac = (double)finished / Math.Max(u.Total, 1);
                                        var overall = (int)Math.Round((_jobIndex + jobFrac) * 100.0 / _jobCount);
                                        _progress.Apply(u with { OverallPercent = Math.Clamp(overall, 0, 100) });
                                        _progress.SetStats(
                                            $"Cola URL {_jobIndex + 1}/{_jobCount}"
                                            + $" · {u.Done} ok"
                                            + (u.Skipped > 0 ? $" · {u.Skipped} omit" : "")
                                            + (u.Failed > 0 ? $" · {u.Failed} err" : "")
                                            + $" · total job {u.Total}");
                                    }
                                    else
                                    {
                                        _progress.Apply(u);
                                    }
                                }
                                catch { /* dispose */ }
                            });
                        }
                        catch { /* dispose race */ }
                    },
                    _cts.Token,
                    knownTracksMeta: meta);

                // Prefer live progress totals over failCount guesses
                var completedOk = lastTotal > 0
                    ? Math.Max(lastDone + lastSkipped - failCount, 0)
                    : Math.Max((job.TrackUrls?.Count ?? 0) - failCount, 0);
                if (lastTotal <= 0 && job.TrackUrls is null)
                    completedOk = failCount == 0 ? 1 : 0; // single URL job without track list

                totalFail += failCount;
                totalOk += completedOk;

                if (_jobCount > 1)
                {
                    _progress.SetItemState($"job:{i}",
                        failCount == 0 ? QueueItemState.Done
                        : completedOk > 0 ? QueueItemState.Done
                        : QueueItemState.Failed,
                        job.Label);
                }

                var status = failCount == 0 ? "ok"
                    : lastTotal > 0 && failCount < lastTotal ? "partial"
                    : "fail";
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
                ? $"Listo: {totalOk} ok, {totalFail} con error."
                : totalOk > 0
                    ? $"Listo: {totalOk} cancion(es) OK."
                    : "Descarga completa.";
            Ui(() =>
            {
                _progress.SetStats(_jobCount > 1
                    ? $"Cola: {_jobCount}/{_jobCount} URLs"
                    : $"{totalOk} ok" + (totalFail > 0 ? $" · {totalFail} err" : ""));
                _progress.EndSession(summary, isError: totalFail > 0, folderPath: destDir,
                    markComplete: totalFail == 0 || totalOk > 0);
            });

            if (totalFail > 0)
                MessageBox.Show(this, summary, "Recodio", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (_queue.Count > 0)
            {
                _queue.Clear();
                _lstQueue.Items.Clear();
            }
            RefreshArchiveMarks();
        }
        catch (OperationCanceledException)
        {
            Ui(() => _progress.EndSession("Descarga cancelada.", folderPath: destDir, markComplete: false));
        }
        catch (Exception ex)
        {
            Ui(() => _progress.EndSession(ex.Message, isError: true, folderPath: destDir, markComplete: false));
            _onHistory?.Invoke(new HistoryEntry
            {
                Name = jobs.Count > 0 ? (jobs[0].Label ?? "spotDL") : "spotDL",
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

    private void HandleDownloadLine(string line)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => HandleDownloadLine(line)); return; }
            if (string.IsNullOrWhiteSpace(line)) return;

            if (line.StartsWith(">> ", StringComparison.Ordinal))
            {
                var msg = line[3..].Trim();
                if (msg.Length > 0)
                    _progress.SetStatus(msg, isError: msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || msg.StartsWith("NO se pudo", StringComparison.OrdinalIgnoreCase));
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
