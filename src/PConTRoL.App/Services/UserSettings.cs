using System.IO;
using System.Text.Json;

namespace PConTRoL.App.Services;

public sealed class ColumnState
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}

/// <summary>Einstellungen pro Windows-Benutzer in %APPDATA%\PConTRoL\settings.json (nicht neben der EXE).</summary>
public sealed class UserSettings
{
    public string MainFolder { get; set; } = "";
    public bool TestMode { get; set; }
    public string TestFolder { get; set; } = "";

    public string PopupBackground { get; set; } = "#FF262626";
    public string PopupText { get; set; } = "#FFF0F0F0";
    public string PopupAccent { get; set; } = "#FFF2C230";
    public double PopupOpacity { get; set; } = 0.92;
    public bool PopupTopmost { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public HashSet<string> CollapsedGroups { get; set; } = [];
    public Dictionary<string, List<ColumnState>> Columns { get; set; } = [];

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 950;
    public bool WindowMaximized { get; set; } = true;
    public double DetailsHeight { get; set; } = 230;

    public string EffectiveRoot => TestMode ? TestFolder : MainFolder;

    public UserSettings Clone() =>
        JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(this, SettingsStore.Options), SettingsStore.Options)!;
}

public static class SettingsStore
{
    // NaN (Fensterposition noch unbekannt) muss speicherbar sein.
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PConTRoL");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Options) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Einstellungen konnten nicht gelesen werden", ex);
        }
        return new UserSettings();
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Einstellungen konnten nicht gespeichert werden", ex);
        }
    }
}

/// <summary>Technische Fehler (Programmabstürze o. ä.) lokal in %APPDATA%\PConTRoL\fehler.log.</summary>
public static class ErrorLog
{
    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "fehler.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{(ex != null ? " | " + ex : "")}{Environment.NewLine}");
        }
        catch
        {
            // Logging darf das Programm nie zum Absturz bringen.
        }
    }
}
