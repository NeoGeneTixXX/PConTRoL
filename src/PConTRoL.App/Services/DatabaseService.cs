using PConTRoL.Core.Database;

namespace PConTRoL.App.Services;

/// <summary>
/// Gemeinsame Datenbank (Neon/PostgreSQL wie beim CSV-NaViGaTOR), nur als Update-Quelle für den <see cref="UpdateService"/>.
/// Die Laufzeiten bleiben lokal im Hauptordner (_PConTRoL\laufzeiten.jsonl).
/// </summary>
public sealed class DatabaseService
{
    private volatile SharedDatabase? _current;

    /// <summary>Die eingerichtete Datenbank (null = keine). Threadsicher lesbar.</summary>
    public SharedDatabase? Current => _current;

    /// <summary>Verbindung aus den Einstellungen übernehmen (nach Start und nach „Speichern“).</summary>
    public void Configure(string? connectionText) =>
        _current = DatabaseConnection.TryBuild(connectionText, out var cs) == null ? new SharedDatabase(cs) : null;
}
