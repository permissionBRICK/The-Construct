# Construct Companion

Construct Companion is the per-user Windows tray app for The Construct. It keeps
port forwards (`construct expose`), notifications (`construct notify`), microphone
passthrough, and instance monitoring available when VS Code is closed. It hosts the
same control panel assets as the extension, for all registered instances. It needs
neither administrator rights nor a separately installed .NET runtime.

**Delivery status:** the desktop app runs against the authenticated IPC host, the real
dispatcher and per-instance runtimes; Linux fake-mode tests cover mixed local/remote and
remote-only registries, forwards, notifications, audio and settings. See the
[message matrix](../companion/README.md#message-matrix) for the implemented workflows. Windows tray, microphone and toast behaviour has not been field-validated; a
successful Linux build is not a Windows runtime test.

The popup can switch instances, register a VM, run lifecycle actions, and open the full panel. The panel also clones and opens projects, removes instances with an option to keep remote VMs, applies CPU/RAM and automatic checkpoint settings, preserves configuration before reprovisioning, creates remote VMs, and starts or explicitly finishes host conversion. Host administration issues and rotates tokens in a native one-time dialog with a copy button. Opening a control surface manually refreshes the Construct update check immediately; successful checks are cached for five minutes and failures for one minute. A host conversion started in VS Code must be finished in its original VS Code profile.

## Install and update

A local build (`-Source local`) needs the Companion sources, which the release archive
does not carry; use a git clone of the repository for that.

Run `Auto-Install.ps1` from a normal, non-elevated PowerShell window. Its per-user
pre-step installs the Companion alongside the VS Code extension before the VM
installation asks for elevation. `Update-Construct.ps1` and a plain
`Provision-AgentVM.ps1` reprovision also install or update it.

Remote installs install the Companion on **your client PC**, too. Forwards, toasts
and microphone capture terminate there, regardless of which host runs the VM. A PC
that only added a remote VM through VS Code receives a once-per-session install
offer in fallback mode; **Construct: Install Construct Companion** opens the same
installer in a visible, non-elevated console.

The Companion step never blocks the VM or extension work:

- A failure prints its safe diagnostic and the installation continues.
- A local build failure reports the native exit status and MSBuild error codes only,
  because dependency output can contain credentials; run
  `dotnet publish companion/src/Construct.Companion -c Release -r win-x64 --self-contained true`
  from the scripts directory to see the full output.
- An elevated Update Construct warns and skips the step; retry from a normal
  PowerShell window. ISO-only and remove-instance operations skip the pre-step.

For a manual install, from your downloaded Construct scripts directory:

```powershell
.\Install-ConstructCompanion.ps1
```

A Companion release (`companion-<commit40>`) is published only for commits that change
a Companion file (its sources, the panel media, the installer scripts; the list is in
`scripts/companion-release-plan.py`). Every host release manifest carries
`companionReleaseTag`: the commit's own Companion build, or the newest published one
when nothing Companion-related changed since. The installer reads the host manifest of
the installed Construct commit (`installedCommit` in `.construct-settings.json`, or
`.construct-revision`) from `constructRepo` (default `permissionBRICK/The-Construct`)
and downloads that Companion release directly: two small manifest downloads, no release
listing and no GitHub API rate limit. "Already current" therefore means the newest
Companion build is installed, even when the Construct version moved. Host releases
without the pointer fall back to `companion-<installedCommit>`, then to the newest
published Companion release from the release listing (bounded to 20 pages; a full
last page is refused with a clear diagnostic).
Both the default and `-Source release` prefer the framework-dependent ZIP when
`dotnet --list-runtimes` lists every shared framework at the manifest's major version.
The current app requires .NET 10's `Microsoft.NETCore.App`,
`Microsoft.WindowsDesktop.App` and `Microsoft.AspNetCore.App`. Missing dotnet or any
required runtime selects the self-contained ZIP. An installed SDK never triggers a
build automatically. Only `-Source local` builds and requires the SDK/solution. A source archive without `.git` needs the `installedCommit`
marker written by the Construct install/update step for a local build.

An identical installed commit is a no-op. `-Force` rebuilds/reinstalls it. Downloads
stream to disk; the detached manifest, ZIP size and SHA-256, checksum-list hash and every
payload file are checked before asking the app to quit. The installer waits at
most 15 seconds for graceful exit and never kills a process. It swaps the install
through `.previous`, retrying each directory move for a few seconds while an
antivirus scan or a closing handle still holds a file, restores the prior
files/registration values on replacement failure and starts the restored app again,
then starts `ConstructCompanion.exe --background` detached. Successful process
creation is the commit point; it is not a runtime health check. A replacement
failure names the step (backing up, moving the new files, registering, starting) and
the Windows error text, so a blocked executable or a held file is visible in the
update console; a held file also names the processes holding it (Windows Restart
Manager). A Companion running from the installation folder whose endpoint cannot be
reached is waited for up to 15 seconds and then refused by name; nothing is killed.

Pass `-SkipCompanion` to Auto-Install, Update Construct, Provision-AgentVM, or the library function to
skip this run. For persistent opt-out, merge `"companion": false` into the scripts
folder's `.construct-settings.json`. This skips installation/updates; it does not
stop or uninstall an existing app. `-Force` does not override the opt-out.

## Files and settings

- App: `%LOCALAPPDATA%\Programs\ConstructCompanion\` (`install.json` records the
  commit, version, source (`framework-dependent`, `self-contained`, or `local-build`),
  release tag, installation time and IPC API version).
- State: `%LOCALAPPDATA%\The-Construct\companion\settings.json` and `endpoint.json`.
  The latter contains a bearer credential: do not paste it into reports or logs.
- Logs: `%LOCALAPPDATA%\The-Construct\companion\logs\companion.log`, five rotating
  files of up to 1 MiB. `%TEMP%` is the path fallback when LOCALAPPDATA is unavailable.
- Per-user registrations: HKCU Run value `ConstructCompanion`, the `construct://`
  protocol, and toast AUMID `PermissionBrick.TheConstruct` (shared with the extension).

Open Settings from the tray or launch `ConstructCompanion.exe --settings`.
Settings include active instance, UI theme, capture device, notifications, forwards
and host label, repatch delay, scripts directory, debug logging, and Start with
Windows. Updates preserve state and an existing `autostart: false` preference.
Instances and remote credentials retain the existing Construct paths and formats.

The VS Code setting `construct.companion` defaults to `auto`: a live compatible
Companion takes over host jobs. Set it to `off` for extension fallback mode. With
no live Companion the extension resumes its existing implementation after a short
grace period. T3 Code's Settings button launches the installed Companion.

## Tray and command line

| Tray color | Active instance |
|---|---|
| Green | SSH online |
| Yellow | Running but SSH unavailable, or lifecycle action in progress |
| Grey | Off or saved |
| Red | Absent, persistent unknown state, or probe error |
| Grey with question mark | No registered instance or scripts directory |

A small dot indicates a Construct update. Left click opens the popup, double click
opens the panel, and right click opens instance selection, power, forwards, mic,
notifications, settings, logs and Quit. Closing a panel leaves the tray running.

Useful flags: `--background`, `--panel`, `--settings`, `--popup`, `--hostadmin`,
`--instance <name>`, `--host <slug>`, `--version`, and `--quit`.

```powershell
& "$env:LOCALAPPDATA\Programs\ConstructCompanion\ConstructCompanion.exe" --selftest --json
```

The headless selftest reports paths/state parsing, a loopback health round trip,
instance probes, microphone device enumeration, WebView2 presence and toast
registration. It never starts tunnels or writes acknowledgements. Exit 0 means the
checks that can run without a VM passed; exit 1 means failure. A new user with no
instances is a supported successful check. Remote-only registries do not require local
Hyper-V. The diagnostic binds the real IPC host with runtime jobs and endpoint
publication disabled, so it cannot replace a running Companion's discovery document.

## Uninstall and troubleshooting

```powershell
.\Install-ConstructCompanion.ps1 -Uninstall
```

Uninstall asks the app to quit, removes the app and its Run/protocol/AUMID
registrations, and keeps settings and logs. Set the persistent opt-out as well if
you do not want the next Construct update to install it again.

For quit timeout, close the Companion from the tray and retry; do not delete its
files while it is running. A malformed endpoint is refused with a stable diagnostic.
A dead PID in a stale endpoint does not require an HTTP request. If `.previous`
exists, updates refuse to overwrite it: close the app, inspect `install.json` in
both folders, and retain a copy before recovery. If replacement failed, restore
`.previous` to `ConstructCompanion` after moving the failed directory aside. If the
new install succeeded and only cleanup failed, remove `.previous` after confirming
the installed version. A failed rollback explicitly reports incomplete recovery.
After a completed rollback the previous Companion is started again; start it from the
Start menu only if the diagnostic says so.

No release found usually means this repository has not published a Companion yet.
For manifest/checksum failure, keep the existing install and retry from the intended
repository; never bypass verification. For unavailable local builds, install a
.NET 10 SDK or use `-Source release`. Missing WebView2 and Hyper-V permission issues
are reported by the app/selftest; Hyper-V state still requires membership in Hyper-V
Administrators. The Run key starts at login and does not restart a crashed app.

Update checks: the Companion, the VS Code panel and T3 Desktop all compare the
installed commit (`installedCommit` in the scripts folder's `.construct-settings.json`)
with the commit in the published release manifest at
`https://github.com/<repo>/releases/latest/download/manifest.json`. A banner or offer
means a newer published commit exists, not a commit count; it disappears after Update
Construct records the new commit. No banner while a newer release exists means the
manifest could not be fetched (offline, a redirect blocked by a proxy) or the checkout
tracks a ref other than `main`, which never receives automatic offers. T3 Desktop
additionally offers Reprovision while a VM's provisioned commit is older than the
installed one; reprovisioning after an update clears it.

The executable is **unsigned**. HTTPS, control of the selected GitHub repository
and SHA-256 are the trust model, as with [host releases](host-release.md); hashes
detect corruption and are not signatures. SmartScreen may warn on manual first
launch. There is no self-update; Update Construct replaces the app.

## Release packaging

`companion/host/New-ConstructCompanionPackage.ps1 -PublishDir <publish>
-OutputDir <new-output> -Commit <sha40> [-Repository owner/repo]` packages a clean,
self-contained win-x64 publish. It emits `construct-companion-<sha7>-win-x64.zip`,
`manifest.json`, and a detached `SHA256SUMS`. Each compressed ZIP contains `app/` (including
`media/`) and `SHA256SUMS`. The manifest binds the repository, main ref, commit,
version, immutable release tag, executable path, API version, and hashes.

The SHA-pinned workflow tests on Linux and Windows, publishes on Windows, requires
headless selftest success with isolated empty state, and creates `companion-<sha>`
with the ZIP and manifest. It never overwrites a release. Production packaging must
use the clean checkout of the supplied commit; the packager cannot establish source
provenance from arbitrary publish files. The Linux layout test uses a fixture exe,
not a runnable Windows binary.

Releases include `construct-companion-<commit7>-win-x64.zip` and
`construct-companion-<commit7>-win-x64-fdd.zip`. Both archives use Optimal ZIP compression. Both contain `app/` and a per-file `SHA256SUMS`; the detached sums file
also lists both ZIPs. Legacy manifest fields continue to describe the self-contained
archive. Additive `frameworkDependentAsset`, `frameworkDependentSha256`,
`frameworkDependentSizeBytes`, `frameworkDependentUncompressedSizeBytes`, and `frameworkDependentSumsSha256` describe FDD.
`runtimes` lists `{name, majorVersion}` requirements derived from the FDD publish's
runtimeconfig, including transitive framework references. Old releases without FDD
metadata still install self-contained; their missing size field retains hash-only
verification.

`payloadUncompressedSizeBytes` and `frameworkDependentUncompressedSizeBytes`
declare each archive's total inflated bytes, including `SHA256SUMS`. Extraction
checks the central directory before writing, limits each entry to 256 MiB and the
total to 1 GiB, and requires the total to match the manifest. A bounded validation
pass also rejects entries whose inflated length differs from their declaration
before creating destination files. Older manifests without totals retain the
absolute limits. Preview runtimes do not satisfy the stable runtime probe.
