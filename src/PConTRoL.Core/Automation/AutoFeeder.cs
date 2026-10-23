namespace PConTRoL.Core.Automation;

public enum AutoFeedState
{
    /// <summary>Automatik ist aus.</summary>
    Off,

    /// <summary>An der Maschine liegt noch etwas.</summary>
    MachineBusy,

    /// <summary>Maschine leer, aber auch der Vorrat ist leer.</summary>
    NoStock,

    /// <summary>Maschine leer, Wartezeit läuft (siehe Remaining).</summary>
    Waiting,

    /// <summary>Maschine lange genug leer: jetzt den nächsten Eintrag schicken.</summary>
    Due,
}

public readonly record struct AutoFeedStatus(AutoFeedState State, TimeSpan Remaining);

/// <summary>
/// Automatik-Modus: Ist an der Maschine <see cref="Delay"/> lang kein Auftrag mehr, wird der nächste Eintrag
/// aus dem Vorrat (Einzelauftrag oder ganze Gruppe) geschickt. Diese Klasse misst nur die Zeit; das Schicken
/// erledigt <see cref="QueueController.FeedNext"/>.
/// </summary>
public sealed class AutoFeeder
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);

    private DateTime? _emptySinceUtc;

    public AutoFeedStatus Observe(bool enabled, int machineCount, int stockCount, DateTime nowUtc)
    {
        if (!enabled || machineCount > 0)
        {
            _emptySinceUtc = null;
            return new(enabled ? AutoFeedState.MachineBusy : AutoFeedState.Off, TimeSpan.Zero);
        }

        _emptySinceUtc ??= nowUtc;
        if (stockCount == 0) return new(AutoFeedState.NoStock, TimeSpan.Zero);

        var remaining = Delay - (nowUtc - _emptySinceUtc.Value);
        return remaining > TimeSpan.Zero ? new(AutoFeedState.Waiting, remaining) : new(AutoFeedState.Due, TimeSpan.Zero);
    }

    /// <summary>Nach einem Versuch (egal ob erfolgreich) wieder von vorn zählen.</summary>
    public void Restart() => _emptySinceUtc = null;
}
