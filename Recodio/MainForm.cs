using System.Text.Json;
using Microsoft.Win32;

namespace Recodio;

public class MainForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Recodio";

    private readonly string _ffmpegPath;
    private readonly string _ytDlpPath;
    private readonly string _spotdlPath;

    private readonly NotifyIcon _trayIcon;
    private FileSystemWatcher? _watcher;
    private System.Windows.Forms.Timer? _debounceTimer;
    private readonly HashSet<string> _pendingWatchFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();

    private Label _lblStatus = null!;
    private Label _lblDeps = null!;
    private TextBox _txtActivity = null!;
    private CheckBox _chkStartup = null!;

    private AppConfig _config = new();
    private readonly List<HistoryEntry> _history = [];

    private bool _sweeping;
    private string _currentFile = "";
    private bool _reallyExit;
    private ConvertFormatForm? _convertFormatForm;
    private DownloadForm? _downloadForm;
    private SpotDlForm? _spotDlForm;
    private readonly CancellationTokenSource _ipcCts = new();
    private IReadOnlyList<ToolStatus> _toolStatus = [];

    public MainForm(string[]? initialFiles = null)
    {
        AppPaths.MigrateFromInstallDir(AppContext.BaseDirectory);
        AppPaths.EnsureDirs();

        _ffmpegPath = ResolveFfmpegPath();
        _ytDlpPath = ResolveYtDlpPath();
        _spotdlPath = SpotDlDownloader.ResolveSpotDlPath();

        LoadConfig();
        LoadHistory();
        try { Directory.CreateDirectory(_config.WatchDir); } catch { /* path may be invalid until user fixes settings */ }

        var appIcon = BuildAppIcon();

        Text = "Recodio";
        Icon = appIcon;
        Size = new Size(580, 580);
        MinimumSize = new Size(500, 440);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var files = FormatConverter.ExpandDroppedPaths(paths);
                if (files.Length > 0) OpenOrAddToConvertForm(files);
            }
        };

        BuildUi();

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = "Recodio",
        };
        _trayIcon.ContextMenuStrip = BuildTrayMenu();
        _trayIcon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreWindow(); };

        PipeIpc.StartServer(files => OpenOrAddToConvertForm(files), _ipcCts.Token);

        Log($"App iniciada (PID {Environment.ProcessId})");
        Log($"Config: {AppPaths.ConfigFile}");
        RefreshDependencyStatus();
        UpdateStatus();
        SetupWatcher();

        // Keep yt-dlp/spotDL fresh: YouTube breaks old versions constantly. Background,
        // throttled to once a day, never blocks startup.
        _ = UpdateToolsAsync(auto: true);

        var watchHint = _config.IsWatchAuto
            ? "Watch folder automatico activo."
            : "Conversion de carpeta en modo manual.";
        _trayIcon.BalloonTipTitle = "Recodio";
        _trayIcon.BalloonTipText = $"Listo. {watchHint}";
        _trayIcon.ShowBalloonTip(2500);

        if (initialFiles is { Length: > 0 })
        {
            var files = initialFiles;
            BeginInvoke(() => OpenOrAddToConvertForm(files));
        }
    }

    private void OpenOrAddToConvertForm(string[] files)
    {
        if (InvokeRequired) { Invoke(() => OpenOrAddToConvertForm(files)); return; }

        if (_convertFormatForm is { IsDisposed: false })
        {
            _convertFormatForm.AddFiles(files);
            return;
        }

        _convertFormatForm = new ConvertFormatForm(_ffmpegPath, files, _config.Quality, _config.OnFileExists, RecordHistory);
        _convertFormatForm.FormClosed += (_, _) => _convertFormatForm = null;
        _convertFormatForm.Show();
    }

    private void BuildUi()
    {
        _lblStatus = new Label
        {
            Text = "Iniciando...",
            Location = new Point(15, 15),
            Size = new Size(540, 28),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lblStatus);

        _lblDeps = new Label
        {
            Text = "",
            Location = new Point(15, 44),
            Size = new Size(540, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };
        Controls.Add(_lblDeps);

        var flow = new FlowLayoutPanel
        {
            Location = new Point(15, 72),
            Size = new Size(540, 90),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };

        flow.Controls.Add(MakeButton("Convertir a formato...", (_, _) => OpenOrAddToConvertForm([])));
        flow.Controls.Add(MakeButton("Descargar media (yt-dlp)...", (_, _) => ShowDownloadForm()));
        flow.Controls.Add(MakeButton("Descargar con spotDL...", (_, _) => ShowSpotDlForm()));
        flow.Controls.Add(MakeButton("Historial...", (_, _) => ShowHistory()));
        flow.Controls.Add(MakeButton("Configuracion...", (_, _) => ShowSettingsDialog()));
        flow.Controls.Add(MakeButton("Abrir carpeta", (_, _) => OpenWatchFolder()));
        flow.Controls.Add(MakeButton("Ver log", (_, _) => OpenLogFile()));
        flow.Controls.Add(MakeButton("Convertir pendientes ahora", async (_, _) => await SweepAsync()));
        flow.Controls.Add(MakeButton("Actualizar descargadores", async (_, _) => await UpdateToolsAsync(auto: false)));
        Controls.Add(flow);

        _chkStartup = new CheckBox
        {
            Text = "Iniciar con Windows",
            Location = new Point(15, 170),
            AutoSize = true,
            Checked = IsStartupEnabled(),
        };
        _chkStartup.CheckedChanged += (_, _) =>
        {
            SetStartupEnabled(_chkStartup.Checked);
            Log(_chkStartup.Checked ? "Inicio con Windows activado" : "Inicio con Windows desactivado");
        };
        Controls.Add(_chkStartup);

        var lblDropHint = new Label
        {
            Text = "Tip: arrastra archivos aca para convertirlos.",
            Location = new Point(200, 172),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        Controls.Add(lblDropHint);

        var lblActivity = new Label { Text = "Actividad:", Location = new Point(15, 200), AutoSize = true };
        Controls.Add(lblActivity);

        _txtActivity = new TextBox
        {
            Location = new Point(15, 220),
            Size = new Size(540, 270),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_txtActivity);

        var btnMinimize = new Button
        {
            Text = "Minimizar a la bandeja",
            Location = new Point(15, 500),
            Size = new Size(160, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        btnMinimize.Click += (_, _) => Hide();
        Controls.Add(btnMinimize);

        var btnExit = new Button
        {
            Text = "Salir",
            Location = new Point(480, 500),
            Size = new Size(75, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        btnExit.Click += (_, _) => ExitApp();
        Controls.Add(btnExit);
    }

    private Button MakeButton(string text, EventHandler onClick)
    {
        var btn = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(3) };
        btn.Click += onClick;
        return btn;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        void Add(string text, EventHandler handler)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += handler;
            menu.Items.Add(item);
        }

        Add("Abrir Recodio", (_, _) => RestoreWindow());
        menu.Items.Add(new ToolStripSeparator());
        Add("Convertir a formato...", (_, _) => { RestoreWindow(); OpenOrAddToConvertForm([]); });
        Add("Descargar media (yt-dlp)...", (_, _) => { RestoreWindow(); ShowDownloadForm(); });
        Add("Descargar con spotDL...", (_, _) => { RestoreWindow(); ShowSpotDlForm(); });
        Add("Convertir pendientes ahora", async (_, _) => await SweepAsync());
        menu.Items.Add(new ToolStripSeparator());
        Add("Historial...", (_, _) => { RestoreWindow(); ShowHistory(); });
        Add("Abrir carpeta vigilada", (_, _) => OpenWatchFolder());
        Add("Abrir descargas media", (_, _) => OpenDir(_config.DownloadDir));
        Add("Abrir descargas spotDL", (_, _) => OpenDir(_config.SpotDlDownloadDir));
        Add("Configuracion...", (_, _) => { RestoreWindow(); ShowSettingsDialog(); });
        menu.Items.Add(new ToolStripSeparator());
        Add("Salir", (_, _) => ExitApp());

        return menu;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _trayIcon.Visible = false;
        _ipcCts.Cancel();
        TeardownWatcher();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.BalloonTipTitle = "Recodio";
            _trayIcon.BalloonTipText = "Sigue corriendo en la bandeja del sistema.";
            _trayIcon.ShowBalloonTip(2000);
            return;
        }
        TeardownWatcher();
        base.OnFormClosing(e);
    }

    private void OpenWatchFolder() => OpenDir(_config.WatchDir);

    private static void OpenDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir la carpeta:\n{ex.Message}", "Recodio");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            AppPaths.EnsureDirs();
            if (!File.Exists(AppPaths.LogFile)) File.WriteAllText(AppPaths.LogFile, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir el log:\n{ex.Message}", "Recodio");
        }
    }

    private void ShowHistory()
    {
        using var form = new HistoryForm(_history, SaveHistory);
        form.ShowDialog(this);
    }

    // Non-modal (like _convertFormatForm) so downloading with yt-dlp and with spotDL can run
    // side by side, and neither blocks the main window while a download is in progress.
    private void ShowDownloadForm()
    {
        if (_downloadForm is { IsDisposed: false })
        {
            if (_downloadForm.WindowState == FormWindowState.Minimized) _downloadForm.WindowState = FormWindowState.Normal;
            _downloadForm.Activate();
            return;
        }

        _downloadForm = new DownloadForm(_ytDlpPath, _ffmpegPath, _config.DownloadDir, dir =>
        {
            _config.DownloadDir = dir;
            SaveConfig();
        }, RecordHistory, _config.ClipboardAutoFill);
        _downloadForm.FormClosed += (_, _) => _downloadForm = null;
        _downloadForm.Show();
    }

    private void ShowSpotDlForm()
    {
        if (_spotDlForm is { IsDisposed: false })
        {
            if (_spotDlForm.WindowState == FormWindowState.Minimized) _spotDlForm.WindowState = FormWindowState.Normal;
            _spotDlForm.Activate();
            return;
        }

        // SpotDlForm mutates _config fields directly via PersistSettings + these callbacks.
        _spotDlForm = new SpotDlForm(_spotdlPath, _ffmpegPath, _config,
            onSettingsChanged: () => { /* config fields already updated by form */ },
            saveConfig: SaveConfig,
            onHistory: RecordHistory);
        _spotDlForm.FormClosed += (_, _) => _spotDlForm = null;
        _spotDlForm.Show();
    }

    private void ShowSettingsDialog()
    {
        using var form = new SettingsForm(_config, _toolStatus);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var themeChanged = _config.Theme != form.Theme;
        var watchDirChanged = !string.Equals(_config.WatchDir, form.WatchDir, StringComparison.OrdinalIgnoreCase);
        var watchModeChanged = !string.Equals(_config.WatchMode, form.WatchMode, StringComparison.OrdinalIgnoreCase);

        _config.WatchDir = form.WatchDir;
        _config.OutputDir = form.OutputDir;
        _config.Quality = form.Quality;
        _config.Theme = form.Theme;
        _config.WatchMode = form.WatchMode;
        _config.WatchConvertFormat = form.WatchConvertFormat;
        _config.OnFileExists = form.OnFileExists;
        _config.ClipboardAutoFill = form.ClipboardAutoFill;

        try { Directory.CreateDirectory(_config.WatchDir); } catch { /* user will fix path */ }
        SaveConfig();
        Log($"Configuracion actualizada: watchDir={_config.WatchDir} mode={_config.WatchMode} format={_config.WatchConvertFormat} quality={_config.Quality} theme={_config.Theme}");

        if (watchDirChanged || watchModeChanged)
            SetupWatcher();

        UpdateStatus();

        if (themeChanged)
        {
            // .NET SystemColorMode is applied at startup; restart is the reliable path.
            var restart = MessageBox.Show(this,
                "El nuevo tema se aplica al reiniciar. ¿Reiniciar Recodio ahora?",
                "Recodio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (restart == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        UseShellExecute = true,
                    });
                }
                catch { /* ignore */ }
                ExitApp();
            }
        }
    }

    private void RefreshDependencyStatus()
    {
        _toolStatus = DependencyChecker.CheckAll(_ffmpegPath, _ytDlpPath, _spotdlPath);
        var summary = DependencyChecker.FormatSummary(_toolStatus);
        if (_lblDeps != null)
        {
            _lblDeps.Text = summary;
            _lblDeps.ForeColor = _toolStatus.Any(t => !t.Found) ? Color.IndianRed : SystemColors.GrayText;
        }
        Log(summary);
        foreach (var t in _toolStatus.Where(t => !t.Found))
            Log($"  → {t.Name}: {t.PathOrHint}");
    }

    // ---------- Watch folder ----------

    private void SetupWatcher()
    {
        TeardownWatcher();
        if (!_config.IsWatchAuto) return;
        if (string.IsNullOrWhiteSpace(_config.WatchDir) || !Directory.Exists(_config.WatchDir))
        {
            Log("Watch automatico: carpeta no existe, no se inicio el watcher.");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_config.WatchDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            _watcher.Created += OnWatchEvent;
            _watcher.Changed += OnWatchEvent;
            _watcher.Renamed += (_, e) => OnWatchPath(e.FullPath);
            _watcher.Error += (_, e) => Log($"Watcher error: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;

            _debounceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _debounceTimer.Tick += async (_, _) => await ProcessPendingWatchFilesAsync();

            Log($"Watch automatico activo en: {_config.WatchDir}");
        }
        catch (Exception ex)
        {
            Log($"No se pudo iniciar el watcher: {ex.Message}");
        }
    }

    private void TeardownWatcher()
    {
        if (_debounceTimer != null)
        {
            _debounceTimer.Stop();
            _debounceTimer.Dispose();
            _debounceTimer = null;
        }
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        lock (_pendingLock) _pendingWatchFiles.Clear();
    }

    private void OnWatchEvent(object sender, FileSystemEventArgs e) => OnWatchPath(e.FullPath);

    private void OnWatchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (FormatConverter.SkipExtensions.Contains(ext)) return;
        // Only convert TO the watch format from non-matching extensions; skip already-converted
        // and temp/partial downloads.
        var targetExt = Formats.Get(_config.WatchConvertFormat).Extension;
        if (string.Equals(ext, targetExt, StringComparison.OrdinalIgnoreCase)) return;

        lock (_pendingLock) _pendingWatchFiles.Add(path);
        if (_debounceTimer == null) return;
        // Restart debounce window on each event.
        if (InvokeRequired)
        {
            BeginInvoke(() =>
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            });
        }
        else
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private async Task ProcessPendingWatchFilesAsync()
    {
        _debounceTimer?.Stop();
        List<string> batch;
        lock (_pendingLock)
        {
            batch = _pendingWatchFiles.ToList();
            _pendingWatchFiles.Clear();
        }
        if (batch.Count == 0) return;
        if (_sweeping)
        {
            // Don't drop events while a sweep is running — requeue and try again shortly.
            lock (_pendingLock) foreach (var f in batch) _pendingWatchFiles.Add(f);
            if (_debounceTimer != null) { _debounceTimer.Stop(); _debounceTimer.Start(); }
            return;
        }

        foreach (var file in batch)
        {
            if (!File.Exists(file)) continue;
            await ConvertFileAsync(file, deleteOriginal: true);
        }
    }

    private static string ResolveFfmpegPath()
    {
        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(wingetPath) ? wingetPath : "ffmpeg";
    }

    private static string ResolveYtDlpPath()
    {
        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "yt-dlp.exe");
        return File.Exists(wingetPath) ? wingetPath : "yt-dlp";
    }

    private void LoadConfig()
    {
        if (!File.Exists(AppPaths.ConfigFile)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile));
            if (loaded != null) _config = loaded;
        }
        catch
        {
            Log("No se pudo leer config.json, usando valores por defecto");
        }
    }

    private void SaveConfig()
    {
        try
        {
            AppPaths.EnsureDirs();
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"No se pudo guardar config: {ex.Message}");
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(AppPaths.HistoryFile)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(AppPaths.HistoryFile));
            if (loaded != null) _history.AddRange(loaded);
        }
        catch { /* ignore corrupt history */ }
    }

    private void SaveHistory()
    {
        try
        {
            AppPaths.EnsureDirs();
            var toSave = _history.Count > 300 ? _history.GetRange(_history.Count - 300, 300) : _history;
            File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"No se pudo guardar historial: {ex.Message}");
        }
    }

    public void RecordHistory(HistoryEntry entry)
    {
        if (InvokeRequired) { Invoke(() => RecordHistory(entry)); return; }
        if (string.IsNullOrWhiteSpace(entry.Date))
            entry.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _history.Add(entry);
        SaveHistory();
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
        try
        {
            AppPaths.EnsureDirs();
            File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
        }
        catch { /* log disk full / locked */ }

        if (_txtActivity == null) return;
        if (_txtActivity.InvokeRequired) { _txtActivity.Invoke(() => AppendActivity(line)); return; }
        AppendActivity(line);
    }

    private void AppendActivity(string line)
    {
        _txtActivity.AppendText(line + Environment.NewLine);
    }

    private void UpdateStatus()
    {
        string text;
        if (!string.IsNullOrEmpty(_currentFile))
        {
            text = $"Convirtiendo: {_currentFile}";
        }
        else if (_config.IsWatchAuto)
        {
            text = $"Listo · watch automatico ({_config.WatchConvertFormat})";
        }
        else
        {
            text = "Listo · watch manual";
        }

        if (_lblStatus.InvokeRequired) _lblStatus.Invoke(() => _lblStatus.Text = text);
        else _lblStatus.Text = text;

        var trayText = text.Length > 0 ? "Recodio - " + text : "Recodio";
        _trayIcon.Text = trayText.Length > 63 ? trayText[..60] + "..." : trayText;
    }

    private bool _updatingTools;

    // Updates yt-dlp (self-update) and spotDL (pip) in the background. auto=true is the
    // daily startup check; auto=false is the manual button and always runs.
    private async Task UpdateToolsAsync(bool auto)
    {
        if (_updatingTools) return;
        if (auto)
        {
            if (DateTime.TryParse(_config.LastToolsUpdateCheck, out var last)
                && (DateTime.Now - last) < TimeSpan.FromHours(24))
                return;
        }
        else if (_downloadForm is { IsDisposed: false } || _spotDlForm is { IsDisposed: false })
        {
            // Updating an exe/package while a download might be using it fails with a file
            // lock; for the manual button just ask the user to close those windows first.
            MessageBox.Show(this, "Cerra las ventanas de descarga antes de actualizar los descargadores.", "Recodio");
            return;
        }

        _updatingTools = true;
        try
        {
            Log("Buscando actualizaciones de yt-dlp y spotDL...");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            try
            {
                var yt = await ToolUpdater.UpdateYtDlpAsync(_ytDlpPath, line => Log($"  yt-dlp: {line}"), cts.Token);
                Log(yt.Summary);
            }
            catch (Exception ex)
            {
                Log($"No se pudo actualizar yt-dlp: {ex.Message}");
            }

            try
            {
                var sp = await ToolUpdater.UpdateSpotDlAsync(_spotdlPath, line => Log($"  spotDL: {line}"), cts.Token);
                Log(sp.Summary);
            }
            catch (Exception ex)
            {
                Log($"No se pudo actualizar spotDL: {ex.Message}");
            }

            _config.LastToolsUpdateCheck = DateTime.Now.ToString("o");
            try { SaveConfig(); } catch { /* transient lock; retried next save */ }
            RefreshDependencyStatus();
        }
        finally
        {
            _updatingTools = false;
        }
    }

    private async Task SweepAsync()
    {
        if (_sweeping) return;
        if (!Directory.Exists(_config.WatchDir))
        {
            MessageBox.Show(this, "La carpeta vigilada no existe. Revisá Configuración.", "Recodio");
            return;
        }
        _sweeping = true;
        try
        {
            var targetExt = Formats.Get(_config.WatchConvertFormat).Extension;
            var files = Directory.EnumerateFiles(_config.WatchDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (FormatConverter.SkipExtensions.Contains(ext)) return false;
                    if (string.Equals(ext, targetExt, StringComparison.OrdinalIgnoreCase)) return false;
                    return FormatConverter.ConvertibleExtensions.Contains(ext)
                        || !string.IsNullOrEmpty(ext); // still try unknown media Spotube may drop
                });
            var any = false;
            foreach (var file in files)
            {
                any = true;
                await ConvertFileAsync(file, deleteOriginal: true);
            }
            if (!any) Log("No hay archivos pendientes en la carpeta vigilada.");
        }
        catch (IOException)
        {
            // folder briefly unavailable (e.g. being renamed); retry next tick
        }
        finally
        {
            _sweeping = false;
            UpdateStatus();
        }
    }

    private async Task ConvertFileAsync(string path, bool deleteOriginal)
    {
        try
        {
            _currentFile = Path.GetFileNameWithoutExtension(path);
            UpdateStatus();

            var result = await FormatConverter.ConvertAsync(
                _ffmpegPath, path, _config.OutputDir, _config.WatchConvertFormat, _config.Quality,
                onFileExists: _config.OnFileExists);
            if (result.Skipped)
            {
                Log($"Omitido {Path.GetFileName(path)}: {result.Log}");
                return;
            }

            if (result.Success)
            {
                Log($"OK -> {result.OutputPath}");
                var sizeKB = new FileInfo(result.OutputPath).Length / 1024;
                RecordHistory(new HistoryEntry
                {
                    Name = Path.GetFileName(result.OutputPath),
                    Path = result.OutputPath,
                    Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    SizeKB = sizeKB,
                    Kind = "convert",
                    Status = "ok",
                    Detail = path,
                });
                ShowBalloon("Convertido", _currentFile, ToolTipIcon.Info);

                // Cleanup only after success is recorded.
                if (deleteOriginal)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Log($"No se pudo eliminar el original {path}: {ex.Message}"); }
                }
            }
            else
            {
                Log($"ERROR convirtiendo {path}: {result.Log}");
                RecordHistory(new HistoryEntry
                {
                    Name = Path.GetFileName(path),
                    Path = path,
                    Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Kind = "convert",
                    Status = "fail",
                    Detail = Truncate(result.Log, 200),
                });
                ShowBalloon("Error al convertir", _currentFile, ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Log($"EXCEPCION procesando {path}: {ex.Message}");
        }
        finally
        {
            _currentFile = "";
            UpdateStatus();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = text;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null) return;
        if (enabled)
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunValueName, false);
    }

    private static Icon BuildAppIcon()
    {
        try
        {
            var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted != null) return extracted;
        }
        catch { /* fall through */ }

        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Recodio.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);
        }
        catch { /* fall through */ }

        return SystemIcons.Application;
    }
}
