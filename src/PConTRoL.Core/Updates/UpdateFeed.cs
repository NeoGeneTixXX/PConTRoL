using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PConTRoL.Core.Updates;

/// <summary>Das neueste Komplettpaket aus einer Velopack-Release-Liste (releases.win.json).</summary>
public sealed record FeedPackage(Version Version, string FileName, string Sha256, long Size, string FeedJson);

/// <summary>
/// Hilfen für die Update-Ablage im Hauptordner (<c>_PConTRoL\updates\</c>): Rechner, die GitHub erreichen,
/// legen dort das neueste Komplettpaket ab; Rechner ohne funktionierende GitHub-Verbindung (z. B. Maschinen-PC
/// mit veralteten Stammzertifikaten) holen es von dort.
/// </summary>
public static class UpdateFeed
{
    public const string FeedFileName = "releases.win.json";
    public const string FolderName = "updates";

    public static string FolderFor(AppPaths paths) => Path.Combine(paths.Shared, FolderName);

    /// <summary>
    /// Sucht das neueste „Full“-Paket und liefert dazu eine Release-Liste, die NUR dieses Paket enthält
    /// (Delta-Pakete werden nicht gespiegelt). Null, wenn die Liste kein brauchbares Komplettpaket enthält.
    /// </summary>
    public static FeedPackage? LatestFull(string feedJson)
    {
        if (JsonNode.Parse(feedJson)?["Assets"] is not JsonArray assets) return null;

        JsonNode? best = null;
        Version? bestVersion = null;
        foreach (var asset in assets)
        {
            if (asset == null || !string.Equals((string?)asset["Type"], "Full", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Version.TryParse((string?)asset["Version"], out var v)) continue;
            var file = (string?)asset["FileName"];
            if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file)) continue; // keine Pfade
            if (string.IsNullOrWhiteSpace((string?)asset["SHA256"])) continue;
            if (bestVersion == null || v > bestVersion)
            {
                best = asset;
                bestVersion = v;
            }
        }
        if (best == null || bestVersion == null) return null;

        var size = best["Size"] is JsonValue s && s.TryGetValue<long>(out var l) ? l : 0;
        var filtered = new JsonObject { ["Assets"] = new JsonArray(best.DeepClone()) };
        return new FeedPackage(bestVersion, (string)best["FileName"]!, ((string)best["SHA256"]!).Trim(), size, filtered.ToJsonString());
    }

    /// <summary>Stimmt die Datei mit der Prüfsumme aus der Release-Liste überein?</summary>
    public static bool Matches(string file, FeedPackage package)
    {
        if (!File.Exists(file)) return false;
        if (package.Size > 0 && new FileInfo(file).Length != package.Size) return false;
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), package.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
