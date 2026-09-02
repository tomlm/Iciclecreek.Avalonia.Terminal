using System;
using System.Collections.Generic;
using Avalonia;
using Iciclecreek.Terminal.Skia;
using NUnit.Framework;
using XTerm.Options;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Who owns a frame header, and for how long.
/// </summary>
/// <remarks>
/// <para>The direct path splits a frame across two media. The rows the classic renderer draws are
/// RECORDED into the display list, immutable and replayable; the rows the Skia layer draws are read
/// LIVE at composite time, out of a header the UI thread owns. <c>Deferred</c> is the contract
/// between the halves, so the header has to stay put for as long as the display list it was
/// recorded against.</para>
///
/// <para>Headers used to rotate on a counter three deep, which is that guarantee guessed at. A
/// fourth frame assembled before the first retired refilled a header the render thread was still
/// reading, and the halves disagreed — rows drawn twice or not at all, cleared by the next
/// unrelated repaint, reproducible on nobody's machine. These tests are the guarantee stated
/// instead: a header comes back when Avalonia disposes the operation using it, and not before.</para>
///
/// <para>Plain NUnit, no Avalonia application: the builder reads a terminal and writes a header,
/// and neither needs a UI thread.</para>
/// </remarks>
[TestFixture]
public class SnapshotPoolTests
{
    private static XTerm.Terminal Terminal()
    {
        var terminal = new XTerm.Terminal(new TerminalOptions { Cols = 20, Rows = 4 });
        terminal.Write("hello");
        return terminal;
    }

    private static TerminalSnapshot Build(SnapshotBuilder builder, XTerm.Terminal terminal) =>
        builder.Build(terminal, terminal.Colors.Take(), terminal.Buffer.YBase, 4, 20,
                      8, 16, 14, "monospace", null, null, () => { },
                      ligatures: false, reverseVideo: false, blinkOn: true,
                      boldIsBright: false, minimumContrast: null);

    /// <summary>
    /// Frames in flight never share a header, however many of them there are.
    /// </summary>
    /// <remarks>
    /// Six is past the old depth of three on purpose: three was the number that made this fail, and
    /// a test that stops at three would have passed against the bug.
    /// </remarks>
    [Test]
    public void Headers_in_flight_are_never_handed_out_twice()
    {
        var terminal = Terminal();
        var builder = new SnapshotBuilder();

        var live = new List<TerminalSnapshot>();
        for (var i = 0; i < 6; i++)
            live.Add(Build(builder, terminal));

        Assert.That(live, Is.Unique, "a header was reused while an earlier frame still held it");
    }

    /// <summary>A retired header is the next one handed out — the pool still pools.</summary>
    [Test]
    public void A_retired_header_is_reused()
    {
        var terminal = Terminal();
        var builder = new SnapshotBuilder();

        var first = Build(builder, terminal);
        builder.Return(first);

        Assert.That(Build(builder, terminal), Is.SameAs(first));
    }

    /// <summary>
    /// Disposing the operation is what retires the header, and disposing it twice does not put the
    /// same header in the free list twice.
    /// </summary>
    /// <remarks>
    /// A header pushed twice is handed to two live frames at once, which is the original bug
    /// arriving by a different road.
    /// </remarks>
    [Test]
    public void Disposing_the_layer_returns_the_header_exactly_once()
    {
        var terminal = Terminal();
        var builder = new SnapshotBuilder();
        using var fonts = new SkiaFontCache();

        var snapshot = Build(builder, terminal);
        var layer = new TerminalSkiaLayer(snapshot, fonts, new Rect(0, 0, 160, 64),
                                          builder, requestPaint: null);

        Assert.That(layer.Stale, Is.False, "nothing has drawn it yet");

        layer.Dispose();
        layer.Dispose();

        Assert.That(Build(builder, terminal), Is.SameAs(snapshot), "the header did not come back");
        Assert.That(Build(builder, terminal), Is.Not.SameAs(snapshot), "it came back twice");
    }

    /// <summary>
    /// A header carries the frame it was built for, and stops carrying it once retired.
    /// </summary>
    /// <remarks>
    /// This is what the tripwire in TerminalSkiaLayer.Render compares against. It cannot fire while
    /// the tests above hold, which is the point of having it: the day something reintroduces early
    /// reuse, the symptom is a skipped frame and a repaint rather than a torn screen.
    /// </remarks>
    [Test]
    public void Every_frame_gets_its_own_id_and_a_retired_header_keeps_none()
    {
        var terminal = Terminal();
        var builder = new SnapshotBuilder();

        var first = Build(builder, terminal);
        var second = Build(builder, terminal);

        Assert.That(first.FrameId, Is.Not.Zero);
        Assert.That(second.FrameId, Is.Not.EqualTo(first.FrameId));

        builder.Return(first);
        Assert.That(first.FrameId, Is.Zero, "a retired header must not answer to its old frame");
    }

    /// <summary>
    /// A layer with no owner returns nothing, so a caller holding its own snapshot keeps it.
    /// </summary>
    /// <remarks>
    /// The benches build a synthetic snapshot and drive the layer against it directly. Handing that
    /// one to a pool it never came from would be a quiet corruption of the next frame.
    /// </remarks>
    [Test]
    public void A_layer_over_a_snapshot_it_does_not_own_returns_nothing()
    {
        var terminal = Terminal();
        var builder = new SnapshotBuilder();
        using var fonts = new SkiaFontCache();

        var snapshot = Build(builder, terminal);
        var borrowed = new TerminalSnapshot();
        borrowed.EnsureCapacity(4, 20);

        new TerminalSkiaLayer(borrowed, fonts, new Rect(0, 0, 160, 64)).Dispose();

        Assert.That(Build(builder, terminal), Is.Not.SameAs(borrowed));
        Assert.That(Build(builder, terminal), Is.Not.SameAs(snapshot), "the live header was retired");
    }
}
