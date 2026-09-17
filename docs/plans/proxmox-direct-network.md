# Proxmox: direct network mode

Status: plan, 2026-09-17. Branch `feat/proxmox-direct-network` (off `feat/proxmox-backend`,
PR #19). Implementation is handed to a T3 Code thread; this document is the brief.

## 1. Goal

A VM on a Proxmox host can be reached **directly** at its own LAN address instead of through the
node's relay ports. The admin picks the host default and whether owners may change it per VM;
optionally the admin gives a VM a fixed IP address. Clients, the provisioner and the agent system
prompt then name the VM's own address on port 22; `construct expose --to host` prints a direct URL
instead of allocating a relay port. Nothing changes for Hyper-V hosts: they stay relay-only, and
the choice is not shown there.

Decisions already taken (do not re-open):

- Proxmox only. Hyper-V keeps the NAT Default Switch and relay forwards; no external switch work.
- Direct mode does **not** imply host forwards. A directly reachable VM has nothing to forward to
  the node; `target=host` forwards are simply not applicable in that mode and are refused with a
  direct URL instead. The `hostForwardsEnabled` / `AllowHostForwards` switches keep their meaning
  for relayed VMs and are untouched.
- Address stability is the admin's choice: a fixed address set in the host admin panel (applied
  through cloud-init), or none, in which case the VM keeps its DHCP lease. Proxmox already pins
  each VM's MAC address in its config, so a DHCP reservation on the LAN also works without any
  Construct involvement; document that.
- Not in scope: a NAT bridge on Proxmox, DNS registration, VM-to-VM isolation, the RAM bar.

## 2. What exists (read these first)

| Piece | Where | Note |
|---|---|---|
| Guest network today | `service/src/Constructd.Proxmox/ProxmoxDriver.cs` (`--net0 virtio,bridge=<Proxmox:Bridge>`, `--ipconfig0 ip=dhcp`, `--agent enabled=1`), `CloudInitSeedBuilder.cs` | one NIC, DHCP on `vmbr0`; the guest's address is read from the QEMU guest agent in `GetEndpointAsync` / `ParseGuestAddress` |
| Relay | `service/src/Constructd.Proxmox/TcpRelayPortForwardManager.cs` | in-process TCP listener on the node per forward; resolves the guest address at connect time; SSH ports 2201+, app ports 2300+ (`ConstructdOptions.SshForwardPorts/AppForwardPorts`) |
| What the client is told | `service/src/Constructd.Api/Endpoints/VmEndpoints.cs` `GET /vms/{name}/endpoint` → `(PublicHost, SshForwardPort, PublicHostFor(vm))`, 409 until the SSH forward exists | the provisioner uses this to stamp `CONSTRUCT_EXTERNAL_HOST` / `CONSTRUCT_EXTERNAL_SSH_PORT` (`Provision-AgentVM.ps1` `Get-ExternalEnvSuffix`, `bin/provision.sh`) and the agent prompt (`config/systemprompt.md` `__AGENT_DNS__`, rendered by `bin/install-ai-tools.sh`, re-rendered by `construct systemprompt`) |
| Forwards | `service/src/Constructd.Api/Endpoints/ForwardEndpoints.cs` (`Policies.VmScoped`; `target=host` gated by `IHostNetworkPolicy.HostForwardsEnabled` and the owner's `AllowHostForwards`), `bin/construct-expose.sh` (remote mode) | |
| Host network config | `service/src/Constructd.Core/Domain/HostConfig.cs` `NetworkConfig(HostForwardsEnabled, DirectAddressReporting)`, defaults in `HostAdminDefaults.cs`, section registered in `Infrastructure/HostConfigValidation.cs` (no validation case yet), read through `Core/Services/HostNetworkPolicy.cs`, surfaced by `GET /host/capabilities` | edited as raw JSON in the extension's config tab (`extension/src/hostadmin.js` `CONFIG_SECTIONS`) |
| Per-VM setting pattern | `service/src/Constructd.Api/Jobs/PrimaryMemorySettings.cs`, `PrimaryCpuSettings.cs` (section `primary-memory:<vm>`, record stamped with `vm.Created`, `ApplyAsync` only when Off, pending value in `VmInventoryProjection` / `Responses.cs`), VM settings routes and the shared modal (`loadVmSettings` / `setVmSettings`, `extension/src/hostadmin.js`, `extension/media/*`), plan `docs/plans/host-vm-settings.md` | copy this pattern |
| Feature flags | `service/src/Constructd.Api/Composition/ReleaseInfo.cs` `ApiFeatures` (Proxmox list lacks `network`), `extension/src/hostadmin.js` `FEATURE_NAMES` | |
| Idle detection | `bin/construct-idle-report.sh` (`ssh-session` = established connections to port 22 inside the guest), host-side "forwards idle" | direct mode has no relay traffic to observe; the guest heartbeat is the only signal, which already works |
| Docs | `docs/proxmox-host.md` §5 Networking, `service/README.md` (Proxmox section, `NetworkConfig` rows), `docs/expose.md`, `docs/remote-host.md` | |

## 3. Design

### 3.1 Host config

Extend `NetworkConfig` (additive, defaults keep today's behaviour):

```
NetworkConfig(
  bool HostForwardsEnabled,        // existing
  bool DirectAddressReporting,     // existing
  string DefaultMode = "relayed",  // "relayed" | "direct": mode for VMs without their own setting
  bool OwnerMaySwitchMode = false) // owners may set their VM's mode; admins always may
```

Validate in `HostConfigValidation` (mode in the set; `direct` refused with `unsupported-on-platform`
when the host is not Proxmox). Expose `defaultMode` and `ownerMaySwitchMode` in
`GET /host/capabilities` next to the existing two.

### 3.2 Per-VM setting

New `VmNetworkSettings` beside `PrimaryMemorySettings`, section `network:<vm>`:

```
Setting(DateTimeOffset Created, string? Mode, string? Address, string? Gateway, string[]? Dns)
```

- `Mode` null = follow the host default. `Address` is CIDR (`203.0.113.50/24`), `Gateway` an IPv4;
  both admin-only and only meaningful in direct mode; `Dns` optional (Proxmox falls back to the
  node's resolver when unset).
- Owner may set `Mode` only when `OwnerMaySwitchMode` is true; admin always. `Address`/`Gateway`/
  `Dns` are admin-only (`Policies.Admin`), with `policy-denied` otherwise.
- Applied like RAM: `ApplyAsync` when the VM is Off, on the next start. It runs
  `qm set <id> --ipconfig0 ip=<cidr>,gw=<gw>` (or `ip=dhcp`) and `--nameserver <list>` (or deletes
  it) through the driver (`IHypervisorDriver` gets one Proxmox-only method or the driver reads the
  setting itself; prefer a small `IGuestNetworkConfigurator` seam implemented by the Proxmox
  driver and `UnsupportedFeaturePlatform` elsewhere). Verify on the fixture that Proxmox
  regenerates the cloud-init drive and re-applies the network config on the next boot after an
  `ipconfig0` change (Proxmox changes the cloud-init instance id with the config; confirm in the
  driver test by asserting the argv, and note in the docs that the setting takes effect on the
  next start).
- Effective mode = `setting.Mode ?? config.DefaultMode`. Effective address in direct mode =
  `setting.Address` (without the prefix) when set, otherwise the guest-agent address.
- Inventory: add `network: { mode, effectiveMode, address, pendingMode, pendingAddress }` to the
  VM projection so the panel can show it.

### 3.3 Endpoint, forwards, relay

- `GET /vms/{name}/endpoint`: in direct mode return `(effectiveAddress, 22, effectiveAddress)`
  as soon as the address is known (guest agent up or fixed address set); 409 `no-address` while
  it is not. Relayed mode unchanged. Allow the route for the VM's own token (`Policies.VmScoped`)
  so the guest can ask where it is reachable (used by 3.4).
- Direct mode allocates **no SSH relay port**: `SshForwardPort` stays null and the relay manager is
  not asked for a listener. Map every reader of `SshForwardPort` (`grep -rn SshForwardPort
  service/src extension/src lib drivers`) and make each one mode-aware; the client's SSH config
  block gets `HostName <address>` / `Port 22`.
- `POST /vms/{name}/forwards` with `target=host` on a direct VM: answer 200 with a forward record
  of `kind: "direct"`, `url: http://<address>:<port>/`, no port allocated, no listener, nothing
  persisted beyond the audit entry. `bin/construct-expose.sh` prints that URL and exits 0.
  `target=client` forwards are untouched (they ride the SSH session).
- Switching a VM from direct to relayed (or back) is a stop/start affair like RAM: forwards that
  exist in the old mode are released at apply time; document it.

### 3.4 Guest side

- The provisioner already stamps `CONSTRUCT_EXTERNAL_HOST` / `CONSTRUCT_EXTERNAL_SSH_PORT` from the
  endpoint route, so a fresh provision in direct mode needs no change beyond the route.
- A mode or address change after provisioning must reach the guest without a reprovision: add a
  small oneshot `construct-endpoint-refresh.service` (installed by `bin/provision.sh` only when
  `CONSTRUCT_SERVICE_URL` is set, next to the idle timer) that at boot calls the endpoint route
  with the VM token, rewrites the two variables in `/etc/construct/config.env` when they differ,
  and runs `construct systemprompt` so the agent prompt says the right address. Reuse the header-
  file curl pattern from `bin/construct-idle-report.sh`; never put the token on a command line.
- `config/systemprompt.md` wording: keep "reachable under the DNS name" but make the sentence
  correct for a bare address (say "address" when the value is numeric).

### 3.5 Extension

- Host admin: a "Network" card on the host page (Proxmox only, feature `network-mode`) with the
  default mode and the owner-may-switch toggle, written through the existing config PUT; the raw
  JSON editor keeps working.
- VM settings modal: a "Network" row: mode (Host default / Relayed / Direct), and for admins
  address, gateway, DNS; shows "applies on next start" like RAM. Owners see the mode selector only
  when the host allows it. Panel VM list shows the effective mode and address.
- `FEATURE_NAMES` gains `network-mode`; `ReleaseInfo` advertises it for Proxmox only.

### 3.6 Docs

`docs/proxmox-host.md` §5 becomes "Relayed or direct": what each mode is, that a direct VM is an
ordinary LAN machine (every port an agent binds on all interfaces is reachable by anyone on the
LAN, no forward, no audit), that the MAC is stable so DHCP reservations work, how to set a fixed
address, and that the setting applies on the next start. `service/README.md`: the new config keys
and the setting; `docs/expose.md`: what `--to host` does in direct mode; `docs/remote-host.md`:
one paragraph.

## 4. Work, in order

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
and the extension tests green.

1. Config: `NetworkConfig` fields, validation, capabilities, defaults; tests in the host-config
   suite.
2. `VmNetworkSettings` + routes (extend the existing VM settings endpoints; do not add a second
   settings modal path), inventory projection fields, fake platform; tests mirroring
   `PrimaryMemorySettings` tests, plus permission tests (owner vs admin, host toggle).
3. Proxmox driver: `IGuestNetworkConfigurator`, `qm set --ipconfig0/--nameserver` argv, apply on
   Off→Start; driver tests on the process-runner fake.
4. Endpoint route + `SshForwardPort` audit + relay allocation skip; forward route `kind: direct`;
   `bin/construct-expose.sh`; tests (`test/construct-expose.test.sh`, endpoint tests).
5. Guest refresh unit in `bin/provision.sh` + script + test (pattern: `test/idle-report.test.sh`).
6. Extension: feature flag, host card, settings row, list column; extension tests.
7. Docs.

## 5. Acceptance

- All suites green on Linux; Windows/Hyper-V behaviour and tests unchanged.
- With `defaultMode: direct` on a Proxmox host, a fresh VM's endpoint is its LAN address on port
  22, its `config.env` and system prompt name that address, `construct expose --to host 3000`
  prints `http://<address>:3000/` without allocating a port, and no relay listener exists for it.
- Setting a fixed address on a stopped VM results in `ipconfig0 ip=<cidr>,gw=<gw>` in its config
  and the endpoint route reports that address after start.
- A Hyper-V host advertises no `network-mode` feature and rejects `defaultMode: direct`.
- Field test (a human, on the test node, not the implementing agent): create a VM in direct mode,
  connect from a PC by address, run an agent, confirm the prompt names the address; then set a
  fixed address, restart, confirm the lease and the endpoint; then flip back to relayed.

## 6. Rules for the implementing agent

- Work on `feat/proxmox-direct-network` in this worktree. Commit in small steps with the repo's
  configured author; never put any other email in commits or files.
- Another thread is implementing host self-update on `feat/proxmox-backend` in the main checkout
  (`/root/repos/construct`). Do not touch that checkout and do not merge or rebase across; the
  human merges.
- Do not run anything against a real host or the test node; suites and fakes are the verification.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation in
  the final report.
