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
sudo /opt/construct/repo/bin/generate-runtime-config.sh   # 5. merge project profiles
/opt/construct/repo/bin/checkout-projects.sh              # 6. (optional) clone project repos
sudo systemctl start construct                            # 7. start the service
docker ps
```

## 8. Connect from VS Code (Remote-SSH)

Add the VM as a Remote-SSH host and connect. Provisioning pre-seeds the Remote-SSH server
(CLI + REH build) and the agent extensions under `~/.vscode-server/`, so even the first
connect skips VS Code's usual server download/unpack wait — it's pinned to the desktop
client's commit when `Provision-AgentVM.ps1` can detect it (`code` on the host PATH),
otherwise to latest stable; on a version mismatch VS Code simply downloads its own build
on first connect as before. Because step 3 already seeded the machine-scope settings,
the Claude Code extension comes up in bypass mode automatically.
