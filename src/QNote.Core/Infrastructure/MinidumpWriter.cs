using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QNote.Infrastructure;

/// <summary>
/// In-process <c>MiniDumpWriteDump</c> wrapper. Flag set and retry policy are copied
/// from dotnet-diagnostics' <c>Dumper.Windows.cs</c> ("Heap" dump type — the same set
/// <c>dotnet-dump collect --type heap</c> uses): private read/write pages carry the GC
/// heap, so SOS/dotnet-dump analysis (<c>clrstack</c>, <c>dumpheap</c>) works, unlike a
/// mini/triage dump. Called only from <see cref="CrashHandler"/>; never throws.
/// </summary>
internal static class MinidumpWriter
{
    [Flags]
    private enum MiniDumpType : uint
    {
        WithDataSegs = 0x00001,
        WithHandleData = 0x00004,
        WithUnloadedModules = 0x00020,
        WithPrivateReadWriteMemory = 0x00200,
        WithFullMemoryInfo = 0x00800,
        WithThreadInfo = 0x01000,
        WithTokenInformation = 0x40000,
    }

    /// <summary>The dotnet-dump "Heap" combination.</summary>
    private const MiniDumpType HeapDump =
        MiniDumpType.WithPrivateReadWriteMemory | MiniDumpType.WithDataSegs
        | MiniDumpType.WithHandleData | MiniDumpType.WithUnloadedModules
        | MiniDumpType.WithFullMemoryInfo | MiniDumpType.WithThreadInfo
        | MiniDumpType.WithTokenInformation;

    /// <summary>
    /// ERROR_PARTIAL_COPY — transient (the process keeps mutating while dbghelp reads
    /// it); dotnet-dump retries it. GetLastWin32Error can surface EITHER the raw Win32
    /// code (299) or the wrapped HRESULT (0x8007012B) — dotnet-diagnostics compares
    /// against the HRESULT form; observed on hardware 2026-09-25 as 0x8007012B.
    /// </summary>
    private const int ErrorPartialCopy = 299;

    private const int ErrorPartialCopyHResult = unchecked((int)0x8007012B);

    private const int MaxAttempts = 10;

    /// <summary>One dump at a time — a second concurrent crash must not re-enter dbghelp.</summary>
    private static int _dumpInProgress;

    /// <summary>Write a heap dump of the current process to <paramref name="path"/>. Best-effort.</summary>
    public static bool TryWriteHeapDump(string path) => TryWriteHeapDump(path, out _);

    /// <summary>
    /// <paramref name="lastError"/> carries the last Win32 error on failure so the
    /// crash triage can record WHY the dump is missing (0 = concurrent dump refused).
    /// </summary>
    public static bool TryWriteHeapDump(string path, out int lastError)
    {
        lastError = 0;
        if (Interlocked.Exchange(ref _dumpInProgress, 1) != 0)
            return false;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using var process = Process.GetCurrentProcess();
                    if (MiniDumpWriteDump(
                            process.Handle,
                            (uint)Environment.ProcessId,
                            stream.SafeFileHandle,
                            HeapDump,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            IntPtr.Zero))
                        return true;

                    lastError = Marshal.GetLastWin32Error();
                    if (lastError != ErrorPartialCopy && lastError != ErrorPartialCopyHResult)
                        return false;

                    Thread.Sleep(50); // let the process quiesce before the next attempt
                }
            }

            return false; // partial copy on every attempt
        }
        catch
        {
            // A crash-path dump writer must never throw (disk full, dbghelp missing, ...).
            return false;
        }
        finally
        {
            Volatile.Write(ref _dumpInProgress, 0);
        }
    }

    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        MiniDumpType dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
