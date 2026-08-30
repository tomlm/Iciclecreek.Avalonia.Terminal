using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Media.TextFormatting;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Iciclecreek.Terminal;

namespace Terminal.RenderBench;

/// <summary>
/// Measures TerminalView.Render against realistic frames.
/// </summary>
/// <remarks>
/// <para>Two numbers per corpus, and they answer different questions. TIME says what a frame costs;
/// ALLOCATION says what it costs the collector, which on a render loop is the number that turns into
/// a stutter later rather than a slow frame now.</para>
/// <para>Allocation is the more trustworthy of the two here: it is deterministic, so a difference of
/// a few bytes is real, while a time difference under a few percent on one machine is noise. Both
/// are reported so a change that trades one for the other is visible.</para>
/// <para>Headless with UseHeadlessDrawing FALSE, so Skia is doing real work -- text is genuinely
/// shaped and geometry genuinely built. With headless drawing on, DrawText is a no-op and the whole
/// measurement is of the loop around it.</para>
/// </remarks>
internal static class Program
{
    private const int Cols = 120;
    private const int Rows = 40;

    // As on every other entry point in the repo. The bench runs headless, but the platform is
    // still initialised for real, and Windows expects an Avalonia Main to be STA.
    [STAThread]
    private static int Main(string[] args)
    {
        var iterations = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 200;

        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(b => b.Instance?.Styles.Add(new FluentTheme()))
            .SetupWithoutStarting();

        var results = new List<Result>();

        foreach (var corpus in Corpus.All)
            results.Add(Measure(corpus, iterations));

        Report(results);
        Microbench();
        PixelCompare();
        return 0;
    }

    private sealed record Corpus(string Name, string Description, Func<int, string> Frame)
    {
        /// <summary>
        /// Each corpus writes one screenful per frame, so every visible line is dirty and the run
        /// BUILD path runs -- which is what a terminal under load actually does. The cached-replay
        /// path is measured separately by the "static" corpus, which writes nothing.
        /// </summary>
        public static readonly Corpus[] All =
        {
            new("ascii", "plain text, one attribute", i =>
            {
                var sb = new StringBuilder();
                for (var r = 0; r < Rows; r++)
                    sb.Append($"line {i:D4}.{r:D2} the quick brown fox jumps over the lazy dog 0123456789 abcdefgh\r\n");
                return sb.ToString();
            }),

            new("sgr", "a colour change every eight columns", i =>
            {
                var sb = new StringBuilder();
                for (var r = 0; r < Rows; r++)
                {
                    for (var c = 0; c < Cols / 8; c++)
                        sb.Append($"\u001b[38;5;{(i + r + c) % 256}m").Append("abcdefgh");
                    sb.Append("\u001b[0m\r\n");
                }
                return sb.ToString();
            }),

            new("truecolor", "a 24-bit colour change every four columns", i =>
            {
                var sb = new StringBuilder();
                for (var r = 0; r < Rows; r++)
                {
                    for (var c = 0; c < Cols / 4; c++)
                        sb.Append($"\u001b[38;2;{(i * 7 + c) % 256};{(r * 3) % 256};{c % 256}m").Append("abcd");
                    sb.Append("\u001b[0m\r\n");
                }
                return sb.ToString();
            }),

            new("unicode", "CJK and emoji, wide cells and clusters", i =>
            {
                var sb = new StringBuilder();
                for (var r = 0; r < Rows; r++)
                {
                    sb.Append("日本語テキスト ");
                    sb.Append("café naïve ");
                    sb.Append("👍🏽 family 👨‍👩‍👧 ");
                    sb.Append($"row {r:D2} pass {i:D3}");
                    sb.Append("\r\n");
                }
                return sb.ToString();
            }),

            new("static", "nothing written; every line replays from cache", _ => string.Empty),
            new("selected", "cached replay with the whole screen selected", _ => string.Empty),
        };
    }

    private sealed record Result(string Name, string Description, double MsPerFrame, long BytesPerFrame);

    private static Result Measure(Corpus corpus, int iterations)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Fill the screen once, so the "static" corpus has something to replay and every corpus
        // starts from a warm buffer rather than an empty one.
        view.Terminal.Write(Corpus.All[0].Frame(0));
        Dispatcher.UIThread.RunJobs();

        // The selection pass walks every cell of every row asking whether it is selected, which only
        // happens when there IS a selection -- so it needs a corpus that has one to be measured at
        // all. Everything else about this frame is identical to "static".
        if (corpus.Name == "selected")
        {
            view.Terminal.Selection.SelectAll();
            Dispatcher.UIThread.RunJobs();
        }

        // Every frame's text built UP FRONT. Building it inside the loop would put the harness's own
        // string work inside the numbers, and for the SGR corpora that is more allocation than the
        // render does.
        var frames = new string[iterations];
        for (var i = 0; i < iterations; i++)
            frames[i] = corpus.Frame(i);

        var target = new RenderTargetBitmap(new PixelSize(1400, 900));

        // Warm up: JIT, font fallback, Skia's own caches. Without this the first frames measure
        // one-time setup and dominate a short run.
        for (var i = 0; i < 20; i++)
        {
            view.Terminal.Write(corpus.Frame(i));
            Dispatcher.UIThread.RunJobs();
            using var warm = target.CreateDrawingContext();
            view.Render(warm);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocated = 0;
        double elapsedMs = 0;

        for (var i = 0; i < iterations; i++)
        {
            // OUTSIDE the measurement. What the emulator costs to parse a frame is a different
            // question from what the view costs to draw one, and mixing them hides both.
            view.Terminal.Write(frames[i]);
            Dispatcher.UIThread.RunJobs();

            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var beforeTicks = Stopwatch.GetTimestamp();

            using (var ctx = target.CreateDrawingContext())
                view.Render(ctx);

            elapsedMs += Stopwatch.GetElapsedTime(beforeTicks).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        }

        target.Dispose();
        window.Close();

        return new Result(corpus.Name, corpus.Description,
                          elapsedMs / iterations,
                          allocated / iterations);
    }

    /// <summary>
    /// What one draw call costs, with nothing of the terminal in the way.
    /// </summary>
    /// <remarks>
    /// The corpus numbers say a frame is expensive; this says which CALL is. Run against the same
    /// Skia backend and the same target, so the only difference between the rows is what is being
    /// asked of the context.
    /// </remarks>
    private static void Microbench()
    {
        const int N = 5000;
        var target = new RenderTargetBitmap(new PixelSize(1400, 900));

        var mutable = new SolidColorBrush(Colors.Orange);
        var immutable = new ImmutableSolidColorBrush(Colors.Orange);
        var typeface = new Typeface(FontFamily.Default);
        var text = new FormattedText("the quick brown fox jumps over the lazy dog",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 14, immutable);
        var rect = new Rect(0, 0, 100, 20);
        var point = new Point(0, 0);

        Console.WriteLine();
        Console.WriteLine($"{"draw call",-38} {"ns/call",10} {"B/call*",12}");
        Console.WriteLine(new string('-', 64));
        // The * on the allocation column: the number is GC.GetAllocatedBytesForCurrentThread,
        // deliberately -- the bench is single-threaded, so scoping to this thread excludes GC and
        // JIT noise from other threads. It is NOT process-wide allocation; the label says so
        // because a reader who takes it for the total will over-trust it.
        Console.WriteLine("  * bytes allocated on the bench thread only");

        // Run BOTH orders. The first reading of a pair on a cold context flattered whichever ran
        // first, which would have turned an ordering artefact into a recommendation.
        Time("FillRectangle, immutable (first)", N, target, ctx => ctx.FillRectangle(immutable, rect));
        Time("FillRectangle, mutable (second)", N, target, ctx => ctx.FillRectangle(mutable, rect));
        Time("FillRectangle, mutable (third)", N, target, ctx => ctx.FillRectangle(mutable, rect));
        Time("FillRectangle, immutable (fourth)", N, target, ctx => ctx.FillRectangle(immutable, rect));
        Time("DrawText, cached FormattedText", N, target, ctx => ctx.DrawText(text, point));
        Time("new FormattedText only (no draw)", N, target, _ =>
        {
            var t = new FormattedText("the quick brown fox jumps over the lazy dog",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 14, immutable);
            GC.KeepAlive(t);
        });
        // The alternative to FormattedText: shape once, keep the glyph run, blit it every frame.
        // This is what a terminal can do and a general text control cannot -- the text of a run does
        // not change between frames, only where it is drawn.
        var sample = "the quick brown fox jumps over the lazy dog";
        var glyphTypeface = typeface.GlyphTypeface;

        // Discovered rather than assumed: the glyph-lookup API moved between Avalonia versions, so
        // the bench asks the type what it has rather than guessing a name that compiles.

        // Shape once, keep the run, blit it every frame. This is what a terminal can do that a
        // general text control cannot: the TEXT of a run does not change between frames, only where
        // it is drawn -- which is the one thing DrawText re-derives on every call.
        var map = glyphTypeface.CharacterToGlyphMap;
        var indices = new ushort[sample.Length];
        for (var i = 0; i < sample.Length; i++)
            indices[i] = map.GetGlyph(sample[i]);

        var glyphRun = new GlyphRun(glyphTypeface, 14, sample.AsMemory(), indices,
                                    baselineOrigin: new Point(0, 14));

        Console.WriteLine("  GlyphInfo constructors:");
        foreach (var c in typeof(GlyphInfo).GetConstructors())
            Console.WriteLine($"    ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        Console.WriteLine($"  measured advance for 'x': {(glyphTypeface.TryGetHorizontalGlyphAdvance(map.GetGlyph('x'), out var adv) ? adv : -1)} design units, em={glyphTypeface.Metrics.DesignEmHeight}");
        Console.WriteLine();

        Time("DrawGlyphRun, cached run", N, target, ctx => ctx.DrawGlyphRun(immutable, glyphRun));

        // A cached run gets replayed at a DIFFERENT screen row as the buffer scrolls, so its origin
        // cannot simply be baked in. Two ways to move it, and the choice matters more than it looks:
        // mutating a run the context may not have consumed yet is a correctness question, not just a
        // speed one.
        var moveCount = 0;
        Time("DrawGlyphRun + set BaselineOrigin", N, target, ctx =>
        {
            glyphRun.BaselineOrigin = new Point(0, 14 + (++moveCount & 1));
            ctx.DrawGlyphRun(immutable, glyphRun);
        });

        Time("DrawGlyphRun + PushTransform", N, target, ctx =>
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(0, ++moveCount & 1)))
                ctx.DrawGlyphRun(immutable, glyphRun);
        });
        Time("build GlyphRun (shape) only", N, target, _ =>
        {
            var r = new GlyphRun(glyphTypeface, 14, sample.AsMemory(), indices, new Point(0, 14));
            GC.KeepAlive(r);
        });

        Time("new SolidColorBrush", N, target, _ => GC.KeepAlive(new SolidColorBrush(Colors.Red)));
        Time("new ImmutableSolidColorBrush", N, target, _ => GC.KeepAlive(new ImmutableSolidColorBrush(Colors.Red)));

        target.Dispose();
        Console.WriteLine();
    }

    private static void Time(string name, int n, RenderTargetBitmap target, Action<DrawingContext> body)
    {
        using (var warm = target.CreateDrawingContext())
            for (var i = 0; i < 200; i++) body(warm);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var ctx = target.CreateDrawingContext();
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();

        for (var i = 0; i < n; i++) body(ctx);

        var ns = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / n;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - b0) / (double)n;

        Console.WriteLine($"{name,-38} {ns,10:F0} {bytes,12:F1}");
    }

    /// <summary>
    /// Renders each corpus with the glyph fast path on and off, and compares the pixels.
    /// </summary>
    /// <remarks>
    /// The test suite cannot see this. Every assertion in it is about buffer state or geometry, and
    /// a run drawn through a different pipeline passes all of them while looking wrong -- which is
    /// the entire risk of this change. What matters is not that the two are byte-identical, since
    /// two shaping pipelines may round a subpixel differently, but that no pixel is far off and
    /// almost none differ at all.
    /// </remarks>
    private static void PixelCompare()
    {
        Console.WriteLine($"{"corpus",-12} {"differing px",14} {"of total",12} {"worst channel",14}");
        Console.WriteLine(new string('-', 60));

        foreach (var corpus in Corpus.All)
        {
            if (corpus.Name == "static") continue;

            var withGlyphs = Capture(corpus, useGlyphRun: true);
            var withText = Capture(corpus, useGlyphRun: false);

            // Over RAW BGRA, one pixel at a time. The first version compared the bytes of the
            // SAVED image, and those are DEFLATE output: equality still meant identical frames,
            // but the moment anything differed, "differing px" counted compressed-stream bytes
            // and "worst channel" read compression artefacts -- numbers that look precise and
            // mean nothing. Here a differing pixel is a pixel and a channel is a channel.
            long differing = 0, worst = 0;
            for (var i = 0; i < withGlyphs.Length; i += 4)
            {
                var pixelDiffers = false;
                for (var c = 0; c < 4; c++)
                {
                    int d = Math.Abs(withGlyphs[i + c] - withText[i + c]);
                    if (d != 0) pixelDiffers = true;
                    if (d > worst) worst = d;
                }
                if (pixelDiffers) differing++;
            }

            var pct = 100.0 * differing / (withGlyphs.Length / 4);
            Console.WriteLine($"{corpus.Name,-12} {differing,14:N0} {pct,11:F3}% {worst,14}");
        }

        Console.WriteLine();
    }

    private static byte[] Capture(Corpus corpus, bool useGlyphRun)
    {
        // The flag is process-global, so it is restored in a finally -- and to what it WAS, not to
        // true -- or one failed capture would silently flip every benchmark after it onto one
        // pipeline. The window and bitmap ride in the same finally for the same reason.
        var previous = TerminalView.GlyphRunFastPathEnabled;
        TerminalView.GlyphRunFastPathEnabled = useGlyphRun;
        Window? window = null;
        RenderTargetBitmap? target = null;
        try
        {
            var view = new TerminalView { Process = "" };
            window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            view.Terminal.Write(corpus.Frame(1));
            Dispatcher.UIThread.RunJobs();

            target = new RenderTargetBitmap(new PixelSize(1400, 900));
            using (var ctx = target.CreateDrawingContext())
                view.Render(ctx);

            return Pixels(target);
        }
        finally
        {
            target?.Dispose();
            window?.Close();
            TerminalView.GlyphRunFastPathEnabled = previous;
        }
    }

    /// <summary>The rendered frame as raw BGRA, straight off the bitmap -- no encoder involved.</summary>
    private static byte[] Pixels(RenderTargetBitmap target)
    {
        var size = target.PixelSize;
        var stride = size.Width * 4;
        var px = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(default, size), handle.AddrOfPinnedObject(), px.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return px;
    }

    private static void Report(List<Result> results)
    {
        Console.WriteLine();
        Console.WriteLine($"{"corpus",-12} {"ms/frame",10} {"B/frame*",14}   description");
        Console.WriteLine(new string('-', 78));
        // The * on the allocation column: the number is GC.GetAllocatedBytesForCurrentThread,
        // deliberately -- the bench is single-threaded, so scoping to this thread excludes GC and
        // JIT noise from other threads. It is NOT process-wide allocation; the label says so
        // because a reader who takes it for the total will over-trust it.
        Console.WriteLine("  * bytes allocated on the bench thread only");

        foreach (var r in results)
            Console.WriteLine($"{r.Name,-12} {r.MsPerFrame,10:F3} {r.BytesPerFrame,14:N0}   {r.Description}");

        Console.WriteLine();
    }
}
