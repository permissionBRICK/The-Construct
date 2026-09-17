#!/usr/bin/env python3
"""Type text into the guest via QMP, mapped for the guest's GERMAN keyboard layout.

Why this exists: `input.sh type` sends US-positional scancodes, which a German-layout
guest renders as different characters (-/ß, :/Ö, \\/#, y<->z ...). The guest clipboard
is also useless once a Remote Utilities / Citrix session is in front (it does not cross
into the remote machine), and SendKeys through run-interactive.ps1 loses the foreground
to its own console window. QMP key events driven by this table are the reliable path -
they behave exactly like physical typing, so they work in nested remote sessions too.

Secrets: pass --file (content is typed, never echoed, file optionally deleted). Only a
character count is printed. Never pass a password via --text (it would land in the
process list and the transcript).

    ./type-de.py --file /tmp/p.txt --delete --then ret
    ./type-de.py --text "ambro-ch" --then tab
"""
import argparse
import os
import socket
import sys
import time

SHIFT = "shift"
ALTGR = "alt_r"

# char -> (qcode, modifier|None) for the German T1 layout on US-positional qcodes.
_PLAIN = {
    " ": "spc", ",": "comma", ".": "dot", "-": "slash", "ß": "minus", "´": "equal",
    "ü": "bracket_left", "+": "bracket_right", "ö": "semicolon", "ä": "apostrophe",
    "#": "backslash", "<": "less", "^": "grave_accent",
}
_SHIFTED = {
    "!": "1", '"': "2", "§": "3", "$": "4", "%": "5", "&": "6", "/": "7", "(": "8",
    ")": "9", "=": "0", "?": "minus", "`": "equal", "Ü": "bracket_left", "*": "bracket_right",
    "Ö": "semicolon", "Ä": "apostrophe", "'": "backslash", ">": "less", "°": "grave_accent",
    ";": "comma", ":": "dot", "_": "slash",
}
_ALTGR = {
    "@": "q", "€": "e", "\\": "minus", "{": "7", "[": "8", "]": "9", "}": "0",
    "~": "bracket_right", "|": "less", "µ": "m", "²": "2", "³": "3",
}


def key_for(ch):
    """Return (qcode, modifier or None) for one character, or None if unsupported."""
    if ch.isdigit():
        return ch, None
    if ch.isalpha() and ch.lower() in "abcdefghijklmnopqrstuvwxyz":
        lower = ch.lower()
        qcode = {"y": "z", "z": "y"}.get(lower, lower)   # German swaps Y and Z
        return qcode, (SHIFT if ch.isupper() else None)
    if ch in _PLAIN:
        return _PLAIN[ch], None
    if ch in _SHIFTED:
        return _SHIFTED[ch], SHIFT
    if ch in _ALTGR:
        return _ALTGR[ch], ALTGR
    return None


def send(sock_path, events):
    payload = (
        '{"execute":"qmp_capabilities"}\n'
        '{"execute":"input-send-event","arguments":{"events":[%s]}}\n' % ",".join(events)
    )
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.connect(sock_path)
        s.sendall(payload.encode())
        time.sleep(0.02)


def key_events(qcode, down):
    return '{"type":"key","data":{"down":%s,"key":{"type":"qcode","data":"%s"}}}' % (
        "true" if down else "false", qcode)


def type_text(sock_path, text, delay, mod_delay):
    unsupported = sorted({c for c in text if key_for(c) is None})
    if unsupported:
        raise SystemExit("type-de: unsupported character(s): %s" % " ".join(unsupported))
    for ch in text:
        qcode, mod = key_for(ch)
        # A modifier MUST be pressed in its own QMP round-trip and held for a moment:
        # remote-session keyboard hooks (Remote Utilities, Citrix HTML5) sample the
        # modifier state asynchronously and silently drop it when press+key arrive in
        # one batch - shifted characters then degrade to their unshifted twin
        # ('A'->'a', '!'->'1', '?'->'ss'), which looks like a wrong password.
        if mod:
            send(sock_path, [key_events(mod, True)])
            time.sleep(mod_delay)
        send(sock_path, [key_events(qcode, True), key_events(qcode, False)])
        if mod:
            time.sleep(mod_delay)
            send(sock_path, [key_events(mod, False)])
        time.sleep(delay)


def press(sock_path, qcode, times, delay):
    for _ in range(times):
        send(sock_path, [key_events(qcode, True), key_events(qcode, False)])
        time.sleep(delay)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", help="file whose content is typed (secrets)")
    ap.add_argument("--text", help="literal text to type (non-secrets only)")
    ap.add_argument("--then", help="extra qcode pressed afterwards, e.g. ret / tab")
    ap.add_argument("--delete", action="store_true", help="delete --file after typing")
    ap.add_argument("--delay", type=float, default=0.05,
                    help="seconds between characters (remote sessions drop keys when faster)")
    ap.add_argument("--mod-delay", type=float, default=0.09,
                    help="hold time for shift/altgr before and after the key (remote relays "
                         "need this or the modifier is lost)")
    ap.add_argument("--clear", type=int, default=0, metavar="N",
                    help="press End then N times BackSpace first (ctrl+a does not clear "
                         "every remote dialog field)")
    ap.add_argument("--qmp", default=os.environ.get(
        "QMP_SOCK", "/opt/winvm/run/winvm-win11.qmp"))
    args = ap.parse_args()

    if args.clear:
        send(args.qmp, [key_events("end", True), key_events("end", False)])
        time.sleep(0.1)
        press(args.qmp, "backspace", args.clear, 0.03)

    if args.file:
        with open(args.file, encoding="utf-8") as handle:
            text = handle.read().rstrip("\r\n")
        if args.delete:
            os.unlink(args.file)
    elif args.text is not None:
        text = args.text
    else:
        raise SystemExit("type-de: --file or --text required")

    type_text(args.qmp, text, args.delay, args.mod_delay)
    if args.then:
        send(args.qmp, [key_events(args.then, True), key_events(args.then, False)])
    print("TYPED chars=%d%s" % (len(text), " then=" + args.then if args.then else ""))


if __name__ == "__main__":
    main()
