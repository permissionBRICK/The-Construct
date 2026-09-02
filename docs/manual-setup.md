# Manual setup on a blank Ubuntu VM

The ordered procedure to take a freshly installed **Ubuntu Server (minimal, headless)** VM to
the ready state by hand — no Windows scripts. Run everything as a sudo-capable user (or `root`).

## Target host

- Ubuntu Server, preferably 24.04 LTS
- SSH and sudo access
- Docker / Docker Compose
- Git, ripgrep
- `/opt/construct` for agent environment files
- `/root/repos` for project checkouts

## 0. Prerequisites

A minimal Ubuntu image often ships without SSH, sudo, git, or curl:

```bash
sudo apt-get update
sudo apt-get install -y openssh-server sudo git curl ca-certificates
sudo systemctl enable --now ssh
```

On Hyper-V NAT this template assumes `<hostname>.mshome.net` (e.g. `agent-vm.mshome.net`);
otherwise use the IP. `bootstrap.sh` installs the rest (jq, ripgrep, unzip, gnupg, Docker, …).

If your VM is **not** on Hyper-V NAT — a VM elsewhere on the LAN, or one reached through a
forwarded port — set the address clients actually use in `/etc/construct/config.env`
(step 4) rather than letting the guest guess it:

```env
CONSTRUCT_EXTERNAL_HOST=vm.example.lan   # empty = $(hostname).mshome.net
CONSTRUCT_EXTERNAL_SSH_PORT=2201         # default 22
```

These two only tell the guest **what address to print** — they change generated output, not
reachability. The console banner, the OpenCode URL and the SMB UNC are built from them, so
setting them stops the VM advertising a `.mshome.net` name that nothing outside Hyper-V can
resolve; whether anything answers at that address is a separate question your network has to
answer. Two specifics:

- `code serve-web` ignores them entirely — it is always advertised as
  `http://localhost:<port>/?tkn=…`, because its token auth only accepts a localhost origin.
  Reach it through a tunnel.
- Every other non-SSH service (OpenCode on 4096, T3 Code, SMB on 445, your own dev servers)
  needs its port to be genuinely reachable at that address — directly on the LAN, or through
  an explicit forward. Behind a [host service](remote-host.md) only SSH is forwarded, so
  there use [`construct expose`](expose.md) and open the link it prints.

## 1. Put this repo at `/opt/construct/repo`

```bash
sudo mkdir -p /opt/construct
sudo chown -R "$USER:$USER" /opt/construct
# from a zip:
unzip /path/to/construct-repo.zip -d /opt/construct/repo
# or from Git:
git clone <CONSTRUCT_ENV_REPO_URL> /opt/construct/repo
```

The repo must live exactly at `/opt/construct/repo` — the scripts and systemd units hard-code
that path.

## 2. Bootstrap the host

```bash
sudo bash /opt/construct/repo/bootstrap.sh
```

Installs base packages and Docker, creates `/opt/construct`, `/root/repos`, and
`/etc/construct`, writes a default `/etc/construct/config.env`, installs the systemd units,
and — on an interactive terminal — launches the AI tool setup workflow. If Docker group
membership changed for your user, log out and back in.

## 3. Select and install AI tools

If the bootstrap ran interactively this already happened. Otherwise:

```bash
sudo /opt/construct/repo/bin/ui-setup.sh
```

The workflow optionally generates a root SSH key, lets you pick tools, records `AI_TOOLS=`, and
runs `bin/install-ai-tools.sh`. When `claude-code` is selected, it automatically applies the
sandbox bypass defaults — see [Provisioning § Bypass-mode defaults](provisioning.md#bypass-mode-defaults).

## 4–7. Configure, generate, check out, start

```bash
sudo nano /etc/construct/config.env                       # 4. set AGENT_NAME, PROJECTS, SSH_USER, AI_TOOLS
                                                          #    (+ CONSTRUCT_EXTERNAL_* if not on Hyper-V NAT)
sudo /opt/construct/repo/bin/generate-runtime-config.sh   # 5. merge project profiles
/opt/construct/repo/bin/checkout-projects.sh              # 6. (optional) clone project repos
sudo systemctl start construct                            # 7. start the service
docker ps
```

## 7b. The `construct` CLI (optional)

`bootstrap.sh` does **not** install the `construct` CLI (`project` / `notify` / `expose`) or
its forward spool — `bin/provision.sh` does. On a hand-built VM, install them the same way
it would:

```bash
sudo install -m 0755 /opt/construct/repo/bin/construct /usr/local/bin/construct
sudo install -m 0755 /opt/construct/repo/bin/construct-expose.sh /usr/local/bin/construct-expose.sh
sudo install -m 0755 /opt/construct/repo/bin/construct-idle-report.sh /usr/local/bin/construct-idle-report.sh
sudo install -d -m 0755 /etc/construct/forwards/{,requests,acks,close}
```

`construct expose` then works as soon as a VS Code window with the Construct extension is
attached; without one, requests simply stay queued (exit code 6). See
[`construct expose`](expose.md).

## 8. Connect from VS Code (Remote-SSH)

Add the VM as a Remote-SSH host and connect. Provisioning pre-seeds the Remote-SSH server
(CLI + REH build) and the agent extensions under `~/.vscode-server/`, so even the first
connect skips VS Code's usual server download/unpack wait — it's pinned to the desktop
client's commit when `Provision-AgentVM.ps1` can detect it (`code` on the host PATH),
otherwise to latest stable; on a version mismatch VS Code simply downloads its own build
on first connect as before. Because step 3 already seeded the machine-scope settings,
the Claude Code extension comes up in bypass mode automatically.
