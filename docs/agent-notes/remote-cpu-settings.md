# Remote primary CPU defaults and changes

Remote `Auto-Install.ps1` and VS Code's new-remote-VM flow now query
`GET /api/v1/vm-defaults` instead of assuming four vCPUs. The recommendation is
the minimum of host logical CPUs, remaining effective user CPU allowance, the
remaining host CPU budget, and the per-VM limit (including the primary API's
64-vCPU ceiling). An unset budget does not constrain the recommendation.
Explicit `-VmCpuCount` values remain explicit choices. An older service without
this endpoint requires a host update or an explicit installer CPU count.

In **Host administration → VMs → CPU count**, enter a number or `max`. `max`
resolves to the allowed maximum when saved. This stores a pending setting and
does not interrupt the guest. The row shows the pending count. Use **Restart**
for a graceful shutdown and start, or **Start** once the VM is off. An ordinary
in-guest reboot, pause, or saved-state resume does not apply a pending count.

`GET /api/v1/vms/{name}/cpu` reports current/desired counts, pending state, and
limits. `PUT` accepts `{ "cpus": 12 }`. Both are owner/admin operations for
primary VMs; VM tokens cannot use them. Limits always refer to the VM's owner,
even for an administrator. Returning the setting to the current count cancels
the pending change. In-flight lifecycle/configuration work blocks edits.

Settings persist in the existing host configuration store under
`primary-cpu:<name>`, tied to the VM creation timestamp so a new VM with the same
name does not inherit an old setting. No database migration is required. The
`primary-cpu` API feature gates the new panel controls on older hosts.

`LifecycleStart` applies the setting only when Hyper-V confirms Off, before
reserving the next run. The PowerShell driver changes only processor count and
verifies it. The canonical VM CPU field is updated after driver success. On
restart, the old CPU reservation is replaced rather than added to the new one;
the existing RAM/storage restart handling is retained. A changed allowance or
failed hardware update stops the start and preserves the pending request for
correction/retry. Starts performed outside Construct do not apply this setting.

Validation: service tests cover default bounds, available budgets, owner/admin
authorization, pending edits, restart accounting, saved-state resume, changed
allowances, hardware failures, and reused VM names. PowerShell tests exercise
the default resolver and Off-only driver behavior; panel adapter tests cover
the max/count dialog, cancellation, pending display, and explicit restart.
WS009 is not reachable from this workspace; live activation requires updating
the host service and the client's Construct extension.
