namespace Recodio;

public class SettingsForm : Form
{
    private readonly TextBox _txtWatch;
    private readonly CheckBox _chkSameFolder;
    private readonly TextBox _txtOut;
    private readonly Button _btnOut;
    private readonly ComboBox _cmbQuality;
    private readonly ComboBox _cmbTheme;
    private readonly ComboBox _cmbWatchMode;
    private readonly ComboBox _cmbWatchFormat;
    private readonly ComboBox _cmbOnExists;
    private readonly CheckBox _chkClipboard;
    private readonly ComboBox _cmbCookies;

    public string WatchDir => _txtWatch.Text.Trim();
    public string OutputDir => _chkSameFolder.Checked ? "" : _txtOut.Text.Trim();
    public string Quality => _cmbQuality.SelectedIndex switch
    {
        1 => "medium",
        2 => "low",
        _ => "high",
    };
    public string Theme => _cmbTheme.SelectedIndex switch
    {
        1 => "light",
        2 => "system",
        _ => "dark",
    };
    public string WatchMode => _cmbWatchMode.SelectedIndex == 1 ? "manual" : "auto";
    public string WatchConvertFormat => Formats.All[_cmbWatchFormat.SelectedIndex].Key;
    public string OnFileExists => _cmbOnExists.SelectedIndex switch
    {
        1 => "overwrite",
        2 => "rename",
        _ => "skip",
    };
    public bool ClipboardAutoFill => _chkClipboard.Checked;
    public string CookiesBrowser => BrowserCookies.KeyAt(_cmbCookies.SelectedIndex);

    public SettingsForm(AppConfig config, IReadOnlyList<ToolStatus>? tools = null)
    {
        Text = "Configuracion - Recodio";
        Size = new Size(500, 720);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var y = 12;

        var lblWatch = new Label { Text = "Carpeta vigilada (p. ej. descargas de Spotube):", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblWatch);
        y += 20;

        _txtWatch = new TextBox { Text = config.WatchDir, Location = new Point(10, y), Size = new Size(380, 20) };
        Controls.Add(_txtWatch);
        var btnWatch = new Button { Text = "...", Location = new Point(400, y - 1), Size = new Size(40, 22) };
        btnWatch.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { InitialDirectory = _txtWatch.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtWatch.Text = fbd.SelectedPath;
        };
        Controls.Add(btnWatch);
        y += 30;

        var lblWatchMode = new Label { Text = "Modo de la carpeta vigilada:", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblWatchMode);
        y += 18;
        _cmbWatchMode = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(320, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbWatchMode.Items.AddRange([
            "Automatico (convertir al detectar archivos nuevos)",
            "Manual (solo con \"Convertir pendientes\")",
        ]);
        _cmbWatchMode.SelectedIndex = config.IsWatchAuto ? 0 : 1;
        Controls.Add(_cmbWatchMode);
        y += 32;

        var lblWatchFmt = new Label { Text = "Formato de conversion del watch:", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblWatchFmt);
        y += 18;
        _cmbWatchFormat = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(230, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var f in Formats.All) _cmbWatchFormat.Items.Add(f.Label);
        var fmtIdx = Array.FindIndex(Formats.All, f => f.Key == config.WatchConvertFormat);
        _cmbWatchFormat.SelectedIndex = fmtIdx >= 0 ? fmtIdx : 0;
        Controls.Add(_cmbWatchFormat);
        y += 32;

        _chkSameFolder = new CheckBox
        {
            Text = "Guardar conversiones en la misma carpeta del original",
            Location = new Point(10, y),
            AutoSize = true,
            Checked = string.IsNullOrEmpty(config.OutputDir),
        };
        Controls.Add(_chkSameFolder);
        y += 26;

        var lblOut = new Label { Text = "Carpeta de salida:", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblOut);
        y += 18;

        _txtOut = new TextBox
        {
            Text = config.OutputDir,
            Location = new Point(10, y),
            Size = new Size(380, 20),
            Enabled = !_chkSameFolder.Checked,
        };
        Controls.Add(_txtOut);
        _btnOut = new Button { Text = "...", Location = new Point(400, y - 1), Size = new Size(40, 22), Enabled = !_chkSameFolder.Checked };
        _btnOut.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (!string.IsNullOrEmpty(_txtOut.Text)) fbd.InitialDirectory = _txtOut.Text;
            if (fbd.ShowDialog() == DialogResult.OK) _txtOut.Text = fbd.SelectedPath;
        };
        Controls.Add(_btnOut);
        y += 28;

        _chkSameFolder.CheckedChanged += (_, _) =>
        {
            _txtOut.Enabled = !_chkSameFolder.Checked;
            _btnOut.Enabled = !_chkSameFolder.Checked;
        };

        var lblQuality = new Label { Text = "Calidad de audio (mp3/etc.):", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblQuality);
        y += 18;
        _cmbQuality = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(230, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbQuality.Items.AddRange(["Alta (VBR ~245 kbps)", "Media (192 kbps)", "Baja (128 kbps)"]);
        _cmbQuality.SelectedIndex = config.Quality switch { "medium" => 1, "low" => 2, _ => 0 };
        Controls.Add(_cmbQuality);
        y += 32;

        var lblExists = new Label { Text = "Si el archivo de destino ya existe:", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblExists);
        y += 18;
        _cmbOnExists = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(280, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbOnExists.Items.AddRange(["Omitir (no sobrescribir)", "Sobrescribir", "Renombrar (archivo (2))"]);
        _cmbOnExists.SelectedIndex = config.OnFileExists switch { "overwrite" => 1, "rename" => 2, _ => 0 };
        Controls.Add(_cmbOnExists);
        y += 32;

        var lblTheme = new Label { Text = "Tema:", Location = new Point(10, y), AutoSize = true };
        Controls.Add(lblTheme);
        y += 18;
        _cmbTheme = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(230, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbTheme.Items.AddRange(["Oscuro", "Claro", "Seguir el sistema"]);
        _cmbTheme.SelectedIndex = config.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        Controls.Add(_cmbTheme);
        y += 28;

        _chkClipboard = new CheckBox
        {
            Text = "Autocompletar URL desde el portapapeles al abrir descargas",
            Location = new Point(10, y),
            AutoSize = true,
            Checked = config.ClipboardAutoFill,
        };
        Controls.Add(_chkClipboard);
        y += 28;

        var lblCookies = new Label
        {
            Text = "Cookies del navegador (automatico en yt-dlp y spotDL):",
            Location = new Point(10, y),
            AutoSize = true,
        };
        Controls.Add(lblCookies);
        y += 18;
        _cmbCookies = new ComboBox
        {
            Location = new Point(10, y),
            Size = new Size(280, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbCookies.Items.AddRange(BrowserCookies.Labels());
        _cmbCookies.SelectedIndex = BrowserCookies.IndexOfKey(config.EffectiveCookiesBrowser());
        Controls.Add(_cmbCookies);
        y += 22;
        var lblCookiesHint = new Label
        {
            Text = BrowserCookies.IsBraveInstalled()
                ? "Brave detectado. Si yt-dlp no puede leer cookies, cierra Brave y reintenta."
                : "Recomendado: Brave o Chrome logueado en YouTube / sitios con age-gate.",
            Location = new Point(10, y),
            Size = new Size(460, 32),
            ForeColor = SystemColors.GrayText,
        };
        Controls.Add(lblCookiesHint);
        y += 36;

        var chkContextMenu = new CheckBox
        {
            Text = "Agregar \"Convertir con Recodio\" al menu contextual de Windows",
            Location = new Point(10, y),
            Size = new Size(460, 36),
            Checked = WindowsContextMenu.IsRegistered(),
        };
        chkContextMenu.CheckedChanged += (_, _) =>
        {
            if (chkContextMenu.Checked) WindowsContextMenu.Register();
            else WindowsContextMenu.Unregister();
        };
        Controls.Add(chkContextMenu);
        y += 42;

        // Dependency status
        var lblDeps = new Label
        {
            Text = "Dependencias:",
            Location = new Point(10, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        Controls.Add(lblDeps);
        y += 20;

        if (tools != null)
        {
            foreach (var t in tools)
            {
                var line = new Label
                {
                    Text = t.Found ? $"✓ {t.Name}: {t.PathOrHint}" : $"✗ {t.Name}: {t.PathOrHint}",
                    Location = new Point(10, y),
                    Size = new Size(460, 18),
                    ForeColor = t.Found ? SystemColors.ControlText : Color.IndianRed,
                    AutoEllipsis = true,
                };
                Controls.Add(line);
                y += 18;
            }
        }
        y += 8;

        var lblPaths = new Label
        {
            Text = $"Config: {AppPaths.ConfigFile}",
            Location = new Point(10, y),
            Size = new Size(460, 32),
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
        };
        Controls.Add(lblPaths);
        y += 36;

        var btnCheckUpdates = new Button
        {
            Text = "Estado winget de yt-dlp / ffmpeg",
            Location = new Point(10, y),
            Size = new Size(320, 26),
        };
        y += 30;
        var lblUpdateResult = new Label { Text = "", Location = new Point(10, y), Size = new Size(460, 28), AutoSize = false };
        Controls.Add(lblUpdateResult);
        y += 30;

        btnCheckUpdates.Click += async (_, _) =>
        {
            btnCheckUpdates.Enabled = false;
            lblUpdateResult.ForeColor = SystemColors.ControlText;
            lblUpdateResult.Text = "Buscando actualizaciones... puede tardar un momento.";
            try
            {
                var ytdlp = await CheckWingetUpgrade("yt-dlp.yt-dlp");
                var ffmpeg = await CheckWingetUpgrade("yt-dlp.FFmpeg");
                lblUpdateResult.Text = $"yt-dlp: {ytdlp}   |   ffmpeg: {ffmpeg}";
            }
            catch (Exception ex)
            {
                lblUpdateResult.ForeColor = Color.IndianRed;
                lblUpdateResult.Text = $"No se pudo buscar actualizaciones: {ex.Message}";
            }
            finally
            {
                btnCheckUpdates.Enabled = true;
            }
        };
        Controls.Add(btnCheckUpdates);

        var btnCheckAppUpdate = new Button
        {
            Text = "Buscar actualizacion de Recodio",
            Location = new Point(10, y),
            Size = new Size(280, 26),
        };
        y += 30;
        var lblAppUpdateResult = new Label
        {
            Text = $"Version instalada: {AppSelfUpdater.CurrentVersion}",
            Location = new Point(10, y),
            Size = new Size(460, 28),
            AutoSize = false,
        };
        Controls.Add(lblAppUpdateResult);
        y += 36;

        btnCheckAppUpdate.Click += async (_, _) =>
        {
            btnCheckAppUpdate.Enabled = false;
            lblAppUpdateResult.ForeColor = SystemColors.ControlText;
            lblAppUpdateResult.Text = "Buscando actualizaciones...";
            try
            {
                var update = await AppSelfUpdater.CheckLatestAsync();
                if (update == null)
                {
                    lblAppUpdateResult.Text = $"Ya estas en la ultima version ({AppSelfUpdater.CurrentVersion}).";
                    return;
                }

                var confirm = MessageBox.Show(
                    $"Hay una version nueva disponible: {update.Version} (tenes {AppSelfUpdater.CurrentVersion}).\n\n" +
                    "Se va a descargar, cerrar Recodio y reiniciarlo automaticamente. ¿Continuar?",
                    "Actualizacion disponible",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;

                lblAppUpdateResult.Text = "Descargando actualizacion...";
                await AppSelfUpdater.DownloadAndApplyAsync(update);
            }
            catch (Exception ex)
            {
                lblAppUpdateResult.ForeColor = Color.IndianRed;
                lblAppUpdateResult.Text = $"No se pudo actualizar: {ex.Message}";
            }
            finally
            {
                btnCheckAppUpdate.Enabled = true;
            }
        };
        Controls.Add(btnCheckAppUpdate);

        var btnSave = new Button { Text = "Guardar", Location = new Point(275, y), DialogResult = DialogResult.OK };
        Controls.Add(btnSave);

        var btnCancel = new Button { Text = "Cancelar", Location = new Point(360, y), DialogResult = DialogResult.Cancel };
        Controls.Add(btnCancel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        // Ensure dialog is tall enough for content
        ClientSize = new Size(480, y + 50);
    }

    // Read-only check: do NOT run `winget upgrade` here (that installs). List the package and
    // report whether winget knows about it; the main "Actualizar descargadores" button uses
    // yt-dlp -U / pip for real updates.
    private static async Task<string> CheckWingetUpgrade(string packageId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "winget",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "list", "--id", packageId, "-e", "--accept-source-agreements", "--disable-interactivity" })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (stdout.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(stdout)
                || !stdout.Contains(packageId, StringComparison.OrdinalIgnoreCase))
                return "no instalado via winget";

            // winget list table includes name + version when installed.
            return "instalado (usa 'Actualizar descargadores' en la ventana principal para actualizar)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            return "winget no disponible";
        }
    }
}
