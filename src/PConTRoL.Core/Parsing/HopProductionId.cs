using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PConTRoL.Core.Parsing;

/// <summary>
/// Setzt <c>_hhdata_ProductionID</c> im Kopf einer .hop – genau so, wie es früher HOLZ-HER Production Control getan hat.
/// Der JobManager an der Maschine sortiert beim Start ZUERST nach dieser ID, danach nach Datum.
///
/// Geändert wird nur diese eine Zeile im VARS-Block vor "START" (fehlt sie, wird sie direkt vor "START" eingefügt).
/// Alle anderen Bytes, die Kodierung und die Zeilenenden bleiben unverändert. Der NC-Code wird nie angefasst.
/// </summary>
public static class HopProductionId
{
    private static readonly Regex IdLine = new(@"^([ \t]*_hhdata_ProductionID[ \t]*:=[ \t]*)([^;\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex StartLine = new(@"^[ \t]*START[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Liefert den Dateiinhalt mit der neuen ID. Ohne "START"-Zeile wird nichts geändert (Ausnahme).</summary>
    public static byte[] Apply(byte[] original, long productionId)
    {
        var unicode = original.Length >= 2 && original[0] == 0xFF && original[1] == 0xFE;
        // Latin1 bildet jedes Byte 1:1 auf ein Zeichen ab – so bleibt bei ANSI-Dateien jedes andere Byte exakt erhalten.
        var encoding = unicode ? Encoding.Unicode : Encoding.Latin1;
        var offset = unicode ? 2 : 0;
        var text = encoding.GetString(original, offset, original.Length - offset);

        var start = StartLine.Match(text);
        if (!start.Success)
            throw new FormatException("In der .hop wurde keine START-Zeile gefunden.");

        var id = productionId.ToString(CultureInfo.InvariantCulture);
        var header = text[..start.Index];
        var existing = IdLine.Match(header);
        string result;
        if (existing.Success)
        {
            var valueStart = existing.Groups[2].Index;
            result = text[..valueStart] + id + text[(valueStart + existing.Groups[2].Length)..];
        }
        else
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            result = text[..start.Index] + "_hhdata_ProductionID := " + id + ";" + newline + text[start.Index..];
        }

        var bytes = encoding.GetBytes(result);
        return unicode ? [0xFF, 0xFE, .. bytes] : bytes;
    }

    /// <summary>Production ID aus dem Text (null = keine oder unlesbar).</summary>
    public static long? Parse(string? value) =>
        long.TryParse(value?.Trim().Trim('\''), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
