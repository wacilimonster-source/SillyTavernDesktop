namespace SillyTavernTray;

internal enum WatchdogObservation { NotRunning, Grace, Healthy, Stale, Missing }

internal static class WatchdogEvaluator
{
    /// <summary>Matches the interval passed to SillyTavern via --heartbeatInterval.</summary>
    public const int HeartbeatIntervalSeconds = 30;

    /// <summary>Two missed heartbeats are treated as unresponsive, like the official healthcheck.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(HeartbeatIntervalSeconds * 2);

    /// <summary>Allow startup to finish and write the first heartbeat before judging.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(HeartbeatIntervalSeconds * 2 + 30);

    public static WatchdogObservation Evaluate(DateTime nowUtc, ServerState state, DateTime? runningSinceUtc, DateTime? heartbeatLastWriteUtc)
    {
        if (state != ServerState.Running) return WatchdogObservation.NotRunning;
        if (runningSinceUtc is null) return WatchdogObservation.Grace;
        if (nowUtc - runningSinceUtc.Value < GracePeriod) return WatchdogObservation.Grace;
        if (heartbeatLastWriteUtc is null) return WatchdogObservation.Missing;
        var age = nowUtc - heartbeatLastWriteUtc.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age <= StaleAfter ? WatchdogObservation.Healthy : WatchdogObservation.Stale;
    }
}

internal sealed class RestartLimiter
{
    private readonly List<DateTime> attempts = new();

    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);

    public bool TryAcquire(DateTime nowUtc)
    {
        Prune(nowUtc);
        if (attempts.Count >= MaxAttempts) return false;
        attempts.Add(nowUtc);
        return true;
    }

    public int Count(DateTime nowUtc) { Prune(nowUtc); return attempts.Count; }
    public void Reset() => attempts.Clear();

    private void Prune(DateTime nowUtc) => attempts.RemoveAll(t => nowUtc - t > Window);
}
