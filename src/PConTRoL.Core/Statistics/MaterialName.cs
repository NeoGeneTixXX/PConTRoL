using System.Text.RegularExpressions;

namespace PConTRoL.Core.Statistics;

/// <summary>
/// Materialnamen vereinheitlichen, damit „Dekor 12mm“, „ dekor  12MM “ usw. nicht als verschiedene Materialien zählen.
/// Gespeichert wird <see cref="Normalize"/>, gruppiert nach <see cref="Key"/>.
/// </summary>
public static class MaterialName
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Außen getrimmt, mehrfache Leerzeichen zu einem zusammengefasst. Schreibweise bleibt.</summary>
    public static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "" : Spaces.Replace(name.Trim(), " ");

    /// <summary>Vergleichsschlüssel: normalisiert und ohne Unterschied zwischen Groß- und Kleinschreibung.</summary>
    public static string Key(string? name) => Normalize(name).ToUpperInvariant();
}
