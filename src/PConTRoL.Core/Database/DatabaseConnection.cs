using Npgsql;

namespace PConTRoL.Core.Database;

/// <summary>
/// Liest eine eingefügte Verbindungszeichenfolge, wie sie Neon ausgibt
/// (<c>postgresql://benutzer:passwort@host/datenbank?sslmode=require</c>), oder die Form <c>Host=…;Database=…</c>.
/// </summary>
public static class DatabaseConnection
{
    /// <summary>Null bei Erfolg, sonst ein Fehlertext für die Oberfläche.</summary>
    public static string? TryBuild(string? text, out string connectionString)
    {
        connectionString = "";
        text = Unwrap(text?.Trim() ?? "");
        if (text.Length == 0) return "Es wurde nichts eingetragen.";

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                      || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                ? FromUrl(text)
                : new NpgsqlConnectionStringBuilder(text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
        {
            return "Die Verbindungszeichenfolge ließ sich nicht lesen: " + ex.Message;
        }

        if (string.IsNullOrWhiteSpace(builder.Host)) return "In der Verbindung fehlt der Server (Host).";
        if (string.IsNullOrWhiteSpace(builder.Database)) return "In der Verbindung fehlt der Datenbankname.";
        if (string.IsNullOrWhiteSpace(builder.Username)) return "In der Verbindung fehlt der Benutzer.";

        // Ohne das hängt ein falsch geschriebener Host eine halbe Minute lang.
        builder.Timeout = 15;
        builder.CommandTimeout = 120;
        // Taucht in pg_stat_activity auf: auf dem Server ist so zu sehen, wer anklopft.
        builder.ApplicationName = "PConTRoL";
        connectionString = builder.ConnectionString;
        return null;
    }

    /// <summary>Kurzfassung ohne Passwort, z. B. „neondb_owner@ep-….neon.tech/neondb“.</summary>
    public static string Describe(string? text) =>
        TryBuild(text, out var cs) == null && new NpgsqlConnectionStringBuilder(cs) is var b
            ? $"{b.Username}@{b.Host}/{b.Database}"
            : "nicht eingerichtet";

    private static NpgsqlConnectionStringBuilder FromUrl(string text)
    {
        var uri = new Uri(text);
        var user = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            Username = Uri.UnescapeDataString(user[0]),
            Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
            // Wie in den Zeichenfolgen der Anbieter: verschlüsselt, Zertifikat ungeprüft. Damit klappt es auch
            // auf Rechnern mit veralteten Stammzertifikaten (dort scheitert GitHub mit „PartialChain“).
            SslMode = SslMode.Require,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            switch (Uri.UnescapeDataString(parts[0]).ToLowerInvariant())
            {
                case "sslmode" when Enum.TryParse<SslMode>(value.Replace("-", ""), ignoreCase: true, out var mode):
                    builder.SslMode = mode;
                    break;
                case "channel_binding" when Enum.TryParse<ChannelBinding>(value, ignoreCase: true, out var binding):
                    builder.ChannelBinding = binding;
                    break;
            }
        }
        return builder;
    }

    /// <summary>Entfernt umschließende Anführungszeichen, denn abgeschrieben wird die Zeichenfolge oft samt Schale.</summary>
    private static string Unwrap(string text)
    {
        while (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0]) text = text[1..^1].Trim();
        return text;
    }
}
