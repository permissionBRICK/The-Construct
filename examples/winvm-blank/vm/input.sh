#!/usr/bin/env bash
# Send synthetic mouse/keyboard input to the guest via QMP (usb-tablet = absolute
# coordinates, so screenshot pixels map 1:1 at the configured resolution).
#
#   ./input.sh click <x> <y>          left click at absolute screen position
#   ./input.sh move  <x> <y>          move pointer only
#   ./input.sh type  "text"           type an ASCII string
#   ./input.sh key   <qcode>[ ...]    press key(s), e.g. ret, esc, ctrl-a
#
# Used to drive GUI apps that expose nothing over UIA (Electron apps such as the
# ChatGPT/Codex desktop client).
set -euo pipefail
cd "$(dirname "$0")"
. ./config.sh

XMAX=32767
qmp_raw() { printf '{"execute":"qmp_capabilities"}\n%s\n' "$1" | socat - "UNIX-CONNECT:$QMP_SOCK" >/dev/null; }

abs() { # x y -> qmp abs event pair
    local x=$1 y=$2
    local ax=$(( x * XMAX / ${VM_XRES:-1920} ))
    local ay=$(( y * XMAX / ${VM_YRES:-1080} ))
    printf '{"type":"abs","data":{"axis":"x","value":%d}},{"type":"abs","data":{"axis":"y","value":%d}}' "$ax" "$ay"
}

send_events() { qmp_raw "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":[$1]}}"; }

# qcode for a printable char; returns "qcode|shift"
qcode_of() {
    local c="$1"
    case "$c" in
        [a-z]) echo "$c|0" ;;
        [A-Z]) echo "$(printf '%s' "$c" | tr 'A-Z' 'a-z')|1" ;;
        [0-9]) echo "$c|0" ;;
        ' ') echo "spc|0" ;;
        '.') echo "dot|0" ;;
        ',') echo "comma|0" ;;
        '-') echo "minus|0" ;;
        '_') echo "minus|1" ;;
        '/') echo "slash|0" ;;
        '\') echo "backslash|0" ;;
        ':') echo "semicolon|1" ;;
        ';') echo "semicolon|0" ;;
        "'") echo "apostrophe|0" ;;
        '"') echo "apostrophe|1" ;;
        '=') echo "equal|0" ;;
        '+') echo "equal|1" ;;
        '(') echo "9|1" ;;
        ')') echo "0|1" ;;
        '?') echo "slash|1" ;;
        '!') echo "1|1" ;;
        '*') echo "8|1" ;;
        '&') echo "7|1" ;;
        '%') echo "5|1" ;;
        '$') echo "4|1" ;;
        '#') echo "3|1" ;;
        '@') echo "2|1" ;;
        '<') echo "comma|1" ;;
        '>') echo "dot|1" ;;
        '[') echo "bracket_left|0" ;;
        ']') echo "bracket_right|0" ;;
        *) echo "" ;;
    esac
}

case "${1:-}" in
    move)  send_events "$(abs "$2" "$3")" ;;
    click)
        send_events "$(abs "$2" "$3")"
        sleep 0.1
        send_events '{"type":"btn","data":{"down":true,"button":"left"}}'
        sleep 0.05
        send_events '{"type":"btn","data":{"down":false,"button":"left"}}'
        ;;
    rclick)
        send_events "$(abs "$2" "$3")"
        sleep 0.1
        send_events '{"type":"btn","data":{"down":true,"button":"right"}}'
        sleep 0.05
        send_events '{"type":"btn","data":{"down":false,"button":"right"}}'
        ;;
    scroll)   # scroll <x> <y> <up|down> [ticks]
              # Long TÜV info dialogs only offer their close button at the very
              # bottom, so paging through them is a routine step.
        x="$2"; y="$3"; dir="${4:-down}"; ticks="${5:-5}"
        button="wheel-$dir"
        send_events "$(abs "$x" "$y")"
        sleep 0.1
        for _ in $(seq 1 "$ticks"); do
            send_events "{\"type\":\"btn\",\"data\":{\"down\":true,\"button\":\"$button\"}}"
            send_events "{\"type\":\"btn\",\"data\":{\"down\":false,\"button\":\"$button\"}}"
            sleep 0.08
        done
        ;;
    key)
        shift
        for k in "$@"; do
            if [[ "$k" == *-* ]]; then   # modifier combo, e.g. ctrl-a
                mod="${k%%-*}"; base="${k##*-}"
                send_events "{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"$mod\"}}},{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"$base\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"$base\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"$mod\"}}}"
            else
                send_events "{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"$k\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"$k\"}}}"
            fi
            sleep 0.03
        done
        ;;
    type)
        text="$2"
        for (( i=0; i<${#text}; i++ )); do
            ch="${text:$i:1}"
            spec="$(qcode_of "$ch")"
            [ -z "$spec" ] && continue
            q="${spec%%|*}"; sh="${spec##*|}"
            if [ "$sh" = "1" ]; then
                send_events "{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"shift\"}}},{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"$q\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"$q\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"shift\"}}}"
            else
                send_events "{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"$q\"}}},{\"type\":\"key\",\"data\":{\"down\":false,\"key\":{\"type\":\"qcode\",\"data\":\"$q\"}}}"
            fi
            sleep 0.02
        done
        ;;
    *) sed -n '2,12p' "$0"; exit 1 ;;
esac
