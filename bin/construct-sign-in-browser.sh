#!/bin/sh
# BROWSER for a coding agent's sign-in (bin/construct-agent-sign-in.py, design 11.8): the link the CLI would open
# goes to the sign-in, which reports it to the client, instead of to a browser this VM does not have.
[ -n "${CONSTRUCT_SIGN_IN_LINKS:-}" ] && [ -n "${1:-}" ] || exit 1
printf '%s\n' "$1" >>"$CONSTRUCT_SIGN_IN_LINKS"
