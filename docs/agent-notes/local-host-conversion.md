# Guided local-to-host conversion

Source: Christoph's requested flow and agreed design, 2026-09-10, haus-vm.

In the connected local VM's Settings, **Make this PC a Construct host…** opens one
prefilled review page. The original Windows identity becomes the host admin, even
if UAC is approved with another administrator's account. The editable LAN/VPN
address prefers the PC's DNS name and falls back to a LAN IPv4 address when the
guest cannot resolve that name. Keeping the host awake on AC is an upfront option;
unchecked preserves the current power plan. No setup inputs are requested after
UAC. On failure the visible console retains the error until a key is pressed;
successful conversions close automatically. Redirected/headless callers do not
pause, and the conversion lock is released before the error console waits.

The option is available only in a Windows UI extension attached to the selected
`hyperv-local` instance. This initial flow adopts that one VM. Other users are
added afterward through Host administration and receive their own VMs; adopting
the existing VM does not share its contents.

Find it in the **Construct panel → gear → Share this PC as a host** section,
not VS Code's global settings. Installed extensions can lack access to the
proposed `vscode.env.remoteAuthority` API. The client now falls back to the
stable remote workspace URI authority to identify the connected VM; otherwise
the old detection incorrectly hid this section even in Remote-SSH windows.
The fallback requires one unambiguous authority and never guesses a host from
`remoteName` alone. This correction requires a client update and window reload;
the published host package is unchanged.

## Installation and adoption

`extension/src/hostconversion.js` captures the target and verifies guest SSH before
launching `service/host/ConvertTo-ConstructHost.ps1` in an elevated, visible,
noninteractive PowerShell console. Cancellation and errors return to the UI.
The console outlives the VS Code window; a persisted handoff lets the extension
finish connecting after it is reopened.

The installer validates the selected Hyper-V VM against the SSH guest's saved
machine ID and SMBIOS UUID. The UUID must match `BIOSGUID` in that VM's realized
`Msvm_VirtualSystemSettingData` record (not its management ID or a snapshot).
Both checks run again immediately before adoption. It installs a verified,
self-contained `host-<commit>` release
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
pwsh -NoProfile -File test/host-conversion-identity.test.ps1
pwsh -NoProfile -File test/host-conversion-source.test.ps1
pwsh -NoProfile -File test/host-conversion-transport.test.ps1
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

The implementation is published in host release
`host-73b3f01c8fe2c30d3ba85930d44b2e019b5b96e7`; the existing host release workflow
completed successfully (run `34518500368`). Local validation passed 322 UI smoke
checks, 368 host installer checks, four client conversion tests, four guest
enrollment tests, and six adoption cases including SQLite persistence. Native
STANDPC PowerShell `5.1.26100.9168` passed the manual conversion checks and the
existing-host refusal. Its service was preserved. main-pc's relay was unavailable;
activate the new client with Update Construct there, then run the guided conversion
on its local VM. Existing installed hosts do not need conversion.

IP-address certificates use an IP subject alternative name, following
[Microsoft's certificate example 9](https://learn.microsoft.com/powershell/module/pki/new-selfsignedcertificate),
so the Linux guest can verify the host when an IPv4 address is used.

WS009 conversion diagnosis (2026-09-10): its non-elevated Windows relay is
`work-pc`. The saved request failed before installation because Hyper-V returned
an empty `IPAddresses` array although the selected guest answered SSH. The guest
KVP daemon was inactive. Direct CIM and SSH reads confirmed that Hyper-V's
`BIOSGUID` and Linux `/sys/class/dmi/id/product_uuid` match. Conversion now uses
that identity instead of treating absent KVP IP reporting as a different VM;
the strict SSH host-key check and saved machine ID remain required. No KVP
daemon or host setting change is needed for this identity check.
The fixed verifier passed against the live WS009 guest from the non-admin relay
with the IP list still empty. Its installed client coordinator was updated after
checking the old file hash, with a `.before-identity-fix` backup. This was a
read-only identity test: the VM remained running and no host was installed.

Ubuntu checksum follow-up: PowerShell returns `SHA256SUMS` as `byte[]` when the
HTTP response lacks a text content type. The old regex therefore saw decimal
byte values rather than checksum lines. Conversion now decodes the body and
selects the newest matching server ISO directly from the checksum catalog,
keeping filename and hash together. The ordinary installer also decodes byte
responses before checksum lookup. Local fixtures cover text/bytes/BOM, numeric
version ordering, exact filenames and conflicting hashes; live metadata checks
for 22.04 and 24.04 pass without downloading either ISO. The error-console key
wait was verified with a terminal and with redirected input.

WS009's relay was removed by Defender before these follow-up changes could be
applied there. Use Update Construct and retry conversion; do not claim the full
conversion has passed until that user test succeeds.

Enrollment follow-up (2026-09-10): WS009 now reaches adoption after the host
installer succeeds, then reports a generic SSH/enrollment failure. The coordinator
was writing its Unicode JSON payload through `Process.StandardInput.Write`, which
uses the Windows console code page in .NET Framework. The bundled shell helpers
contain non-ASCII characters; a CP850 regression reproduces corrupted UTF-8. The
coordinator now writes UTF-8 bytes directly to stdin, explicitly decodes UTF-8
output, and includes bounded stderr in errors after removing enrollment tokens.
The guest provides fixed diagnostics for encoding/JSON, DNS, timeout, refusal,
TLS, HTTP authentication, and systemd failures without printing exception payloads.

On an installed-host retry, guest enrollment code comes from the current client
coordinator checkout, so Update Construct picks up these fixes even though the
protected host release is intentionally not reinstalled. The original journal
and VM remain in place. Local tests cover OEM/UTF-16/BOM defaults, error redaction,
and rollback; the device-specific cause is not confirmed until WS009 retries or
provides the newly visible diagnostic. Its portable SSH endpoint was not yet
listening during this investigation.

The subsequent WS009 retry surfaced HTTP 401 from the identity endpoint. This
was a separate conversion bug: `adopt-host.py` sent its scoped VM credential as
`Authorization: Bearer`, but Constructd reserves Bearer for user tokens and
requires `Authorization: VmToken` for guest credentials. The existing guest
CLI helpers already use VmToken. Enrollment now uses that scheme and records
it explicitly in the guest config, replacing any stale override. Error redaction
covers both authentication schemes.

`GuestEnrollmentTests` runs the production Python verifier against the real API
and authentication handlers over loopback HTTPS with SQLite storage and a fake
hypervisor. It reproduced the exact HTTP 401 before the header correction;
afterward initial adoption and retry enrollment succeed, while the rotated old
token still receives 401. No authentication checks were relaxed. Run locally with:
`dotnet test service/tests/Constructd.Tests/Constructd.Tests.csproj -c Release --filter FullyQualifiedName~GuestEnrollmentTests`.
The prior guest unit tests bypassed HTTPS verification and therefore missed this
contract mismatch. WS009 still needs Update Construct and a conversion retry;
its already-installed service does not require replacement for this fix.

The next WS009 handoff reached the VS Code `/whoami` check, then failed with Node's
certificate-chain error. The result was already successful, so retry goes directly
to client completion. VS Code's default `http.proxySupport=override` replaces custom
HTTPS agents for hostnames even with a DIRECT proxy resolution, discarding the
Construct certificate-pin gate. The earlier TLS tests used 127.0.0.1, which VS Code
specifically exempts from that replacement. The pinned host transport now uses
VS Code's saved original Node HTTPS module (`https.__vscodeOriginal`) when available.
It preserves the existing direct TLS/pin check without changing editor-wide proxy
or certificate settings. Plain Node continues using the normal HTTPS module.

`extension/test/remotehost-proxy.test.js` checks hostname requests through an agent
override, including matching/wrong/missing pins and withholding credentials until
a match. Setting `CONSTRUCT_VSCODE_PROXY_AGENT` to an installed @vscode/proxy-agent
path also runs Microsoft's actual patch; both cases reproduced chain rejection
before the fix and passed afterward. The 218 remote-host checks and four client
conversion checks pass. Apply with Update Construct, reload the VS Code window,
and retry conversion to finish the saved handoff; no new host installation or
VM enrollment is required.

Upstream behavior: [VS Code module patching](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/node/proxyResolver.ts),
[proxy agent replacement](https://github.com/microsoft/vscode-proxy-agent/blob/main/src/index.ts).

After a successful elevated result, `watchPending` automatically retries client
completion once per VS Code session. Thus updating/reloading after the TLS fix can
finish conversion without another click. Once the registry is `hyperv-remote`, the
local-only conversion button disappears. This is the expected transition; current
WS009 registry state has not been read directly. A subsequent reprovision exposed
the undefined `Write-Note` seed-selection bug described in
[reinstall-seed-user-20260906.md](reinstall-seed-user-20260906.md); host management
does not change the adopted Linux guest's original seed account.
