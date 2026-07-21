using Microsoft.Win32;

namespace Recodio;

public static class WindowsContextMenu
{
    private const string VerbName = "RecodioConvert";
    private const string MenuText = "Convertir con Recodio";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\SystemFileAssociations\{FormatConverter.ConvertibleExtensions[0]}\shell\{VerbName}", false);
        return key != null;
    }

    public static void Register()
    {
        var exePath = Application.ExecutablePath;
        foreach (var ext in FormatConverter.ConvertibleExtensions)
        {
            var shellPath = $@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbName}";
            using var shellKey = Registry.CurrentUser.CreateSubKey(shellPath);
            shellKey.SetValue("", MenuText);
            shellKey.SetValue("Icon", $"\"{exePath}\",0");

            using var cmdKey = shellKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }
    }

    public static void Unregister()
    {
        foreach (var ext in FormatConverter.ConvertibleExtensions)
        {
            var shellPath = $@"Software\Classes\SystemFileAssociations\{ext}\shell";
            using var shellKey = Registry.CurrentUser.OpenSubKey(shellPath, true);
            shellKey?.DeleteSubKeyTree(VerbName, false);
        }
    }
}
