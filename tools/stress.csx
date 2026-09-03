#!/usr/bin/env dotnet-script
//
//  test.csx — a tearing probe for a terminal.
//
//  Draws the whole screen as solid rows, each labelled with a letter and given a colour that
//  belongs to that letter. Every frame shifts the sequence down by one, so a correctly drawn
//  screen ALWAYS reads as an unbroken run: A B C ... Z A B ...
//
//  A tear is then impossible to miss and impossible to argue about. If any row's letter does not
//  follow the row above it, the screen is showing two frames at once, and the boundary is exactly
//  where the terminal stopped drawing one and started drawing the other. The letter tells you how
//  many frames apart the two halves are.
//
//  Each frame is wrapped in DEC private mode 2026 (synchronised output): CSI ? 2026 h before, and
//  CSI ? 2026 l after. A terminal that honours it must not present anything between the two, so
//  with it on there should be no tear at all. --nosync leaves the wrapper off, which is the
//  control: if both look the same, the mode is not doing anything.
//
//  Usage:
//    dotnet script test.csx                 50 fps, synchronised output on
//    dotnet script test.csx --nosync        the control -- no BSU/ESU
//    dotnet script test.csx --fps 30        a different rate
//    dotnet script test.csx --plain         no colour, letters only
//    dotnet script test.csx --heavy      a colour change on EVERY CELL
//    dotnet script test.csx --stride 1   advance ONE letter a frame (subtle)
//    dotnet script test.csx --mono       every cell, ONE colour for the whole frame
//
//  --mono is the control for --heavy, and the pair is what separates the two things a renderer
//  can be slow at. Both repaint every cell of the screen, so the CELL COUNT is identical and so
//  is the work of putting a glyph in each one. What differs is colour changes: --heavy changes on
//  every cell, so a renderer that groups cells into runs of one style gets a run per cell, while
//  --mono gives it one run per row. Compare the two and the answer falls out -- if --mono is fast
//  and --heavy is slow, the cost is the runs, not the cells; if both are slow, it is the cells.
//
//  Adjacent frames are made as different as possible on purpose. At a stride of one letter, a
//  frame that is torn shows as a single repeated or skipped letter in the run -- true, correct, and
//  very easy to look straight past. At the default stride of 13 the two halves of a torn screen are
//  half an alphabet and half a colour wheel apart, and the boundary is unmissable. The parity flip
//  below does the same job again in a way that needs no reading at all: consecutive frames use
//  inverted colours, so ANY tear puts two colour schemes on screen at once.
//
//  --heavy exists because the light form draws a frame in about a tenth the bytes a real
//  full-screen program does. One run of identical characters per row is one colour change per row;
//  a program like cacademo changes colour on every cell, which is an escape sequence per cell and
//  roughly ten times the frame. That matters here, because a frame small enough to arrive between
//  two paints cannot tear no matter what the terminal does -- so a clean light run says nothing
//  about a heavy one. Use --heavy to compare like with like.
//
//  Any key quits.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

const string ESC = "\u001b";

bool sync = true;
bool colour = true;
bool heavy = false;
bool mono = false;
int stride = 13;
int targetFps = 50;

for (var i = 0; i < Args.Count; i++)
{
    switch (Args[i])
    {
        case "--nosync": sync = false; break;
        case "--plain": colour = false; break;
        case "--heavy": heavy = true; break;
        case "--mono": mono = true; break;
        case "--stride" when i + 1 < Args.Count: stride = int.Parse(Args[++i]); break;
        case "--fps" when i + 1 < Args.Count: targetFps = int.Parse(Args[++i]); break;
        case "-h":
        case "--help":
            Console.WriteLine("stress.csx [--nosync] [--plain] [--heavy] [--mono] [--stride N] [--fps N]");
            return;
    }
}

EnableVirtualTerminal();

// Our own writer, not Console.Out. The writer behind Console.Out has a 256-character buffer with
// AutoFlush on, so a frame-sized write leaves the process as a thousand syscall-sized fragments --
// each one separately parsed by the pseudoconsole. Measured on this probe's own frames: ~10fps
// through Console.Out, ~99 through a 64K writer flushed once per frame. The encoding goes first,
// because on a real console it also moves the output code page to UTF-8.
try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16, leaveOpen: true)
{
    AutoFlush = false,
};

// Thread.Sleep cannot wake earlier than the system timer tick, which defaults to about 15.6ms on
// Windows -- most of a 20ms frame. Without this the pacing below cannot hit 50fps at all, and the
// frame rate this reports would be measuring the timer rather than the terminal.
var raisedTimer = RaiseTimerResolution();

int cols = SafeWidth();
int rows = SafeHeight();

var frameBuffer = new StringBuilder(1 << 16);
var clock = Stopwatch.StartNew();
var windowStart = 0L;
var framesInWindow = 0;
var rate = 0.0;
var lastFrameBytes = 0;
long frame = 0;

// Alternate screen, cursor hidden, cleared.
Write($"{ESC}[?1049h{ESC}[?25l{ESC}[0m{ESC}[2J");

try
{
    var interval = Stopwatch.Frequency / (double)targetFps;
    var next = (double)clock.ElapsedTicks;

    while (!KeyPressed())
    {
        // A resize mid-run would otherwise draw a screen that does not fit, which reads as a tear
        // and is not one.
        var w = SafeWidth();
        var h = SafeHeight();

        if (w != cols || h != rows)
        {
            cols = w;
            rows = h;
            Write($"{ESC}[2J");
        }

        frameBuffer.Clear();

        if (sync)
            frameBuffer.Append(ESC).Append("[?2026h");

        if (mono)
        {
            // Once for the frame, and it still alternates between frames -- so a tear is two
            // shades on screen at once exactly as it is under colour, without giving the renderer
            // a second style to group.
            frameBuffer.Append(ESC).Append(frame % 2 == 0
                ? "[0;38;5;250;48;5;235m"
                : "[0;38;5;235;48;5;250m");
        }

        for (var row = 0; row < rows; row++)
        {
            // Row 0 carries the frame's starting letter and every row below follows it, so the
            // whole screen is one unbroken alphabetical run. Any break in that run is a tear.
            var letter = (char)('A' + ((row + (frame * stride)) % 26));

            frameBuffer.Append(ESC).Append('[').Append(row + 1).Append(";1H");

            if (colour && !mono)
            {
                // The colour belongs to the LETTER, not the row, so a torn band is wrong in both
                // ways at once and is visible from across the room.
                // Inverted on alternate frames, so a tear shows as two colour schemes at once
                // even to someone not reading the letters.
                var shade = (int)(((row + (frame * stride)) % 26) * 8) % 216;
                var bg = 16 + (frame % 2 == 0 ? shade : 215 - shade);
                frameBuffer.Append(ESC).Append("[0;38;5;16;48;5;").Append(bg).Append('m');
            }

            if (heavy && !mono)
            {
                // A colour change per cell, which is what a full-screen program actually emits and
                // roughly ten times the bytes of the run above. The colour walks with the column so
                // no two neighbours share one and nothing can be coalesced away.
                for (var col = 0; col < cols - 1; col++)
                {
                    var cellShade = (int)(((row + (frame * stride) + col) % 26) * 8) % 216;
                    var cellBg = 16 + (frame % 2 == 0 ? cellShade : 215 - cellShade);
                    frameBuffer.Append(ESC).Append("[0;38;5;16;48;5;").Append(cellBg).Append('m')
                               .Append(letter);
                }
            }
            else
            {
                frameBuffer.Append(letter, Math.Max(0, cols - 1));
            }
        }

        // Bottom right, over the top of whatever row is there.
        var status = $" {rate,5:0.0} fps  frame {frame}  {(sync ? "sync" : "NOSYNC")}"
                   + $"{(heavy ? " heavy" : string.Empty)}{(mono ? " mono" : string.Empty)}"
                   + $"  stride {stride}  {lastFrameBytes / 1024,4} KB ";
        frameBuffer.Append(ESC).Append('[').Append(rows).Append(';')
                   .Append(Math.Max(1, cols - status.Length)).Append('H')
                   .Append(ESC).Append("[0;37;40m").Append(status);

        if (sync)
            frameBuffer.Append(ESC).Append("[?2026l");

        lastFrameBytes = frameBuffer.Length;
        Write(frameBuffer.ToString());

        frame++;
        framesInWindow++;

        var now = clock.ElapsedTicks;
        var elapsed = (now - windowStart) / (double)Stopwatch.Frequency;

        if (elapsed >= 0.5)
        {
            rate = framesInWindow / elapsed;
            framesInWindow = 0;
            windowStart = now;
        }

        // Paced against a running deadline rather than by sleeping a fixed amount, so a slow frame
        // is absorbed instead of pushing every frame after it later.
        next += interval;
        var remaining = next - clock.ElapsedTicks;

        if (remaining > 0)
            Thread.Sleep(TimeSpan.FromSeconds(remaining / Stopwatch.Frequency));
        else
            next = clock.ElapsedTicks;
    }
}
finally
{
    // Close any update still open before leaving, or a terminal that honours the mode is left
    // holding a frame for ever.
    if (sync)
        Write($"{ESC}[?2026l");

    Write($"{ESC}[0m{ESC}[?25h{ESC}[?1049l");

    if (raisedTimer)
        try { timeEndPeriod(1); } catch { }
}

void Write(string s)
{
    stdout.Write(s);
    stdout.Flush();
}

bool KeyPressed()
{
    try { return Console.KeyAvailable; }
    catch (InvalidOperationException) { return false; }
}

int SafeWidth()
{
    try { return Console.WindowWidth > 0 ? Console.WindowWidth : 80; }
    catch { return 80; }
}

int SafeHeight()
{
    try { return Console.WindowHeight > 0 ? Console.WindowHeight : 24; }
    catch { return 24; }
}

static bool RaiseTimerResolution()
{
    if (!OperatingSystem.IsWindows())
        return false;

    try { return timeBeginPeriod(1) == 0; }
    catch { return false; }
}

static void EnableVirtualTerminal()
{
    if (!OperatingSystem.IsWindows())
        return;

    try
    {
        var handle = GetStdHandle(-11);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return;

        if (!GetConsoleMode(handle, out var mode))
            return;

        SetConsoleMode(handle, mode | 0x0004);   // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
    catch { }
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

[DllImport("winmm.dll", SetLastError = true)]
static extern uint timeBeginPeriod(uint uPeriod);

[DllImport("winmm.dll", SetLastError = true)]
static extern uint timeEndPeriod(uint uPeriod);
