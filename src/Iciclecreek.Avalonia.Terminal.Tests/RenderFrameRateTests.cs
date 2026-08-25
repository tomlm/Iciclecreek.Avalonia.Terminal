using Iciclecreek.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Avalonia.Terminal.Tests;

/// <summary>
/// The render throttle's frame rate is global mutable state reachable by any host that can reference the
/// assembly, which is what makes the guard rail worth asserting: a rejected value has to leave the previous
/// rate standing, because the alternative is every terminal in the app repainting at the wrong cadence — or
/// not at all — with nothing on screen to say why.
///
/// <para>No Avalonia here. The property is arithmetic and validation; the coordinated frame it feeds needs a
/// UI thread that these assertions do not.</para>
/// </summary>
[TestFixture]
public class RenderFrameRateTests
{
    private int _original;

    // Captured and put back rather than left wherever a test happened to leave it. A stray rate would not
    // fail anything downstream, it would quietly change the cadence the rest of the suite renders at, which
    // is the worse outcome of the two.
    [SetUp]
    public void CaptureOriginal() => _original = TerminalRenderThrottle.TargetFrameRate;

    [TearDown]
    public void RestoreOriginal() => TerminalRenderThrottle.TargetFrameRate = _original;

    [Test]
    public void Defaults_To_Thirty_Frames_Per_Second()
    {
        Assert.That(_original, Is.EqualTo(30));
    }

    [TestCase(1)]
    [TestCase(30)]
    [TestCase(60)]
    [TestCase(1000)]
    public void Accepts_A_Rate_Inside_The_Range(int framesPerSecond)
    {
        TerminalRenderThrottle.TargetFrameRate = framesPerSecond;

        Assert.That(TerminalRenderThrottle.TargetFrameRate, Is.EqualTo(framesPerSecond));
    }

    // Zero is the case the range exists for. It divides into an infinite interval — not a duration any frame
    // can be scheduled from — and it would fail deep inside ScheduleFrame on the PTY read thread rather than
    // at the assignment the host actually got wrong. A negative rate is quieter and no better: it yields a
    // negative interval, which compares as already elapsed and defeats the throttle completely.
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1001)]
    public void Rejects_A_Rate_Outside_The_Range(int framesPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerminalRenderThrottle.TargetFrameRate = framesPerSecond);
    }

    [Test]
    public void A_Rejected_Rate_Leaves_The_Previous_One_In_Place()
    {
        TerminalRenderThrottle.TargetFrameRate = 45;

        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalRenderThrottle.TargetFrameRate = 0);

        Assert.That(TerminalRenderThrottle.TargetFrameRate, Is.EqualTo(45));
    }
}
