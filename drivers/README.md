# drivers/

Hypervisor drivers — the one place that knows how to create, control and locate a VM on
a particular backend. `Load-ConstructDriver.ps1` maps a backend id to its driver file and
dot-sources it into the caller's scope.

- `hyperv-local/` — the local Hyper-V driver, and the **default**: a missing instance
  registry, a missing entry or an empty `backend` field all resolve here, so an install
  that never heard of instances behaves exactly as it always has.
- `hyperv-remote/` — VMs on somebody else's Hyper-V, driven through the `constructd` HTTP
  API. Built on `lib/AgentVm.Remote.ps1`; user-facing guide in
  [docs/remote-host.md](../docs/remote-host.md).

The extension has the mirror-image dispatch in `extension/src/drivers/`. The contract —
function table, state enum, VM descriptor, capability flags, how to add a backend, the
`hyperv-remote` mapping and the Proxmox mapping notes — is documented in
[docs/drivers.md](../docs/drivers.md).
