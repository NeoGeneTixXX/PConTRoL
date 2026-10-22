using PConTRoL.Core.Logging;

namespace PConTRoL.Core.Files;

public sealed record RetryPolicy(int Attempts, TimeSpan Delay)
{
    /// <summary>5 Versuche à 400 ms – reicht, wenn die Maschine eine Datei nur kurz liest.</summary>
    public static readonly RetryPolicy Default = new(5, TimeSpan.FromMilliseconds(400));
}

/// <summary>Fehler bei einer Dateiaktion. IsLocked: Datei war gesperrt (Erneut versuchen sinnvoll).</summary>
public sealed class FileOperationException(string message, bool isLocked, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsLocked { get; } = isLocked;
}

/// <summary>
/// Führt Verschieben und Datum-Setzen aus und kann alles wieder rückgängig machen.
/// Es wird NIE eine Datei gelöscht oder überschrieben und nie der Inhalt verändert.
/// </summary>
public sealed class FileTransaction
{
    private readonly RetryPolicy _retry;
    private readonly ActionLog? _log;
    private readonly List<(string Description, Action Undo)> _undo = [];

    public FileTransaction(RetryPolicy retry, ActionLog? log)
    {
        _retry = retry;
        _log = log;
    }

    public void SetLastWriteTimeUtc(string path, DateTime newUtc, string reason)
    {
        var old = File.GetLastWriteTimeUtc(path);
        if (old == newUtc) return;
        Retry(() => File.SetLastWriteTimeUtc(path, newUtc), path);
        _undo.Add(($"Datum zurück: {Path.GetFileName(path)}", () => File.SetLastWriteTimeUtc(path, old)));
        _log?.Write("DATUM", Path.GetFileName(path), reason, old, newUtc);
    }

    public void Move(string source, string destination, string reason)
    {
        if (!File.Exists(source))
            throw new FileOperationException($"Datei nicht gefunden: {source}", isLocked: false);
        if (File.Exists(destination))
            throw new FileOperationException($"Ziel existiert bereits, es wird nichts überschrieben: {destination}", isLocked: false);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var time = File.GetLastWriteTimeUtc(source);
        Retry(() => File.Move(source, destination, overwrite: false), source);
        _undo.Add(($"Zurückverschieben: {Path.GetFileName(destination)}", () => File.Move(destination, source, overwrite: false)));
        _log?.Write("VERSCHOBEN", Path.GetFileName(source), $"{reason}: {source} -> {destination}", time, File.GetLastWriteTimeUtc(destination));
    }

    /// <summary>Macht alle bisherigen Schritte in umgekehrter Reihenfolge rückgängig.</summary>
    /// <returns>Liste der Schritte, die NICHT rückgängig gemacht werden konnten (sollte leer sein).</returns>
    public List<string> Rollback()
    {
        var failed = new List<string>();
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            var (description, undo) = _undo[i];
            try
            {
                Retry(undo, description);
                _log?.Write("RÜCKGÄNGIG", description);
            }
            catch (Exception ex)
            {
                failed.Add($"{description}: {ex.Message}");
                _log?.Write("FEHLER-RÜCKGÄNGIG", description, ex.Message);
            }
        }
        _undo.Clear();
        return failed;
    }

    private void Retry(Action action, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < _retry.Attempts && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(_retry.Delay);
            }
            catch (UnauthorizedAccessException) when (attempt < _retry.Attempts)
            {
                Thread.Sleep(_retry.Delay);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                throw new FileOperationException(
                    $"Die Datei ist gesperrt (vermutlich liest die Maschine sie gerade): {Path.GetFileName(path)}", isLocked: true, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new FileOperationException(
                    $"Kein Zugriff auf {Path.GetFileName(path)} (gesperrt oder schreibgeschützt).", isLocked: true, ex);
            }
        }
    }
}
