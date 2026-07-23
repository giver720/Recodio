namespace Recodio;

public class ConvertFormatForm : Form
{
    private readonly string _ffmpegPath;
    private readonly Action<HistoryEntry>? _onHistory;
    private readonly ListBox _lstFiles;
    private readonly ComboBox _cmbFormat;
    private readonly ComboBox _cmbQuality;
    private readonly ComboBox _cmbOnExists;
    private readonly TextBox _txtDest;
    private readonly CheckBox _chkDeleteOriginal;
    private readonly DownloadProgressPanel _progress;
    private readonly Button _btnConvert;
    private readonly Button _btnCancel;

    private CancellationTokenSource? _cts;
    private bool _closeRequested;

    public ConvertFormatForm(
        string ffmpegPath,
        IEnumerable<string> initialFiles,
        string defaultQuality,
        string onFileExists = "skip",
        Action<HistoryEntry>? onHistory = null)
    {
        _ffmpegPath = ffmpegPath;
        _onHistory = onHistory;

        Text = "Convertir a otro formato";
        Size = new Size(620, 620);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(540, 560);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddFiles(FormatConverter.ExpandDroppedPaths(paths));
        };

        var lblFiles = new Label { Text = "Archivos a convertir (o arrastralos aca):", Location = new Point(10, 10), AutoSize = true };
        Controls.Add(lblFiles);

        _lstFiles = new ListBox
        {
            Location = new Point(10, 30),
            Size = new Size(580, 120),
            AllowDrop = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            HorizontalScrollbar = true,
            SelectionMode = SelectionMode.MultiExtended,
        };
        _lstFiles.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _lstFiles.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddFiles(FormatConverter.ExpandDroppedPaths(paths));
        };
        Controls.Add(_lstFiles);

        var btnAdd = new Button { Text = "Agregar...", Location = new Point(10, 155), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        btnAdd.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Elegi archivos" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        };
        Controls.Add(btnAdd);

        var btnRemove = new Button { Text = "Quitar", Location = new Point(105, 155), Size = new Size(80, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        btnRemove.Click += (_, _) =>
        {
            foreach (var item in _lstFiles.SelectedItems.Cast<string>().ToList())
                _lstFiles.Items.Remove(item);
        };
        Controls.Add(btnRemove);

        AddFiles(initialFiles);

        var lblFormat = new Label { Text = "Convertir a:", Location = new Point(10, 192), AutoSize = true };
        Controls.Add(lblFormat);

        _cmbFormat = new ComboBox { Location = new Point(10, 210), Size = new Size(220, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var f in Formats.All) _cmbFormat.Items.Add(f.Label);
        _cmbFormat.SelectedIndex = 0;
        Controls.Add(_cmbFormat);

        var lblQuality = new Label { Text = "Calidad (audio):", Location = new Point(245, 192), AutoSize = true };
        Controls.Add(lblQuality);

        _cmbQuality = new ComboBox { Location = new Point(245, 210), Size = new Size(200, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbQuality.Items.AddRange(["Alta", "Media", "Baja"]);
        _cmbQuality.SelectedIndex = defaultQuality switch { "medium" => 1, "low" => 2, _ => 0 };
        Controls.Add(_cmbQuality);

        var lblExists = new Label { Text = "Si ya existe:", Location = new Point(10, 242), AutoSize = true };
        Controls.Add(lblExists);
        _cmbOnExists = new ComboBox { Location = new Point(100, 239), Size = new Size(200, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbOnExists.Items.AddRange(["Omitir", "Sobrescribir", "Renombrar"]);
        _cmbOnExists.SelectedIndex = onFileExists switch { "overwrite" => 1, "rename" => 2, _ => 0 };
        Controls.Add(_cmbOnExists);

        var lblDest = new Label { Text = "Carpeta de destino (vacio = misma carpeta del original):", Location = new Point(10, 270), AutoSize = true };
        Controls.Add(lblDest);

        _txtDest = new TextBox { Location = new Point(10, 288), Size = new Size(520, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_txtDest);

        var btnDest = new Button { Text = "...", Location = new Point(540, 287), Size = new Size(40, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnDest.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(this) == DialogResult.OK) _txtDest.Text = fbd.SelectedPath;
        };
        Controls.Add(btnDest);

        _chkDeleteOriginal = new CheckBox
        {
            Text = "Eliminar el archivo original despues de convertir (solo queda la conversion)",
            Location = new Point(10, 318),
            AutoSize = true,
            Checked = false,
        };
        Controls.Add(_chkDeleteOriginal);

        _progress = new DownloadProgressPanel(10, 348, 580, this);

        _btnConvert = new Button { Text = "Convertir", Location = new Point(405, 570), Size = new Size(85, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnConvert.Click += async (_, _) => await ConvertAllAsync();
        Controls.Add(_btnConvert);

        _btnCancel = new Button { Text = "Cancelar", Location = new Point(495, 570), Size = new Size(80, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        FormClosing += (_, e) =>
        {
            if (_cts == null)
            {
                _progress.StopTimer();
                return;
            }
            e.Cancel = true;
            _closeRequested = true;
            _cts.Cancel();
        };
        FormClosed += (_, _) => _progress.StopTimer();
    }

    private void CloseIfPending()
    {
        if (_closeRequested && _cts == null) Close();
    }

    public void AddFiles(IEnumerable<string> files)
    {
        if (InvokeRequired) { Invoke(() => AddFiles(files)); return; }
        foreach (var f in files)
        {
            if (!_lstFiles.Items.Contains(f)) _lstFiles.Items.Add(f);
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ConvertAllAsync()
    {
        if (_lstFiles.Items.Count == 0)
        {
            MessageBox.Show(this, "Agrega al menos un archivo.", "Recodio");
            return;
        }

        var files = _lstFiles.Items.Cast<string>().ToList();
        var formatKey = Formats.All[_cmbFormat.SelectedIndex].Key;
        var quality = _cmbQuality.SelectedIndex switch { 1 => "medium", 2 => "low", _ => "high" };
        var onExists = _cmbOnExists.SelectedIndex switch { 1 => "overwrite", 2 => "rename", _ => "skip" };
        var destDir = _txtDest.Text.Trim();

        _btnConvert.Enabled = false;
        _btnCancel.Enabled = true;

        _progress.Reset(
            statsText: $"0 ok · {files.Count} pend · total {files.Count}",
            status: "Iniciando conversion...");
        var openDir = !string.IsNullOrWhiteSpace(destDir) ? destDir : Path.GetDirectoryName(files[0]);
        _progress.StartSession(openDir);
        _progress.SetQueueItems(files.Select(f =>
            new ProgressQueueItem(f, Path.GetFileName(f))));

        _cts = new CancellationTokenSource();
        var okCount = 0;
        var failCount = 0;
        var skipCount = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;
                var file = files[i];
                var fileNum = i + 1;
                var fileName = Path.GetFileName(file);

                _progress.SetItemState(file, QueueItemState.Downloading);
                _progress.Apply(new DownloadProgressUpdate
                {
                    Done = okCount,
                    Total = files.Count,
                    Skipped = skipCount,
                    Failed = failCount,
                    FilePercent = 0,
                    OverallPercent = (int)Math.Round(100.0 * i / files.Count),
                    CurrentIndex = fileNum,
                    CurrentTitle = fileName,
                    Status = $"Archivo {fileNum} de {files.Count}",
                    Phase = "convert",
                    ItemKey = file,
                    ItemState = QueueItemState.Downloading,
                });

                try
                {
                    var result = await FormatConverter.ConvertAsync(_ffmpegPath, file, destDir, formatKey, quality,
                        onProgress: pct =>
                        {
                            try
                            {
                                if (IsDisposed || !IsHandleCreated) return;
                                BeginInvoke(() =>
                                {
                                    try
                                    {
                                        if (IsDisposed) return;
                                        _progress.Apply(new DownloadProgressUpdate
                                        {
                                            Done = okCount,
                                            Total = files.Count,
                                            Skipped = skipCount,
                                            Failed = failCount,
                                            FilePercent = pct,
                                            OverallPercent = (int)Math.Round((i + pct / 100.0) * 100.0 / files.Count),
                                            CurrentIndex = fileNum,
                                            CurrentTitle = fileName,
                                            Status = $"Convirtiendo… {pct}%",
                                            Phase = "convert",
                                        });
                                    }
                                    catch { /* dispose */ }
                                });
                            }
                            catch { /* dispose */ }
                        },
                        onFileExists: onExists,
                        ct: _cts.Token);

                    if (result.Skipped)
                    {
                        skipCount++;
                        _progress.SetItemState(file, QueueItemState.Skipped);
                        _progress.SetStatus($"Omitido: {fileName}");
                    }
                    else if (result.Success)
                    {
                        okCount++;
                        _progress.SetItemState(file, QueueItemState.Done);
                        long sizeKb = 0;
                        try { sizeKb = new FileInfo(result.OutputPath).Length / 1024; } catch { /* ignore */ }
                        _onHistory?.Invoke(new HistoryEntry
                        {
                            Name = Path.GetFileName(result.OutputPath),
                            Path = result.OutputPath,
                            SizeKB = sizeKb,
                            Kind = "convert",
                            Status = "ok",
                            Detail = file,
                        });
                        if (_chkDeleteOriginal.Checked)
                        {
                            try { File.Delete(file); }
                            catch (Exception ex)
                            {
                                _progress.SetStatus($"No se pudo eliminar original: {ex.Message}", isError: true);
                            }
                        }
                    }
                    else
                    {
                        failCount++;
                        _progress.SetItemState(file, QueueItemState.Failed);
                        _progress.SetStatus(Truncate(result.Log, 120), isError: true);
                        _onHistory?.Invoke(new HistoryEntry
                        {
                            Name = fileName,
                            Path = file,
                            Kind = "convert",
                            Status = "fail",
                            Detail = Truncate(result.Log, 200),
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    _progress.SetStatus("Cancelado.");
                    break;
                }
                catch (Exception ex)
                {
                    failCount++;
                    _progress.SetItemState(file, QueueItemState.Failed);
                    _progress.SetStatus(ex.Message, isError: true);
                    _onHistory?.Invoke(new HistoryEntry
                    {
                        Name = fileName,
                        Path = file,
                        Kind = "convert",
                        Status = "fail",
                        Detail = ex.Message,
                    });
                }
            }
        }
        finally
        {
            var summary = _cts?.IsCancellationRequested == true
                ? "Cancelado."
                : $"Listo: {okCount} convertidos"
                  + (skipCount > 0 ? $", {skipCount} omitidos" : "")
                  + (failCount > 0 ? $", {failCount} error" : "")
                  + ".";

            _progress.SetStats($"{okCount} ok"
                + (skipCount > 0 ? $" · {skipCount} omit" : "")
                + (failCount > 0 ? $" · {failCount} err" : "")
                + $" · total {files.Count}");
            var cancelled = _cts?.IsCancellationRequested == true;
            _progress.EndSession(summary, isError: failCount > 0, folderPath: openDir,
                markComplete: !cancelled && failCount == 0);

            if (failCount > 0 && _cts?.IsCancellationRequested != true)
                MessageBox.Show(this, summary, "Recodio", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            _btnConvert.Enabled = true;
            _btnCancel.Enabled = false;
            _cts?.Dispose();
            _cts = null;
            CloseIfPending();
        }
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;
}
