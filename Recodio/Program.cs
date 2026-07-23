using System.Text.Json;

namespace Recodio;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Global\\Recodio_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            if (args.Length > 0 && PipeIpc.TrySendFiles(args))
            {
                return;
            }
            if (args.Length == 0)
            {
                MessageBox.Show("Recodio ya esta corriendo. Fijate en la bandeja del sistema.", "Recodio");
            }
            return;
        }

        AppPaths.MigrateFromInstallDir(AppContext.BaseDirectory);
        Application.SetColorMode(ResolveColorMode());
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }

    private static SystemColorMode ResolveColorMode()
    {
        try
        {
            var configFile = AppPaths.ConfigFile;
            // Also check legacy path if migration hasn't run yet for this process path.
            if (!File.Exists(configFile))
                configFile = Path.Combine(AppContext.BaseDirectory, "config.json");

            if (File.Exists(configFile))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile));
                if (config != null)
                {
                    return config.Theme switch
                    {
                        "light" => SystemColorMode.Classic,
                        "system" => SystemColorMode.System,
                        _ => SystemColorMode.Dark,
                    };
                }
            }
        }
        catch { /* fall back to dark */ }
        return SystemColorMode.Dark;
    }
}
