using System.IO.Compression;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using PConTRoL.Core.Timing;

namespace PConTRoL.Core.Database;

/// <summary>Eine Datei des neuesten Updates in der Datenbank (Zeile aus <see cref="SharedDatabase.AppDataTable"/>).</summary>
public sealed record UpdateFileEntry(string File, string Version, long Size, string Sha256, DateTime ChangedUtc);

public sealed record RunTimeSyncResult(int Uploaded, int Downloaded, int Total);

/// <summary>
/// Gemeinsame Datenbank (Neon/PostgreSQL), dieselbe wie beim CSV-NaViGaTOR. PConTRoL legt dort nur eigene Tabellen an
/// (<c>pcontrol_…</c>) und fasst die des Navigators nie an.
///
/// - <b>Laufzeiten</b>: jede gemessene Platte, von allen Rechnern. Abgleich in beide Richtungen mit
///   <c>_PConTRoL\laufzeiten.jsonl</c>; ohne Datenbank läuft die Zeitschätzung trotzdem.
/// - <b>Updates</b> wie im Navigator: <see cref="AppDataTable"/> listet die Dateien des neuesten Updates
///   (Name, Version, Größe, SHA-256), <see cref="UpdateTable"/> hält ihren Inhalt gzip-gepackt, über die Prüfsumme
///   adressiert. Große Dateien (das Velopack-Paket hat ~70 MB) liegen dort in Teilen zu <see cref="PartSize"/>.
/// </summary>
public sealed class SharedDatabase(string connectionString)
{
    public const string AppDataTable = "pcontrol_appdata";
    public const string UpdateTable = "pcontrol_update";
    public const string RunTimeTable = "pcontrol_laufzeit";
    public const int PartSize = 8 * 1024 * 1024;

    private bool _schemaReady;

    private NpgsqlConnection CreateConnection() => new(connectionString);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            if (!_schemaReady)
            {
                await EnsureSchemaAsync(connection, ct);
                _schemaReady = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task EnsureSchemaAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = $"""
            create table if not exists {RunTimeTable} (
                id text primary key,
                start_utc timestamptz not null,
                ende_utc timestamptz not null,
                sekunden double precision not null,
                material text not null default '',
                dim_x double precision not null default 0,
                dim_y double precision not null default 0,
                dim_z double precision not null default 0,
                teile integer not null default 0,
                nest_job text not null default '',
                datei text not null default '',
                rechner text not null default '',
                angelegt timestamptz not null default now()
            );
            create table if not exists {AppDataTable} (
                datei text primary key,
                version text not null default '',
                groesse bigint not null default 0,
                pruefsumme text not null default '',
                geaendert timestamptz not null default now()
            );
            create table if not exists {UpdateTable} (
                pruefsumme text not null,
                teil integer not null,
                groesse bigint not null,
                inhalt bytea not null,
                angelegt timestamptz not null default now(),
                primary key (pruefsumme, teil)
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Verbindung prüfen (legt dabei die Tabellen an). Liefert die Zahl der Laufzeiten in der Datenbank.</summary>
    public async Task<long> TestAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"select count(*) from {RunTimeTable}", connection);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------------ Laufzeiten

    /// <summary>Lokale Laufzeiten hochladen, die der Datenbank fehlen, und fremde in die lokale Datei übernehmen.</summary>
    public async Task<RunTimeSyncResult> SyncRunTimesAsync(RunTimeStore store, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var remote = new List<RunTimeRecord>();
        await using (var cmd = new NpgsqlCommand(
                         $"select id, start_utc, ende_utc, sekunden, material, dim_x, dim_y, dim_z, teile, nest_job, datei, rechner from {RunTimeTable}",
                         connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                remote.Add(new RunTimeRecord
                {
                    Id = reader.GetString(0),
                    StartUtc = reader.GetDateTime(1),
                    EndUtc = reader.GetDateTime(2),
                    Seconds = reader.GetDouble(3),
                    Material = reader.GetString(4),
                    DimX = reader.GetDouble(5),
                    DimY = reader.GetDouble(6),
                    DimZ = reader.GetDouble(7),
                    Parts = reader.GetInt32(8),
                    NestJob = reader.GetString(9),
                    File = reader.GetString(10),
                    Computer = reader.GetString(11),
                });
        }

        var remoteIds = new HashSet<string>(remote.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var missing = store.Load().Where(r => !remoteIds.Contains(r.Id)).ToList();
        foreach (var batch in missing.Chunk(500)) await InsertRunTimesAsync(connection, batch, ct);

        var downloaded = store.AddRange(remote);
        return new RunTimeSyncResult(missing.Count, downloaded, remote.Count + missing.Count);
    }

    private static async Task InsertRunTimesAsync(NpgsqlConnection connection, RunTimeRecord[] batch, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            insert into {RunTimeTable} (id, start_utc, ende_utc, sekunden, material, dim_x, dim_y, dim_z, teile, nest_job, datei, rechner)
            select * from unnest(@id, @start, @ende, @sek, @mat, @dx, @dy, @dz, @teile, @job, @datei, @rechner)
            on conflict (id) do nothing
            """, connection);
        static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc);
        cmd.Parameters.AddWithValue("id", batch.Select(r => r.Id).ToArray());
        cmd.Parameters.AddWithValue("start", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, batch.Select(r => Utc(r.StartUtc)).ToArray());
        cmd.Parameters.AddWithValue("ende", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, batch.Select(r => Utc(r.EndUtc)).ToArray());
        cmd.Parameters.AddWithValue("sek", batch.Select(r => r.Seconds).ToArray());
        cmd.Parameters.AddWithValue("mat", batch.Select(r => r.Material).ToArray());
        cmd.Parameters.AddWithValue("dx", batch.Select(r => r.DimX).ToArray());
        cmd.Parameters.AddWithValue("dy", batch.Select(r => r.DimY).ToArray());
        cmd.Parameters.AddWithValue("dz", batch.Select(r => r.DimZ).ToArray());
        cmd.Parameters.AddWithValue("teile", batch.Select(r => r.Parts).ToArray());
        cmd.Parameters.AddWithValue("job", batch.Select(r => r.NestJob).ToArray());
        cmd.Parameters.AddWithValue("datei", batch.Select(r => r.File).ToArray());
        cmd.Parameters.AddWithValue("rechner", batch.Select(r => r.Computer).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ Updates

    /// <summary>Die Dateien des neuesten Updates laut Datenbank (leer, wenn noch keins hochgeladen wurde).</summary>
    public async Task<IReadOnlyList<UpdateFileEntry>> GetUpdateFilesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"select datei, version, groesse, pruefsumme, geaendert from {AppDataTable} order by datei", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<UpdateFileEntry>();
        while (await reader.ReadAsync(ct))
            result.Add(new UpdateFileEntry(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetDateTime(4)));
        return result;
    }

    /// <summary>
    /// Legt die Dateien eines Updates in der Datenbank ab und ersetzt damit das vorige (dessen Inhalte werden gelöscht).
    /// Alles in einer Transaktion: Andere Rechner sehen entweder das alte oder das vollständige neue Update.
    /// False, wenn gerade ein anderer Rechner hochlädt.
    /// </summary>
    public async Task<bool> PublishUpdateAsync(string version, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var names = files.Select(Path.GetFileName).ToArray();
        var existing = (await GetUpdateFilesAsync(ct)).ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("select pg_try_advisory_xact_lock(hashtext(@name))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue("name", UpdateTable);
            if (!(bool)(await lockCmd.ExecuteScalarAsync(ct))!) return false;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);
            var sha = await Sha256Async(file, ct);
            if (existing.TryGetValue(name, out var known) && known.Sha256 == sha && known.Version == version) continue;

            if (!await HasContentAsync(connection, tx, sha, ct)) await UploadContentAsync(connection, tx, file, sha, info.Length, ct);

            await using var upsert = new NpgsqlCommand($"""
                insert into {AppDataTable} (datei, version, groesse, pruefsumme, geaendert) values (@datei, @version, @groesse, @sha, now())
                on conflict (datei) do update set version = excluded.version, groesse = excluded.groesse,
                    pruefsumme = excluded.pruefsumme, geaendert = excluded.geaendert
                """, connection, tx);
            upsert.Parameters.AddWithValue("datei", name);
            upsert.Parameters.AddWithValue("version", version);
            upsert.Parameters.AddWithValue("groesse", info.Length);
            upsert.Parameters.AddWithValue("sha", sha);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        // Nur das neueste Update vorhalten: alte Einträge und nicht mehr benutzte Inhalte weg.
        await using (var cleanup = new NpgsqlCommand($"""
                         delete from {AppDataTable} where not (datei = any(@names));
                         delete from {UpdateTable} where pruefsumme not in (select pruefsumme from {AppDataTable});
                         """, connection, tx))
        {
            cleanup.Parameters.AddWithValue("names", names);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<bool> HasContentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sha, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"select exists (select 1 from {UpdateTable} where pruefsumme = @sha)", connection, tx);
        cmd.Parameters.AddWithValue("sha", sha);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task UploadContentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string file, string sha, long size,
        CancellationToken ct)
    {
        var packed = Path.Combine(Path.GetTempPath(), $"PConTRoL-{sha[..16]}-{Environment.ProcessId}.gz");
        try
        {
            await using (var source = File.OpenRead(file))
            await using (var target = File.Create(packed))
            await using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
                await source.CopyToAsync(gzip, ct);

            await using var input = File.OpenRead(packed);
            var buffer = new byte[PartSize];
            for (var part = 0; ; part++)
            {
                var read = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (read == 0 && part > 0) break;
                await using var insert = new NpgsqlCommand(
                    $"insert into {UpdateTable} (pruefsumme, teil, groesse, inhalt) values (@sha, @teil, @groesse, @inhalt)", connection, tx);
                insert.Parameters.AddWithValue("sha", sha);
                insert.Parameters.AddWithValue("teil", part);
                insert.Parameters.AddWithValue("groesse", size);
                insert.Parameters.AddWithValue("inhalt", NpgsqlDbType.Bytea, buffer.AsMemory(0, read));
                await insert.ExecuteNonQueryAsync(ct);
                if (read < buffer.Length) break;
            }
        }
        finally
        {
            try { File.Delete(packed); } catch (IOException) { }
        }
    }

    /// <summary>Lädt eine Update-Datei herunter, entpackt sie und prüft Größe und SHA-256. Erst dann liegt sie unter <paramref name="target"/>.</summary>
    public async Task DownloadUpdateFileAsync(UpdateFileEntry entry, string target, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var packed = target + ".gz-" + Environment.MachineName;
        var tmp = target + ".tmp-" + Environment.MachineName;
        try
        {
            await using (var connection = await OpenAsync(ct))
            await using (var cmd = new NpgsqlCommand($"select inhalt from {UpdateTable} where pruefsumme = @sha order by teil", connection))
            {
                cmd.Parameters.AddWithValue("sha", entry.Sha256);
                await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
                await using var output = File.Create(packed);
                var parts = 0;
                while (await reader.ReadAsync(ct))
                {
                    await using var part = reader.GetStream(0);
                    await part.CopyToAsync(output, ct);
                    parts++;
                }
                if (parts == 0) throw new InvalidDataException($"Inhalt von {entry.File} fehlt in der Datenbank.");
            }

            await using (var input = File.OpenRead(packed))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var output = File.Create(tmp))
                await gzip.CopyToAsync(output, ct);

            if (new FileInfo(tmp).Length != entry.Size || await Sha256Async(tmp, ct) != entry.Sha256)
                throw new InvalidDataException($"Prüfsumme von {entry.File} aus der Datenbank stimmt nicht – verworfen.");
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(packed); } catch (IOException) { }
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>SHA-256 in Kleinbuchstaben, wie in den Tabellen des Navigators.</summary>
    public static async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
