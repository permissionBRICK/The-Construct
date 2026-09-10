# Guided local-to-host conversion

Source: Christoph's requested flow and agreed design, 2026-09-10, haus-vm.

In the connected local VM's Settings, **Make this PC a Construct host…** opens one
prefilled review page. The original Windows identity becomes the host admin, even
if UAC is approved with another administrator's account. The editable LAN/VPN
address prefers the PC's DNS name and falls back to a LAN IPv4 address when the
guest cannot resolve that name. Keeping the host awake on AC is an upfront option;
unchecked preserves the current power plan. No inputs are requested after UAC.

The option is available only in a Windows UI extension attached to the selected
`hyperv-local` instance. This initial flow adopts that one VM. Other users are
added afterward through Host administration and receive their own VMs; adopting
the existing VM does not share its contents.

## Installation and adoption

`extension/src/hostconversion.js` captures the target and verifies guest SSH before
launching `service/host/ConvertTo-ConstructHost.ps1` in an elevated, visible,
noninteractive PowerShell console. Cancellation and errors return to the UI.
The console outlives the VS Code window; a persisted handoff lets the extension
finish connecting after it is reopened.

The installer validates the selected Hyper-V VM against the SSH destination and
guest machine ID. It installs a verified, self-contained `host-<commit>` release
under `%ProgramData%\ConstructHost\scripts`, with the executable under
`scripts\service\publish` and database under `ConstructHost\data`. These protected
service files are separate from the existing client checkout. The package includes
`Create-AgentVM.ps1`, which a freshly installed standalone host needs for new VMs.

The service uses the VM's existing switch. The Ubuntu source URL and official
SHA256 are discovered automatically; a matching local source ISO is reused after
verification. Native ISO tooling is prepared, while building media is deferred
until it is needed. No WSL installation or WSL platform features are required.

The offline `constructd admin vms adopt ... --json` command registers an existing
running VM using its measured hardware and Hyper-V GUID as incarnation, allocates
its public SSH forward, and issues a primary VM token. It does not call VM create,
delete, stop, save, rename or provisioning. New adoptions are private with idle
shutdown disabled, preserving the running local workload. Existing records with a
different owner or incarnation are refused. Repeating the command resumes the same
adoption and reuses its port; token rotation repairs an interrupted guest handoff.
Run this command only through the conversion coordinator with constructd stopped.

`bin/adopt-host.py` receives the guest credential over the existing verified SSH
connection's stdin. It verifies HTTPS access to the VM's host identity endpoint
before replacing the token, certificate, and selected config keys. It refreshes
the Construct CLI helpers and enables the activity heartbeat timer. Application
services, including T3 Code, remain running. Ordinary failures restore the prior
guest files; no full reprovision occurs.

The initiating client retains its direct SSH address, alias, key and config branch.
Only management switches to `hyperv-remote`; other clients obtain the public SSH
forward from the service. Before switching the registry, the extension checks the
new admin credential and the VM state. The credential travels back encrypted to a
per-conversion RSA key held in VS Code SecretStorage, then is stored in SecretStorage
and the existing per-user DPAPI store used by PowerShell lifecycle commands.

## Recovery and limits

An existing host installation is refused; this is not a replacement for host
updates or an arbitrary Hyper-V import tool. A protected conversion journal records
the owner, VM GUID and installation state. Retries keep the same conversion ID.
A host-installed retry uses the original address. Failed client enrollment leaves
the original instance configuration intact and retains the encrypted handoff for
retry. No failure path deletes the VM or its disks.

The defaults publish on the existing LAN/VPN (API 7462, SSH 2201–2299, application
forwards 2300–2999). This flow does not configure public internet routing or DNS.
The published address must resolve to the Windows host and be usable by the guest.
Only a single-switch, running Construct primary with its saved root SSH key is
accepted. A live service or an unrelated conversion is never overwritten.

## Verification

Manual local commands (no new GitHub workflow):

```text
node --test extension/test/hostconversion.test.js
python3 test/host-conversion.test.py
pwsh -NoProfile -File test/host-conversion.test.ps1
dotnet test service/Constructd.sln -c Release
node extension/test/ui-smoke.js
```

Coverage includes ownership and incarnation conflicts, absent/off VMs, repeated
adoption, forward reuse, primary token rotation, guest identity/HTTPS refusal,
guest-file rollback, target changes during installation, preserved SSH identity,
Windows argument quoting, atomic handoff files, safe package extraction, and RSA.
Windows PowerShell 5.1 checks also run through STANDPC's relay, including refusal
to overwrite its already-running host. This does not constitute a full conversion
of a fresh local Windows installation; that end-to-end user test is still required.

IP-address certificates use an IP subject alternative name, following
[Microsoft's certificate example 9](https://learn.microsoft.com/powershell/module/pki/new-selfsignedcertificate),
so the Linux guest can verify the host when an IPv4 address is used.
