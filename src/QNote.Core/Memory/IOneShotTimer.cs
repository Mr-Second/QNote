namespace QNote.Memory;

/// <summary>
/// One-shot delay abstraction (production: <see cref="ThreadingOneShotTimer"/>).
/// <see cref="Arm"/> restarts the delay when already armed; <see cref="Elapsed"/>
/// fires at most once per arm, on a thread-pool thread.
/// </summary>
public interface IOneShotTimer : IDisposable
{
    /// <summary>Raised once when an armed delay elapses without a <see cref="Cancel"/>.</summary>
    event Action? Elapsed;

    /// <summary>(Re)start the one-shot delay.</summary>
    void Arm(TimeSpan delay);

    /// <summary>Cancel a pending fire; harmless when nothing is armed.</summary>
    void Cancel();
}
