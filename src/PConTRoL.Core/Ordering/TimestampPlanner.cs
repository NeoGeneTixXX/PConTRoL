namespace PConTRoL.Core.Ordering;

public sealed record TimestampChange(string Key, DateTime OldUtc, DateTime NewUtc);

/// <summary>
/// Setzt eine gewünschte Abarbeitungsreihenfolge in Änderungsdaten um.
///
/// Regeln:
/// - Der laufende Auftrag (fixedFirst) behält sein Datum.
/// - Der längste Anfang der Liste, der schon richtig sortiert ist, bleibt unverändert
///   (so wenige Dateien wie möglich anfassen).
/// - Der Rest bekommt Zeitstempel im Abstand von 2 s (mindestens 1 s), möglichst nicht in der Zukunft,
///   damit später von außen hinzugefügte Dateien (Datum = jetzt) weiter hinten landen.
/// </summary>
public static class TimestampPlanner
{
    public static readonly TimeSpan PreferredStep = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinimumStep = TimeSpan.FromSeconds(1);

    /// <param name="processingOrder">Aufträge in der Reihenfolge, in der die Maschine sie abarbeiten soll.</param>
    /// <param name="fixedFirst">true: Element 0 läuft gerade, sein Datum darf nicht geändert werden.</param>
    public static IReadOnlyList<TimestampChange> Plan(
        IReadOnlyList<(string Key, DateTime CurrentUtc)> processingOrder,
        bool fixedFirst,
        DateTime nowUtc,
        MachineSortOrder sortOrder)
    {
        var n = processingOrder.Count;
        if (n == 0) return [];

        var current = processingOrder.Select(p => p.CurrentUtc).ToArray();
        DateTime[] planned;

        if (sortOrder == MachineSortOrder.OldestFirst)
        {
            planned = PlanAscending(current, nowUtc);
        }
        else if (!fixedFirst)
        {
            // Neueste zuerst: rückwärts betrachtet muss die Zeit aufsteigen.
            var reversed = current.Reverse().ToArray();
            planned = PlanAscending(reversed, nowUtc).Reverse().ToArray();
        }
        else
        {
            planned = PlanDescendingWithFixedTop(current);
        }

        var changes = new List<TimestampChange>();
        for (var i = 0; i < n; i++)
        {
            if (i == 0 && fixedFirst && planned[0] != current[0])
                throw new InvalidOperationException("Interner Fehler: Datum des laufenden Auftrags würde geändert.");
            if (planned[i] != current[i])
                changes.Add(new TimestampChange(processingOrder[i].Key, current[i], planned[i]));
        }
        return changes;
    }

    /// <summary>Ergebnis ist streng aufsteigend. Index 0 bleibt immer erhalten.</summary>
    private static DateTime[] PlanAscending(DateTime[] current, DateTime nowUtc)
    {
        var n = current.Length;
        var planned = (DateTime[])current.Clone();

        var keep = 1;
        while (keep < n && current[keep] > current[keep - 1]) keep++;
        var remaining = n - keep;
        if (remaining == 0) return planned;

        var start = current[keep - 1];
        var gap = nowUtc - start;
        var step = remaining > 0 ? TimeSpan.FromTicks(gap.Ticks / remaining) : PreferredStep;
        if (step > PreferredStep) step = PreferredStep;
        if (step < MinimumStep) step = MinimumStep;

        // Möglichst nah an "jetzt" enden, aber nie vor dem letzten behaltenen Datum.
        var first = nowUtc - TimeSpan.FromTicks(step.Ticks * (remaining - 1));
        if (first <= start) first = start + step;

        for (var i = 0; i < remaining; i++)
            planned[keep + i] = TruncateToSecond(first + TimeSpan.FromTicks(step.Ticks * i), planned[keep + i - 1]);
        return planned;
    }

    /// <summary>Neueste zuerst mit festem obersten Auftrag: alle anderen müssen älter sein, absteigend.</summary>
    private static DateTime[] PlanDescendingWithFixedTop(DateTime[] current)
    {
        var n = current.Length;
        var planned = (DateTime[])current.Clone();
        var keep = 1;
        while (keep < n && current[keep] < current[keep - 1]) keep++;
        for (var i = keep; i < n; i++)
            planned[i] = planned[i - 1] - PreferredStep;
        return planned;
    }

    /// <summary>Auf ganze Sekunden abrunden, aber immer mindestens 1 s nach dem Vorgänger bleiben.</summary>
    private static DateTime TruncateToSecond(DateTime value, DateTime previous)
    {
        var truncated = new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        var minimum = previous + MinimumStep;
        return truncated < minimum ? minimum : truncated;
    }
}
