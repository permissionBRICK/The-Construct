# T3 pairing forwarding (2026-09-11)

Both the Desktop PowerShell pairing helper and the VS Code pairing script embed
`extension/vm/construct-t3-pairing-base.sh`. On managed VMs it requests/reuses a
host forward for the effective T3 listener. A policy/allowance refusal (CLI exit
7) falls back to a client forward and waits for the client's acknowledgement.
Other host failures remain errors. Pairing uses the returned allocated port,
never the guest port as a guess. Local unmanaged instances retain their configured
origin.

Client links use `localhost`, which is already included in Construct's T3 TLS
certificate. The effective public URL determines whether TLS actually came up;
the HTTPS preference alone is insufficient. Nginx preserves the request Host and
port, so pairing against the forwarded origin also matches T3's DPoP validation.
This does not rewrite shared guest configuration.

The client forwarding protocol is unchanged. Today the VS Code extension must
be connected to handle the request; the companion application being developed
separately can take over that same protocol. Update Construct on the user PC and
retry Link; this helper fix needs no T3 binary rebuild or guest reprovision.

The Windows host allocator also probes candidate ports with an exclusive socket
bind. New host application and SSH forwards skip occupied or Windows-reserved
ports within their configured ranges. Client forwards need no host listener.
This requires a host service update. Existing published ports remain stable;
the probe is not an atomic reservation across the subsequent netsh call.

CI exercises both real generated shell scripts, including Desktop's PowerShell
wrapper, with isolated forwarding/SSH/T3 process doubles. Service tests cover
occupied candidates, reuse after release, exhausted ranges, and a real occupied
socket. These tests do not contact WS009 or restart a live T3 service.
