using System.Windows.Threading;
using PConTRoL.Core.Database;
using PConTRoL.Core.Timing;

namespace PConTRoL.App.Services;

/// <summary>
/// Gemeinsame Datenbank (Neon/PostgreSQL wie beim CSV-NaViGaTOR): gleicht die Laufzeiten alle
/// <see cref="SyncInterval"/> in beide Richtungen ab und dient dem <see cref="UpdateService"/> als Update-Quelle.
/// Ohne eingerichtete Datenbank oder ohne Netz läuft alles weiter, nur eben mit den Laufzeiten aus dem Hauptordner.
/// </summary>
public sealed class DatabaseService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    private readonly DispatcherTimer _timer = new() { Interval = SyncInterval };
    private readonly Func<RunTimeStore?> _store;
    private volatile SharedDatabase? _current;
    private int _syncing;
    private string? _lastError;

    public DatabaseService(Func<RunTimeStore?> store) => _store = store;

    /// <summary>Die eingerichtete Datenbank (null = keine). Threadsicher lesbar.</summary>
    public SharedDatabase? Current => _current;

    /// <summary>Kurzer Stand für die Einstellungen, z. B. „Abgeglichen 10:42 · 312 Laufzeiten“.</summary>
    public string StatusText { get; private set; } = "Datenbank nicht eingerichtet";

    /// <summary>Neue Laufzeiten von anderen Rechnern sind angekommen (UI-Thread).</summary>
    public event EventHandler? RunTimesChanged;

    /// <summary>Verbindung aus den Einstellungen übernehmen (nach Start und nach „Speichern“).</summary>
    public void Configure(string? connectionText)
    {
        _current = DatabaseConnection.TryBuild(connectionText, out var cs) == null ? new SharedDatabase(cs) : null;
        StatusText = _current == null ? "Datenbank nicht eingerichtet" : "Datenbank eingerichtet, noch nicht abgeglichen";
        _lastError = null;
    }

    public void Start()
    {
        _timer.Tick += async (_, _) => await SyncNowAsync();
        _timer.Start();
        _ = SyncNowAsync();
    }

    /// <summary>Laufzeiten abgleichen. Fehler werden protokolliert (nur beim Wechsel), stören aber nie.</summary>
    public async Task SyncNowAsync()
    {
        var db = _current;
        var store = _store();
        if (db == null || store == null || Interlocked.Exchange(ref _syncing, 1) == 1) return;
        try
        {
            var result = await Task.Run(() => db.SyncRunTimesAsync(store));
            StatusText = $"Abgeglichen {DateTime.Now:HH:mm} · {result.Total} Laufzeiten in der Datenbank";
            if (_lastError != null) ErrorLog.Write("Datenbank wieder erreichbar.");
            _lastError = null;
            if (result.Downloaded > 0) RunTimesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            var message = UpdateService.Describe(ex);
            StatusText = $"Datenbank nicht erreichbar ({DateTime.Now:HH:mm}): {message}";
            if (message != _lastError) ErrorLog.Write("Abgleich der Laufzeiten mit der Datenbank fehlgeschlagen", ex);
            _lastError = message;
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }
}
