using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// In-memory <see cref="IToastScheduler"/> for headless coordinator/service
/// tests: models the OS schedule as a uuid→reminder map with the CONTRACTED
/// replace semantics (Schedule replaces by Tag), records every operation for
/// call-order assertions, and can be switched into a throwing mode to verify
/// the best-effort seams (data mutations must survive scheduler failures).
/// </summary>
internal sealed class FakeToastScheduler : IToastScheduler
{
    private readonly Dictionary<string, TodoReminder> _scheduled = new(StringComparer.Ordinal);

    /// <summary>Operation log (name + key args) for call-order assertions.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The uuid→reminder map standing in for the OS schedule.</summary>
    public IReadOnlyDictionary<string, TodoReminder> Scheduled => _scheduled;

    /// <summary>Every ShowNow payload, in order (catch-up assertions).</summary>
    public List<TodoReminder> ShownNow { get; } = [];

    /// <summary>When set, every member throws — best-effort seam testing.</summary>
    public bool ThrowOnAll { get; set; }

    public Task ScheduleAsync(TodoReminder reminder, CancellationToken ct = default)
    {
        Calls.Add($"Schedule:{reminder.TaskUuid}");
        if (ThrowOnAll)
            throw new InvalidOperationException("scheduler down (test)");
        _scheduled[reminder.TaskUuid] = reminder; // replace by Tag — the impl contract
        return Task.CompletedTask;
    }

    public Task CancelAsync(string taskUuid, CancellationToken ct = default)
    {
        Calls.Add($"Cancel:{taskUuid}");
        if (ThrowOnAll)
            throw new InvalidOperationException("scheduler down (test)");
        _scheduled.Remove(taskUuid);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetScheduledTaskUuidsAsync(CancellationToken ct = default)
    {
        Calls.Add("Enumerate");
        if (ThrowOnAll)
            throw new InvalidOperationException("scheduler down (test)");
        IReadOnlyList<string> uuids = _scheduled.Keys.ToList();
        return Task.FromResult(uuids);
    }

    public Task ShowNowAsync(TodoReminder reminder, CancellationToken ct = default)
    {
        Calls.Add($"ShowNow:{reminder.TaskUuid}");
        if (ThrowOnAll)
            throw new InvalidOperationException("scheduler down (test)");
        ShownNow.Add(reminder);
        return Task.CompletedTask;
    }
}
