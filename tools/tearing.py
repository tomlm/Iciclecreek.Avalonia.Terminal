#!/usr/bin/env python3
"""Show what synchronized output (DEC private mode 2026) is for.

Draws a full-screen frame in pieces, with a few milliseconds between them, which is
what a TUI redraw looks like under load. A renderer that paints between the pieces
shows a frame half old and half new.

    python3 tools/tearing.py            # no synchronization: watch it assemble in bands
    python3 tools/tearing.py --sync     # synchronized: whole frames only

Run it inside a terminal built from this repo. The two runs are the same program one
escape sequence apart, so anything you see between them is the renderer's doing.

Worth trying deliberately: interrupt the --sync run with Ctrl-C partway through a
frame. The display catches up rather than staying frozen, which is the 150 ms
timeout in TerminalView doing its job -- an application that begins an update and
then dies must not be able to freeze the terminal.
"""
import sys, time, shutil

ESC = "\x1b"
SYNC = "--sync" in sys.argv
FRAMES = 30
BANDS = 14          # pieces per frame — each one is a chance to be caught mid-draw
BAND_PAUSE = 0.004  # the slow redraw a real TUI has under load

cols, rows = shutil.get_terminal_size((80, 24))
rows = max(6, rows - 1)

# Two clearly different frames, so a half-drawn one is unmistakable.
PALETTES = [
    (196, 202, 208, 214),   # reds and oranges
    (21, 27, 33, 39),       # blues
]

def band_rows(band):
    step = max(1, rows // BANDS)
    start = band * step
    return range(start, min(rows, start + step))

out = sys.stdout.write

try:
    out(f"{ESC}[?1049h")          # alternate screen, like a real TUI
    out(f"{ESC}[?25l")            # hide the cursor

    for frame in range(FRAMES):
        colours = PALETTES[frame % 2]
        label = "SYNCHRONIZED  (whole frames)" if SYNC else "NOT SYNCHRONIZED  (watch it tear)"

        if SYNC:
            out(f"{ESC}[?2026h")  # begin atomic update

        for band in range(BANDS):
            for r in band_rows(band):
                colour = colours[r % len(colours)]
                out(f"{ESC}[{r + 1};1H{ESC}[48;5;{colour}m{' ' * cols}{ESC}[0m")

            out(f"{ESC}[1;3H{ESC}[48;5;{colours[0]}m{ESC}[97m {label}  frame {frame + 1}/{FRAMES} {ESC}[0m")
            sys.stdout.flush()
            time.sleep(BAND_PAUSE)   # <- the window a renderer can paint into

        if SYNC:
            out(f"{ESC}[?2026l")  # end: now the frame may be shown

        sys.stdout.flush()
        time.sleep(0.05)

finally:
    out(f"{ESC}[?25h")
    out(f"{ESC}[?1049l")
    sys.stdout.flush()
