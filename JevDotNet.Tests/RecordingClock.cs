/// <summary>
/// A clock for retry tests: it never moves, records every delay asked of it, and finishes each delay at once
/// unless <see cref="HoldDelays"/> is set, in which case delays only end by cancellation.
/// </summary>
internal sealed class RecordingClock : TimeProvider
{
    private readonly TaskCompletionSource _delayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DateTimeOffset Now { get; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public bool HoldDelays { get; init; }

    /// <summary>Completes when the first delay has started.</summary>
    public Task DelayStarted => _delayStarted.Task;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (Delays) Delays.Add(dueTime);
        _delayStarted.TrySetResult();
        // Fired from the pool so the caller finishes setting up its timer before the delay completes.
        if (!HoldDelays) ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
