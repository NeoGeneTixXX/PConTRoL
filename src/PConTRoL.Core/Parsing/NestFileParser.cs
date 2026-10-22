using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PConTRoL.Core.Parsing;

/// <summary>Gelesene Infos aus .lbl und Kopf der .hop. Fehlt etwas, bleibt das Feld leer.</summary>
public sealed class NestFileInfo
{
    public string? NestFileName { get; set; }
    public string? OriginalHopPath { get; set; }
    public int? BoardId { get; set; }
    public int? BoardCount { get; set; }
    public string? Material { get; set; }
    public double? DimX { get; set; }
    public double? DimY { get; set; }
    public double? DimZ { get; set; }
    public int? PartsOnBoard { get; set; }
    public double? Efficiency { get; set; }
    public int? Priority { get; set; }
    public string? ProductionId { get; set; }
    public List<PartInfo> Parts { get; } = [];
}

/// <summary>
/// Liest .lbl (INI-Format) und den Kopf der .hop (bis "START"). Der NC-Code wird nicht gelesen.
/// Dateien werden immer mit FileShare.ReadWrite|Delete geöffnet, damit die Maschine nie blockiert wird.
/// </summary>
public static class NestFileParser
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Regex VarLine = new(@"^\s*(\w+)\s*:=\s*(.*?)\s*;?\s*$", RegexOptions.Compiled);

    static NestFileParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static NestFileInfo Parse(string hopPath, string? lblPath)
    {
        var info = new NestFileInfo();
        if (lblPath != null && File.Exists(lblPath))
            ApplyLbl(info, ReadAllTextShared(lblPath));
        if (File.Exists(hopPath))
            ApplyHopHeader(info, ReadHopHeader(hopPath));
        return info;
    }

    public static void ApplyLbl(NestFileInfo info, string text)
    {
        var sections = ParseIni(text);

        if (sections.TryGetValue("Global", out var global))
        {
            info.NestFileName = Get(global, "NSTFileName");
            info.OriginalHopPath = Get(global, "HopFilename");
            info.BoardCount = ParseInt(Get(global, "BoardCount"));
            info.BoardId = ParseInt(Get(global, "BoardID"));
        }

        if (sections.TryGetValue("Board", out var board))
        {
            info.Material = NullIfEmpty(Get(board, "Material"));
            info.DimX = ParseDouble(Get(board, "DimX"));
            info.DimY = ParseDouble(Get(board, "DimY"));
            info.DimZ = ParseDouble(Get(board, "DimZ"));
            info.PartsOnBoard = ParseInt(Get(board, "PartsOnBoard"));
            info.Efficiency = ParseDouble(Get(board, "Efficiency"));
        }

        foreach (var (name, values) in sections
                     .Where(s => s.Key.StartsWith("Part_", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(s => ParseInt(s.Key[5..]) ?? int.MaxValue))
        {
            var rd = Get(values, "FreeText4") ?? "";
            if (rd.Length == 0)
                rd = Path.GetFileNameWithoutExtension(Get(values, "SysFilenameShort") ?? "");
            var size = Get(values, "FreeText5") ?? "";
            if (size.Length == 0 && ParseDouble(Get(values, "DimX")) is { } dx && ParseDouble(Get(values, "DimY")) is { } dy)
                size = $"{dx:0.#} x {dy:0.#}";
            info.Parts.Add(new PartInfo(
                rd,
                Get(values, "FreeText3") ?? "",
                Get(values, "FreeText2") ?? "",
                size,
                Get(values, "FreeText7") ?? ""));
        }
    }

    public static void ApplyHopHeader(NestFileInfo info, IEnumerable<string> headerLines)
    {
        foreach (var raw in headerLines)
        {
            var m = VarLine.Match(raw);
            if (!m.Success) continue;
            var value = m.Groups[2].Value.Trim().Trim('\'');
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "dx": info.DimX ??= ParseDouble(value); break;
                case "dy": info.DimY ??= ParseDouble(value); break;
                case "dz": info.DimZ ??= ParseDouble(value); break;
                case "_hhdata_material": info.Material ??= NullIfEmpty(value); break;
                case "_hhdata_prodprio": info.Priority = ParseInt(value); break;
                case "_hhdata_productionid": info.ProductionId = NullIfEmpty(value); break;
            }
        }
    }

    /// <summary>Liest nur die Zeilen bis "START" (max. 300 Zeilen).</summary>
    public static List<string> ReadHopHeader(string hopPath)
    {
        var lines = new List<string>();
        using var stream = OpenShared(hopPath);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252));
        while (lines.Count < 300 && reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("START", StringComparison.OrdinalIgnoreCase)) break;
            lines.Add(line);
        }
        return lines;
    }

    public static string ReadAllTextShared(string path)
    {
        using var stream = OpenShared(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Decode(ms.ToArray());
    }

    /// <summary>UTF-8 (mit/ohne BOM), UTF-16 mit BOM, sonst Windows-1252.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return Utf8Strict.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(bytes); }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out current))
                    result[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (current == null || eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static int? ParseInt(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    internal static double? ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
