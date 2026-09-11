"use strict";

const ssh = require("./ssh");

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
    const result = await (opts._ssh || ssh).runRemoteScript(buildConsoleScript(name), { cfg: require("./instances").toSshCfg(instance), timeoutMs: 90000 });
    if (result.code !== 0) throw new Error((result.stderr || "Could not create a console link. Check that the primary VM and Construct client are connected.").trim());
    const lines = String(result.stdout || "").trim().split(/\r?\n/);
    if (lines.length !== 1) throw new Error("The console gateway did not return one browser link");
    let url;
    try { url = new URL(lines[0]); } catch (_) { throw new Error("The console gateway returned an invalid browser link"); }
    if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || !url.hash)
      throw new Error("The console gateway returned an invalid browser link");
    // The fragment is a console credential. Do not log or persist it.
    if (!await vscode.env.openExternal(vscode.Uri.parse(url.href))) throw new Error("The browser could not open the console link");
  });
}

module.exports = { buildConsoleScript, openGuestConsole };
