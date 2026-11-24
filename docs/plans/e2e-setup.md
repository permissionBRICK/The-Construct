# An end-to-end test environment (proposal)

Status: proposal, nothing of it is built. The regular checks (`docs/local-checks.md`) run on one Linux
development VM with every hypervisor, host and network faked. This page lists what a real end-to-end setup
needs and which of today's tests would run there.

## What exists today

- The `e2e` group of `test/run-local-checks.sh`, on the development VM: the engine with the real guest CLI
  (bash, node, curl, python) over HTTPS on SQLite (`HostAdminEndToEndTests`), and the UI in headless
  Chromium on the real engine in fake mode and two real device agents (`ui/tests/e2e`).
- Tests that run only when `CONSTRUCT_E2E_SSH_*` names a guest (`ProvisionGuestE2ETests`,
  `AgentSignInGuestE2ETests`), in the `e2e` group; skipped otherwise.
- The guest rig (`test/e2e/guest-rig.sh`, the `guest` group): one Ubuntu guest, as a child VM on a host or
  under local QEMU, provisioned with the checkout.
- By hand: `test/run-local-windows-checks.ps1` (Windows PowerShell 5.1) and
  `test/windows-license-lifecycle.live.ps1` (an elevated, disposable Hyper-V host).

## What the environment needs

1. **Hosts**, each restorable to a clean snapshot before a run:
   - a Windows host with Hyper-V (a Windows VM with nested virtualization is enough for everything but
     performance), the engine installed by the real host installer;
   - a Proxmox VE host (nested in a VM), the engine installed by its installer.
2. **Clients**: a Windows client VM with the device agent, its tray shell, the UI and the VS Code extension
   installed by `ConstructSetup.exe`; a Linux client with the agent from its install script. A second device
   of the same account for pairing, and a browser for the vault's phone pages.
3. **Guests**: the Ubuntu autoinstall image the guest rig builds, as primaries and children on both hosts;
   a Windows guest for the license reuse path.
4. **Releases**: the release under test and the one before it, packaged as the release workflow does, from a
   private channel the hosts and clients point at, so updates and the Move over run for real.
5. **Network**: the hosts, clients and guests on one isolated network, with the host's forwards, the HTTPS
   shared port and LAN access reachable from the clients, and no route to anything else.
6. **Accounts and secrets**: test-only users, tokens and vault entries, from the key vault or created by the
   run; nothing shared with a production host.
7. **A runner** (for instance `test/e2e/run.sh`): restore the snapshots, install the release, drive each
   scenario through the device agent's local API or the UI (Playwright, as `ui/tests/e2e` does today), collect
   the engines', agents' and guests' logs, and leave nothing running.

Cadence: before a nightly is promoted to stable, and by hand for changes to an installer, an updater, a
driver or the Move over. Not per pull request.

## Scenarios, and the tests that would run there

| Scenario | Covered today by | In the environment |
|---|---|---|
| A host installed, updated from the previous release, and rolled back | `host-installer*.test.ps1`, `host-updater.test.*`, `host-release-installer.test.ps1` (stubbed); `Updates/*` engine tests | the real installers and updater on both hosts |
| A VM created, provisioned, reprovisioned, reinstalled and restored | engine provisioning tests (fake driver, scripted guest); `ui/tests/e2e` (fake engine); `ProvisionGuestE2ETests` (env-gated); the guest rig | on Hyper-V and Proxmox, through the UI |
| The coding agents signed in on a guest | `AgentSignInTests` (scripted); `AgentSignInGuestE2ETests` (env-gated) | on a real guest, through the UI |
| Children with leases, media and the console on Proxmox and Hyper-V | `ProxmoxDriverTests`, `HyperVDriverTests` (recorded commands); `ChildVmJobTests` | real children on both hosts |
| Forwards, the HTTPS shared port and LAN access from a client | `HttpsGatewayTests`, `ForwardTests` (loopback); `construct-expose.test.sh` (stubbed) | across the isolated network |
| Pairing, recovery and the vault's approvals | agent `IdentityTests`, `VaultTests` (in process) | two clients and a phone browser |
| The Move over from an old install | `UpgradeTests` (fixtures of the old formats) | a real old install on the Windows client, moved |
| Backups from a guest to the host and back | `backup-e2e.test.sh` (local engine in fake mode) | real guests and host storage |
| A Windows guest keeps its activation across a reinstall | `windows-license-lifecycle.live.ps1` (by hand) | on the Hyper-V host |
| Windows PowerShell 5.1 behaviour of the shipped scripts | `run-local-windows-checks.ps1` (by hand) | on the Windows client |

The stubbed and in-process tests stay where they are: they find most faults in seconds. The environment adds
what only real hosts show (installers, drivers, networking, updates between releases). The env-gated tests,
the guest rig and the two scripts run by hand would move into its runner, and the local `e2e` group would
remain the quick end-to-end smoke test on the development VM.
