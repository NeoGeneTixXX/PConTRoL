using PConTRoL.Core.Parsing;
using PConTRoL.Core.Scanning;

namespace PConTRoL.Core.Statistics;

public sealed record ArchiveImportResult(int Files, int Added, int Known, int Invalid);

/// <summary>
/// Liest einen Archivordner mit bereits bearbeiteten Platten einmalig in die Statistik ein.
/// Durchsucht alle Unterordner nach .lbl-Dateien (die .hop daneben ist optional). Schon erfasste Platten
/// (gleicher .lbl-Inhalt) werden übersprungen, ein erneuter Import ist also unschädlich.
/// </summary>
public static class ArchiveImporter
{
    public static ArchiveImportResult Import(string folder, StatisticsStore store, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancel = default)
    {
        var files = Directory.EnumerateFiles(folder, "*.lbl", SearchOption.AllDirectories).ToList();
        var fresh = new List<BoardRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int known = 0, invalid = 0;

        for (var i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var lbl = files[i];
            try
            {
                var hash = NestFileParser.HashOf(NestFileParser.ReadAllBytesShared(lbl));
                if (!seen.Add(hash) || store.Contains(hash))
                {
                    known++;
                    continue;
                }

                var hop = Path.ChangeExtension(lbl, ".hop");
                var info = NestFileParser.Parse(hop, lbl);
                var key = Path.GetFileNameWithoutExtension(lbl);
                var time = File.GetLastWriteTimeUtc(File.Exists(hop) ? hop : lbl);
                var record = BoardRecord.Create(info.LblHash, time, info.Material, info.DimX, info.DimY, info.UsedArea,
                    FolderScanner.DeriveNestJob(key, info), key, BoardRecord.SourceArchive);
                if (record == null) invalid++;
                else fresh.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                invalid++;
            }
            if (i % 25 == 0 || i == files.Count - 1) progress?.Report((i + 1, files.Count));
        }

        var added = store.AddRange(fresh);
        return new ArchiveImportResult(files.Count, added, known + (fresh.Count - added), invalid);
    }
}
