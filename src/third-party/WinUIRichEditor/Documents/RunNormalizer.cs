using System.Collections.Concurrent;

namespace WinUIRichEditor.Documents;

/// <summary>Load-time model compaction. Parsers/loaders often emit a separate <see cref="Run"/> per source
/// span — e.g. Word/Google-Docs HTML wraps each word in its own &lt;span&gt; — which inflates object count
/// and string allocations. This merges adjacent runs with identical formatting (the same operation the
/// edit paths already do via <see cref="TextRange.CoalesceRuns"/>) and interns shared font-family strings
/// so thousands of runs reference one instance instead of each holding a copy. Content is unchanged
/// (merged runs are byte-for-byte the same text + formatting), so .flow round-trip stays compatible.</summary>
internal static class RunNormalizer
{
    // Font names are few (dozens) and process-stable, so this pool stays tiny. Concurrent because async
    // loaders may parse off the UI thread.
    private static readonly ConcurrentDictionary<string, string> _fontPool = new();

    /// <summary>Returns a shared instance of <paramref name="family"/> so duplicate font names across runs
    /// don't each allocate their own string.</summary>
    public static string Intern(string family) => _fontPool.GetOrAdd(family, family);

    /// <summary>Coalesces adjacent equal-format runs and interns font families across the whole document,
    /// recursing into the cells of block tables AND of inline tables. Returns the same instance for
    /// call-site convenience. Idempotent and safe on any freshly loaded document.</summary>
    public static FlowDocument Compact(FlowDocument doc)
    {
        foreach (var b in doc.Blocks) CompactBlock(b);
        return doc;
    }

    private static void CompactBlock(Block b)
    {
        switch (b)
        {
            case Paragraph p:
                foreach (var inl in p.Inlines)
                {
                    if (inl is Run r && r.FontFamily is { } f) r.FontFamily = Intern(f);
                    // An INLINE table's cells hold real loaded content but hang off the paragraph's
                    // inlines rather than the block list, so this walk used to skip them entirely — a
                    // document whose text lives in inline tables got no compaction at all.
                    else if (inl is InlineTable it)
                        foreach (var row in it.Table.Cells)
                            foreach (var cell in row)
                                foreach (var cb in cell.Blocks)
                                    CompactBlock(cb);
                }
                TextRange.CoalesceRuns(p);
                break;
            case TableBlock tb:
                foreach (var row in tb.Cells)
                    foreach (var cell in row)
                        foreach (var cb in cell.Blocks)
                            CompactBlock(cb);
                break;
        }
    }
}
