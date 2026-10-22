using System.Globalization;
using System.Text;

namespace PConTRoL.Core.Logging;

/// <summary>
/// Protokolliert jede Dateiaktion: Zeit | Rechner\Benutzer | Aktion | Datei | Details | Datum alt | Datum neu.
/// Eine Datei pro Monat im gemeinsamen Ordner. Wenn der nicht beschreibbar ist, lokal in %APPDATA%.
/// </summary>
public sealed class ActionLog
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly string _folder;
    private readonly string _fallbackFolder;
    private readonly object _lock = new();

    public ActionLog(string folder, string? fallbackFolder = null)
    {
        _folder = folder;
        _fallbackFolder = fallbackFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PConTRoL");
    }

    public string CurrentFile => Path.Combine(_folder, FileName(DateTime.Now));

    public void Write(string action, string file, string details = "", DateTime? oldUtc = null, DateTime? newUtc = null)
    {
        var line = string.Join(" | ",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", De),
            $"{Environment.MachineName}\\{Environment.UserName}",
            action,
            file,
            details,
            Fmt(oldUtc),
            Fmt(newUtc));

        lock (_lock)
        {
            if (!TryAppend(_folder, line))
                TryAppend(_fallbackFolder, line);
        }
    }

    private static bool TryAppend(string folder, string line)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.CreateDirectory(folder);
                using var stream = new FileStream(Path.Combine(folder, FileName(DateTime.Now)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private static string FileName(DateTime now) => $"aktionen_{now:yyyy-MM}.log";

    private static string Fmt(DateTime? utc) =>
        utc is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", De) : "";
}
