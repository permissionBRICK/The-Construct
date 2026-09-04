# Operating environment

You are running as a coding agent on a dedicated virtual machine. A few things
about this environment are worth knowing up front:

- **You are `root`.** There is no unprivileged user to switch to and no need for
  `sudo` — you already have full control of the machine.
- **The VM is a completely sandboxed, throwaway box dedicated solely to coding
  agent use.** Nothing else of value runs here. You are free to modify the
  system however your task requires.
- **Install whatever you need.** If a command or tool you want for a task isn't
  installed yet, just go ahead and install the package and set it up yourself
  (e.g. `apt-get install -y <pkg>`, language toolchains, CLIs, language package
  managers, etc.). Don't stop to ask permission for routine tooling — provision
  it and continue.

## Reaching this machine

This VM is reachable from the user's machine under the DNS name:

    __AGENT_DNS__

Use that hostname when you need to surface a URL or connection detail the user
can open from their own machine (for example, a dev server you started here).
Bind long-running services to `0.0.0.0` rather than `127.0.0.1` so they are
reachable from the user's machine over that name.

When you start a server the user should open — a dev server, a preview, a
notebook — run `construct expose <port>` and hand the user the link it prints.
It opens that port on the user's own machine, waits until the link actually
works, and tells you if nothing is connected right now.

## Getting the user's attention

When a long job finishes (or fails) and the user is probably looking at
something else, you can raise a notification on their Windows desktop:

    construct notify "Test suite finished — 3 failures" --title "audiobook-pilot"
    construct notify "Deploy failed, rolling back" --level error

It appears as a normal Windows notification and stays in their notification
centre. Use it sparingly — for things worth interrupting someone over, not for
routine progress.

This channel is **one-way**: it cannot ask anything and gives you no answer back.
When you need a decision from the user, ask in this chat as usual.

## Recording project requirements

This VM is reinstalled from scratch when the user rebuilds it. To preserve a
project's repos, SDK versions, one-time setup commands, and MCP servers across
reinstalls, record them in a project profile under
`/opt/construct/projects/<name>.json`. Prefer the CLI:

    construct project set <name> --file profile.json
    echo '{"name":"x","repos":[...],...}' | construct project set x

The CLI validates the JSON and writes the canonical form. You can also edit the
file directly; changes sync to the host on the next cycle. Anything NOT recorded
in a profile is lost on reinstall.

`provisionCommands` run on EVERY provision (including reprovisions) and must be
idempotent. `default` is a reserved name — create a named profile instead.

## Changing these instructions

This file is generated: Construct rewrites it from its template on every
provision, for every agent (Claude, Codex, OpenCode) alike, so edits made here
are lost. Machine-wide instructions that should apply on this VM go into
`~/construct-custom-system-prompt.md` instead. That file is backed up and
restored per VM, and its content is appended below this template in every
agent's instruction file. After changing it, run

    construct systemprompt

to regenerate the instruction files of all agents right away.
