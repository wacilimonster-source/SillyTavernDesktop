using Xunit;
using SillyTavernTray;

namespace TrayUnitTests;

public class WatchdogEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongRunning = Now - TimeSpan.FromMinutes(10);

    [Fact]
    public void NotRunning_WhenServiceIsNotRunning()
    {
        Assert.Equal(WatchdogObservation.NotRunning,
            WatchdogEvaluator.Evaluate(Now, ServerState.Stopped, LongRunning, Now));
        Assert.Equal(WatchdogObservation.NotRunning,
            WatchdogEvaluator.Evaluate(Now, ServerState.Starting, null, Now));
    }

    [Fact]
    public void Grace_WithinGracePeriod()
    {
        var justStarted = Now - TimeSpan.FromSeconds(10);
        Assert.Equal(WatchdogObservation.Grace, WatchdogEvaluator.Evaluate(Now, ServerState.Running, justStarted, null));
        Assert.Equal(WatchdogObservation.Grace, WatchdogEvaluator.Evaluate(Now, ServerState.Running, justStarted, Now - TimeSpan.FromMinutes(99)));
    }

    [Fact]
    public void Grace_WhenRunningSinceUnknown()
    {
        Assert.Equal(WatchdogObservation.Grace, WatchdogEvaluator.Evaluate(Now, ServerState.Running, null, Now));
    }

    [Fact]
    public void Missing_WhenHeartbeatNeverWritten()
    {
        Assert.Equal(WatchdogObservation.Missing, WatchdogEvaluator.Evaluate(Now, ServerState.Running, LongRunning, null));
    }

    [Fact]
    public void Healthy_WhenHeartbeatIsFresh()
    {
        var fresh = Now - TimeSpan.FromSeconds(10); // < StaleAfter (60 s)
        Assert.Equal(WatchdogObservation.Healthy, WatchdogEvaluator.Evaluate(Now, ServerState.Running, LongRunning, fresh));
    }

    [Fact]
    public void Stale_WhenHeartbeatIsOlderThanTwoIntervals()
    {
        var stale = Now - TimeSpan.FromSeconds(90);
        Assert.Equal(WatchdogObservation.Stale, WatchdogEvaluator.Evaluate(Now, ServerState.Running, LongRunning, stale));
    }

    [Fact]
    public void FutureHeartbeatTimestamp_IsTreatedAsHealthy()
    {
        // Clock skew: a last-write in the future must not be judged stale (negative age clamps to zero).
        var future = Now + TimeSpan.FromMinutes(5);
        Assert.Equal(WatchdogObservation.Healthy, WatchdogEvaluator.Evaluate(Now, ServerState.Running, LongRunning, future));
    }
}

public class RestartLimiterTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static RestartLimiter NewLimiter(int max = 3, int windowMinutes = 10) => new()
    {
        MaxAttempts = max,
        Window = TimeSpan.FromMinutes(windowMinutes)
    };

    [Fact]
    public void AllowsUpToMaxAttempts_ThenBlocks()
    {
        var limiter = NewLimiter();
        Assert.True(limiter.TryAcquire(Now));
        Assert.True(limiter.TryAcquire(Now.AddSeconds(1)));
        Assert.True(limiter.TryAcquire(Now.AddSeconds(2)));
        Assert.False(limiter.TryAcquire(Now.AddSeconds(3)));
        Assert.Equal(3, limiter.Count(Now.AddSeconds(3)));
    }

    [Fact]
    public void AttemptsExpireAfterWindow()
    {
        var limiter = NewLimiter();
        Assert.True(limiter.TryAcquire(Now));
        Assert.True(limiter.TryAcquire(Now));
        Assert.True(limiter.TryAcquire(Now));
        Assert.False(limiter.TryAcquire(Now));
        // Window is 10 minutes; everything acquired at 'Now' has expired by Now+11min.
        Assert.True(limiter.TryAcquire(Now.AddMinutes(11)));
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var limiter = NewLimiter();
        limiter.TryAcquire(Now);
        limiter.TryAcquire(Now);
        limiter.TryAcquire(Now);
        limiter.Reset();
        Assert.Equal(0, limiter.Count(Now));
        Assert.True(limiter.TryAcquire(Now));
    }

    [Fact]
    public void PartialExpiry_FreesOnlyExpiredSlots()
    {
        var limiter = NewLimiter();
        Assert.True(limiter.TryAcquire(Now));                 // oldest — expires at +10 min
        Assert.True(limiter.TryAcquire(Now.AddMinutes(5)));
        Assert.True(limiter.TryAcquire(Now.AddMinutes(6)));
        // At +12 min the first attempt has expired (12 > 10) but the other two remain:
        // exactly one slot is free, so the next restart is allowed.
        Assert.True(limiter.TryAcquire(Now.AddMinutes(12)));
        // Now the limiter is full again (+5, +6, +12 are all within the window at +13).
        Assert.False(limiter.TryAcquire(Now.AddMinutes(13)));
        // After +16 min both early attempts have expired and a slot frees up again.
        Assert.True(limiter.TryAcquire(Now.AddMinutes(16)));
    }
}
