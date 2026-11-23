# The Construct for VS Code

A window onto the Construct device agent (`agent/`), which holds all of the logic. The activity-bar
view shows the agent's pop-out surface, **The Construct: Open the Construct Window** its window
surface in an editor tab. Everything on them is the agent's own page.

What the extension does itself:

- **Finds the agent** through its token file (`endpoint.json` in the agent's directory) and checks
  that the process answering `/api/health` is the one the file names. When the agent is installed
  but not running, it starts it in the background (`construct-agent.exe --background`, on Linux the
  systemd user unit or `construct-agent run`). When it is not installed, the view offers one button
  that runs the agent's install script for the OS (carried in the package's `install/`) and shows
  what failed in place.
- **Opens a VM in VS Code** over Remote-SSH: **Open a VM in VS Code**, or the agent's page asking
  for it. The agent writes the SSH alias; the extension opens `/root/repos` on it in a new window.
- **Offers to register the open folder as a project** in a Remote-SSH window on one of your VMs:
  a checkout directly under `/root/repos` with an `origin` that no profile has, under a name no
  profile has. It writes the profile through the store engine's profile route and switches it on
  for that VM; **Register This Folder as a Project** runs the same check and says why not.

In a remote extension host, or in VS Code inside a Construct VM, it only says that it runs on your PC.

## Install

The install scripts install it from the agent package they install (`construct.vsix`, built by
`scripts/package-agent.py`) with `code --install-extension --force`, over the old control panel
(same id, higher version): `install.ps1` and `Update-Construct.ps1`, `agent/install/Install-ConstructAgent.ps1`
and `agent/install/install-construct-agent.sh`. VS Code not installed is skipped; VS Code refusing
the extension fails the install.

## Tests

```sh
node --test extension/test/agent.test.js extension/test/page.test.js extension/test/workspace.test.js extension/test/extension.test.js
```

`src/projects.js` and its tests are the profile validator the VM's `construct project set` loads
(`bin/project-set.js`); `vm/` holds guest scripts the agent and `bin/` use. Neither is part of the
extension package.
