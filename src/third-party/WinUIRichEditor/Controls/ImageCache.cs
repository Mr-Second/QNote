using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>Decodes image bytes to device-bound <see cref="CanvasBitmap"/>s off the render path.
/// A Win2D bitmap is device-bound, unlike Avalonia's device-free <c>Bitmap</c>,
/// so the model keeps the encoded <c>RawBytes</c> and this cache holds the decoded GPU bitmap.
/// <see cref="Get"/> returns null until the decode finishes, then fires <see cref="OnReady"/> so the
/// control can repaint. Keyed by the bytes' CONTENT hash: pasting the same picture twice (distinct
/// arrays, equal content) shares one decode and one GPU bitmap — array identity only deduplicated
/// <c>Clone()</c>-shared arrays (undo snapshots). The hash is computed once per array and memoized by
/// array identity (<see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>),
/// so the per-draw <see cref="Get"/>
/// never rehashes. It never mutates the model's <c>RawBytes</c> (the <c>Image</c> setter would
/// discard them).
/// <para>Pictures are decoded to the pixel size they are DRAWN at (<see cref="ImageDecoder"/>), with headroom,
/// and re-decoded larger when a draw asks for more (zoom in, a resize handle) — the old bitmap keeps drawing
/// until the sharper one lands. Printing decodes synchronously at print resolution into bitmaps that live only
/// for the print (<see cref="GetForPrint"/> / <see cref="EndPrint"/>).</para></summary>
internal sealed class ImageCache
{
    private readonly Dictionary<object, CanvasBitmap?> _decoded = new(); // value null = decode failed
    // Keys whose bitmap is the source's full size: no request can get more detail, so none re-decodes.
    private readonly HashSet<object> _atSourceSize = new();
    private readonly HashSet<object> _inflight = new();
    // Array identity -> content-hash key, computed once per array (weak: dropped with the array).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> _hashOf = new();

    /// <summary>Raised on the UI thread when a decode completes (the control re-invalidates).</summary>
    public event Action? OnReady;

    // Decodes aim this much above the requested size, so a slow zoom doesn't re-decode on every notch.
    internal const double Headroom = 1.25;

    // The cache key for an image: content hash of its bytes (memoized per array), or the element
    // itself when there are no bytes. Callers passing byte[] keys (Invalidate/Prune) map through this
    // too, so all key spaces agree.
    private object KeyFor(byte[]? rawBytes, object element)
    {
        if (rawBytes == null) return element;
        if (!_hashOf.TryGetValue(rawBytes, out var h))
        {
            h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBytes));
            _hashOf.Add(rawBytes, h);
        }
        return h;
    }

    /// <summary>Returns the decoded bitmap for an image element, or null while decoding/failed.
    /// <paramref name="already"/> is an in-model bitmap (the rare "set a bitmap directly" path).
    /// <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> is the device-pixel size it is about to
    /// be drawn at: a cached bitmap smaller than that is still returned, and a sharper one is decoded behind it.</summary>
    public CanvasBitmap? Get(ICanvasResourceCreator device, object key, byte[]? rawBytes, CanvasBitmap? already,
        int pixelWidth, int pixelHeight, Rect where = default)
    {
        if (already != null) return already;
        object k = KeyFor(rawBytes, key);
        if (rawBytes != null) _seen[k] = (where, ++_stamp);
        if (_decoded.TryGetValue(k, out var bmp))
        {
            if (bmp != null && rawBytes != null && NeedsMore(k, bmp, pixelWidth, pixelHeight) && _inflight.Add(k))
                _ = DecodeAsync(device, k, rawBytes, pixelWidth, pixelHeight);
            return bmp;
        }
        if (rawBytes != null && _inflight.Add(k))
            _ = DecodeAsync(device, k, rawBytes, pixelWidth, pixelHeight);
        return null;
    }

    // +1 in Smaller: rounding between the request and the aspect-fitted size.
    private bool NeedsMore(object key, CanvasBitmap bmp, int w, int h)
        => !_atSourceSize.Contains(key) && Smaller(bmp, w, h);

    private static (int w, int h) Target(int w, int h)
        => ((int)Math.Ceiling(Math.Max(1, w) * Headroom), (int)Math.Ceiling(Math.Max(1, h) * Headroom));

    private async Task DecodeAsync(ICanvasResourceCreator device, object key, byte[] rawBytes, int w, int h)
    {
        var (tw, th) = Target(w, h);
        (byte[] Pixels, int Width, int Height, int NaturalWidth, int NaturalHeight)? decoded = null;
        try { decoded = await Task.Run(() => ImageDecoder.Decode(rawBytes, tw, th)); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        // If the key was invalidated/cleared/pruned while decoding, don't resurrect a stale entry.
        if (!_inflight.Remove(key)) return;
        CanvasBitmap? result = null;
        try { if (decoded is { } d) result = CreateBitmap(device, d.Pixels, d.Width, d.Height); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }

        if (_decoded.TryGetValue(key, out var old) && old != null)
        {
            if (result == null) return; // a failed UPGRADE keeps the picture it has
            old.Dispose();
        }
        Unretire(key); // its recorded size is stale; the next Prune measures it again
        _decoded[key] = result; // null = undecodable: cached so it isn't retried every frame
        if (decoded is { } full && full.Width == full.NaturalWidth && full.Height == full.NaturalHeight) _atSourceSize.Add(key);
        else _atSourceSize.Remove(key);
        OnReady?.Invoke();
    }

    private static CanvasBitmap CreateBitmap(ICanvasResourceCreator device, byte[] pixels, int w, int h)
        => CanvasBitmap.CreateFromBytes(device, pixels, w, h,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);

    // ---- printing ----------------------------------------------------------------------------------------

    // Bitmaps decoded for the print in progress. Separate from _decoded so a print at 300 DPI doesn't leave
    // screen memory holding print-sized pictures; EndPrint disposes them.
    // full = the source's own size, so a bigger request on a later page can't get more from a re-decode.
    private readonly Dictionary<object, (CanvasBitmap? bmp, bool full)> _print = new();

    /// <summary>The bitmap to print an image element with, decoded NOW if the screen cache has nothing sharp
    /// enough — printing draws synchronously, and an async decode would print the loading placeholder.</summary>
    public CanvasBitmap? GetForPrint(object key, byte[]? rawBytes, CanvasBitmap? already, int pixelWidth, int pixelHeight)
    {
        if (already != null) return already;
        object k = KeyFor(rawBytes, key);
        if (_decoded.TryGetValue(k, out var screen) && screen != null && !NeedsMore(k, screen, pixelWidth, pixelHeight))
            return screen;
        if (_print.TryGetValue(k, out var printed)
            && (printed.bmp == null || printed.full || !Smaller(printed.bmp, pixelWidth, pixelHeight)))
            return printed.bmp ?? screen;
        if (rawBytes == null) return screen;

        CanvasBitmap? result = null;
        bool full = false;
        try
        {
            if (ImageDecoder.Decode(rawBytes, pixelWidth, pixelHeight) is { } d)
            {
                result = CreateBitmap(CanvasDevice.GetSharedDevice(), d.Pixels, d.Width, d.Height);
                full = d.Width == d.NaturalWidth && d.Height == d.NaturalHeight;
            }
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        printed.bmp?.Dispose();
        _print[k] = (result, full);
        return result ?? screen;
    }

    private static bool Smaller(CanvasBitmap bmp, int w, int h)
    {
        var px = bmp.SizeInPixels;
        return w > px.Width + 1 || h > px.Height + 1;
    }

    /// <summary>Disposes the bitmaps decoded for the print that just finished.</summary>
    public void EndPrint()
    {
        bool any = false;
        foreach (var (bmp, _) in _print.Values) if (bmp != null) { bmp.Dispose(); any = true; }
        _print.Clear();
        if (any) OnReleased?.Invoke(); // print-resolution pictures are the largest this cache ever holds
    }

    /// <summary>Drops the cached bitmap for one element so its (changed) bytes are re-decoded.
    /// The decoded <see cref="CanvasBitmap"/> is a device-bound GPU resource, so dispose it
    /// explicitly rather than waiting for the finalizer. A byte[] key maps to its content hash;
    /// content-identical siblings simply re-decode on demand.</summary>
    public void Invalidate(object key)
    {
        object k = key is byte[] b ? KeyFor(b, key) : key;
        Evict(k);
        _inflight.Remove(k);
    }

    /// <summary>Raised after a decoded bitmap is disposed (the control asks the device to trim — see
    /// <c>RichEditor.ScheduleDeviceTrim</c>).</summary>
    public event Action? OnReleased;

    private void Evict(object key)
    {
        if (_decoded.TryGetValue(key, out var bmp) && bmp != null)
        {
            bmp.Dispose();
            OnReleased?.Invoke();
        }
        _decoded.Remove(key);
        _atSourceSize.Remove(key);
        _seen.Remove(key);
        Unretire(key);
    }

    // ---- pictures scrolled out of view --------------------------------------------------------------------

    // Where each picture was last drawn (control DIPs) and a use stamp that orders the draws, for TrimOffscreen.
    private readonly Dictionary<object, (Rect where, long stamp)> _seen = new();
    private long _stamp;

    /// <summary>Disposes the bitmaps of pictures still IN the document but drawn nowhere near
    /// <paramref name="keep"/> (the viewport plus a margin, in control DIPs), least recently drawn first, until
    /// what is left out there totals at most <paramref name="keepBytes"/>. A picture scrolled back into view
    /// re-decodes; the loading placeholder shows until it lands.
    /// <para>Why this exists (measured 2026-09-27, MemBaseline scroll mode): the cache held every picture ever
    /// DRAWN, and paging once to the end of a document draws all of them. 2,000 paragraphs with 100 photos:
    /// Private bytes 183 MB at the top, 501 MB after scrolling to the end and back, and it never came down while
    /// the document was open. Prune can't help — every one of those pictures is still in the document.</para>
    /// <para>Pictures an edit removed are not counted here; the retired list has its own budget (Prune).</para></summary>
    public void TrimOffscreen(Rect keep, long keepBytes)
    {
        List<(object key, long stamp, long bytes)>? far = null;
        long farBytes = 0;
        foreach (var (k, bmp) in _decoded)
        {
            if (bmp == null || _retiredIndex.ContainsKey(k)) continue;
            // No recorded draw (a test seed): nothing says where it is, so it is the first to go.
            var seen = _seen.TryGetValue(k, out var s) ? s : (where: Rect.Empty, stamp: 0L);
            if (Overlaps(seen.where, keep)) continue;
            long bytes = PixelBytes(bmp);
            (far ??= new()).Add((k, seen.stamp, bytes));
            farBytes += bytes;
        }
        if (far != null && farBytes > keepBytes)
        {
            far.Sort((a, b) => a.stamp.CompareTo(b.stamp));
            foreach (var (k, _, bytes) in far)
            {
                if (farBytes <= keepBytes) break;
                Evict(k);
                farBytes -= bytes;
            }
        }
        // A position recorded for a key that neither holds a bitmap nor is decoding (a decode that was pruned
        // in flight) would otherwise sit here until the same picture is drawn again.
        if (_seen.Count > _decoded.Count + _inflight.Count)
        {
            List<object>? stale = null;
            foreach (var k in _seen.Keys)
                if (!_decoded.ContainsKey(k) && !_inflight.Contains(k)) (stale ??= new()).Add(k);
            if (stale != null) foreach (var k in stale) _seen.Remove(k);
        }
    }

    private static bool Overlaps(Rect a, Rect b)
        => !a.IsEmpty && !b.IsEmpty && a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    // Entries the document no longer references but that are kept decoded anyway, oldest first, so undoing
    // the edit that removed a picture finds it warm. Bounded by the retainBytes each Prune passes.
    //
    // Why this exists (measured 2026-09-16, MemBaseline images mode): Prune used to run only on a document
    // swap, so a picture removed by EDITING — Delete, Backspace, cut, paste over it — stayed decoded until the
    // next swap. Six 4000x3000 photos inserted and deleted three times held 18 full-size bitmaps with none
    // in the document: Private bytes 112 -> 1404 MB. Disposing on the spot instead would re-decode (placeholder
    // flash) on every Ctrl+Z of a delete, the reason a swap prunes rather than clears.
    private readonly LinkedList<(object key, long bytes)> _retired = new();
    private readonly Dictionary<object, LinkedListNode<(object key, long bytes)>> _retiredIndex = new();
    private long _retiredBytes;

    /// <summary>True when nothing is decoded or decoding — the edit-path sweep skips its document walk.</summary>
    public bool IsEmpty => _decoded.Count == 0 && _inflight.Count == 0;

    /// <summary>Drops cache entries whose key is not in <paramref name="live"/> — the set of RawBytes the
    /// current document references. Entries leaving the document are kept, oldest disposed first, while their
    /// decoded pixels total at most <paramref name="retainBytes"/>; 0 disposes every one of them (a genuine
    /// document swap, a lost device). An entry back in the document (undo) stops counting against the budget.</summary>
    public void Prune(HashSet<object> live, long retainBytes = 0)
    {
        // Map the live byte arrays to the cache's content-hash key space first.
        var liveKeys = new HashSet<object>();
        foreach (var k in live) liveKeys.Add(k is byte[] b ? KeyFor(b, k) : k);

        foreach (var (k, bmp) in _decoded)
        {
            if (liveKeys.Contains(k)) Unretire(k);
            else if (!_retiredIndex.ContainsKey(k))
            {
                long bytes = PixelBytes(bmp);
                _retiredIndex[k] = _retired.AddLast((k, bytes));
                _retiredBytes += bytes;
            }
        }
        while (_retired.First is { } oldest && (retainBytes <= 0 || _retiredBytes > retainBytes))
            Evict(oldest.Value.key);
        _inflight.RemoveWhere(k => !liveKeys.Contains(k)); // their decodes discard themselves on completion
    }

    private void Unretire(object key)
    {
        if (!_retiredIndex.Remove(key, out var node)) return;
        _retiredBytes -= node.Value.bytes;
        _retired.Remove(node);
    }

    // What a decoded bitmap holds: 4 bytes per pixel (BGRA8). A failed decode holds nothing.
    private static long PixelBytes(CanvasBitmap? bmp)
    {
        if (bmp == null) return 0;
        var px = bmp.SizeInPixels;
        return (long)px.Width * px.Height * 4;
    }

    // ---- test seams: decodes are async and device-bound, so tests place entries directly ----
    internal int DecodedCount => _decoded.Count;
    internal int SeenCount => _seen.Count;
    internal Rect? SeenAt(byte[] rawBytes) => _seen.TryGetValue(KeyFor(rawBytes, rawBytes), out var s) ? s.where : null;
    internal long RetiredBytes => _retiredBytes;
    internal bool IsCached(byte[] rawBytes) => _decoded.ContainsKey(KeyFor(rawBytes, rawBytes));
    internal bool IsDecoding(byte[] rawBytes) => _inflight.Contains(KeyFor(rawBytes, rawBytes));
    internal CanvasBitmap? CachedBitmap(byte[] rawBytes) => _decoded.GetValueOrDefault(KeyFor(rawBytes, rawBytes));
    // Seeded entries count as source-size, so a draw never starts a real decode that would replace them.
    internal void Seed(byte[] rawBytes, CanvasBitmap? bitmap)
    {
        var k = KeyFor(rawBytes, rawBytes);
        _decoded[k] = bitmap;
        _atSourceSize.Add(k);
    }

    // No Clear(): the document-swap path deliberately uses Prune(liveKeys) instead, so undo/redo
    // snapshots that share RawBytes keep their bitmaps warm (a Clear there caused a placeholder flash
    // and a full re-decode on every Ctrl+Z). Prune with an empty set (retainBytes 0) is a Clear.
}
