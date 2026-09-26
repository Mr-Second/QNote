namespace QNote.Memory;

/// <summary>
/// Process-memory primitives used by <see cref="WorkingSetTrimController"/>.
/// Abstracted so the controller's timing/state logic is headless-testable; the
/// production implementation is <see cref="WorkingSetInterop"/>.
/// </summary>
public interface IWorkingSetInterop
{
    /// <summary>Immediate compacting full GC (LOH compaction included).</summary>
    void CompactingCollect();

    /// <summary>Deep trim: return the process working set to the OS (psapi EmptyWorkingSet).</summary>
    void EmptyWorkingSet();
}
