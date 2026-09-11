set -eu
export CONSTRUCT_VM_PORT_BASE={{port}}
export CONSTRUCT_VM_PORT_COUNT={{count}}
CONSTRUCT_SHIM_B64={{shim}}
export CONSTRUCT_SHIM_B64
CONSTRUCT_ENABLE_B64={{enable}}
f=$(mktemp) && printf %s "$CONSTRUCT_ENABLE_B64" | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc