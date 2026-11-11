using Microsoft.Win32;

namespace Remoter.App.Features;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Remoter";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать автозапуск: {ex.Message}");
            return false;
        }
    }

    public static bool Enable()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\" --tray");
            Log.Info($"Автозапуск включён: {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось включить автозапуск", ex);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Автозапуск выключен");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось выключить автозапуск", ex);
            return false;
        }
    }

    public static bool Toggle() => IsEnabled() ? Disable() : Enable();
}
