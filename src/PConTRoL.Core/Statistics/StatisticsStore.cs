using PConTRoL.Core.Storage;

namespace PConTRoL.Core.Statistics;

/// <summary>
/// Dauerhafte Statistik im gemeinsamen Ordner (_PConTRoL\statistik.jsonl), eine JSON-Zeile pro Platte.
/// Nur anhängen, nie umschreiben (siehe <see cref="JsonLinesStore{T}"/>).
/// </summary>
public sealed class StatisticsStore(string path) : JsonLinesStore<BoardRecord>(path, r => r.Id);
