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
    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgress;
    private readonly TextBox _txtLog;
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
        Size = new Size(600, 620);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 500);
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
            Size = new Size(560, 140),
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

        var btnAdd = new Button { Text = "Agregar...", Location = new Point(10, 175), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        btnAdd.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Elegi archivos" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        };
        Controls.Add(btnAdd);

        var btnRemove = new Button { Text = "Quitar", Location = new Point(105, 175), Size = new Size(80, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        btnRemove.Click += (_, _) =>
        {
            foreach (var item in _lstFiles.SelectedItems.Cast<string>().ToList())
                _lstFiles.Items.Remove(item);
        };
        Controls.Add(btnRemove);

        AddFiles(initialFiles);

        var lblFormat = new Label { Text = "Convertir a:", Location = new Point(10, 212), AutoSize = true };
        Controls.Add(lblFormat);

        _cmbFormat = new ComboBox { Location = new Point(10, 230), Size = new Size(220, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var f in Formats.All) _cmbFormat.Items.Add(f.Label);
        _cmbFormat.SelectedIndex = 0;
        Controls.Add(_cmbFormat);

        var lblQuality = new Label { Text = "Calidad (audio):", Location = new Point(245, 212), AutoSize = true };
        Controls.Add(lblQuality);

        _cmbQuality = new ComboBox { Location = new Point(245, 230), Size = new Size(200, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbQuality.Items.AddRange(["Alta", "Media", "Baja"]);
        _cmbQuality.SelectedIndex = defaultQuality switch { "medium" => 1, "low" => 2, _ => 0 };
        Controls.Add(_cmbQuality);

        var lblExists = new Label { Text = "Si ya existe:", Location = new Point(10, 262), AutoSize = true };
        Controls.Add(lblExists);
        _cmbOnExists = new ComboBox { Location = new Point(100, 259), Size = new Size(200, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbOnExists.Items.AddRange(["Omitir", "Sobrescribir", "Renombrar"]);
        _cmbOnExists.SelectedIndex = onFileExists switch { "overwrite" => 1, "rename" => 2, _ => 0 };
        Controls.Add(_cmbOnExists);

        var lblDest = new Label { Text = "Carpeta de destino (vacio = misma carpeta del original):", Location = new Point(10, 290), AutoSize = true };
        Controls.Add(lblDest);

        _txtDest = new TextBox { Location = new Point(10, 308), Size = new Size(500, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_txtDest);

        var btnDest = new Button { Text = "...", Location = new Point(520, 307), Size = new Size(40, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnDest.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(this) == DialogResult.OK) _txtDest.Text = fbd.SelectedPath;
        };
        Controls.Add(btnDest);

        _chkDeleteOriginal = new CheckBox
        {
            Text = "Eliminar el archivo original despues de convertir (solo queda la conversion)",
            Location = new Point(10, 338),
            AutoSize = true,
            Checked = true,
        };
        Controls.Add(_chkDeleteOriginal);

        _progressBar = new ProgressBar { Location = new Point(10, 368), Size = new Size(560, 18), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_progressBar);

        _lblProgress = new Label { Text = "", Location = new Point(10, 390), AutoSize = true };
        Controls.Add(_lblProgress);

        _txtLog = new TextBox
        {
            Location = new Point(10, 412),
            Size = new Size(560, 120),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtLog);

        _btnConvert = new Button { Text = "Convertir", Location = new Point(405, 542), Size = new Size(85, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _btnConvert.Click += async (_, _) => await ConvertAllAsync();
        Controls.Add(_btnConvert);

        _btnCancel = new Button { Text = "Cancelar", Location = new Point(495, 542), Size = new Size(80, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        FormClosing += (_, e) =>
        {
            if (_cts == null) return;
            e.Cancel = true;
            _closeRequested = true;
            _cts.Cancel();
        };
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
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _txtLog.Clear();

        _cts = new CancellationTokenSource();
        var okCount = 0;
        var failCount = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;
                var file = files[i];
                var fileNum = i + 1;
                var fileName = Path.GetFileName(file);
                _progressBar.Value = 0;
                _lblProgress.Text = $"Archivo {fileNum} de {files.Count}: {fileName}";
                AppendLog($">> {fileName}");

                try
                {
                    var result = await FormatConverter.ConvertAsync(_ffmpegPath, file, destDir, formatKey, quality,
                        onProgress: pct => SetFileProgress(pct, fileNum, files.Count, fileName),
                        onFileExists: onExists,
                        ct: _cts.Token);
                    if (result.Skipped)
                    {
                        AppendLog($"   omitido: {result.Log}");
                    }
                    else if (result.Success)
                    {
                        okCount++;
                        AppendLog($"   OK -> {result.OutputPath}");
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
                            try { File.Delete(file); AppendLog("   original eliminado"); }
                            catch (Exception ex) { AppendLog($"   no se pudo eliminar el original: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        failCount++;
                        AppendLog($"   ERROR: {Truncate(result.Log, 300)}");
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
                    AppendLog("   cancelado.");
                    break;
                }
                catch (Exception ex)
                {
                    failCount++;
                    AppendLog($"   EXCEPCION: {ex.Message}");
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
            _lblProgress.Text = _cts?.IsCancellationRequested == true
                ? "Cancelado."
                : $"Listo: {okCount} convertidos, {failCount} con error.";

            _btnConvert.Enabled = true;
            _btnCancel.Enabled = false;
            _cts?.Dispose();
            _cts = null;
            CloseIfPending();
        }
    }

    private void SetFileProgress(int percent, int fileNum, int totalFiles, string fileName)
    {
        if (InvokeRequired) { Invoke(() => SetFileProgress(percent, fileNum, totalFiles, fileName)); return; }
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        _lblProgress.Text = $"Archivo {fileNum} de {totalFiles}: {fileName} - {percent}%";
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;

    private void AppendLog(string line)
    {
        if (InvokeRequired) { Invoke(() => AppendLog(line)); return; }
        _txtLog.AppendText(line + Environment.NewLine);
    }
}
