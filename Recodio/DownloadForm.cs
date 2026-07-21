namespace Recodio;

public class DownloadForm : Form
{
    private readonly string _ytDlpPath;
    private readonly string _ffmpegPath;
    private readonly Action<string> _onDestDirChanged;

    private readonly TextBox _txtUrl;
    private readonly Button _btnAnalyze;
    private readonly Label _lblInfo;
    private readonly CheckedListBox _clbEntries;
    private readonly Button _btnAll;
    private readonly Button _btnNone;
    private readonly RadioButton _rbMp4;
    private readonly RadioButton _rbMp3;
    private readonly ComboBox _cmbVideoQuality;
    private readonly ComboBox _cmbAudioQuality;
    private readonly CheckBox _chkOrganizeFolders;
    private readonly CheckBox _chkRemoveSponsors;
    private readonly TextBox _txtDest;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _txtLog;
    private readonly Button _btnDownload;
    private readonly Button _btnCancel;
    private readonly Button _btnClose;

    private List<PlaylistEntry> _entries = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _analyzeCts;
    private bool _closeRequested;

    public DownloadForm(string ytDlpPath, string ffmpegPath, string initialDestDir, Action<string> onDestDirChanged)
    {
        _ytDlpPath = ytDlpPath;
        _ffmpegPath = ffmpegPath;
        _onDestDirChanged = onDestDirChanged;

        Text = "Descargar video / playlist";
        Size = new Size(640, 662);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 542);

        var lblUrl = new Label { Text = "URL (video o playlist):", Location = new Point(10, 12), AutoSize = true };
        Controls.Add(lblUrl);

        _txtUrl = new TextBox { Location = new Point(10, 32), Size = new Size(500, 22) };
        Controls.Add(_txtUrl);

        _btnAnalyze = new Button { Text = "Analizar", Location = new Point(520, 31), Size = new Size(90, 24) };
        _btnAnalyze.Click += async (_, _) => await AnalyzeAsync();
        Controls.Add(_btnAnalyze);

        _lblInfo = new Label { Text = "Pega una URL y presiona Analizar.", Location = new Point(10, 62), AutoSize = true };
        Controls.Add(_lblInfo);

        _clbEntries = new CheckedListBox
        {
            Location = new Point(10, 85),
            Size = new Size(600, 140),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
        };
        Controls.Add(_clbEntries);

        _btnAll = new Button { Text = "Todos", Location = new Point(10, 230), Size = new Size(80, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        _btnAll.Click += (_, _) => SetAllChecked(true);
        Controls.Add(_btnAll);

        _btnNone = new Button { Text = "Ninguno", Location = new Point(95, 230), Size = new Size(80, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        _btnNone.Click += (_, _) => SetAllChecked(false);
        Controls.Add(_btnNone);

        var grpFormat = new GroupBox { Text = "Formato", Location = new Point(10, 262), Size = new Size(600, 45), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _rbMp4 = new RadioButton { Text = "MP4 (video)", Location = new Point(10, 18), AutoSize = true, Checked = true };
        _rbMp3 = new RadioButton { Text = "MP3 (solo audio)", Location = new Point(140, 18), AutoSize = true };
        grpFormat.Controls.Add(_rbMp4);
        grpFormat.Controls.Add(_rbMp3);
        Controls.Add(grpFormat);

        var lblVideoQ = new Label { Text = "Calidad de video:", Location = new Point(10, 315), AutoSize = true };
        Controls.Add(lblVideoQ);
        _cmbVideoQuality = new ComboBox { Location = new Point(10, 333), Size = new Size(190, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbVideoQuality.Items.AddRange(["Mejor disponible", "2160p (4K)", "1440p (2K)", "1080p", "720p", "480p", "360p"]);
        _cmbVideoQuality.SelectedIndex = 0;
        Controls.Add(_cmbVideoQuality);

        var lblAudioQ = new Label { Text = "Calidad de audio:", Location = new Point(220, 315), AutoSize = true };
        Controls.Add(lblAudioQ);
        _cmbAudioQuality = new ComboBox { Location = new Point(220, 333), Size = new Size(190, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbAudioQuality.Items.AddRange(["Alta (VBR ~245 kbps)", "Media (192 kbps)", "Baja (128 kbps)"]);
        _cmbAudioQuality.SelectedIndex = 0;
        Controls.Add(_cmbAudioQuality);

        _rbMp4.CheckedChanged += (_, _) => _cmbVideoQuality.Enabled = _rbMp4.Checked;
        _rbMp3.CheckedChanged += (_, _) => _cmbVideoQuality.Enabled = !_rbMp3.Checked;

        _chkOrganizeFolders = new CheckBox
        {
            Text = "Organizar en subcarpetas por playlist (videos sueltos quedan sueltos)",
            Location = new Point(10, 360),
            AutoSize = true,
            Checked = true,
        };
        Controls.Add(_chkOrganizeFolders);

        _chkRemoveSponsors = new CheckBox
        {
            Text = "Recortar auspicios automaticamente (SponsorBlock)",
            Location = new Point(10, 384),
            AutoSize = true,
            Checked = false,
        };
        Controls.Add(_chkRemoveSponsors);

        var lblDest = new Label { Text = "Carpeta de destino:", Location = new Point(10, 410), AutoSize = true };
        Controls.Add(lblDest);
        _txtDest = new TextBox { Text = initialDestDir, Location = new Point(10, 428), Size = new Size(500, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_txtDest);
        var btnDest = new Button { Text = "...", Location = new Point(520, 427), Size = new Size(40, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnDest.Click += (_, _) =>
        {
            // InitialDirectory (not SelectedPath): SelectedPath opens the picker at the PARENT
            // with the current name pre-typed, which makes "Seleccionar carpeta" look broken.
            using var fbd = new FolderBrowserDialog { InitialDirectory = _txtDest.Text.Trim() };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            _txtDest.Text = fbd.SelectedPath;
            // Persist immediately on pick, not just after a completed download - otherwise
            // closing the dialog or cancelling loses the choice for next time.
            _onDestDirChanged(fbd.SelectedPath);
        };
        Controls.Add(btnDest);

        // A hand-typed path used to persist only when a download actually started, so
        // type + close-dialog silently lost the choice. Persist as soon as focus leaves.
        _txtDest.Leave += (_, _) =>
        {
            var typed = _txtDest.Text.Trim();
            if (typed.Length > 0) _onDestDirChanged(typed);
        };

        _progressBar = new ProgressBar { Location = new Point(10, 460), Size = new Size(600, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_progressBar);

        _txtLog = new TextBox
        {
            Location = new Point(10, 487),
            Size = new Size(600, 100),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtLog);

        _btnDownload = new Button { Text = "Descargar", Location = new Point(345, 594), Size = new Size(90, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnDownload.Click += async (_, _) => await StartDownloadAsync();
        Controls.Add(_btnDownload);

        _btnCancel = new Button { Text = "Cancelar", Location = new Point(440, 594), Size = new Size(80, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        _btnClose = new Button { Text = "Cerrar", Location = new Point(525, 594), Size = new Size(85, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnClose.Click += (_, _) => Close();
        Controls.Add(_btnClose);

        FormClosing += (_, e) =>
        {
            // Let a running analysis/download finish cancelling before the form (and its
            // controls) actually get disposed, otherwise the operation's own finally block
            // crashes touching disposed controls once it resumes.
            if (_cts == null && _analyzeCts == null) return;
            e.Cancel = true;
            _closeRequested = true;
            _cts?.Cancel();
            _analyzeCts?.Cancel();
        };

        // Without this, the default focus lands elsewhere and a paste (Ctrl+V) right after
        // opening the dialog silently goes nowhere.
        Shown += (_, _) => _txtUrl.Focus();
    }

    private void CloseIfPending()
    {
        if (_closeRequested && _cts == null && _analyzeCts == null) Close();
    }

    private void SetAllChecked(bool value)
    {
        for (var i = 0; i < _clbEntries.Items.Count; i++) _clbEntries.SetItemChecked(i, value);
    }

    private async Task AnalyzeAsync()
    {
        var url = _txtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Pega una URL primero.", "Recodio");
            return;
        }

        _btnAnalyze.Enabled = false;
        _lblInfo.Text = "Analizando...";
        _clbEntries.Items.Clear();
        _analyzeCts = new CancellationTokenSource();
        try
        {
            var result = await YtDlpDownloader.AnalyzeAsync(_ytDlpPath, url, _analyzeCts.Token);
            _entries = result.Entries;
            foreach (var e in _entries) _clbEntries.Items.Add(e.Title, true);

            _lblInfo.Text = result.IsPlaylist
                ? $"Playlist: {result.PlaylistTitle} ({_entries.Count} videos) - desmarca los que no quieras descargar"
                : "Video individual detectado.";
        }
        catch (OperationCanceledException)
        {
            _lblInfo.Text = "Analisis cancelado.";
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "No se pudo analizar la URL.";
            MessageBox.Show(this, ex.Message, "Error al analizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnAnalyze.Enabled = true;
            _analyzeCts.Dispose();
            _analyzeCts = null;
            CloseIfPending();
        }
    }

    private async Task StartDownloadAsync()
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, "Primero analiza una URL.", "Recodio");
            return;
        }

        var selectedIndices = new List<int>();
        for (var i = 0; i < _clbEntries.Items.Count; i++)
        {
            if (_clbEntries.GetItemChecked(i)) selectedIndices.Add(_entries[i].Index);
        }
        if (selectedIndices.Count == 0)
        {
            MessageBox.Show(this, "Selecciona al menos un video.", "Recodio");
            return;
        }

        var destDir = _txtDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(destDir))
        {
            MessageBox.Show(this, "Elegi una carpeta de destino.", "Recodio");
            return;
        }
        _onDestDirChanged(destDir);

        var format = _rbMp3.Checked ? "mp3" : "mp4";
        var videoQuality = _cmbVideoQuality.SelectedIndex switch
        {
            1 => "2160", 2 => "1440", 3 => "1080", 4 => "720", 5 => "480", 6 => "360", _ => "best",
        };
        var audioQuality = _cmbAudioQuality.SelectedIndex switch { 1 => "medium", 2 => "low", _ => "high" };

        var url = _txtUrl.Text.Trim();
        var playlistItems = _entries.Count > 1 ? selectedIndices : null;

        _btnDownload.Enabled = false;
        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true;
        _progressBar.Value = 0;
        _txtLog.Clear();

        _cts = new CancellationTokenSource();
        try
        {
            var failCount = await YtDlpDownloader.DownloadAsync(
                _ytDlpPath, _ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                _chkOrganizeFolders.Checked, _chkRemoveSponsors.Checked, destDir,
                onLine: AppendLog,
                onProgress: pct => Invoke(() => _progressBar.Value = Math.Clamp(pct, 0, 100)),
                _cts.Token);

            var summary = failCount > 0
                ? $"Descarga completa: {failCount} video(s) con error (revisa el log)."
                : "Descarga completa.";
            AppendLog($">> {summary}");
            MessageBox.Show(this, summary, "Recodio");
        }
        catch (OperationCanceledException)
        {
            AppendLog(">> Descarga cancelada.");
        }
        catch (Exception ex)
        {
            AppendLog($">> ERROR: {ex.Message}");
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

    private void AppendLog(string line)
    {
        if (InvokeRequired) { Invoke(() => AppendLog(line)); return; }
        _txtLog.AppendText(line + Environment.NewLine);
    }
}
