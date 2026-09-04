# Remote access & services

The ways into the VM — VS Code Remote-SSH, the browser IDE, remote tunnels, the Codex
app-server — plus the systemd services that keep everything running.

## VS Code server & remote access

Independent of `AI_TOOLS`, provisioning installs the standalone VS Code CLI ("VS Code Server") to
`/usr/local/bin/code` **by default** (`VSCODE_SERVER=true`) so VS Code Remote-SSH works out of the
box and `code serve-web` / `code tunnel` are available. Two browser-reachable front ends sit on top
of it.

### `code serve-web` — browser VS Code

Browser-based VS Code, **on by default** (`VSCODE_SERVE_WEB=true`) via
`code-serve-web.service`. It binds `0.0.0.0:8000` (`VSCODE_SERVE_WEB_HOST`/`PORT`). There is
no account sign-in; access is gated by a **connection token** generated into
`VSCODE_SERVE_WEB_TOKEN_FILE`.

**Open it as `http://localhost:8000/?tkn=<token>`, through a tunnel** — that is what the
guest's banner and the provisioner both print. serve-web's token authentication only accepts
a **localhost origin**, so browsing straight to `http://agent-vm.mshome.net:8000/` does not
authenticate even though the port is listening. Two ways to get a local port:

```powershell
ssh -L 8000:127.0.0.1:8000 agent-vm       # from your PC, for as long as it runs
```

```bash
construct expose 8000 --label "serve-web" # from the VM; prints the localhost link
```

The second works identically on a [remote-host](remote-host.md) VM, where the address the
VM sits on is not one your PC can reach at all.

Note this is a root-level IDE (terminal + filesystem). Binding it to `127.0.0.1`
(`VSCODE_SERVE_WEB_HOST=127.0.0.1`) makes the tunnel mandatory rather than merely the way it
authenticates.

### `code tunnel` — no inbound port

Reachable through `https://vscode.dev/tunnel/<name>` with **no inbound port** — opt-in: enable
it with `VSCODE_TUNNEL=true` (a config-file line or `-VsCodeTunnel true` on the host script).
`VSCODE_TUNNEL_NAME` is the tunnel identifier; left blank it is derived from the hostname
(lowercased, `[a-z0-9-]`). The CLI data dir and sign-in token live at `/var/lib/vscode-tunnel`,
so registration survives restarts and re-provisions.

- **First-time registration** needs a **one-time** browser sign-in (GitHub/Microsoft). When you
  select the tunnel, `code-tunnel.service` starts and emits a device-login link; the host
  provisioner (`Provision-AgentVM.ps1`) reads it back and **pauses so you can sign in** against a
  still-valid code, then press Enter to finish setup. (Running headless? Read the
  link with `journalctl -u code-tunnel -n 50`.)
- **Re-provisioning** always re-deploys the `code-tunnel` service when it was previously deployed
  or is still registered — so a registered VM keeps autostarting the tunnel even with
  `VSCODE_TUNNEL=false`. The interactive sign-in is only re-run when `VSCODE_TUNNEL=true` **and**
  the VM isn't already registered.

## Console banner

The VM writes connection info to `/etc/issue.d/construct.issue` via
`construct-console-info.service`, so getty shows it on the physical console before the login
prompt.

The address it prints is **`CONSTRUCT_EXTERNAL_HOST` / `CONSTRUCT_EXTERNAL_SSH_PORT`** from
`/etc/construct/config.env` — the address *clients* use, which is not always something the
guest could work out for itself. Left empty (the default, and every local install) they fall
back to `<hostname>.mshome.net` and port 22, so a Hyper-V NAT VM prints exactly what it
always did, with the current IP as a fallback. A VM behind a
[host service](remote-host.md) has them filled in with the service host and its allocated
SSH forward, so the **SSH** line names an address you can actually dial rather than a NAT
name only the hypervisor host can resolve.

> **The banner tells you where the VM is, not that every port there is open.** The service
> publishes exactly two kinds of forward: the VM's own SSH port, and the host forwards
> somebody asked for with `construct expose --to host`. Nothing else — no OpenCode, no
> T3 Code, no SMB — is mapped automatically, so a URL the banner builds from
> `CONSTRUCT_EXTERNAL_HOST` for one of those ports names the service host on a port where
> nothing is listening. On a remote VM, reach an HTTP port with
> [`construct expose <port>`](expose.md) and open the link it prints.

## Workspace file share (SMB)

Provisioning runs a Samba/SMB server on the VM (`bin/setup-smb-share.sh`, managed unit `smbd`)
that exposes the workspace (`WORKSPACE_ROOT`, default `/root/repos`) to the host PC. The repos
are reachable at `\\agent-vm.mshome.net\repo`, but the host does **not** map a drive letter by
default. Opt in to the host-side auto-mount with `-MountRepoShare true`:

```powershell
net use Z: \\agent-vm.mshome.net\repo /user:dev <password> /savecred /persistent:yes
```

- **Server on by default; drive mount off by default.** The SMB server runs unless you turn it
  off per provision with `Provision-AgentVM.ps1 -SmbShare false`, or persistently with
  `SMB_SHARE=false` in `/etc/construct/config.env`. The host-side auto-mount is off unless you
  ask for it with `-MountRepoShare true` (the repos stay reachable via the UNC path either way).
- **Drive letter.** Defaults to `Z` (`-SmbDriveLetter`) for the default instance and for
  anybody who states the parameter; a **non-default instance** without an explicit letter
  starts from the next free one instead, so a second VM does not take the alternate-letter
  path (and, non-interactively, whatever was free that day) on every provision. A
  re-provisioned VM keeps the letter its share is already mapped to. The chosen letter is
  used without asking when it is free or already mapped to this VM's share. If it is in use
  by something else (another network share or a local disk), the installer prompts you to
  pick another free letter (or skip); a non-interactive run falls back to the next free
  letter automatically. If a prior
  run mapped the share to a different letter, that mapping is detected and refreshed in place.
- **Access as root.** The share is configured with `force user = root`, so the host reads and
  writes the repos as **root** — the same identity the coding agents use. The host
  authenticates as the SMB user (`SMB_USER`, default `dev`); that account exists only for SMB
  login and has no shell.
- **Stable credentials.** The password is generated once and stored in
  `/etc/construct/config.env` (`SMB_USER` / `SMB_PASSWORD` / `SMB_SHARE_NAME`). Every reprovision
  reuses it, so the host's saved mapping keeps working without re-entering anything. Clear
  `SMB_PASSWORD` (or pass a new value via the environment) to rotate it on the next provision.
- The host maps the share over the stable `agent-vm.mshome.net` DNS name (not the VM's DHCP
  IP), so the persistent mapping survives the VM's address changing. `/savecred` stores the
  login in Windows Credential Manager; `/persistent:yes` reconnects it at logon and after any
  VM reboot.

> ⚠ **SMB is a local-VM feature today.** The UNC is built from `CONSTRUCT_EXTERNAL_HOST`, so
> a VM behind a [host service](remote-host.md) prints `\\<service host>\repo` — and the
> service publishes **no SMB forward**, only the VM's SSH port and explicit
> `construct expose --to host` forwards. There is nothing listening on 445 at that address,
> so the UNC will not connect and `-MountRepoShare` has nothing to map. Use Remote-SSH (or
> `scp`/`sshfs` over the VM's SSH port) to reach a remote VM's files. This is a known gap,
> not a configuration mistake.

Read back the details on the VM from the login banner, or (using the instance's SSH alias —
`agent-vm` for the default VM):

```bash
ssh agent-vm "sudo cat /etc/construct/smb-status"
```

## Ports an agent opens for you (`construct expose`)

Everything above is a way *in*. The other direction — a dev server, preview or notebook an
agent started **inside** the VM — goes through `construct expose`:

```console
$ construct expose 5173 --label "vite dev"
http://localhost:5173/
```

By default the port is opened on **your PC**: the extension spawns an `ssh -N -L` tunnel of
its own to the active instance, and the CLI prints the link only once that forward is
actually live. Nothing is exposed to the LAN, and it works identically on a local and a
remote VM. Full contract, the `--to host` alternative, exit codes and configuration:
[`construct expose`](expose.md).

## Codex remote

For Codex, prefer the supported SSH host workflow in Codex App: configure the VM as an SSH host,
ensure `codex` is on the remote PATH, and let Codex App start the remote app-server through SSH.
The managed `codex-app-server.service` is for experimental WebSocket app-server usage and
defaults to `0.0.0.0` for NAT-only VM setups.

## T3 Code desktop and web

T3 Code is an optional client for the coding agents already authenticated in the VM. Enable
**T3 Code web GUI** in the Construct control panel to run its server in the VM and connect with
either the paired browser UI or T3 Code's Windows Desktop app. The separate **Build patched T3
Code + Desktop** setting builds the selected stable/nightly source once inside the VM, uses that
same patched build for both clients, and silently installs or updates Desktop on the host during
provisioning. Routine reprovisions skip the build and installation when neither T3 Code nor
Construct has changed.

The patched clients add a mic button beside Send and a **Ctrl+T** shortcut. Speech appears live
at the cursor without replacing existing input, and the recording ring responds to microphone
level. This reuses Construct's microphone tunnel, so **Microphone passthrough** must also be
enabled and a VS Code window running the Construct extension must remain open while recording.
The patch also adds Claude usage-limit recovery and OpenCode background-task monitoring. See
[Control panel: Patched T3 Code server + Desktop build](control-panel.md#patched-t3-code-server--desktop-build)
for setup, update, caching, and compatibility details.

## Agent runtime

The template includes a minimal local runtime in `agent-runtime/` so the VM can start before a
real construct image exists. It prints the merged project requirements and then stays alive.
Replace `agent-runtime/entrypoint.sh` or set `AGENT_IMAGE` to a registry image when the real
agent runtime is available.

## Service lifecycle

```bash
sudo systemctl start|stop|restart|status construct
```

Provisioning also manages these units (when their tools/features are selected): `opencode-serve`,
`t3code-serve`, `codex-app-server`, `code-serve-web` (browser VS Code), `code-tunnel` (the VS Code
remote tunnel), `smbd` (the workspace file share), and — **only on a VM behind a
[host service](remote-host.md)** — `construct-idle-report.timer`, the activity heartbeat that
keeps a busy VM from being idled out. A local install gets no such timer: there is no service
to report to, and idle policy isn't enforced locally. Inspect any of them with
`systemctl status <unit>` / `journalctl -u <unit>`.

Container logs:

```bash
docker logs construct
journalctl -u construct -n 100 --no-pager
```

Update from Git:

```bash
cd /opt/construct/repo
git pull
sudo /opt/construct/repo/bin/generate-runtime-config.sh
sudo systemctl restart construct
```
