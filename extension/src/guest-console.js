"use strict";

const ssh = require("./ssh");
const { mintLiveLink } = require("./console");

function buildConsoleScript(name) {
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$/.test(name)) throw new Error("Invalid guest VM name");
  return `construct vm console '${name}' --web\n`;
}

/** Mint a fresh gateway ticket through the selected primary, then open it locally.
 * The existing gateway owns console authorization and the client port forward. */
async function openGuestConsole(instance, name, opts = {}) {
  const vscode = opts._vscode || require("vscode");
  return vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: `Connecting to ${name} console…`,
    cancellable: false,
  }, async () => {
    const run = script => (opts._ssh || ssh).runRemoteScript(script, { cfg: require("./instances").toSshCfg(instance), timeoutMs: 90000 });
    const link = await mintLiveLink(() => run(buildConsoleScript(name)), run, opts.probeLink);
    // The fragment is a console credential. Do not log or persist it.
    if (!await vscode.env.openExternal(vscode.Uri.parse(link))) throw new Error("The browser could not open the console link");
  });
}

module.exports = { buildConsoleScript, openGuestConsole };
