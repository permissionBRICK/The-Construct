# The URL contains a client port. expose --close expects an id or a VM port.
# Resolve the exact advertised console forward, including non-default gateway ports.
id=$(construct expose --list | awk -v port={{clientPort}} '$3 == "client" && $4 == "open" && $5 == "Guest" && $6 == "console" && $NF ~ (":" port "(/|$)") { print $1; exit }')
[ -n "$id" ] || exit 1
construct expose --close "$id"
