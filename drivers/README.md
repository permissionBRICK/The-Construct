# drivers/

Hypervisor drivers — the one place that knows how to create, control and locate a VM on
a particular backend. `Load-ConstructDriver.ps1` maps a backend id to its driver file and
dot-sources it into the caller's scope; `hyperv-local/` is the local Hyper-V driver and
this build's only backend (also the default, so an install with no instance registry
behaves exactly as it always has). The extension has the mirror-image dispatch in
`extension/src/drivers/`. The contract — function table, state enum, VM descriptor,
capability flags, how to add a backend, and the Proxmox mapping notes — is documented in
[docs/drivers.md](../docs/drivers.md).
