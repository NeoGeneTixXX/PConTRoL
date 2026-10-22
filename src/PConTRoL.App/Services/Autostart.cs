using Microsoft.Win32;

namespace PConTRoL.App.Services;

/// <summary>"Mit Windows starten" über HKCU\...\Run (nur für den aktuellen Benutzer).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PConTRoL";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Autostart konnte nicht gesetzt werden", ex);
        }
    }
}
