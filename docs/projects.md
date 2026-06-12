# Project profiles & configuration

How the VM is configured: the host-local `config.env`, the `projects/*.json` profiles, MCP
servers, per-project provisioning commands, and repo checkout.

## Local config (`/etc/construct/config.env`)

Host-specific config lives outside Git at `/etc/construct/config.env`:

```env
AGENT_NAME=agent-vm-01
PROJECTS=default,your-name-here
AGENT_HOME=/opt/construct
WORKSPACE_ROOT=/root/repos
SSH_USER=agent
```

Don't put long-lived secrets here. Prefer SSH keys, short-lived tokens, or a secret manager.

## Project profiles

Project profiles live in `projects/*.json`; the schema is documented in
`projects/project.schema.json`. Each selected project may declare:

- repos to clone
- SDK versions needed by project containers
- MCP servers (see below)
- optional host packages (disabled by default)
- custom provisioning commands run on every provision (see below)
- test commands and notes

Selected projects are read from `PROJECTS` in `/etc/construct/config.env`. Requirements are
merged across all selected projects and deduplicated by:

```bash
sudo /opt/construct/repo/bin/generate-runtime-config.sh
```

The generated files are written to `/opt/construct/runtime/generated.json` and
`/opt/construct/runtime/generated.env`.

## MCP servers

The `mcp` array takes two kinds of entry:

- A **string** (`"filesystem"`, `"browser"`, `"github"`) — a docker-compose MCP
  container profile (the original mechanism; see `docker-compose.yaml`).
- An **object** — an MCP server written directly into each coding agent's own
  config (Claude Code, Codex, Opencode) by `bin/configure-mcp.sh` during
  provisioning. Two transports:

  ```jsonc
  // stdio (e.g. an npx server)
  { "name": "context7", "type": "stdio", "command": "npx",
    "args": ["-y", "@upstash/context7-mcp"], "env": { "KEY": "val" } }

  // http
  { "name": "sentry", "type": "http", "url": "https://mcp.sentry.dev/mcp",
    "headers": { "Authorization": "Bearer ..." }, "bearerTokenEnvVar": "SENTRY_MCP_TOKEN" }
  ```

  Optional on either form:

  - `"agents"`: subset of `["claude", "claude-code", "codex", "opencode"]` — only
    configure the server into those agents (default: all). List the same `name`
    twice with different `agents` to give an agent a different config.
  - `"enabled"`: set `false` to add the server **flagged disabled** (default
    true) so you can toggle it on in the agent UI. Opencode (`enabled: false`)
    and Codex (`enabled = false`) store a present-but-disabled entry; Claude has
    no global disable, so it is disabled per directory
    (`projects.<dir>.disabledMcpServers`) for the workspace and every repo dir.

  Servers are written **globally** (user scope) for all agents. Notes: Codex http
  supports only the URL plus an optional bearer-token env var — arbitrary
  `headers` are applied to Claude/Opencode only. Servers you list are upserted by
  name; unrelated/hand-added servers are left untouched.

Because MCP servers are declared in the project JSON, they are preserved across a
reinstall: the [save/restore](backup-restore.md) flow backs up the VM's stored
project profiles and restores any the host doesn't already have.

## Provisioning commands

A profile may declare `provisionCommands` — a list of bash commands run on **every**
provision, as the project's "every-time" setup hook (build steps, fetching deps,
seeding a local `.env`, …):

```jsonc
{
  "name": "customer-portal",
  "repos": [{ "url": "git@github.com:acme/customer-portal.git", "directory": "customer-portal" }],
  "provisionCommands": [
    "npm ci",
    "cp -n .env.example .env || true"
  ]
}
```

Behaviour:

- **When** — `bin/run-provision-commands.sh` runs late in the provision, **after** the
  profile's repos are checked out **and** after the SDKs/runtimes (`node`, `python`,
  `dotnet`) are installed, so build steps find both their source and their toolchains.
- **Order** — commands run top-to-bottom; across several selected profiles they run in
  profile order.
- **Working directory** — each command runs from the profile's **first repo checkout**
  (`/root/repos/<directory>`), so `npm ci` / `dotnet restore` just work. Profiles with no
  repo (or whose repo isn't on disk — e.g. `CHECKOUT_PROJECTS=false` or a failed clone)
  run from the workspace root instead.
- **Idempotency** — they run every time, so they must be safe to re-run. Prefer
  idempotent forms (`npm ci`, `cp -n`, `… || true`).
- **Failure** — a command that exits non-zero is logged but does **not** abort the
  provision or the remaining commands (same as repo checkout and MCP setup).
- **Environment** — runs as root with `config.env` sourced and the merged `AGENT_*`
  vars derived from `generated.json`, so `WORKSPACE_ROOT`, `AGENT_PROJECTS`,
  `AGENT_REPOS_JSON` (valid JSON), `AGENT_SDKS_JSON`, `AGENT_MCP`, etc. are available.

Run them by hand with `sudo /opt/construct/repo/bin/run-provision-commands.sh`.

## Checkout projects

When the selected projects declare repos, the provisioner checks them out
automatically during setup (it passes `CHECKOUT_PROJECTS=true`). To run it by hand:

```bash
/opt/construct/repo/bin/checkout-projects.sh
```

Repos are cloned under `/root/repos`.

**Credentials for private repos.** If any selected project's repos use `https://`
URLs, the installer asks **once** up front for a git username + token (press Enter to
skip if the repos are public). The credentials are written to a temporary file used as a
one-shot `store --file=` credential helper for the checkout, so all repos clone without
re-prompting. They are persisted into `~/.git-credentials` only if you also opted into
"store git credentials" — otherwise they are used for the checkout and discarded. (SSH
`git@…` URLs don't trigger the prompt — a username/token can't authenticate them; they
rely on whatever SSH auth is already configured on the VM.)
