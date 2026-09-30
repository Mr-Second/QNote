using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WinUIRichEditor;

/// <summary>Details of one swallowed fault. See <see cref="RichEditorDiagnostics"/>.</summary>
public sealed class RichEditorFaultEventArgs : EventArgs
{
    internal RichEditorFaultEventArgs(Exception exception, string member, string file, int line)
    {
        Exception = exception;
        Member = member;
        File = file;
        Line = line;
    }

    /// <summary>The exception that was swallowed.</summary>
    public Exception Exception { get; }

    /// <summary>Name of the method the fault occurred in.</summary>
    public string Member { get; }

    /// <summary>Source file name (no directory).</summary>
    public string File { get; }

    /// <summary>Line number of the reporting <c>catch</c>.</summary>
    public int Line { get; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{File}:{Line} {Member} — {Exception.GetType().Name}: {Exception.Message}";
}

/// <summary>Opt-in visibility into faults this library handles internally and does not surface.
/// <para>The control swallows exceptions in roughly fifty places, deliberately: a font that fails to
/// decode, a clipboard flavor the source app lied about, a text layout that rejects a metrics query
/// mid-resize. Each has a defined fallback, and none of them should interrupt the user. The problem
/// was that a HOST had no way to see any of it — a paste that quietly did nothing looked identical to
/// a paste of nothing. Subscribing to <see cref="Fault"/> makes those visible without changing what
/// the control does about them.</para>
/// <para>Each distinct fault — one <c>catch</c> site, one exception type — is reported ONCE. Several
/// of these sites sit in the render and caret-metrics paths, where a persistent fault would otherwise
/// fire many times a second and bury everything else. Call <see cref="Reset"/> to re-arm.</para>
/// <para>This is a diagnostic channel, not an error-handling one: the fallback has already happened by
/// the time the event fires, and nothing a handler does changes it. A handler that throws is ignored.
/// Handlers may be invoked on a background thread (image decoding and document parsing run off the UI
/// thread), so marshal before touching UI.</para>
/// <para>The event is static and holds its subscribers for the life of the process — unsubscribe when
/// the host shuts down if it would otherwise keep an object graph alive.</para></summary>
/// <example>
/// <code>
/// RichEditorDiagnostics.Fault += (_, e) => Debug.WriteLine(e);
/// </code>
/// </example>
public static class RichEditorDiagnostics
{
    private static readonly HashSet<(string File, int Line, Type Type)> Seen = new();
    private static readonly object Gate = new();

    /// <summary>Raised the first time each distinct internal fault occurs. See
    /// <see cref="RichEditorDiagnostics"/> for threading and lifetime.</summary>
    public static event EventHandler<RichEditorFaultEventArgs>? Fault;

    /// <summary>Forgets which faults have already been reported, so they raise
    /// <see cref="Fault"/> again. Use it to scope reporting to one operation.</summary>
    public static void Reset()
    {
        lock (Gate) Seen.Clear();
    }

    // Call sites pass nothing but the exception: the location comes from the compiler as constants, so
    // an unsubscribed host pays one static read and a null check — cheap enough for the per-frame
    // render path, which is the only reason wiring EVERY swallow site uniformly is affordable.
    internal static void Report(
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var handler = Fault; // snapshot: unsubscribe may race with the raise below
        if (handler == null) return;

        string name = FileName(file);
        lock (Gate)
        {
            if (!Seen.Add((name, line, ex.GetType()))) return;
        }

        // A diagnostic channel must not manufacture the very failure it reports: the fallback for this
        // fault has already run, and letting a handler's exception escape would undo it.
        try { handler(null, new RichEditorFaultEventArgs(ex, member, name, line)); }
        catch { }
    }

    private static string FileName(string path)
    {
        int i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i < 0 ? path : path[(i + 1)..];
    }
}
