using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PConTRoL.Core.Database;
using PConTRoL.Core.Updates;

const string repo = "https://github.com/NeoGeneTixXX/PConTRoL";

var connectionText = Environment.GetEnvironmentVariable("PCONTROL_DB");
if (string.IsNullOrWhiteSpace(connectionText)) connectionText = FromUserSettings();
if (DatabaseConnection.TryBuild(connectionText, out var cs) is { } error)
{
    Console.Error.WriteLine("Keine Datenbank: " + error);
    Console.Error.WriteLine("PCONTROL_DB setzen oder in PConTRoL unter Einstellungen → Datenbank eintragen.");
    return 2;
}

var tag = args.Length > 0 ? "v" + args[0].TrimStart('v') : null;
var feedUrl = tag == null ? $"{repo}/releases/latest/download/{UpdateFeed.FeedFileName}" : $"{repo}/releases/download/{tag}/{UpdateFeed.FeedFileName}";

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
var package = UpdateFeed.LatestFull(await http.GetStringAsync(feedUrl));
if (package == null)
{
    Console.Error.WriteLine("In releases.win.json steht kein Komplettpaket.");
    return 1;
}
var version = package.Version.ToString(3);
Console.WriteLine($"Release {version}: {package.FileName} ({package.Size / 1024.0 / 1024.0:0.0} MB)");

var folder = Path.Combine(Path.GetTempPath(), "PConTRoL-DbUpload");
Directory.CreateDirectory(folder);
var local = Path.Combine(folder, package.FileName);
if (!UpdateFeed.Matches(local, package))
{
    Console.WriteLine("Lade von GitHub …");
    await using (var source = await http.GetStreamAsync($"{repo}/releases/download/v{version}/{package.FileName}"))
    await using (var file = File.Create(local))
        await source.CopyToAsync(file);
    if (!UpdateFeed.Matches(local, package))
    {
        Console.Error.WriteLine("Prüfsumme stimmt nicht – abgebrochen.");
        return 1;
    }
}
var feed = Path.Combine(folder, UpdateFeed.FeedFileName);
File.WriteAllText(feed, package.FeedJson);

var db = new SharedDatabase(cs);
var newest = (await db.GetUpdateFilesAsync())
    .Where(f => f.File.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase))
    .Select(f => Version.TryParse(f.Version, out var v) ? v : null).Max();
if (newest != null && newest > package.Version)
{
    Console.WriteLine($"In der Datenbank liegt schon die neuere Version {newest} – nichts zu tun.");
    return 0;
}

Console.WriteLine("Lade in die Datenbank hoch …");
if (!await db.PublishUpdateAsync(version, [local, feed]))
{
    Console.Error.WriteLine("Ein anderer Rechner lädt gerade hoch. Bitte gleich noch einmal versuchen.");
    return 3;
}
foreach (var f in await db.GetUpdateFilesAsync()) Console.WriteLine($"  {f.File}  {f.Version}  {f.Size:N0} Bytes  {f.Sha256[..12]}…");
Console.WriteLine($"Version {version} liegt in der Datenbank. Alle Rechner finden sie bei der nächsten stündlichen Prüfung.");
return 0;

// Die Verbindung, die PConTRoL für diesen Windows-Benutzer gespeichert hat (DPAPI, siehe UserSettings.DatabaseProtected).
static string FromUserSettings()
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PConTRoL", "settings.json");
    try
    {
        var stored = (string?)JsonNode.Parse(File.ReadAllText(path))?["DatabaseProtected"];
        if (string.IsNullOrWhiteSpace(stored)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
    }
    catch (Exception ex) when (ex is IOException or FormatException or CryptographicException or System.Text.Json.JsonException)
    {
        return "";
    }
}
