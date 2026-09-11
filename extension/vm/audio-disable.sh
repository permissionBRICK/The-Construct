set -eu
export CONSTRUCT_TUNNEL_SELF_PORT={{self}}
export CONSTRUCT_VM_PORT_BASE={{port}}
export CONSTRUCT_VM_PORT_COUNT={{count}}
CONSTRUCT_DISABLE_B64={{disable}}
f=$(mktemp) && printf %s "$CONSTRUCT_DISABLE_B64" | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc