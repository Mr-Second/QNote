namespace QNote.Memory;

/// <summary>
/// <see cref="IOneShotTimer"/> over <see cref="System.Threading.Timer"/>.
/// <see cref="Elapsed"/> fires on a thread-pool thread — safe for the working-set
/// trim, which touches no UI state.
/// </summary>
public sealed class ThreadingOneShotTimer : IOneShotTimer
{
    private readonly System.Threading.Timer _timer;

    public ThreadingOneShotTimer() =>
        _timer = new System.Threading.Timer(_ => Elapsed?.Invoke(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <inheritdoc/>
    public event Action? Elapsed;

    /// <inheritdoc/>
    public void Arm(TimeSpan delay) => _timer.Change(delay, Timeout.InfiniteTimeSpan);

    /// <inheritdoc/>
    public void Cancel() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <inheritdoc/>
    public void Dispose() => _timer.Dispose();
}
