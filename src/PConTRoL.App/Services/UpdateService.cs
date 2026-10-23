using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace PConTRoL.App.Services;

public enum UpdateCheckState
{
    /// <summary>Nicht per Setup installiert (z. B. lose EXE): kann sich nicht selbst aktualisieren.</summary>
    NotInstalled,
    UpToDate,

    /// <summary>Neue Version heruntergeladen, wartet aufs Einspielen.</summary>
    Ready,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckState State, string? Version = null, string? Error = null);

/// <summary>
/// Automatische Updates über GitHub Releases (Velopack).
///
/// - Prüft beim Start und dann alle <see cref="CheckInterval"/> im Hintergrund.
/// - Lädt ein neues Release still herunter, spielt es aber NIE selbst ein:
///   nur nach Bestätigung (<see cref="ApplyAndRestart"/>) oder beim nächsten Programmstart
///   (Velopack wendet heruntergeladene Updates in <c>VelopackApp.Run()</c> an).
/// - Fehler landen in %APPDATA%\PConTRoL\update.log, die laufende Version arbeitet weiter.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/NeoGeneTixXX/PConTRoL";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private readonly DispatcherTimer _timer = new() { Interval = CheckInterval };
    private Task<UpdateCheckResult>? _running;

    /// <summary>Heruntergeladenes, noch nicht eingespieltes Update (oder null).</summary>
    public VelopackAsset? Pending { get; private set; }

    /// <summary>Wird ausgelöst, sobald ein neues Update heruntergeladen und bereit ist.</summary>
    public event EventHandler<VelopackAsset>? UpdateReady;

    /// <summary>Angezeigte Programmversion, z. B. "1.2.0".</summary>
    public static string CurrentVersion { get; } = ReadVersion();

    /// <summary>
    /// Nur eine per Setup installierte App kann sich aktualisieren. Beim Start aus Visual Studio
    /// oder als lose EXE bleibt die Update-Prüfung aus.
    /// </summary>
    public bool IsEnabled => _manager.IsInstalled;

    public void Start()
    {
        UpdateLog.ReportLastAttempt(CurrentVersion);
        if (!IsEnabled) return;

        _timer.Tick += async (_, _) => await CheckNowAsync();
        _timer.Start();
        _ = CheckNowAsync();
    }

    /// <summary>
    /// Sucht nach einem neuen Release und lädt es herunter. Läuft schon eine Prüfung (z. B. die automatische),
    /// wird auf deren Ergebnis gewartet. Nur vom UI-Thread aufrufen.
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        if (!IsEnabled) return new(UpdateCheckState.NotInstalled);
        if (_running != null) return await _running;
        _running = RunCheckAsync();
        try
        {
            return await _running;
        }
        finally
        {
            _running = null;
        }
    }

    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null)
                return Pending != null ? new(UpdateCheckState.Ready, Pending.Version.ToString()) : new(UpdateCheckState.UpToDate);

            var target = info.TargetFullRelease;
            if (Pending?.Version == target.Version) return new(UpdateCheckState.Ready, target.Version.ToString());

            await _manager.DownloadUpdatesAsync(info);
            Pending = target;
            UpdateLog.Write($"Version {target.Version} heruntergeladen, wartet auf Bestätigung oder nächsten Start.");
            UpdateLog.MarkAttempt(target.Version.ToString());
            UpdateReady?.Invoke(this, target);
            return new(UpdateCheckState.Ready, target.Version.ToString());
        }
        catch (Exception ex)
        {
            // Kein Netz, GitHub nicht erreichbar, Rate-Limit … – einfach beim nächsten Mal wieder versuchen.
            UpdateLog.Write("Update-Prüfung fehlgeschlagen", ex);
            return new(UpdateCheckState.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Startet den Velopack-Updater, der nach dem Beenden dieses Prozesses das Update einspielt und
    /// PConTRoL neu startet. Der Aufrufer muss danach sauber beenden. Liefert false bei Fehler
    /// (dann läuft die alte Version einfach weiter).
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (Pending == null) return false;
        try
        {
            UpdateLog.Write($"Update auf {Pending.Version} bestätigt, PConTRoL wird neu gestartet.");
            UpdateLog.MarkAttempt(Pending.Version.ToString());
            _manager.WaitExitThenApplyUpdates(Pending, silent: false, restart: true, []);
            return true;
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"Update auf {Pending.Version} konnte nicht gestartet werden – alte Version läuft weiter", ex);
            return false;
        }
    }

    private static string ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                ?? "?";
        var plus = v.IndexOf('+'); // Git-Hash, den das SDK anhängt
        return plus >= 0 ? v[..plus] : v;
    }
}

/// <summary>Update-Protokoll in %APPDATA%\PConTRoL\update.log (Velopack selbst schreibt zusätzlich velopack.log).</summary>
public static class UpdateLog
{
    private static string FilePath => Path.Combine(SettingsStore.Folder, "update.log");
    private static string MarkerPath => Path.Combine(SettingsStore.Folder, "update-ziel.txt");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(FilePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Environment.MachineName} | {message}{(ex != null ? " | " + ex.Message : "")}{Environment.NewLine}");
        }
        catch
        {
            // Logging darf das Programm nie zum Absturz bringen.
        }
    }

    /// <summary>Merkt sich die Zielversion, damit der nächste Start Erfolg oder Fehlschlag protokollieren kann.</summary>
    public static void MarkAttempt(string targetVersion)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(MarkerPath, targetVersion);
        }
        catch
        {
            // Ohne Marker fehlt nur der Log-Eintrag beim nächsten Start.
        }
    }

    /// <summary>Beim Start: Wurde ein Update versucht? Dann Ergebnis ins Log schreiben.</summary>
    public static void ReportLastAttempt(string currentVersion)
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var target = File.ReadAllText(MarkerPath).Trim();
            File.Delete(MarkerPath);
            if (target.Length == 0) return;

            if (string.Equals(target, currentVersion, StringComparison.OrdinalIgnoreCase))
                Write($"Update auf {target} erfolgreich installiert.");
            else
                Write($"Update auf {target} wurde (noch) nicht installiert – Version {currentVersion} läuft weiter. Details ggf. in velopack.log.");
        }
        catch
        {
            // Nicht kritisch.
        }
    }
}
