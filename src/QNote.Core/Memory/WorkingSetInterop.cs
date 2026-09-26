using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QNote.Memory;

/// <summary>
/// Production <see cref="IWorkingSetInterop"/>: <c>GC.Collect(Forced, blocking,
/// compacting)</c> for the immediate trim and psapi!EmptyWorkingSet for the deep
/// trim. Classic <c>[DllImport]</c> per project convention (LibraryImport's source
/// generator would require AllowUnsafeBlocks). <see cref="EmptyWorkingSet"/> throws
/// a <see cref="Win32Exception"/> on failure so the controller can log the reason —
/// callers must treat every call here as best-effort.
/// </summary>
public sealed class WorkingSetInterop : IWorkingSetInterop
{
    /// <inheritdoc/>
    public void CompactingCollect() =>
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    /// <inheritdoc/>
    public void EmptyWorkingSet()
    {
        if (!EmptyWorkingSetNative(GetCurrentProcess()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSetNative(nint hProcess);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
