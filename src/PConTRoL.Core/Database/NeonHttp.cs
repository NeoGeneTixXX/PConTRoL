using System.Globalization;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json.Nodes;
using Npgsql;

namespace PConTRoL.Core.Database;

/// <summary>
/// SQL über HTTPS (Port 443) statt über den Postgres-Port 5432, wie beim CSV-NaViGaTOR: Neon nimmt unter
/// <c>https://&lt;host&gt;/sql</c> Abfragen als JSON entgegen. In Firmennetzen ist 5432 nach außen oft gesperrt
/// (Fehler „Failed to connect … 5432 … timed out“), 443 dagegen fast immer offen.
/// </summary>
internal sealed class NeonHttp
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // Wie SslMode=Require bei Npgsql: verschlüsselt, Servername muss stimmen, die Zertifikatskette wird aber nicht
        // verlangt. Sonst scheitert es auf Rechnern ohne Windows Update („PartialChain“) genauso wie GitHub.
        // Was heruntergeladen wird, prüft PConTRoL ohnehin über SHA-256.
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None,
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    private readonly Uri _endpoint;
    private readonly string _connectionUrl;

    public NeonHttp(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        _endpoint = new Uri($"https://{b.Host}/sql");
        var port = b.Port is 0 or 5432 ? "" : ":" + b.Port.ToString(CultureInfo.InvariantCulture);
        _connectionUrl = $"postgresql://{Uri.EscapeDataString(b.Username ?? "")}:{Uri.EscapeDataString(b.Password ?? "")}@{b.Host}{port}/"
                         + $"{Uri.EscapeDataString(b.Database ?? "")}?sslmode=require";
    }

    /// <summary>Führt eine Abfrage aus (Parameter als $1, $2 …) und liefert die Zeilen; alle Werte kommen als Text.</summary>
    public async Task<IReadOnlyList<JsonObject>> QueryAsync(string sql, object?[] parameters, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new { query = sql, @params = parameters }),
        };
        request.Headers.Add("Neon-Connection-String", _connectionUrl);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Datenbank über HTTPS: {(string?)body?["message"] ?? response.ReasonPhrase} (HTTP {(int)response.StatusCode})");
        return body?["rows"] is JsonArray rows ? rows.OfType<JsonObject>().ToList() : [];
    }

    /// <summary>Ganze Zahlen kommen je nach Typ als JSON-Zahl (integer) oder als Text (bigint).</summary>
    public static long Number(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<long>(out var number) ? number : long.Parse((string)value!, CultureInfo.InvariantCulture);

    /// <summary>bytea kommt als Text „\x1f8b…“.</summary>
    public static byte[] Bytes(JsonNode? value)
    {
        var text = (string?)value ?? "";
        return Convert.FromHexString(text.StartsWith(@"\x", StringComparison.Ordinal) ? text[2..] : text);
    }
}
