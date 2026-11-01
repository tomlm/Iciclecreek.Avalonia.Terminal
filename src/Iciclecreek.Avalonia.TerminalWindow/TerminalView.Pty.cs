using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using Porta.Pty;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XTerm.Buffer;
using XTerm.Events;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{

    public partial class TerminalView
    {
        public void WaitForExit(int ms) => _ptyConnection?.WaitForExit(ms);

        protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromLogicalTree(e);

            // The remembered top level goes with the tree it belonged to. A re-parented view finds
            // the new one on its next frame.
            _topLevel = null;

            // Nothing left to unwind, and nothing that wants unwinding twice.
            if (_disposed)
                return;

            // Mirror of the guard in OnAttachedToLogicalTree, which already notes that _terminal is null
            // during initial attachment because OnInitialized has not fired yet. Attachment is NOTIFIED in
            // that window, so a handler that re-parents the view on attach detaches it while the emulator
            // still does not exist — and every unsubscribe below then throws.
            //
            // CleanupProcess still runs: a view can have been handed a connection through AttachConnection
            // without ever having been initialised, and that connection still has to be let go.
            if (_terminal == null)
            {
                if (!_suppressCleanupOnDetach)
                    CleanupProcess();
                return;
            }

            UnsubscribeTerminalEvents();

            // A view detached mid-update must not keep the gate closed: a view re-attached inside
            // that window would start out refusing to paint for no reason.
            _atomicUpdate = false;

            if (!_suppressCleanupOnDetach)
                CleanupProcess();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            // Re-entering begins a new motion stream. Its first position is meaningful even when it
            // happens to be the same edge cell as the final event before the pointer left.
            _lastReportedMotion = null;
            ClearHoveredUrl();
        }

        private async Task SendToPtyAsync(string data, CancellationToken ct = default)
        {
            InputSent?.Invoke(this, data);

            // Capture the connection reference locally to avoid any potential race conditions
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(data))
                return;

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-asked after the wait, not just captured before it.
                //
                // The capture above stops the reference changing mid-write, which is a different
                // problem from this one. Waiting on the semaphore is an await, and a queue of
                // keystrokes waiting behind a slow write can sit here across a detach or a relaunch --
                // at which point the captured connection is one the view has handed back to its owner,
                // and writing to it types this view's input into somebody else's process.
                //
                // Dropped rather than redirected to the current connection. Input aimed at a process
                // that is no longer here belongs to nothing: sending it onwards would put half a
                // command line into whatever replaced it.
                if (!ReferenceEquals(_ptyConnection, ptyConnection))
                    return;

                var bytes = Utf8NoBom.GetBytes(data);
                await ptyConnection.WriterStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await ptyConnection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error writing to PTY: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Drive this view from a PTY the CALLER owns, instead of one the view spawns.
        /// </summary>
        /// <remarks>
        /// <para>The view already knows how to render a PTY and report its exit; what it cannot currently do is
        /// take one it did not create. A host that keeps connections alive across UI changes — a pane that is
        /// closed and reopened, a session moved between tabs, a process that must outlive the control showing
        /// it — has to own the <see cref="IPtyConnection"/> itself, and today there is no way to hand it over.</para>
        /// <para>Ownership follows the caller. An attached connection is neither killed NOR disposed when the
        /// view is cleaned up — it is unsubscribed and its reader stopped, which detaches this view without
        /// stopping the process behind it. (Disposing would stop it: closing the pty ends the child on every
        /// platform.) A connection the view spawned through <see cref="LaunchProcess()"/> is killed and disposed
        /// as before. <see cref="DetachConnection"/> does the same thing on demand.</para>
        /// <para>It also makes the exit paths testable. A test can hand the view a connection whose child has
        /// exited but not yet been reaped — the window the EOF/reap handling exists for — and assert what gets
        /// reported, instead of racing a real shell and hoping to land in it.</para>
        /// </remarks>
        public void AttachConnection(IPtyConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            CleanupProcess();
            _externalConnection = true;
            _processCts = new CancellationTokenSource();

            // Same ordering as the spawn path: publish the connection, SUBSCRIBE, then start the reader. An
            // attached connection may already have a live process behind it, so an exit can arrive immediately
            // — subscribing after the reader starts is a window in which it is missed entirely.
            var sessionId = InstallConnection(connection);
            connection.ProcessExited += OnPtyProcessExited;

            // Bytes a detached reader parked are the EARLIEST undelivered output (see
            // PendingHandoverBytes), so they are replayed now, through the same pipeline a read would
            // use, rather than waiting for our reader to get the gate. Anything a stale reader parks
            // after this is still earlier than what our reader reads next, and it replays that before
            // its first read.
            var handover = PendingHandoverBytes.For(connection);
            if (handover.Claim() is { Length: > 0 } parked)
            {
                var replayLatch = true;   // mid-session bytes; readiness must not re-announce
                if (!ConsumeOutputChunk(parked, ref replayLatch, _processCts.Token, sessionId))
                    handover.Unclaim(parked);
            }

            // A thread of its own, not the pool — see ReadLoopAsync for why the read is blocking.
            //
            // No readiness wait here, unlike the spawn path, and that is deliberate. AttachConnection is
            // synchronous and called from the UI thread; blocking it for up to five seconds would freeze the
            // app to protect against losing a few bytes. Subscribing above already removes the part that
            // matters — an exit can no longer be missed — and for an attached connection the caller already
            // owned it, so output from before the attach was never ours to catch.
            // The token is read HERE, not inside the lambda. Read there it is a field access that
            // happens on the new thread whenever it gets scheduled -- and CleanupProcess nulls
            // _processCts, so a relaunch or a close landing in that gap killed the reader with an
            // unobserved NullReferenceException before it had read a byte. Nothing reports that: the
            // task is discarded, so the exception goes nowhere and the terminal simply never shows
            // output.
            var readerToken = _processCts.Token;

            // Called directly: ReadPtyOutputAsync starts the reader's thread itself.
            _readLoopTask = ReadPtyOutputAsync(connection, sessionId, readerToken);
        }

        /// <summary>
        /// Stop following the current connection and hand it back, without stopping the process behind it.
        /// </summary>
        /// <returns>The connection that was detached, or <c>null</c> if none was attached.</returns>
        /// <remarks>
        /// <para>Detaching already happens implicitly — closing the view, or attaching a replacement, does it —
        /// but only as a side effect of cleanup, where it is easy to get wrong. It was wrong here until
        /// recently: cleanup disposed the connection and a comment called that the detach, when disposing is
        /// what ends the child. Giving the operation a name is what makes that mistake visible next time.</para>
        /// <para>Ownership passes to the caller for whatever it returns, including a connection this view
        /// spawned itself — detaching one of those hands over a process the view would otherwise have killed,
        /// so the caller must dispose it when done. The view is left with nothing attached and
        /// <see cref="IsLive"/> false.</para>
        /// <para>Nothing the process says is lost or reordered by moving it to another view, and nothing
        /// it says after this returns is painted here: output whose write had not started when the detach
        /// landed goes to the next owner instead. That holds in both I/O modes. What
        /// <see cref="UseAsyncIo"/> (or any connection whose <see cref="IPtyConnection.SupportsCancellableRead"/>
        /// is true) adds is that the reader stops HERE: in blocking mode it stays parked in its read until
        /// the process next speaks, costing a thread and a read buffer -- though no longer this view -- and
        /// the chunk that wakes it is passed to the next view that attaches.</para>
        /// <para>"The next owner" means the next VIEW. Output already taken off the stream when the detach
        /// lands -- read but not yet written here -- is held for the next view that attaches this
        /// connection, and there is no way for a host to claim it instead. So a host that detaches and then
        /// reads <see cref="IPtyConnection.ReaderStream"/> itself can miss that one chunk in either mode. A
        /// cancellable connection only narrows the window to a chunk that is mid-delivery at the moment of
        /// the detach (or one whose delivery outlasts this method's two-second wait for the reader); a
        /// blocking one adds the chunk that wakes its parked reader, whenever the process next speaks.</para>
        /// </remarks>
        public IPtyConnection? DetachConnection()
        {
            IPtyConnection? connection;
            lock (_exitGate)
            {
                connection = _ptyConnection;
            }

            if (connection is null)
            {
                return null;
            }

            // Marked external BEFORE cleanup, which is what makes cleanup let it live: the same flag the
            // attach path sets, meaning exactly the same thing — this process is somebody else's now.
            _externalConnection = true;
            var readLoop = _readLoopTask;
            _readLoopTask = null;

            // Cleanup clears the session under _terminalLock, the lock the read loop writes under, and
            // the loop asks whether its session is still current inside that same lock. So a chunk is
            // either written before this detach or not here at all: one the loop was holding when the
            // detach landed is parked for the next owner, in order, instead of painted into this view.
            CleanupProcess();

            if (connection.SupportsCancellableRead)
            {
                // CleanupProcess cancelled the token, and a cancellable read honours it while parked
                // -- so the loop is unwinding NOW, having consumed nothing. Waiting for it makes the
                // handover deterministic: when this returns, exactly zero readers hold the stream,
                // and the next owner's first read sees the next byte the process writes. The timeout
                // is a ceiling for a loop mid-chunk, not an expected cost; a loop that outlives it
                // parks its final chunk like a blocking one would, so nothing is lost either way.
                //
                // Outside _terminalLock, deliberately: a loop mid-chunk needs that lock to finish.
                try { readLoop?.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { /* the loop's own exit paths already spoke for it */ }
            }
            else
            {
                // A blocking reader cannot be stopped without closing the stream, which is the thing
                // being handed over, and cancelling _processCts does not touch a blocking read. So the
                // loop stays parked until the process next speaks, and takes that chunk.
                //
                // It no longer races the next owner for it. The two share the connection's read gate
                // (PendingHandoverBytes), so the next owner's reader waits behind this one instead of
                // parking a second read on the same descriptor; this one parks the chunk it took, and
                // the next owner replays it before reading on -- in order, whether it attached before
                // the chunk arrived or after. And the parked thread no longer holds this view: the
                // loop reaches its view only through a lease that cleanup has just revoked.
            }

            return connection;
        }

        /// <summary>
        /// Launch the terminal process with the current Process, ProcessArgs, and StartingDirectory properties. If the process is already running, it will be
        /// terminated and replaced with a new instance using the updated properties. 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <remarks>
        /// A no-op under a XAML designer. The previewer renders live controls and runs the handlers
        /// wired to them, so both the automatic launch in OnLoaded and an explicit call from a
        /// consumer's code reach here inside Visual Studio's and Rider's designers -- and a preview
        /// that rebuilds on every edit would spawn a child process per refresh, none of which the
        /// developer asked for or can see exit. The check sits HERE rather than at the automatic
        /// caller so one guard covers both paths. Design.IsDesignMode is set by the previewer's
        /// entry point before the AppBuilder runs, so it is already true when the first control loads.
        /// </remarks>
        public async Task LaunchProcess()
        {
            if (Design.IsDesignMode)
                return;

            CleanupProcess();
            _externalConnection = false;   // this view owns what it spawns

            // This launch's own source, so the code after the spawn can tell whether it is still the
            // current launch or has been superseded while it was awaited.
            var launchCts = new CancellationTokenSource();

            try
            {
                _processCts = launchCts;
                // NOT reset here: the interlock is armed by InstallConnection, together with the connection
                // itself. Arming it early opens a window in which the OUTGOING connection is still the live one
                // AND the flag is clear, which is the same defect with the operands swapped.

                // Determine the process to launch based on OS if not explicitly set
                string processToLaunch = Process;
                if (string.IsNullOrEmpty(processToLaunch))
                {
                    processToLaunch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
                }

                // Held in a local as well as raised, because the PTY needs a directory that is definitely not
                // null and the field is only non-null by way of what SetAndRaise just did to it.
                var workingDirectory = StartingDirectory ?? Environment.CurrentDirectory;
                SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory, workingDirectory);

                var options = new PtyOptions
                {
                    Name = processToLaunch,
                    Cols = _terminal.Cols,
                    Rows = _terminal.Rows,
                    Cwd = workingDirectory,
                    App = processToLaunch,
                    VerbatimCommandLine = VerbatimCommandLine,
                    UseAsyncIo = UseAsyncIo
                };

                // Merged by the PTY layer into the environment the child would otherwise inherit, so a caller
                // adding one variable does not have to rebuild the rest.
                //
                // TERM is set here because nothing else sets it. The PTY layer does not, and on Windows the
                // environment has none to inherit, so the child was being launched with TERM absent entirely
                // -- which every curses-based program then has to guess around. `ucs-detect` reported this
                // terminal as "vtwin10", which is not something the terminal said: it is blessed's Windows
                // fallback for "no TERM, assume a Win10 console", and it costs the program every capability
                // it would otherwise have used.
                //
                // xterm-256color and not xterm-kitty. TERM is a claim about the WHOLE terminal, and
                // xterm-kitty asserts the keyboard protocol, notifications, text sizing and clipboard as well
                // as the graphics. The keyboard protocol matters most: it changes how applications SEND input,
                // so claiming it without answering risks breaking key handling to win a format negotiation
                // that already falls back correctly.
                var environment = EnvironmentVariables is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(EnvironmentVariables);

                if (!environment.ContainsKey("TERM"))
                    environment["TERM"] = DefaultTermType;

                // COLORTERM alongside it, because TERM cannot carry this. A terminfo entry describes an
                // indexed palette, so xterm-256color says "256 colours" and a program quantises to them --
                // this terminal takes full RGB, and that would be discarding colour it could have shown.
                // It is the first thing consulted by everything that looks, ahead of terminfo entirely.
                if (!environment.ContainsKey("COLORTERM"))
                    environment["COLORTERM"] = DefaultColorTerm;

                options.Environment = environment;


                // Add arguments if provided
                if (ProcessArgs != null && ProcessArgs.Count > 0)
                {
                    options.CommandLine = ProcessArgs.ToArray();
                }

                var spawned = await (SpawnOverride ?? PtyProvider.SpawnAsync)(options, launchCts.Token);

                // Superseded while the spawn was awaited -- disposed, detached from the tree, relaunched, or
                // handed a connection to attach. Each of those ran CleanupProcess, which had no connection
                // to kill yet because this one did not exist, so installing it now would orphan whatever
                // replaced it, or leave a process running behind a view that is gone. An early launch
                // (LaunchBeforeLoaded) makes the window wide enough to land in: a view can be disposed
                // without ever having been shown.
                if (_disposed || !ReferenceEquals(_processCts, launchCts))
                {
                    try
                    {
                        spawned.Kill();
                        spawned.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup errors, as CleanupProcess does
                    }
                    return;
                }

                var spawnedSessionId = InstallConnection(spawned);

                // The emulator can be re-gridded while the spawn is awaited -- for an early launch the first
                // layout usually lands there -- and ArrangeOverride had no connection then to pass the size
                // on to. Without this the process keeps the size it was spawned at while the screen shows
                // another. Both run on the UI thread, so from here on ArrangeOverride sees the connection.
                if (_terminal.Cols != options.Cols || _terminal.Rows != options.Rows)
                {
                    try { spawned.Resize(_terminal.Cols, _terminal.Rows); }
                    catch (Exception ex) { Debug.WriteLine($"[{_instanceId}] Error resizing PTY: {ex.Message}"); }
                }

                // Subscribe to process exit event for reliable exit detection
                spawned.ProcessExited += OnPtyProcessExited;

                // Start reading from the PTY connection, and do not continue until the loop is actually
                // reading. The loop is handed THIS connection so a relaunch cannot redirect it onto the next
                // one — see ReadLoopAsync.
                //
                // The process is already running the moment SpawnAsync returns, so every instant before the
                // first read is a window in which it can write, finish, and have its output discarded. A
                // shell that exits immediately loses EVERYTHING; one that lives loses its opening prompt and
                // banner, which presents as a pane that opened blank.
                //
                // Measured downstream over the same Porta.Pty layer: starting 24 short-lived shells at once
                // lost 23 of 24 outputs entirely while reporting a clean exit 0. It never reproduced on an
                // idle developer machine and was near-total on a contended CI box.
                var readerUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _readLoopTask = Task.Factory.StartNew(
                    () => ReadPtyOutputAsync(spawned, spawnedSessionId, _processCts.Token, readerUp),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();

                // Bounded and never fatal: if the reader cannot start, the terminal behaves exactly as it
                // used to rather than hanging the caller that opened it.
                await Task.WhenAny(readerUp.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A launch that was superseded mid-spawn has nothing to report to: its cancellation is
                // the supersession, and a disposed view's emulator is gone.
                if (_disposed || !ReferenceEquals(_processCts, launchCts))
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var oldMax = MaxScrollback;
                    var oldY = _terminal.Buffer.ViewportY;
                    _terminal.WriteLine($"Error launching process: {ex.Message}\n");
                    RaiseMaxScrollbackChanged(oldMax, MaxScrollback);
                    RaiseViewportYChanged(oldY, _terminal.Buffer.ViewportY);
                });
            }
        }

        /// <summary>
        /// Test seam: replaces <see cref="PtyProvider.SpawnAsync"/> for this view's launches, so a test can
        /// hold a spawn in flight and decide when, and with what, it completes.
        /// </summary>
        internal Func<PtyOptions, CancellationToken, Task<IPtyConnection>>? SpawnOverride { get; set; }

        /// <summary>
        /// Launch the terminal process with the specified parameters, updating the Process, ProcessArgs, and StartingDirectory properties. 
        /// If the process is already running, it will be terminated and replaced with a new instance using the updated properties.
        /// </summary>
        /// <param name="startingDirectory"></param>
        /// <param name="process"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public virtual async Task LaunchProcess(string? startingDirectory, string process, params string[] args)
        {
            StartingDirectory = startingDirectory;
            Process = process;
            ProcessArgs = args ?? Array.Empty<string>();
            await LaunchProcess();
        }

        /// <summary>
        /// Running totals for the read loop, for diagnostics: what came off the pty, how long waiting
        /// for the terminal lock took, and how long <c>Terminal.Write</c> spent parsing it. Times are
        /// in <see cref="System.Diagnostics.Stopwatch"/> ticks. Updated on the read task; read them
        /// from anywhere with <see cref="Interlocked.Read(ref long)"/>.
        /// </summary>
        internal sealed class OutputCounters
        {
            public long Bytes;
            public long Chunks;
            public long LockWaitTicks;
            public long WriteTicks;
        }

        internal OutputCounters Counters { get; } = new();

        /// <summary>Test seam: runs on the delivering thread as a chunk enters
        /// <see cref="ConsumeOutputChunk"/>, before it decides whether anyone is listening.</summary>
        internal Action? BeforeChunkConsumed;

        /// <summary>
        /// Runs one chunk of process output through everything a chunk gets: the sniffer event, the
        /// emulator, scroll-follow, readiness, IME and the repaint.
        /// </summary>
        /// <param name="chunk">The bytes as read. May alias the read loop's buffer, which the next read
        /// overwrites: anything that outlives this call -- the OutputReceived report -- gets a copy, made
        /// here.</param>
        /// <remarks>
        /// Extracted from the read loop so an ATTACH can replay bytes a detached reader parked
        /// (<see cref="PendingHandoverBytes"/>) through the identical pipeline — a second,
        /// almost-the-same delivery path would drift. <paramref name="shellReadyPosted"/> is the
        /// loop's once-per-process latch; a replay passes true, because parked bytes are mid-session
        /// output and must not re-announce readiness. <paramref name="cancellationToken"/> is the
        /// loop's token, which the posted ShellReady callback compares against the CURRENT process's to
        /// refuse stale delivery — a replay passes the current one.
        /// </remarks>
        /// <returns>False, having delivered nothing anywhere, when <paramref name="sessionId"/> is no
        /// longer this view's session; the caller parks the chunk for the next owner. That is asked inside
        /// <c>_terminalLock</c>, which cleanup holds while it clears the session, so a detach cannot land
        /// between the answer and the write.</returns>
        private bool ConsumeOutputChunk(ReadOnlyMemory<byte> chunk, ref bool shellReadyPosted, CancellationToken cancellationToken, long sessionId)
        {
            // Snapshot before write so we can tell whether YBase moved (MaxScrollback is
            // YBase: it grows as output scrolls lines off the top, and drops when the
            // output erases the scrollback; ScrollToBottom only moves ViewportY).
            var oldMax = MaxScrollback;
            var oldY = _terminal.Buffer.ViewportY;

            // Sampled BEFORE the write. Ordering is the subtle part: once _terminal.Write has run
            // YBase has already advanced, so a view that genuinely WAS at the tail reads as
            // not-following and the terminal stops following its own output.
            //
            // Alternate-buffer apps (vim, htop) position their own cursor and the scroll below is
            // skipped for them regardless, so they count as following.
            _followBottom = _isAlternateBuffer || (_autoScroll && _terminal.Buffer.IsAtBottom);

            // Read once: the two halves below must agree on which path reports this chunk, or a
            // flip in between would report it twice or not at all. Guarded so an unsubscribed
            // terminal pays nothing: without it every chunk allocates a closure and queues a
            // dispatcher callback for no subscriber. The ?.Invoke inside each path still covers the
            // race where the last handler unsubscribes before delivery.
            //
            // And the copy is made HERE, by the same decision. It used to be made by the read loop on
            // its own look at OutputReceived, so a handler subscribing between the two looks -- a
            // logger attached to a terminal that is already streaming -- was handed a slice of the
            // loop's buffer, which the next read overwrote before the dispatcher ran the report: the
            // next chunk's bytes under this chunk's SessionId. The read-task path copies too, though
            // it reports synchronously, because OutputReceivedEventArgs promises bytes that do not
            // alias a reused buffer and decodes Output lazily, whenever a handler first asks.
            BeforeChunkConsumed?.Invoke();
            var sniffed = OutputReceived != null;
            var sniffOnReadTask = _outputOnReadTask;
            var reported = sniffed ? new ReadOnlyMemory<byte>(chunk.ToArray()) : default;

            long lockRequested = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_terminalLock)
            {
                // Asked HERE, under the lock, and not only by the loop after its read. Cleanup clears
                // the session holding this lock, so the answer cannot go stale before the write below:
                // asked outside it, a detach landing in between still painted the chunk into the view
                // that had just handed the connection away, and the next owner never saw it.
                if (Interlocked.Read(ref _sessionId) != sessionId)
                    return false;

                // Posted once the chunk is certain to be written here, and BEFORE it is: parsing
                // posts its own UI-thread events -- a title, a bell -- and the dispatcher runs posts
                // in order, so this one is heard first, as it always was. Posted after the write it
                // queued behind them, and a title handler that moved the session to another view
                // made the guard this callback used to carry discard a chunk this view had painted.
                //
                // No such guard now. Only accepted chunks get here, so there is nothing stale to
                // filter: the callback reports a chunk this view DID paint, whatever happens to the
                // session before it runs, and SessionId says which process it came from.
                if (sniffed && !sniffOnReadTask)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { OutputReceived?.Invoke(this, new OutputReceivedEventArgs(reported) { SessionId = sessionId }); }
                        catch { /* a sniffer must never take the app down */ }
                    });
                }

                long lockTaken = System.Diagnostics.Stopwatch.GetTimestamp();

                // Any capture taken before this write describes a buffer that no longer exists
                // once it lands; bumping FIRST means a capture published from inside the write —
                // the ESU handler — carries the new generation and is current on arrival.
                Interlocked.Increment(ref _liveWriteGeneration);

                // Declared for the renderer BEFORE the first byte parses. An application's atomic
                // update only protects from the BSU byte onward — a paint landing between this
                // chunk's arrival and its BSU being reached saw "generation moved, no update open",
                // declined the capture, and read the buffer mid-write: the residual tear. While
                // this flag is up the renderer prefers the last COMPLETE capture over the live
                // buffer; once the write finishes, live is quiescent and serves as before.
                _bufferWriteInProgress = true;

                // The cap on waiting for window queries is per chunk. See OnTerminalWindowInfoRequested.
                _windowInfoTimedOut = false;

                try
                {
                    // Bytes straight through, no UTF-16 round trip. This fixes a real defect, not just
                    // an allocation: decoding each read on its own corrupts any multi-byte sequence the
                    // read boundary happens to split, and pty reads end wherever they end. The parser
                    // carries the partial sequence into the next chunk instead -- which is what
                    // OutputReceivedEventArgs.Bytes has been promising subscribers all along.
                    _terminal.Write(chunk.Span);
                }
                finally
                {
                    _bufferWriteInProgress = false;

                    Interlocked.Add(ref Counters.WriteTicks, System.Diagnostics.Stopwatch.GetTimestamp() - lockTaken);
                    Interlocked.Add(ref Counters.LockWaitTicks, lockTaken - lockRequested);
                    Interlocked.Add(ref Counters.Bytes, chunk.Length);
                    Interlocked.Increment(ref Counters.Chunks);
                }

                // See _inputStartRow. A change of row means the shell drew something new, so the
                // recorded input start is stale — but where the prompt ENDS is not known until the
                // user types, since the prompt may still be arriving.
                int cursorRow = _terminal.Buffer.YBase + _terminal.Buffer.Y;
                if (cursorRow != _lastOutputRow)
                {
                    _lastOutputRow = cursorRow;
                    _inputStartPending = !_semanticPrompt;
                }

                // For output that never declares a frame, the chunk boundary is the closest thing
                // to one: the buffer is quiescent here, still under the lock, on the thread that
                // owns it. Skipped mid-update — a capture there would freeze exactly the half-drawn
                // state this machinery exists to keep off the screen — and throttled, because a
                // chunk boundary is worth at most a paint interval of freshness.
                if (!_atomicUpdate)
                    _frameCapture.PublishThrottled(_terminal, Interlocked.Read(ref _liveWriteGeneration));
            }

            // On the read task the handler runs here, straight through, AFTER the write and outside
            // the lock -- unconditionally, since the session check above already accepted the chunk.
            // Not before the write: that would put host code inside _terminalLock, and a handler that
            // marshals to the UI thread while the UI thread waits for the lock is the deadlock
            // ReaderThreadDeadlockTests exist for. Not before the lock either: a chunk the check then
            // refused would have been reported here AND by the view it went to.
            //
            // The catch matters MORE on this path than on the dispatcher one, and for a different
            // reason: an escaping exception here propagates into the read loop and ends it, leaving a
            // live process with a frozen view and nothing reported.
            if (sniffed && sniffOnReadTask)
            {
                try { OutputReceived?.Invoke(this, new OutputReceivedEventArgs(reported) { SessionId = sessionId }); }
                catch { /* a sniffer must never kill the read loop */ }
            }

            // Signal on the first chunk only. Posting per chunk would keep queueing UI-thread
            // callbacks for the life of the process, which is pure overhead once the shell is
            // long since ready and adds up under high-throughput output.
            if (!shellReadyPosted)
            {
                shellReadyPosted = true;
                Dispatcher.UIThread.Post(() =>
                {
                    // The callback can still be queued when a relaunch swaps the process out
                    // underneath it; the token identifies which process it belongs to.
                    if (_processCts?.Token != cancellationToken)
                        return;

                    ShellReady?.Invoke(this, EventArgs.Empty);
                });
            }

            // Auto-scroll to bottom when new content arrives, but only in normal buffer.
            // Alternate buffer (used by full-screen apps like vim, htop, asciiquarium)
            // handles its own cursor positioning and shouldn't be scrolled.
            if (!_isAlternateBuffer)
            {
                if (_followBottom)
                {
                    _terminal.Buffer.ScrollToBottom();
                }
                else if (!_autoScroll && _terminal.Buffer.ViewportY != oldY)
                {
                    // Gating ScrollToBottom is NOT enough to mean "never auto-scrolls", which is what
                    // this property advertises. The emulator advances ViewportY itself as YBase grows
                    // whenever the view is sitting at the bottom, so with the scroll merely skipped a
                    // terminal with auto-scroll off still tracked the tail exactly — measured at
                    // ViewportY == MaxScrollback after every chunk, indistinguishable from on.
                    //
                    // ScrollToBottom only ever mattered for a view that had been scrolled AWAY, which
                    // is why skipping it looks sufficient and is not. Holding the position here is
                    // what actually hands the viewport to the host.
                    _terminal.Buffer.ViewportY = Math.Min(oldY, MaxScrollback);
                }

                // Read and notified either way: a view parked in the scrollback still needs its
                // scrollbar to learn that the buffer grew underneath it.
                var newY = _terminal.Buffer.ViewportY;
                var newMax = MaxScrollback;

                if (oldMax != newMax || oldY != newY)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (oldMax != newMax)
                            RaisePropertyChanged(MaxScrollbackProperty, oldMax, newMax);
                        if (oldY != newY)
                            RaisePropertyChanged(ViewportYProperty, oldY, newY);
                    });
                }
            }

            // Notify IME of cursor position change after terminal processes data
            NotifyInputMethodCoalesced();

            // Output is the only thing that can start or stop an animation, and the clock is
            // a dispatcher timer, so the decision has to be made on the UI thread. The check
            // behind it is a walk of a list that is empty for a terminal showing text.
            //
            // Coalesced for the same reason as the IME notification above: this is a state SYNC,
            // so N queued calls and one queued call reach the same answer, and only the count
            // differs.
            if (Interlocked.CompareExchange(ref _animationSyncQueued, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref _animationSyncQueued, 0);
                    SyncAnimationClock();
                });
            }

            RequestPaint();
            return true;
        }

        /// <summary>
        /// Tells the IME its cursor rectangle and surrounding text have moved, at most once per
        /// dispatcher drain no matter how many chunks asked.
        /// </summary>
        /// <remarks>
        /// <para>Both notifications say "what you cached is stale, come and re-read it" — they carry
        /// no payload of their own. So N of them queued back to back and one of them queued reach the
        /// identical state, and the only difference is how much UI-thread time is spent getting there.</para>
        /// <para>That difference is the whole bug. This is called once per pty CHUNK, which for a
        /// full-screen animation redrawing every cell is hundreds of times a second — far faster than
        /// the UI thread can retire them, because on Windows NotifyCursorRectangleChanged reaches
        /// IMM32 (ImmSetCandidateWindow), which sends messages and so RE-ENTERS the window procedure
        /// on its way through. The dispatcher queue then grows without bound, the message loop never
        /// gets back to pumping input or painting, and the window goes "Not Responding" while the
        /// child process is still happily writing. Measured with libcaca's cacademo: unresponsive
        /// within about five seconds, responsive indefinitely once coalesced.</para>
        /// <para>The latch is cleared BEFORE the notifications rather than after, which is what makes
        /// coalescing lossless. Cleared after, a chunk landing while the notification runs would see
        /// the latch still set, skip, and then find it cleared with nothing queued — and the IME would
        /// be left reading a line the buffer no longer holds until some later chunk happened along.</para>
        /// </remarks>
        private void NotifyInputMethodCoalesced()
        {
            // Coalescing alone was not enough, and a dump of a frozen window is what said so: the
            // UI thread sat in ImmSetCandidateWindow, reached from Imm32InputMethod.SetCursorRect,
            // while the pty reader was parked in ReadFile with nothing to do. The terminal was not
            // behind on its input at all -- it was spending the UI thread inside the IME.
            //
            // One notification per dispatcher drain is still one IMM32 call per drain, and against
            // a full-screen animation that is tens of blocking calls a second, for a cursor the
            // user is not typing at. So the OUTPUT-driven notification is rate limited as well as
            // coalesced. An IME needs the rectangle to be right when composition starts, not to
            // track a running animation; a tenth of a second is far below anything a person can
            // act on and two orders of magnitude off the flood.
            //
            // Deliberately time-based rather than "only when the cursor moved": a full-screen
            // application moves the cursor on every frame by definition, so a movement test admits
            // exactly the case that hurts and excludes the quiet one it would help.
            // Nothing unfocused can be composing, so nothing unfocused needs telling. This is
            // the free half of the fix: a terminal in a background tab or an unfocused window now
            // costs nothing at all, however hard its process is writing.
            if (!_imeFocused)
                return;

            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _imeNotifiedAt);

            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ImeNotifyInterval)
                return;

            if (Interlocked.CompareExchange(ref _imeNotifyQueued, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _imeNotifiedAt, now);

            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _imeNotifyQueued, 0);

                _inputMethodClient?.NotifyCursorRectangleChanged();

                // And the TEXT around it, which the client advertises support for and was
                // never told about. An IME asks once, caches, and waits to be told -- so it
                // was composing against whatever the line held the first time it looked.
                _inputMethodClient?.NotifySurroundingTextChanged();
            });
        }

        /// <summary>
        /// The view a read loop delivers to, held only while that view's session is the loop's.
        /// </summary>
        /// <remarks>
        /// A blocking read cannot be unparked, so after a detach the loop stays in it until the process
        /// next speaks -- at an idle prompt, indefinitely. While the loop was an instance method it held
        /// the view, its emulator and its scrollback alive for all of that time. The loop is static now
        /// and reaches its view only through this, which <see cref="InstallConnection"/> revokes
        /// whenever the session changes.
        /// </remarks>
        private sealed class ReaderLease
        {
            private volatile TerminalView? _view;

            public ReaderLease(TerminalView view) => _view = view;

            /// <summary>The owning view, or null once the loop's session has ended.</summary>
            public TerminalView? View => _view;

            /// <summary>Whether the loop's session has ended -- asked without the loop's own frame ever
            /// holding the view, which an unoptimised build keeps alive for as long as the frame runs.</summary>
            public bool Revoked => _view is null;

            public void Revoke() => _view = null;
        }

        /// <summary>The lease of the loop reading the current session. Guarded by <see cref="_exitGate"/>.</summary>
        private ReaderLease? _readerLease;

        /// <summary>Starts the read loop for <paramref name="connection"/>, on a thread of its own.</summary>
        /// <param name="connection">The connection the loop reads. See <see cref="ReadLoopAsync"/>.</param>
        /// <param name="sessionId">Identity of <paramref name="connection"/>. See <see cref="ReadLoopAsync"/>.</param>
        /// <remarks>
        /// The thread is started here, from a static method, even when the caller is already on a
        /// thread it started for the purpose. A closure that captured the view, sitting under the
        /// loop's frames for as long as it runs, is exactly the reference <see cref="ReaderLease"/>
        /// exists to drop; the caller's thread finishes as soon as this returns.
        /// </remarks>
        private Task ReadPtyOutputAsync(
            IPtyConnection connection, long sessionId, CancellationToken cancellationToken, TaskCompletionSource? up = null)
        {
            var lease = new ReaderLease(this);
            lock (_exitGate)
            {
                // A loop started for a session that has already ended -- a relaunch or a close landed
                // before its thread was scheduled -- is born revoked, and stops before reading.
                if (_sessionId == sessionId)
                {
                    _readerLease?.Revoke();
                    _readerLease = lease;
                }
                else
                {
                    lease.Revoke();
                }
            }

            return StartReadLoop(lease, connection, sessionId, cancellationToken, up);
        }

        private static Task StartReadLoop(
            ReaderLease lease, IPtyConnection connection, long sessionId, CancellationToken cancellationToken, TaskCompletionSource? up)
            => Task.Factory.StartNew(
                () => ReadLoopAsync(lease, connection, sessionId, cancellationToken, up),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

        /// <summary>Reads <paramref name="connection"/> until it ends or the loop's session does.</summary>
        /// <param name="lease">
        /// How the loop reaches its view, which it holds only while the session is its own. See
        /// <see cref="ReaderLease"/>.
        /// </param>
        /// <param name="connection">
        /// The connection this loop reads, passed in rather than re-read from the field on every
        /// iteration. A read waits for as long as the process stays quiet, and a relaunch during one swaps
        /// the field — so a loop that consults _ptyConnection afterwards can find itself operating on the
        /// NEXT process: waiting on it, reading its exit code, and claiming the exit interlock
        /// <see cref="InstallConnection"/> had just reset for it, which swallows that process's own exit.
        /// </param>
        /// <param name="sessionId">
        /// Identity of <paramref name="connection"/>, stamped on every chunk this loop reports. Passed in for
        /// the same reason the connection is: by the time a chunk reaches a subscriber the view may have moved
        /// on, and the subscriber needs to know which process it is hearing rather than only that one is live.
        /// </param>
        /// <param name="cancellationToken">
        /// The session's token. It ends the loop between reads, and during one only when the connection
        /// supports cancellable reads; a blocking read ends when the stream closes or the process speaks.
        /// </param>
        /// <param name="up">Completed once the loop is running and about to read, for a caller waiting to
        /// know that before it carries on. See the note at the top of the method. Null when nobody is.</param>
        private static async Task ReadLoopAsync(
            ReaderLease lease, IPtyConnection connection, long sessionId, CancellationToken cancellationToken, TaskCompletionSource? up)
        {
            // Raised BEFORE the first read, and deliberately not after one. Signalling after a read would
            // make readiness depend on the process PRODUCING output, so a shell that prints nothing on
            // startup would never signal and every launch would pay the full five-second wait. The guarantee
            // wanted is only that the loop is running and the next thing it does is read.
            //
            // The window this leaves — between the signal and the read — is closed by the caller subscribing
            // to ProcessExited before starting the loop, so an exit landing in it is still seen.
            up?.TrySetResult();

            // One reader at a time per connection, across views -- see PendingHandoverBytes. Held from
            // before the read until what it returned has been delivered or parked, so the order readers
            // pass through it is stream order. A new owner's loop waits here behind a detached view's
            // parked read instead of parking a second read beside it on the same descriptor.
            var handover = PendingHandoverBytes.For(connection);
            var cancellable = connection.SupportsCancellableRead;
            var endOfStream = false;

            try
            {
                var buffer = new byte[0x40000];

                // Local rather than a field: this method runs once per launch, so the flag is
                // per-process by construction and a chunk still in flight from a previous process
                // cannot consume the current one's signal.
                var shellReadyPosted = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Waited for the way the read below is: a blocking loop waits on its own thread,
                    // because an await that yields here would move it onto the pool for good.
                    if (cancellable)
                        await handover.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    else
                        handover.Gate.Wait(cancellationToken);

                    try
                    {
                        // Asked under the gate and before reading: a loop whose session ended while it
                        // waited must not take a chunk off a stream that is no longer its own.
                        if (lease.Revoked)
                            break;

                        // Whatever a detached view's reader parked was read before anything this loop is
                        // about to read, so it goes first.
                        if (handover.Claim() is { } parked)
                        {
                            var replayLatch = true;   // mid-session bytes; readiness must not re-announce
                            if (!Deliver(lease, parked, ref replayLatch, cancellationToken, sessionId))
                            {
                                handover.Unclaim(parked);
                                break;
                            }
                        }

                        // SYNCHRONOUS, on the thread StartNew handed this loop. `await ReadAsync` undid the
                        // LongRunning hint entirely: LongRunning owns a dedicated thread only up to the first
                        // await that YIELDS, and every continuation after that is scheduled on the THREAD POOL.
                        // Worse, the stream underneath is a FileStream opened isAsync: false on Windows, whose
                        // ReadAsync performs no overlapped I/O — it parks a POOL thread in a blocking read for
                        // the whole life of the process, because ConPTY does not signal EOF while the
                        // pseudoconsole is open.
                        //
                        // Measured downstream over the same layer, 24 concurrent short-lived processes on a
                        // 4-vCPU box: time-to-first-output was 137 ms with a dedicated thread and 7546 ms
                        // pooled, and under load the pooled form lost output entirely rather than merely
                        // delaying it. A blocking read on a thread we own cannot be starved, and costs one
                        // thread per terminal — which the pooled form was already costing, minus the scheduling.
                        //
                        // Cancellation is by teardown rather than by token: disposing the connection closes the
                        // stream and the blocking read throws, which the catch below handles.
                        int bytesRead;
                        if (cancellable)
                        {
                            // A connection that PROMISES cancellable reads gets the awaited form: parked
                            // on the pty layer's poller rather than on this thread, and the token lands
                            // while waiting for data -- before read(2) runs -- so a detach unparks the
                            // loop without consuming a byte and without touching the stream it is
                            // handing over. The measured case against `await ReadAsync` (137 ms vs
                            // 7,546 ms, output lost under load) damned SYNC-OVER-ASYNC -- fake async
                            // over a blocking descriptor parking pool threads -- and says nothing about
                            // an event-driven wait, which is what the capability certifies.
                            try
                            {
                                bytesRead = await connection.ReaderStream
                                    .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // A detach or teardown. Nothing was consumed -- that is the guarantee
                                // the capability advertises, and Porta.Pty tests it.
                                break;
                            }
                        }
                        else
                        {
                            bytesRead = connection.ReaderStream.Read(buffer, 0, buffer.Length);
                        }

                        if (bytesRead == 0)
                        {
                            endOfStream = true;
                            break;
                        }

                        // Ownership is asked AGAIN on the other side of the read, because the read blocks
                        // for as long as the process stays quiet -- at an idle prompt, indefinitely -- and a
                        // detach or a relaunch lands in that window routinely. It is asked inside the lock
                        // the write happens under (ConsumeOutputChunk), so a detach cannot slip in between
                        // the answer and the write either.
                        //
                        // A chunk that is no longer this view's is not dropped and not painted here: it is
                        // parked, and the connection's next owner replays it before its own first read.
                        // Under the gate that is always in order, whether that owner attached before this
                        // chunk arrived or after. Then the loop stops, rather than read a second chunk out
                        // of a stream that is not its own.
                        if (!Deliver(lease, buffer.AsMemory(0, bytesRead), ref shellReadyPosted, cancellationToken, sessionId))
                        {
                            handover.Park(buffer.AsSpan(0, bytesRead));
                            break;
                        }
                    }
                    finally
                    {
                        handover.Gate.Release();
                    }
                }

                // Outside the gate: there is nothing left to read, and the reap and the report wait on
                // things another reader has no business queueing behind.
                if (endOfStream && lease.View is { } owner)
                    await owner.OnReadLoopEndOfStreamAsync(connection);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                if (lease.View is { } view)
                    await view.OnReadLoopFailedAsync(connection, ex, sessionId);
            }
        }

        /// <summary>Hands one chunk to the lease's view. False when it is no longer that view's to paint.</summary>
        /// <remarks>Its own method so the view is a local of THIS frame, gone when it returns, rather than
        /// one the loop's state machine would hold across its next read.</remarks>
        private static bool Deliver(
            ReaderLease lease, ReadOnlyMemory<byte> bytes, ref bool shellReadyPosted, CancellationToken cancellationToken, long sessionId)
        {
            if (lease.View is not { } view)
                return false;

            // Passed as read, uncopied: ConsumeOutputChunk copies when, and only when, it reports the
            // chunk, so an unsubscribed view allocates nothing per read at all.
            return view.ConsumeOutputChunk(bytes, ref shellReadyPosted, cancellationToken, sessionId);
        }

        /// <summary>The read loop reached EOF on a connection this view still owns.</summary>
        private async Task OnReadLoopEndOfStreamAsync(IPtyConnection connection)
        {
            // Process has exited — fallback in case OnPtyProcessExited didn't fire first.
            //
            // EOF on the master side means the child closed its end, which can beat the
            // child actually being REAPED — and until it is, ExitCode is still its
            // default 0. Reading it straight away reports a clean exit for a process
            // that failed, whenever this path wins the race against OnPtyProcessExited.
            //
            // Reaping happens BEFORE the interlock is claimed, which does two things:
            // it makes the exit code readable, and it gives OnPtyProcessExited — which
            // carries the code authoritatively — its chance to win the race instead of
            // being locked out by a claim staked before we knew anything. The child is
            // gone by definition, so this returns almost immediately; the timeout is a
            // ceiling for a pathological reap, not an expected cost.
            var reaped = false;
            try { reaped = connection.WaitForExit(ExitReapGraceMs); }
            catch { /* never let reaping be the reason output stops */ }

            // A child that will not reap inside the grace period leaves no trustworthy
            // code, and the one we would otherwise read is 0 — the single wrong answer
            // that reads as SUCCESS. So it is still not reported here.
            //
            // It is NOT abandoned either. Leaving the interlock unclaimed means no
            // ProcessExited is raised AT ALL if the pty layer's own event never fires
            // — and a host that is never told the process ended cannot leave the state
            // it entered when the process started. Trading "no wrong exit code" for
            // "no notification" loses more than it saves; the notification is the part
            // a host cannot reconstruct.
            //
            // The child is dead by definition, so the reap WILL land — the grace period
            // is only a ceiling on how long this READ LOOP waits for it. Hand the wait
            // off, so the loop ends now and the host still hears about it.
            if (!reaped)
            {
                ReapInBackground(connection);
            }

            // TryClaimExit rather than a bare interlock: this loop may have been waiting on a read
            // while a relaunch replaced the connection, in which case the exit it is holding belongs
            // to a process this view has already moved on from.
            if (reaped && TryClaimExit(connection, out var exitSessionId))
                await ReportClaimedExitAsync(connection, exitSessionId, reaped: true);
        }

        /// <summary>The read loop failed reading a connection this view still owned when it asked.</summary>
        private async Task OnReadLoopFailedAsync(IPtyConnection connection, Exception ex, long sessionId)
        {
            // Only speak for the connection this loop was reading, and only while it is still the
            // one the view owns.
            //
            // The interlock alone could not answer that. InstallConnection RESETS it as it
            // publishes a new connection, so a stale loop -- one whose stream was closed out from
            // under it by exactly that replacement, which is how it got here -- read the flag as
            // 0 and took it for permission to speak. It then wrote "Error reading from process"
            // into the terminal belonging to its SUCCESSOR: a relaunch that worked, reporting a
            // failure, describing a process that is already gone.
            //
            // ReferenceEquals is asked first because it is the question that was missing. The
            // interlock stays behind it for its original job: a stream closing after the process
            // has already exited is expected, and not worth a line of red.
            if (!ReferenceEquals(_ptyConnection, connection))
                return;

            // If the process has already exited the stream closing is expected — swallow silently.
            if (_processExitHandled != 0)
                return;

            // A read that FAILS is how a Unix pty says the child is gone: once the slave side closes,
            // read() on the master returns EIO -- an exception here -- rather than the 0 bytes the EOF
            // path waits for. On Linux that is the ORDINARY end of a process. Only printing it leaves the
            // interlock unclaimed and the connection installed, so IsLive stays true and, if the pty
            // layer's own event does not fire, no ProcessExited is raised for a process already dead.
            //
            // So establish whether the child is actually gone before calling this an error.
            var reaped = false;
            try { reaped = connection.WaitForExit(ExitReapGraceMs); }
            catch { /* disposed underneath us; below, that is a failed read like any other */ }

            if (reaped)
            {
                // Not an error at all: the end of a process by the platform's other route, reported as
                // EOF reports it. A false claim means the event got there first, or the view moved on.
                if (TryClaimExit(connection, out var exitSessionId))
                    await ReportClaimedExitAsync(connection, exitSessionId, reaped: true);
                return;
            }

            // Asked again, because that wait is up to a second long and a relaunch or the exit event can
            // land in it. This line is for the connection the view still owns, as above.
            if (!ReferenceEquals(_ptyConnection, connection) || _processExitHandled != 0)
                return;

            // The child is still there, so this really is an I/O failure. The exit is handed off rather
            // than abandoned with it: this loop is ending, and a child that dies later must still be
            // announced, for the same reason the EOF path hands off a slow reap.
            await WriteMessageAsync(TerminalMessageEventArgs.ReadError(ex, sessionId));
            ReapInBackground(connection);
        }

        /// <summary>
        /// Report an exit whose interlock the caller has claimed: the notice first, then the event.
        /// </summary>
        /// <param name="connection">The connection that ended.</param>
        /// <param name="exitSessionId">Its id, as <see cref="TryClaimExit"/> returned it.</param>
        /// <param name="reaped">Whether the child was reaped. If not, the code is reported as unknown:
        /// the one that could be read is the default 0, which would read as success.</param>
        private async Task ReportClaimedExitAsync(IPtyConnection connection, long exitSessionId, bool reaped)
        {
            int? code = null;
            if (reaped)
            {
                try { code = connection.ExitCode; } catch { /* fall through as unknown */ }
            }

            await WriteMessageAsync(TerminalMessageEventArgs.ProcessExited(code, exitSessionId));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProcessExited?.Invoke(this, code is { } c
                    ? new ProcessExitedEventArgs(c) { SessionId = exitSessionId }
                    : ProcessExitedEventArgs.UnknownCode(exitSessionId));
            });
        }

        /// <summary>
        /// Write one of the view's own lines, giving <see cref="WritingMessage"/> its say first.
        /// </summary>
        /// <remarks>
        /// <para>With no handler this is <see cref="WriteOwnLine"/> on the calling thread, exactly as before
        /// the event existed. With one, the handler and the write run together in a single UI-thread
        /// callback: the handler has to finish before the write, or <c>Handled</c> would be racing the line
        /// it means to stop, and doing both in one post keeps lines in the order they were queued -- the read
        /// error ahead of the exit notice that usually follows it.</para>
        /// <para>Every caller has already passed its interlock or ownership check; this only decides what,
        /// if anything, is written.</para>
        /// </remarks>
        private async Task WriteMessageAsync(TerminalMessageEventArgs message)
        {
            if (WritingMessage == null)
            {
                WriteOwnLine(message.Text);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    WritingMessage?.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    // The host's bug should not cost the user the line, nor stop the exit from being
                    // reported -- the callers go on to raise ProcessExited after this returns.
                    Debug.WriteLine($"TerminalView: a WritingMessage handler threw; writing the default line. {DescribeWithoutMessages(ex)}");
                    message.Handled = false;
                    message.Text = message.DefaultText;
                }

                // Asked again here, after the callers' guards, because this callback runs LATER than they did:
                // it was queued behind whatever the UI thread had pending, and a handler is free to relaunch
                // or re-attach. Either installs a new session, and the line would then describe a process the
                // buffer no longer belongs to -- "Process exited" under the prompt of its successor.
                // TerminalControl always subscribes to forward the event, so this is the path most hosts are
                // on, not a corner. The handler still saw the message; its SessionId says whose it was.
                if (!message.Handled && message.SessionId == SessionId)
                    WriteOwnLine(message.Text);
            });
        }

        /// <summary>
        /// Make <paramref name="connection"/> the live one and arm the exit interlock for it, atomically.
        /// Null clears both — the teardown case.
        /// </summary>
        /// <returns>The id minted for <paramref name="connection"/>, to stamp on the events it produces;
        /// 0 when clearing. Returned rather than read back from <see cref="SessionId"/> afterwards, because
        /// by then a relaunch may already have installed the next one.</returns>
        private long InstallConnection(IPtyConnection? connection)
        {
            lock (_exitGate)
            {
                _ptyConnection = connection;
                Interlocked.Exchange(ref _processExitHandled, 0);
                _sessionId = connection is null ? 0 : ++_sessionCounter;

                // The outgoing session's loop loses its view with the session. It may be parked in a
                // read it cannot leave until the process speaks; it must not hold this view meanwhile.
                _readerLease?.Revoke();
                _readerLease = null;

                return _sessionId;
            }
        }

        /// <summary>
        /// Claim the right to report the exit OF THIS CONNECTION. False when somebody already reported it, and
        /// false when the connection is no longer the live one — a stale loop must not speak for its successor.
        /// </summary>
        /// <param name="sessionId">The id of the connection claimed, for stamping the exit event. Read under
        /// the same lock as the claim: taken afterwards it could belong to a connection installed since.</param>
        private bool TryClaimExit(IPtyConnection connection, out long sessionId)
        {
            lock (_exitGate)
            {
                sessionId = 0;
                if (!ReferenceEquals(_ptyConnection, connection)) return false;
                if (Interlocked.Exchange(ref _processExitHandled, 1) != 0) return false;
                sessionId = _sessionId;
                return true;
            }
        }

        /// <summary>
        /// Keep waiting for a child that did not reap inside <see cref="ExitReapGraceMs"/>, off the
        /// read loop, and report the exit when it finally lands.
        /// </summary>
        /// <remarks>
        /// <para>The read loop must not block on this — it is the thing that would otherwise be
        /// pumping output — but the exit still has to be reported, or the host is left believing a
        /// dead process is running.</para>
        /// <para>Claims the same interlock, so if <see cref="OnPtyProcessExited"/> gets there first
        /// with the authoritative code, this stays silent. If the ceiling expires the exit IS still
        /// reported, with <see cref="ProcessExitedEventArgs.ExitCodeKnown"/> false — "ended, outcome
        /// unreadable" is honest, whereas 0 would read as success and silence reads as running.</para>
        /// <para>The connection may be disposed underneath this at any point (a relaunch, a close).
        /// That is not an error worth surfacing: it means the exit is moot.</para>
        /// </remarks>
        private void ReapInBackground(IPtyConnection connection)
        {
            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(ExitReapCeilingMs);
                var reaped = false;

                while (DateTime.UtcNow < deadline)
                {
                    if (Volatile.Read(ref _processExitHandled) != 0) return;   // someone else reported it
                    try
                    {
                        if (connection.WaitForExit(ExitReapPollMs)) { reaped = true; break; }
                    }
                    catch
                    {
                        return;   // disposed / gone — nothing left to report about
                    }
                    await Task.Yield();
                }

                if (!TryClaimExit(connection, out var exitSessionId)) return;

                await ReportClaimedExitAsync(connection, exitSessionId, reaped);
            });
        }

        private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e)
        {
            // Interlocked ensures only one of (event, EOF path, exception path) prints the message.
            // Claims for the connection that raised it, so a late event from a replaced connection cannot
            // speak for its successor either. A null sender predates this and is treated as the live one.
            long exitSessionId;
            if (sender is IPtyConnection origin)
            {
                if (!TryClaimExit(origin, out exitSessionId)) return;
            }
            else
            {
                if (Interlocked.Exchange(ref _processExitHandled, 1) != 0) return;
                exitSessionId = SessionId;
            }

            _ = ReportAsync();

            // The notice first and the event second, as the other two exit paths do; a pty-layer callback
            // cannot be awaited, so the pair runs on without holding it.
            async Task ReportAsync()
            {
                await WriteMessageAsync(TerminalMessageEventArgs.ProcessExited(e.ExitCode, exitSessionId));

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Raise event on UI thread so subscribers can safely update UI
                    var args = new ProcessExitedEventArgs(e.ExitCode) { SessionId = exitSessionId };
                    ProcessExited?.Invoke(this, args);
                });
            }
        }

        private void CleanupProcess()
        {
            _processCts?.Cancel();
            ReleaseImageBitmaps();

            if (_ptyConnection != null)
            {
                try
                {
                    // Unsubscribe from event before cleanup
                    _ptyConnection.ProcessExited -= OnPtyProcessExited;

                    // An ATTACHED connection belongs to its owner: neither killed NOR disposed. Closing or
                    // re-parenting a view must not stop the process behind it, and Dispose does stop it —
                    // disposing without any Kill() leaves the child dead within 300ms on both Windows
                    // (PseudoConsoleConnection) and Unix, where closing the master fd sends SIGHUP to the
                    // foreground process group. An earlier revision of this code disposed unconditionally and
                    // described it as the detach; it was the opposite.
                    //
                    // Detaching needs nothing from Dispose. The unsubscribe above drops this view's event, and
                    // the cancelled _processCts plus the read loop's revoked lease and session check stop the reader.
                    // Disposing an object the view does not own would be wrong even if the process survived it.
                    if (!_externalConnection)
                    {
                        _ptyConnection.Kill();
                        _ptyConnection.Dispose();
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
                finally
                {
                    // Cleared with the flag, under the gate: a loop still unwinding must not find a null
                    // connection paired with a clear flag and conclude it owns the exit.
                    //
                    // And under _terminalLock, which the read loop holds while it checks that its
                    // session is current and writes the chunk. Cleared outside it, the session could end
                    // between that check and the write, and a detaching view painted a chunk that
                    // belonged to the connection's next owner. The UI thread already takes this lock
                    // (ClearScreen, ArrangeOverride), and a write holding it never waits on the UI
                    // thread without a bound -- see OnTerminalWindowInfoRequested.
                    lock (_terminalLock)
                    {
                        InstallConnection(null);
                    }
                }
            }

            _processCts?.Dispose();
            _processCts = null;
        }

    }
}
