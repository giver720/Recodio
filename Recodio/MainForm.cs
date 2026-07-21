using System.Text.Json;
using Microsoft.Win32;

namespace Recodio;

public class MainForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Recodio";

    private readonly string _appDir;
    private readonly string _logFile;
    private readonly string _configFile;
    private readonly string _historyFile;
    private readonly string _ffmpegPath;
    private readonly string _ytDlpPath;
    private readonly string _spotdlPath;

    private readonly NotifyIcon _trayIcon;

    private Label _lblStatus = null!;
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

    public MainForm(string[]? initialFiles = null)
    {
        _appDir = AppContext.BaseDirectory;
        _logFile = Path.Combine(_appDir, "watcher.log");
        _configFile = Path.Combine(_appDir, "config.json");
        _historyFile = Path.Combine(_appDir, "history.json");
        _ffmpegPath = ResolveFfmpegPath();
        _ytDlpPath = ResolveYtDlpPath();
        _spotdlPath = SpotDlDownloader.ResolveSpotDlPath();

        LoadConfig();
        LoadHistory();
        Directory.CreateDirectory(_config.WatchDir);

        var appIcon = BuildAppIcon();

        Text = "Recodio";
        Icon = appIcon;
        Size = new Size(560, 560);
        MinimumSize = new Size(480, 420);
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
        UpdateStatus();

        _trayIcon.BalloonTipTitle = "Recodio";
        _trayIcon.BalloonTipText = "Listo. La conversion es manual: usa \"Convertir pendientes ahora\" o \"Convertir a formato...\".";
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

        _convertFormatForm = new ConvertFormatForm(_ffmpegPath, files, _config.Quality);
        _convertFormatForm.FormClosed += (_, _) => _convertFormatForm = null;
        _convertFormatForm.Show();
    }

    private void BuildUi()
    {
        _lblStatus = new Label
        {
            Text = "Iniciando...",
            Location = new Point(15, 15),
            Size = new Size(520, 30),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_lblStatus);

        var flow = new FlowLayoutPanel
        {
            Location = new Point(15, 55),
            Size = new Size(520, 80),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };

        flow.Controls.Add(MakeButton("Convertir a formato...", (_, _) => OpenOrAddToConvertForm([])));
        flow.Controls.Add(MakeButton("Descargar video...", (_, _) => ShowDownloadForm()));
        flow.Controls.Add(MakeButton("Descargar con spotDL...", (_, _) => ShowSpotDlForm()));
        flow.Controls.Add(MakeButton("Historial...", (_, _) => new HistoryForm(_history).ShowDialog(this)));
        flow.Controls.Add(MakeButton("Configuracion...", (_, _) => ShowSettingsDialog()));
        flow.Controls.Add(MakeButton("Abrir carpeta", (_, _) => OpenWatchFolder()));
        flow.Controls.Add(MakeButton("Ver log", (_, _) => OpenLogFile()));
        flow.Controls.Add(MakeButton("Convertir pendientes ahora", async (_, _) => await SweepAsync()));
        Controls.Add(flow);

        _chkStartup = new CheckBox
        {
            Text = "Iniciar con Windows",
            Location = new Point(15, 145),
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
            Location = new Point(200, 147),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        Controls.Add(lblDropHint);

        var lblActivity = new Label { Text = "Actividad:", Location = new Point(15, 175), AutoSize = true };
        Controls.Add(lblActivity);

        _txtActivity = new TextBox
        {
            Location = new Point(15, 195),
            Size = new Size(520, 260),
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
            Location = new Point(15, 465),
            Size = new Size(160, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        btnMinimize.Click += (_, _) => Hide();
        Controls.Add(btnMinimize);

        var btnExit = new Button
        {
            Text = "Salir",
            Location = new Point(460, 465),
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
        var openItem = new ToolStripMenuItem("Abrir Recodio");
        openItem.Click += (_, _) => RestoreWindow();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Salir");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

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
        base.OnFormClosing(e);
    }

    private void OpenWatchFolder()
    {
        Directory.CreateDirectory(_config.WatchDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_config.WatchDir) { UseShellExecute = true });
    }

    private void OpenLogFile()
    {
        if (!File.Exists(_logFile)) File.WriteAllText(_logFile, "");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_logFile) { UseShellExecute = true });
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
        });
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

        _spotDlForm = new SpotDlForm(_spotdlPath, _ffmpegPath, _config,
            (dir, format, bitrate, lyrics, threads, skipExisting, organizeInFolders, sponsorBlock) =>
        {
            _config.SpotDlDownloadDir = dir;
            _config.SpotDlFormat = format;
            _config.SpotDlBitrate = bitrate;
            _config.SpotDlLyrics = lyrics;
            _config.SpotDlThreads = threads;
            _config.SpotDlSkipExisting = skipExisting;
            _config.SpotDlOrganizeInFolders = organizeInFolders;
            _config.SpotDlSponsorBlock = sponsorBlock;
            SaveConfig();
        }, SaveConfig);
        _spotDlForm.FormClosed += (_, _) => _spotDlForm = null;
        _spotDlForm.Show();
    }

    private void ShowSettingsDialog()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var themeChanged = _config.Theme != form.Theme;
        _config.WatchDir = form.WatchDir;
        _config.OutputDir = form.OutputDir;
        _config.Quality = form.Quality;
        _config.Theme = form.Theme;
        Directory.CreateDirectory(_config.WatchDir);
        SaveConfig();
        Log($"Configuracion actualizada: watchDir={_config.WatchDir} outputDir={_config.OutputDir} quality={_config.Quality} theme={_config.Theme}");

        if (themeChanged)
        {
            MessageBox.Show(this, "El nuevo tema se aplicara la proxima vez que abras la aplicacion.",
                "Recodio", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (!File.Exists(_configFile)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configFile));
            if (loaded != null) _config = loaded;
        }
        catch
        {
            Log("No se pudo leer config.json, usando valores por defecto");
        }
    }

    private void SaveConfig()
    {
        File.WriteAllText(_configFile, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadHistory()
    {
        if (!File.Exists(_historyFile)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_historyFile));
            if (loaded != null) _history.AddRange(loaded);
        }
        catch { /* ignore corrupt history */ }
    }

    private void SaveHistory()
    {
        var toSave = _history.Count > 200 ? _history.GetRange(_history.Count - 200, 200) : _history;
        File.WriteAllText(_historyFile, JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
        File.AppendAllText(_logFile, line + Environment.NewLine);

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
        else
        {
            text = "";
        }

        if (_lblStatus.InvokeRequired) _lblStatus.Invoke(() => _lblStatus.Text = text);
        else _lblStatus.Text = text;

        var trayText = text.Length > 0 ? "Recodio - " + text : "Recodio";
        _trayIcon.Text = trayText.Length > 63 ? trayText[..60] + "..." : trayText;
    }

    private async Task SweepAsync()
    {
        if (_sweeping) return;
        if (!Directory.Exists(_config.WatchDir)) return;
        _sweeping = true;
        try
        {
            var files = Directory.EnumerateFiles(_config.WatchDir, "*", SearchOption.AllDirectories)
                .Where(f => !FormatConverter.SkipExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            foreach (var file in files)
            {
                await ConvertFileAsync(file, deleteOriginal: true);
            }
        }
        catch (IOException)
        {
            // folder briefly unavailable (e.g. being renamed); retry next tick
        }
        finally
        {
            _sweeping = false;
        }
    }

    private async Task ConvertFileAsync(string path, bool deleteOriginal)
    {
        try
        {
            _currentFile = Path.GetFileNameWithoutExtension(path);
            UpdateStatus();

            var result = await FormatConverter.ConvertAsync(_ffmpegPath, path, _config.OutputDir, "mp3", _config.Quality);
            if (result.Skipped) return;

            if (result.Success)
            {
                Log($"OK -> {result.OutputPath}");
                var sizeKB = new FileInfo(result.OutputPath).Length / 1024;
                _history.Add(new HistoryEntry
                {
                    Name = Path.GetFileName(result.OutputPath),
                    Path = result.OutputPath,
                    Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    SizeKB = sizeKB,
                });
                SaveHistory();
                ShowBalloon("Convertido a MP3", _currentFile, ToolTipIcon.Info);

                // Cleanup only, after the successful conversion is already recorded - a locked
                // original (still open, AV scan, etc.) shouldn't make a real success look like
                // a failure or drop it from history.
                if (deleteOriginal)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Log($"No se pudo eliminar el original {path}: {ex.Message}"); }
                }
            }
            else
            {
                Log($"ERROR convirtiendo {path}: {result.Log}");
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
        // Prefer the real, designed icon embedded into the exe (via <ApplicationIcon> in
        // the csproj) instead of the old runtime-generated placeholder bitmap.
        try
        {
            var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted != null) return extracted;
        }
        catch { /* fall through to file/system fallback */ }

        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Recodio.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);
        }
        catch { /* fall through to system fallback */ }

        return SystemIcons.Application;
    }
}
