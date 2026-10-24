namespace PConTRoL.Core.Timing;

/// <summary>
/// Geplanter Ablauf eines Auftrags an der Maschine. Zeiten in UTC, null = nicht schätzbar
/// (keine Messungen, oder ein Auftrag davor ist nicht schätzbar).
/// </summary>
public sealed record ForecastEntry(JobInfo Job, DateTime? StartUtc, DateTime? EndUtc, RunTimeEstimate? Estimate)
{
    /// <summary>Restzeit des laufenden Auftrags (nur beim ersten Eintrag, wenn sein Start bekannt ist).</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Beim laufenden Auftrag: Start gemessen (true) oder unbekannt, weil PConTRoL erst danach gestartet wurde.</summary>
    public bool StartKnown { get; init; } = true;
}

public static class QueueForecast
{
    /// <summary>
    /// Wann welcher Auftrag an der Maschine dran ist: der oberste läuft (seit <paramref name="runningSinceUtc"/>),
    /// jeder weitere beginnt, wenn der vorige fertig ist.
    /// </summary>
    public static IReadOnlyList<ForecastEntry> Build(IReadOnlyList<JobInfo> machine, RunTimeEstimator estimator,
        DateTime? runningSinceUtc, DateTime nowUtc)
    {
        var result = new List<ForecastEntry>(machine.Count);
        DateTime? cursor = null;
        for (var i = 0; i < machine.Count; i++)
        {
            var job = machine[i];
            var estimate = estimator.Estimate(job);
            if (i == 0)
            {
                if (estimate == null)
                {
                    result.Add(new ForecastEntry(job, runningSinceUtc, null, null) { StartKnown = runningSinceUtc != null });
                    continue;
                }
                if (runningSinceUtc is { } since)
                {
                    var remaining = estimate.Duration - (nowUtc - since);
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero; // überfällig: gleich fertig
                    cursor = nowUtc + remaining;
                    result.Add(new ForecastEntry(job, since, cursor, estimate) { Remaining = remaining });
                }
                else
                {
                    // Start unbekannt: vorsichtig mit der vollen Dauer ab jetzt rechnen.
                    cursor = nowUtc + estimate.Duration;
                    result.Add(new ForecastEntry(job, null, cursor, estimate) { StartKnown = false });
                }
                continue;
            }

            var start = cursor;
            var end = start != null && estimate != null ? start + estimate.Duration : null;
            result.Add(new ForecastEntry(job, start, end, estimate));
            cursor = end;
        }
        return result;
    }
}
