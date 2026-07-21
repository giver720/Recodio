namespace Recodio;

public class SpotDlForm : Form
{
    private record QueueItem(string Label, string Query);

    private readonly string _spotdlPath;
    private readonly string _ffmpegPath;
    private readonly AppConfig _config;
    private readonly Action<string, string, string, bool, int, bool, bool, bool> _onSettingsChanged;
    private readonly Action _saveConfig;

    private readonly TextBox _txtQuery;
    private readonly ListBox _lstQueue;
    private readonly List<QueueItem> _queue = [];
    private readonly ComboBox _cmbFormat;
    private readonly ToolTip _formatTip;
    private readonly ComboBox _cmbBitrate;
    private readonly CheckBox _chkLyrics;
    private readonly NumericUpDown _numThreads;
    private readonly CheckBox _chkSkipExisting;
    private readonly CheckBox _chkOrganizeFolders;
    private readonly CheckBox _chkSponsorBlock;
    private readonly TextBox _txtDest;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgress;
    private readonly TextBox _txtLog;
    private readonly Button _btnDownload;
    private readonly Button _btnCancel;

    private CancellationTokenSource? _cts;
    private bool _closeRequested;

    public SpotDlForm(string spotdlPath, string ffmpegPath, AppConfig config,
        Action<string, string, string, bool, int, bool, bool, bool> onSettingsChanged, Action saveConfig)
    {
        _spotdlPath = spotdlPath;
        _ffmpegPath = ffmpegPath;
        _config = config;
        _onSettingsChanged = onSettingsChanged;
        _saveConfig = saveConfig;

        Text = "Descargar con spotDL";
        Size = new Size(660, 756);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(600, 656);

        // ---------- Section 1: que descargar ----------
        var grpWhat = new GroupBox { Text = "Que descargar", Location = new Point(10, 10), Size = new Size(624, 184), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(grpWhat);

        var lblQuery = new Label { Text = "URL de Spotify (cancion/album/playlist/artista) o busqueda de texto:", Location = new Point(10, 20), AutoSize = true };
        grpWhat.Controls.Add(lblQuery);

        _txtQuery = new TextBox { Location = new Point(10, 38), Size = new Size(600, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        grpWhat.Controls.Add(_txtQuery);

        var lblQueue = new Label { Text = "Cola de descargas (opcional, se procesan en orden):", Location = new Point(10, 96), AutoSize = true };
        grpWhat.Controls.Add(lblQueue);

        _lstQueue = new ListBox { Location = new Point(10, 114), Size = new Size(504, 60), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        grpWhat.Controls.Add(_lstQueue);

        var btnAddToQueue = new Button { Text = "+ Agregar a la cola", Location = new Point(464, 64), Size = new Size(150, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnAddToQueue.Click += (_, _) =>
        {
            var query = _txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show(this, "Escribi una URL o busqueda antes de agregarla a la cola.", "Recodio");
                return;
            }
            _queue.Add(new QueueItem(query, query));
            _lstQueue.Items.Add(query);
        };
        grpWhat.Controls.Add(btnAddToQueue);

        var btnRemoveQueue = new Button { Text = "Quitar", Location = new Point(524, 114), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnRemoveQueue.Click += (_, _) =>
        {
            var idx = _lstQueue.SelectedIndex;
            if (idx < 0) return;
            _queue.RemoveAt(idx);
            _lstQueue.Items.RemoveAt(idx);
        };
        grpWhat.Controls.Add(btnRemoveQueue);

        var btnClearQueue = new Button { Text = "Vaciar", Location = new Point(524, 144), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnClearQueue.Click += (_, _) =>
        {
            _queue.Clear();
            _lstQueue.Items.Clear();
        };
        grpWhat.Controls.Add(btnClearQueue);

        // ---------- Section 2: opciones de descarga ----------
        var grpOptions = new GroupBox { Text = "Opciones de descarga", Location = new Point(10, 202), Size = new Size(624, 218), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(grpOptions);

        var lblFormat = new Label { Text = "Formato:", Location = new Point(10, 24), AutoSize = true };
        grpOptions.Controls.Add(lblFormat);

        _cmbFormat = new ComboBox { Location = new Point(10, 42), Size = new Size(150, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var f in SpotDlDownloader.Formats) _cmbFormat.Items.Add(f);
        _cmbFormat.SelectedIndex = Math.Max(0, Array.IndexOf(SpotDlDownloader.Formats, config.SpotDlFormat));
        grpOptions.Controls.Add(_cmbFormat);

        // opus is (usually) exactly what YouTube Music serves, so spotDL just moves the file
        // instead of re-encoding it with ffmpeg - meaningfully faster on big playlists. Any
        // other format always needs a per-song ffmpeg pass (confirmed in spotDL's own source).
        _formatTip = new ToolTip();
        _formatTip.SetToolTip(_cmbFormat,
            "opus = mas rapido: spotDL mueve el archivo tal cual sin recodificar.\nCualquier otro formato siempre pasa por ffmpeg cancion por cancion.");

        var lblBitrate = new Label { Text = "Bitrate:", Location = new Point(175, 24), AutoSize = true };
        grpOptions.Controls.Add(lblBitrate);

        _cmbBitrate = new ComboBox { Location = new Point(175, 42), Size = new Size(150, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var b in SpotDlDownloader.Bitrates) _cmbBitrate.Items.Add(b);
        _cmbBitrate.SelectedIndex = Math.Max(0, Array.IndexOf(SpotDlDownloader.Bitrates, config.SpotDlBitrate));
        grpOptions.Controls.Add(_cmbBitrate);

        _chkLyrics = new CheckBox
        {
            Text = "Incluir letras (Genius)",
            Location = new Point(340, 44),
            AutoSize = true,
            Checked = config.SpotDlLyrics,
        };
        grpOptions.Controls.Add(_chkLyrics);

        var lblThreads = new Label { Text = "Hilos:", Location = new Point(10, 74), AutoSize = true };
        grpOptions.Controls.Add(lblThreads);

        _numThreads = new NumericUpDown { Location = new Point(60, 72), Size = new Size(50, 22), Minimum = 1, Maximum = SpotDlDownloader.MaxThreads, Value = Math.Clamp(config.SpotDlThreads, 1, SpotDlDownloader.MaxThreads) };
        grpOptions.Controls.Add(_numThreads);

        _chkSkipExisting = new CheckBox
        {
            Text = "Solo descargar canciones nuevas (recordar lo ya descargado)",
            Location = new Point(130, 74),
            AutoSize = true,
            Checked = config.SpotDlSkipExisting,
        };
        grpOptions.Controls.Add(_chkSkipExisting);

        _chkOrganizeFolders = new CheckBox
        {
            Text = "Organizar en subcarpetas por playlist/album (las canciones sueltas quedan sueltas)",
            Location = new Point(130, 96),
            AutoSize = true,
            Checked = config.SpotDlOrganizeInFolders,
        };
        grpOptions.Controls.Add(_chkOrganizeFolders);

        _chkSponsorBlock = new CheckBox
        {
            Text = "Quitar partes que no son musica (SponsorBlock: intros, outros, sponsors)",
            Location = new Point(130, 118),
            AutoSize = true,
            Checked = config.SpotDlSponsorBlock,
        };
        grpOptions.Controls.Add(_chkSponsorBlock);

        var btnResetArchive = new LinkLabel { Text = "reiniciar registro de esta carpeta", Location = new Point(130, 142), AutoSize = true };
        grpOptions.Controls.Add(btnResetArchive);

        var lblDest = new Label { Text = "Carpeta de destino:", Location = new Point(10, 170), AutoSize = true };
        grpOptions.Controls.Add(lblDest);

        _txtDest = new TextBox { Text = config.SpotDlDownloadDir, Location = new Point(10, 188), Size = new Size(504, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        grpOptions.Controls.Add(_txtDest);

        var btnDest = new Button { Text = "...", Location = new Point(524, 187), Size = new Size(40, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnDest.Click += (_, _) =>
        {
            // InitialDirectory (not SelectedPath): SelectedPath makes the Vista picker open at
            // the PARENT folder with the current name pre-typed in the "Carpeta:" box, so
            // clicking "Seleccionar carpeta" either returns the old folder again or just
            // navigates without closing - it looks like the button does nothing. With
            // InitialDirectory the dialog opens inside the current folder with an empty box,
            // and select always returns the folder being viewed.
            using var fbd = new FolderBrowserDialog { InitialDirectory = _txtDest.Text.Trim() };
            if (fbd.ShowDialog(this) != DialogResult.OK) return;
            _txtDest.Text = fbd.SelectedPath;
            // Persist immediately on pick, not just after a completed download - otherwise
            // closing the dialog or cancelling loses the choice for next time.
            _config.SpotDlDownloadDir = fbd.SelectedPath;
            _saveConfig();
        };
        grpOptions.Controls.Add(btnDest);

        // A hand-typed path used to persist only when a download actually started, so
        // type + close-dialog silently lost the choice. Persist as soon as focus leaves.
        _txtDest.Leave += (_, _) =>
        {
            var typed = _txtDest.Text.Trim();
            if (typed.Length == 0 || typed == _config.SpotDlDownloadDir) return;
            _config.SpotDlDownloadDir = typed;
            _saveConfig();
        };

        btnResetArchive.Click += (_, _) =>
        {
            var path = SpotDlDownloader.ArchivePathFor(_txtDest.Text.Trim());
            if (File.Exists(path))
            {
                File.Delete(path);
                MessageBox.Show(this, "Registro reiniciado. La proxima descarga en esta carpeta va a revisar todo de nuevo.", "Recodio");
            }
            else
            {
                MessageBox.Show(this, "Esta carpeta todavia no tiene un registro guardado.", "Recodio");
            }
        };

        // ---------- Progress + log (always visible, no group box needed) ----------
        _progressBar = new ProgressBar { Location = new Point(10, 432), Size = new Size(624, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_progressBar);

        _lblProgress = new Label { Text = "", Location = new Point(10, 455), AutoSize = true };
        Controls.Add(_lblProgress);

        _txtLog = new TextBox
        {
            Location = new Point(10, 479),
            Size = new Size(624, 210),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtLog);

        _btnDownload = new Button { Text = "Descargar", Location = new Point(474, 699), Size = new Size(90, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnDownload.Click += async (_, _) => await StartDownloadAsync();
        Controls.Add(_btnDownload);

        _btnCancel = new Button { Text = "Cancelar", Location = new Point(570, 699), Size = new Size(64, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        FormClosing += (_, e) =>
        {
            // Let a running download finish cancelling before the form (and its controls)
            // actually get disposed, otherwise the operation's own finally block crashes
            // touching disposed controls once it resumes.
            if (_cts == null) return;
            e.Cancel = true;
            _closeRequested = true;
            _cts.Cancel();
        };

        // Without this, the default focus can land elsewhere and a paste (Ctrl+V) right
        // after opening the dialog silently goes nowhere.
        Shown += (_, _) => _txtQuery.Focus();
    }

    private void CloseIfPending()
    {
        if (_closeRequested && _cts == null) Close();
    }

    private async Task StartDownloadAsync()
    {
        // An empty queue means "just download what's in the query field right now" - keeps
        // the single-item flow working exactly as before for anyone who ignores the queue.
        List<QueueItem> items;
        if (_queue.Count > 0)
        {
            items = [.. _queue];
        }
        else
        {
            var query = _txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show(this, "Pega una URL de Spotify, escribi una busqueda, o agrega items a la cola.", "Recodio");
                return;
            }
            items = [new QueueItem(query, query)];
        }

        var destDir = _txtDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(destDir))
        {
            MessageBox.Show(this, "Elegi una carpeta de destino.", "Recodio");
            return;
        }

        var format = SpotDlDownloader.Formats[_cmbFormat.SelectedIndex];
        var bitrate = SpotDlDownloader.Bitrates[_cmbBitrate.SelectedIndex];
        var lyrics = _chkLyrics.Checked;
        var threads = (int)_numThreads.Value;
        var skipExisting = _chkSkipExisting.Checked;
        var organizeInFolders = _chkOrganizeFolders.Checked;
        var sponsorBlock = _chkSponsorBlock.Checked;
        _onSettingsChanged(destDir, format, bitrate, lyrics, threads, skipExisting, organizeInFolders, sponsorBlock);

        _btnDownload.Enabled = false;
        _btnCancel.Enabled = true;
        _progressBar.Value = 0;
        _txtLog.Clear();
        AppendLog($">> Carpeta de destino: {destDir}");
        if (organizeInFolders)
            AppendLog(">> Las canciones de playlists/albumes van a una subcarpeta con ese nombre; las sueltas quedan directo en la carpeta.");

        _cts = new CancellationTokenSource();
        var totalOk = 0;
        var totalFail = 0;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                _lblProgress.Text = items.Count > 1
                    ? $"Cola {i + 1} de {items.Count}: {item.Label}"
                    : "Buscando en Spotify...";
                AppendLog($">> {item.Label}");

                var lastTotal = 0;
                var failCount = await SpotDlDownloader.DownloadAsync(
                    _spotdlPath, _ffmpegPath, item.Query, format, bitrate, lyrics, threads, skipExisting,
                    organizeInFolders, sponsorBlock, destDir,
                    onLine: AppendLog,
                    onProgress: (done, total) => { lastTotal = total; SetProgress(done, total); },
                    _cts.Token);

                totalFail += failCount;
                totalOk += Math.Max(lastTotal - failCount, 0);
            }

            // spotDL can exit 0 even when some songs failed to match/download - don't call
            // that "completa" without saying how many actually made it.
            var summary = totalFail > 0
                ? $"Descarga completa: {totalOk} ok, {totalFail} con error (revisa el log)."
                : "Descarga completa.";
            AppendLog($">> {summary}");
            MessageBox.Show(this, summary, "Recodio");

            // Queue consumed successfully - clear it so re-clicking Descargar doesn't repeat it.
            if (_queue.Count > 0)
            {
                _queue.Clear();
                _lstQueue.Items.Clear();
            }
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
