#!/usr/bin/env bash
# A guided tour of every emulator feature the demo terminal now hosts.
# Run INSIDE a Demo terminal window: bash tools/demo-tour.sh
# Watch two places: this window (visuals + protocol replies) and the console
# that launched the Demo (notifications + attention requests print there).

ESC=$(printf '\033')
BEL=$(printf '\007')
ST="$ESC"'\'

FIRST_STEP=1
say() {
    if [ "$FIRST_STEP" = 1 ]; then FIRST_STEP=0; else
        printf '\n\033[2m-- press any key for the next step --\033[0m'
        IFS= read -rs -n 1 < /dev/tty
        printf '\r\033[K'
    fi
    printf '\n\033[1m== %s ==\033[0m\n' "$1"
}
pause() { sleep "${1:-1.5}"; }

# Send a probe and print whatever the terminal answers, made visible.
probe() {
    printf '%b' "$1" > /dev/tty
    local reply="" ch
    while IFS= read -rs -t 0.4 -n 1 ch < /dev/tty; do reply+="$ch"; done
    printf '   reply: '; printf '%s' "$reply" | cat -v; echo
}

say "OSC 66 - kitty text sizing (watch the glyphs)"
printf '%b' "${ESC}]66;s=2;Big Heading${ST}\n\n"
printf 'normal text under it\n'
printf '%b' "small: ${ESC}]66;n=1:d=2;half-size text${ST}\n"
printf '%b' "wide:  ${ESC}]66;s=2:w=2;AB${ST}\n\n\n"
pause 2

say "OSC 99 - desktop notifications (check the Demo console)"
printf '%b' "${ESC}]99;;Hello from the tour${ST}"
printf '%b' "${ESC}]99;i=tour:p=title:d=0;Deploy finished${ST}"
printf '%b' "${ESC}]99;i=tour:p=body:d=1;All 3 services are green${ST}"
pause

say "OSC 22 - pointer shapes (watch the mouse cursor over this window)"
for shape in wait pointer text crosshair not-allowed; do
    printf '  %s\n' "$shape"
    printf '%b' "${ESC}]22;${shape}${BEL}"
    pause 1
done
printf '%b' "${ESC}]22;${BEL}"      # reset
printf '  (reset)\n'

say "OSC 52 - clipboard write (then Cmd+V somewhere to verify)"
printf '%b' "${ESC}]52;c;$(printf 'pasted by the tour' | base64)${BEL}"
printf '  wrote: "pasted by the tour"\n'
pause

say "OSC 52 - clipboard read-back (enabled in the DEMO only; off by default)"
probe "${ESC}]52;c;?${BEL}"

say "DECRQM - who answers for what"
printf '  mode 5522 (bracketed paste MIME): '; probe "${ESC}[?5522\$p"
printf '  mode 2026 (synchronized output):  '; probe "${ESC}[?2026\$p"

say "Kitty keyboard - the probe Neovim sends"
probe "${ESC}[?u"

say "OSC 1337 - attention (check the Demo console; on macOS the Dock may bounce)"
printf '%b' "${ESC}]1337;RequestAttention=yes${BEL}"
pause
printf '%b' "${ESC}]1337;RequestAttention=no${BEL}"
printf '  requested, then cancelled\n'

say "OSC 1337 - ReportCellSize"
probe "${ESC}]1337;ReportCellSize${ST}"

say "Mode 5522 - bracketed paste MIME (the finale)"
printf '  Enabling the mode. Press Cmd+V IN THIS WINDOW within 8 seconds;\n'
printf '  instead of pasting text, the terminal ANNOUNCES the paste - raw bytes below:\n'
printf '%b' "${ESC}[?5522h"
raw=""; while IFS= read -rs -t 8 -n 1 ch < /dev/tty; do raw+="$ch"; done
printf '%s' "$raw" | cat -v; echo
printf '%b' "${ESC}[?5522l"
printf '  (mode off again - Cmd+V pastes normally now)\n'

say "done"
printf 'Everything above rode the same pty this shell is on.\n'
