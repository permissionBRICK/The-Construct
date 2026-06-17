# Remote access & services

The ways into the VM — VS Code Remote-SSH, the browser IDE, remote tunnels, the Codex
app-server — plus the systemd services that keep everything running.

## VS Code server & remote access

Independent of `AI_TOOLS`, provisioning installs the standalone VS Code CLI ("VS Code Server") to
`/usr/local/bin/code` **by default** (`VSCODE_SERVER=true`) so VS Code Remote-SSH works out of the
box and `code serve-web` / `code tunnel` are available. Two browser-reachable front ends sit on top
of it.

### `code serve-web` — browser VS Code

Browser-based VS Code served directly over HTTP, **on by default** (`VSCODE_SERVE_WEB=true`)
via `code-serve-web.service`. It binds `0.0.0.0:8000` (`VSCODE_SERVE_WEB_HOST`/`PORT`) and is
reachable at `http://<dns>:<port>/?tkn=<token>`. There is no account sign-in; access is gated
by a **connection token** generated into `VSCODE_SERVE_WEB_TOKEN_FILE`. Note this is a
root-level IDE (terminal + filesystem) — keep it on trusted VM networks. (To require an SSH
tunnel instead of network exposure, set `VSCODE_SERVE_WEB_HOST=127.0.0.1` and reach it via
`ssh -L 8000:127.0.0.1:8000`.)

### `code tunnel` — no inbound port

Reachable through `https://vscode.dev/tunnel/<name>` with **no inbound port** — opt-in: enable
it with `VSCODE_TUNNEL=true` (a config-file line or `-VsCodeTunnel true` on the host script).
`VSCODE_TUNNEL_NAME` is the tunnel identifier; left blank it is derived from the hostname
(lowercased, `[a-z0-9-]`). The CLI data dir and sign-in token live at `/var/lib/vscode-tunnel`,
so registration survives restarts and re-provisions.

- **First-time registration** needs a **one-time** browser sign-in (GitHub/Microsoft). When you
  select the tunnel, `code-tunnel.service` starts and emits a device-login link; the host
  provisioner (`Provision-AgentVM.ps1`) reads it back and **pauses before the reboot** so you can
  sign in against a still-valid code, then press Enter to continue. (Running headless? Read the
  link with `journalctl -u code-tunnel -n 50`.)
- **Re-provisioning** always re-deploys the `code-tunnel` service when it was previously deployed
  or is still registered — so a registered VM keeps autostarting the tunnel even with
  `VSCODE_TUNNEL=false`. The interactive sign-in is only re-run when `VSCODE_TUNNEL=true` **and**
  the VM isn't already registered.

## Console banner

The VM writes connection info to `/etc/issue.d/construct.issue` via
`construct-console-info.service`, so getty shows it on the physical console before the login
prompt. On Hyper-V NAT, the banner uses `<hostname>.mshome.net` (e.g. `agent-vm.mshome.net`)
and prints the current IP only as a fallback.

## Workspace file share (SMB)

Provisioning runs a Samba/SMB server on the VM (`bin/setup-smb-share.sh`, managed unit `smbd`)
that exposes the workspace (`WORKSPACE_ROOT`, default `/root/repos`) to the host PC, and the
host-side provisioner auto-mounts it as a drive:

```powershell
net use Z: \\agent-vm.mshome.net\repo /user:dev <password> /savecred /persistent:yes
```

- **On by default.** Turn it off per provision with `Provision-AgentVM.ps1 -SmbShare false`,
  or persistently with `SMB_SHARE=false` in `/etc/construct/config.env`. Skip only the
  host-side auto-mount (leave the server running) with `-MountRepoShare false`. Pick the drive
  letter with `-SmbDriveLetter` (default `Z`; the next free letter is used if it's taken).
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
  login in Windows Credential Manager; `/persistent:yes` reconnects it at logon and after the
  VM's post-provision reboot.

Read back the details on the VM from the login banner, or:

```bash
ssh agent-vm "sudo cat /etc/construct/smb-status"
```

## Codex remote

For Codex, prefer the supported SSH host workflow in Codex App: configure the VM as an SSH host,
ensure `codex` is on the remote PATH, and let Codex App start the remote app-server through SSH.
The managed `codex-app-server.service` is for experimental WebSocket app-server usage and
defaults to `0.0.0.0` for NAT-only VM setups.

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
`codex-app-server`, `code-serve-web` (browser VS Code), `code-tunnel` (the VS Code remote
tunnel), and `smbd` (the workspace file share). Inspect any of them with
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
