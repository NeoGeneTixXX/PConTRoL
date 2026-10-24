using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows.Threading;
using PConTRoL.Core;
using PConTRoL.Core.Database;
using PConTRoL.Core.Updates;
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
/// Automatische Updates über GitHub Releases (Velopack), mit zwei Ausweichwegen: Hauptordner und Datenbank.
///
/// - Rechner, die GitHub erreichen, legen das neueste Paket zusätzlich in &lt;Hauptordner&gt;\_PConTRoL\updates\ ab
///   und, wenn eingerichtet, in der gemeinsamen Datenbank (wie beim CSV-NaViGaTOR: Dateiliste + gzip-Inhalte).
///   Rechner ohne funktionierende GitHub-Verbindung (z. B. Maschinen-PC mit veralteten Stammzertifikaten,
///   Fehler „PartialChain“) holen das Update dann von dort. Die Datenbank prüft das Zertifikat nicht und klappt deshalb
///   auch auf solchen Rechnern.
/// - Prüft beim Start und dann alle <see cref="CheckInterval"/> im Hintergrund.
/// - Lädt ein neues Release still herunter, spielt es aber NIE selbst ein:
///   nur nach Bestätigung (<see cref="ApplyAndRestart"/>) oder beim nächsten Programmstart
///   (Velopack wendet heruntergeladene Updates in <c>VelopackApp.Run()</c> an).
/// - Fehler landen in %APPDATA%\PConTRoL\update.log, die laufende Version arbeitet weiter.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/NeoGeneTixXX/PConTRoL";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private readonly DispatcherTimer _timer = new() { Interval = CheckInterval };
    private readonly Func<string?> _mainFolder;
    private readonly Func<SharedDatabase?> _database;
    private Task<UpdateCheckResult>? _running;
    private UpdateManager? _pendingManager;
    private int _mirroring;

    /// <param name="mainFolder">Aktueller Hauptordner (für die Update-Ablage in _PConTRoL\updates).</param>
    /// <param name="database">Gemeinsame Datenbank (null = keine eingerichtet).</param>
    public UpdateService(Func<string?>? mainFolder = null, Func<SharedDatabase?>? database = null)
    {
        _mainFolder = mainFolder ?? (() => null);
        _database = database ?? (() => null);
    }

    /// <summary>Lokale Ablage: von GitHub geladenes Paket (zum Weitergeben) bzw. aus der Datenbank geladenes Update.</summary>
    private static string CacheFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PConTRoL-Updates");

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

    /// <summary>
    /// Erst GitHub. Klappt das, wird das neueste Paket zusätzlich im Hauptordner und in der Datenbank abgelegt
    /// (für Rechner ohne funktionierende GitHub-Verbindung). Klappt GitHub nicht: erst der Hauptordner, dann die Datenbank.
    /// </summary>
    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        Exception githubError;
        try
        {
            var result = await CheckWithAsync(_manager, "GitHub");
            _ = Task.Run(MirrorAsync); // Download + Prüfsumme (~70 MB) nicht im UI-Thread
            return result;
        }
        catch (Exception ex)
        {
            // Kein Netz, Zertifikat, Rate-Limit … – dann die Ausweichwege versuchen.
            githubError = ex;
            UpdateLog.Write("Update-Prüfung über GitHub fehlgeschlagen", ex);
        }

        var errors = new List<string> { Describe(githubError) };
        UpdateCheckResult? fallback = null;

        var folder = MirrorFolder();
        if (folder == null || !File.Exists(Path.Combine(folder, UpdateFeed.FeedFileName)))
            errors.Add("Im Hauptordner (_PConTRoL\\updates) liegt kein Update.");
        else
        {
            try
            {
                fallback = await CheckWithAsync(new UpdateManager(new SimpleFileSource(new DirectoryInfo(folder))), "Hauptordner");
                if (fallback.State == UpdateCheckState.Ready) return fallback;
            }
            catch (Exception ex)
            {
                UpdateLog.Write("Update über den Hauptordner fehlgeschlagen", ex);
                errors.Add("Hauptordner: " + Describe(ex));
            }
        }

        // Die Ablage im Hauptordner kann veraltet sein: deshalb auch dann noch die Datenbank fragen.
        var db = _database();
        if (db == null) errors.Add("Keine Datenbank eingerichtet.");
        else
        {
            try
            {
                var result = await CheckDatabaseAsync(db);
                if (result.State == UpdateCheckState.Ready || fallback == null) return result;
            }
            catch (Exception ex)
            {
                UpdateLog.Write("Update über die Datenbank fehlgeschlagen", ex);
                errors.Add("Datenbank: " + Describe(ex, "der Datenbank"));
            }
        }

        return fallback ?? new(UpdateCheckState.Failed, Error: string.Join(" ", errors));
    }

    /// <summary>
    /// Wie im Navigator: Dateiliste aus der Datenbank lesen und nur dann, wenn dort eine neuere Version liegt, die Dateien
    /// laden (Prüfsumme geprüft). Daraus wird dann wie aus einem Ordner aktualisiert.
    /// </summary>
    private async Task<UpdateCheckResult> CheckDatabaseAsync(SharedDatabase db)
    {
        var files = await Task.Run(() => db.GetUpdateFilesAsync());
        var feed = files.FirstOrDefault(f => f.File.Equals(UpdateFeed.FeedFileName, StringComparison.OrdinalIgnoreCase));
        var package = files.FirstOrDefault(f => f.File.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase));
        if (feed == null || package == null) throw new InvalidOperationException("In der Datenbank liegt noch kein Update.");

        if (Version.TryParse(package.Version, out var offered) && Version.TryParse(CurrentVersion, out var current) && offered <= current)
            return Pending != null ? new(UpdateCheckState.Ready, Pending.Version.ToString()) : new(UpdateCheckState.UpToDate);

        await Task.Run(async () =>
        {
            foreach (var entry in new[] { package, feed })
            {
                var target = Path.Combine(CacheFolder, entry.File);
                if (File.Exists(target) && new FileInfo(target).Length == entry.Size && await SharedDatabase.Sha256Async(target) == entry.Sha256)
                    continue;
                await db.DownloadUpdateFileAsync(entry, target);
            }
            DeleteOldPackages(CacheFolder, package.File);
        });
        return await CheckWithAsync(new UpdateManager(new SimpleFileSource(new DirectoryInfo(CacheFolder))), "Datenbank");
    }

    /// <summary>Prüft eine Quelle und lädt ein neues Release herunter. Fehler werden weitergereicht.</summary>
    private async Task<UpdateCheckResult> CheckWithAsync(UpdateManager manager, string via)
    {
        var info = await manager.CheckForUpdatesAsync();
        if (info == null)
            return Pending != null ? new(UpdateCheckState.Ready, Pending.Version.ToString()) : new(UpdateCheckState.UpToDate);

        var target = info.TargetFullRelease;
        if (Pending?.Version == target.Version) return new(UpdateCheckState.Ready, target.Version.ToString());

        await manager.DownloadUpdatesAsync(info);
        Pending = target;
        _pendingManager = manager;
        UpdateLog.Write($"Version {target.Version} über {via} heruntergeladen, wartet auf Bestätigung oder nächsten Start.");
        UpdateLog.MarkAttempt(target.Version.ToString());
        UpdateReady?.Invoke(this, target);
        return new(UpdateCheckState.Ready, target.Version.ToString());
    }

    private string? MirrorFolder()
    {
        var root = _mainFolder();
        if (string.IsNullOrWhiteSpace(root)) return null;
        var paths = new AppPaths(root);
        return paths.IsValid ? UpdateFeed.FolderFor(paths) : null;
    }

    /// <summary>
    /// Holt das neueste Komplettpaket (Prüfsumme geprüft) von GitHub in die lokale Ablage und gibt es weiter:
    /// samt passender releases.win.json in den Hauptordner und in die Datenbank.
    /// Läuft im Hintergrund nur auf Rechnern, die GitHub erreichen; Fehler stören nie.
    /// </summary>
    private async Task MirrorAsync()
    {
        var folder = MirrorFolder();
        var db = _database();
        if ((folder == null && db == null) || Interlocked.Exchange(ref _mirroring, 1) == 1) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var package = UpdateFeed.LatestFull(await http.GetStringAsync($"{RepoUrl}/releases/latest/download/{UpdateFeed.FeedFileName}"));
            if (package == null) return;

            Directory.CreateDirectory(CacheFolder);
            var local = Path.Combine(CacheFolder, package.FileName);
            if (!UpdateFeed.Matches(local, package))
            {
                var tmp = local + ".tmp-" + Environment.MachineName;
                await using (var source = await http.GetStreamAsync($"{RepoUrl}/releases/download/v{package.Version.ToString(3)}/{package.FileName}"))
                await using (var file = File.Create(tmp))
                    await source.CopyToAsync(file);
                if (!UpdateFeed.Matches(tmp, package))
                {
                    File.Delete(tmp);
                    UpdateLog.Write($"Prüfsumme von {package.FileName} stimmt nicht – verworfen.");
                    return;
                }
                File.Move(tmp, local, overwrite: true);
            }
            var localFeed = Path.Combine(CacheFolder, UpdateFeed.FeedFileName);
            WriteIfChanged(localFeed, package.FeedJson);
            DeleteOldPackages(CacheFolder, package.FileName);

            if (folder != null) MirrorToFolder(folder, package, local);
            if (db != null) await MirrorToDatabaseAsync(db, package, local, localFeed);
        }
        catch (Exception ex)
        {
            UpdateLog.Write("Weitergeben des Updates fehlgeschlagen", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _mirroring, 0);
        }
    }

    private static void MirrorToFolder(string folder, FeedPackage package, string local)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, package.FileName);
            if (!File.Exists(target) || new FileInfo(target).Length != package.Size)
            {
                var tmp = target + ".tmp-" + Environment.MachineName;
                File.Copy(local, tmp, overwrite: true);
                File.Move(tmp, target, overwrite: true);
                UpdateLog.Write($"Version {package.Version.ToString(3)} im Hauptordner abgelegt ({folder}).");
            }
            WriteIfChanged(Path.Combine(folder, UpdateFeed.FeedFileName), package.FeedJson);
            DeleteOldPackages(folder, package.FileName);
        }
        catch (Exception ex)
        {
            UpdateLog.Write("Ablage des Updates im Hauptordner fehlgeschlagen", ex);
        }
    }

    private static async Task MirrorToDatabaseAsync(SharedDatabase db, FeedPackage package, string local, string localFeed)
    {
        try
        {
            var version = package.Version.ToString(3);
            var files = await db.GetUpdateFilesAsync();
            var known = files.FirstOrDefault(f => f.File.Equals(package.FileName, StringComparison.OrdinalIgnoreCase));
            if (known != null && known.Version == version && files.Any(f => f.File == UpdateFeed.FeedFileName)) return;

            // Nie eine neuere Version in der Datenbank durch eine ältere ersetzen (z. B. wenn GitHub kurz hinterherhinkt).
            var newest = files.Where(f => f.File.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase))
                .Select(f => Version.TryParse(f.Version, out var v) ? v : null).Max();
            if (newest != null && newest > package.Version) return;

            if (await db.PublishUpdateAsync(version, [local, localFeed]))
                UpdateLog.Write($"Version {version} in der Datenbank abgelegt.");
        }
        catch (Exception ex)
        {
            UpdateLog.Write("Ablage des Updates in der Datenbank fehlgeschlagen", ex);
        }
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var tmp = path + ".tmp-" + Environment.MachineName;
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Ältere selbst abgelegte Pakete wegräumen (nur *-full.nupkg in diesem Ordner).</summary>
    private static void DeleteOldPackages(string folder, string keep)
    {
        foreach (var old in Directory.EnumerateFiles(folder, "*-full.nupkg")
                     .Where(f => !string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(old); } catch (IOException) { }
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
            (_pendingManager ?? _manager).WaitExitThenApplyUpdates(Pending, silent: false, restart: true, []);
            return true;
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"Update auf {Pending.Version} konnte nicht gestartet werden – alte Version läuft weiter", ex);
            return false;
        }
    }

    /// <summary>
    /// Fehlertext mit allen inneren Ursachen („SSL nicht möglich → Zertifikatskette ungültig: UntrustedRoot“).
    /// Bei SSL-Fehlern steht die eigentliche Ursache fast immer erst in der inneren Exception.
    /// </summary>
    public static string Describe(Exception ex, string target = "GitHub")
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (parts.Count == 0 || !parts[^1].Contains(e.Message, StringComparison.Ordinal))
                parts.Add(e.Message.Trim());
        var text = string.Join(" → ", parts);
        if (ex is System.Net.Http.HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException }
            || ex.InnerException is System.Security.Authentication.AuthenticationException)
            text += $" – Die Verbindung zu {target} wurde abgelehnt (Zertifikat, Windows-Uhrzeit, Firewall/Virenscanner prüfen).";
        return text;
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
            // Volle Exception (inkl. innerer Ursachen), sonst fehlt bei SSL-Fehlern genau das Entscheidende.
            File.AppendAllText(FilePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Environment.MachineName} | {message}{(ex != null ? " | " + UpdateService.Describe(ex) + Environment.NewLine + ex : "")}{Environment.NewLine}");
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
