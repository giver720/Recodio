namespace Recodio;

public class SettingsForm : Form
{
    private readonly TextBox _txtWatch;
    private readonly CheckBox _chkSameFolder;
    private readonly TextBox _txtOut;
    private readonly Button _btnOut;
    private readonly ComboBox _cmbQuality;
    private readonly ComboBox _cmbTheme;

    public string WatchDir => _txtWatch.Text;
    public string OutputDir => _chkSameFolder.Checked ? "" : _txtOut.Text;
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

    public SettingsForm(AppConfig config)
    {
        Text = "Configuracion - Recodio";
        Size = new Size(480, 545);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lblWatch = new Label { Text = "Carpeta que se vigila (descargas de Spotube):", Location = new Point(10, 15), AutoSize = true };
        Controls.Add(lblWatch);

        _txtWatch = new TextBox { Text = config.WatchDir, Location = new Point(10, 35), Size = new Size(360, 20) };
        Controls.Add(_txtWatch);

        var btnWatch = new Button { Text = "...", Location = new Point(380, 34), Size = new Size(40, 22) };
        btnWatch.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { SelectedPath = _txtWatch.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtWatch.Text = fbd.SelectedPath;
        };
        Controls.Add(btnWatch);

        _chkSameFolder = new CheckBox
        {
            Text = "Guardar los mp3 en la misma carpeta del original",
            Location = new Point(10, 70),
            AutoSize = true,
            Checked = string.IsNullOrEmpty(config.OutputDir),
        };
        Controls.Add(_chkSameFolder);

        var lblOut = new Label { Text = "Carpeta de salida:", Location = new Point(10, 100), AutoSize = true };
        Controls.Add(lblOut);

        _txtOut = new TextBox
        {
            Text = config.OutputDir,
            Location = new Point(10, 120),
            Size = new Size(360, 20),
            Enabled = !_chkSameFolder.Checked,
        };
        Controls.Add(_txtOut);

        _btnOut = new Button { Text = "...", Location = new Point(380, 119), Size = new Size(40, 22), Enabled = !_chkSameFolder.Checked };
        _btnOut.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (!string.IsNullOrEmpty(_txtOut.Text)) fbd.SelectedPath = _txtOut.Text;
            if (fbd.ShowDialog() == DialogResult.OK) _txtOut.Text = fbd.SelectedPath;
        };
        Controls.Add(_btnOut);

        _chkSameFolder.CheckedChanged += (_, _) =>
        {
            _txtOut.Enabled = !_chkSameFolder.Checked;
            _btnOut.Enabled = !_chkSameFolder.Checked;
        };

        var lblQuality = new Label { Text = "Calidad del mp3:", Location = new Point(10, 155), AutoSize = true };
        Controls.Add(lblQuality);

        _cmbQuality = new ComboBox
        {
            Location = new Point(10, 175),
            Size = new Size(230, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbQuality.Items.AddRange(["Alta (VBR ~245 kbps)", "Media (192 kbps)", "Baja (128 kbps)"]);
        _cmbQuality.SelectedIndex = config.Quality switch { "medium" => 1, "low" => 2, _ => 0 };
        Controls.Add(_cmbQuality);

        var lblTheme = new Label { Text = "Tema:", Location = new Point(10, 210), AutoSize = true };
        Controls.Add(lblTheme);

        _cmbTheme = new ComboBox
        {
            Location = new Point(10, 228),
            Size = new Size(230, 22),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbTheme.Items.AddRange(["Oscuro", "Claro", "Seguir el sistema"]);
        _cmbTheme.SelectedIndex = config.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        Controls.Add(_cmbTheme);

        var lblRestart = new Label
        {
            Text = "El tema se aplica al reiniciar la aplicacion.",
            Location = new Point(10, 258),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        Controls.Add(lblRestart);

        var chkContextMenu = new CheckBox
        {
            Text = "Agregar \"Convertir con Recodio\" al menu contextual de Windows",
            Location = new Point(10, 285),
            Size = new Size(450, 36),
            Checked = WindowsContextMenu.IsRegistered(),
        };
        chkContextMenu.CheckedChanged += (_, _) =>
        {
            if (chkContextMenu.Checked) WindowsContextMenu.Register();
            else WindowsContextMenu.Unregister();
        };
        Controls.Add(chkContextMenu);

        var btnCheckUpdates = new Button
        {
            Text = "Buscar actualizaciones de yt-dlp / ffmpeg",
            Location = new Point(10, 328),
            Size = new Size(280, 26),
        };
        var lblUpdateResult = new Label { Text = "", Location = new Point(10, 360), Size = new Size(450, 34), AutoSize = false };
        Controls.Add(lblUpdateResult);

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
            Location = new Point(10, 400),
            Size = new Size(280, 26),
        };
        var lblAppUpdateResult = new Label
        {
            Text = $"Version instalada: {AppSelfUpdater.CurrentVersion}",
            Location = new Point(10, 432),
            Size = new Size(450, 34),
            AutoSize = false,
        };
        Controls.Add(lblAppUpdateResult);

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
                // DownloadAndApplyAsync ends the process on success - nothing after this runs.
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

        var btnSave = new Button { Text = "Guardar", Location = new Point(255, 475), DialogResult = DialogResult.OK };
        Controls.Add(btnSave);

        var btnCancel = new Button { Text = "Cancelar", Location = new Point(340, 475), DialogResult = DialogResult.Cancel };
        Controls.Add(btnCancel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

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
        foreach (var a in new[] { "upgrade", "--id", packageId, "-e", "--accept-package-agreements", "--accept-source-agreements", "--silent" })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (stdout.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase))
                return "ya esta actualizado";
            if (stdout.Contains("No installed package found", StringComparison.OrdinalIgnoreCase))
                return "no instalado";
            if (stdout.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
                return "actualizado correctamente";
            return $"revisa manualmente (winget upgrade --id {packageId})";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            return "winget no disponible";
        }
    }
}
