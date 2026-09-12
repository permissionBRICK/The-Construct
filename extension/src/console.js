"use strict";
const net = require("net");
const cp = require("child_process");
const path = require("path");
const ssh = require("./ssh");
const instances = require("./instances");
const guestScripts = require("./guest-scripts");

function planConsole(instance, platform = process.platform) {
  const backend = String(instance && instance.backend || "hyperv-local").toLowerCase();
  if (backend === "hyperv-remote") return { mode: "remote" };
  if (backend === "hyperv-local" && platform === "win32") return { mode: "local" };
  return { mode: "unsupported", reason: backend === "hyperv-local" ? "Console is available from Windows only" : "Console needs a Hyper-V VM on this PC or a Construct host service" };
}
function stateFor(instance, platform) {
  const plan = planConsole(instance, platform);
  return plan.mode === "unsupported" ? { supported: false, reason: plan.reason } : { supported: true };
}
function buildEnsureGatewayScript() { return guestScripts.render("console-ensure"); }
function buildCloseForwardScript(port) {
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid console port");
  return guestScripts.render("console-close", { clientPort: String(port) });
}
function parseEnsureOutput(stdout) { return (String(stdout).match(/^CONSOLE_GATEWAY=(ready|installed|no-docker|missing-source|install-failed)$/m) || [])[1] || "install-failed"; }
function buildMintScript({ local = false } = {}) { return `construct vm console self --web${local ? " --connection-stdin" : ""}\n`; }
function parseBrowserLink(stdout) {
  const lines = String(stdout || "").trim().split(/\r?\n/);
  if (lines.length !== 1) throw new Error("The console gateway did not return one browser link");
  let url;
  try { url = new URL(lines[0]); } catch (_) { throw new Error("The console gateway returned an invalid browser link"); }
  if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || url.hash.length <= 1)
    throw new Error("The console gateway returned an invalid browser link");
  return url.href;
}
function parseHandoff(stdout) {
  let value;
  try { value = JSON.parse(stdout); } catch (_) { throw new Error("Console access could not be prepared on this PC. Update Construct and retry."); }
  if (value && value.setupRequired === true && ["no-credential", "grant-missing", "credential-out-of-sync"].includes(value.reason)) return { setupRequired: true, reason: value.reason };
  if (value && ["vm-not-running", "vmconnect-unreachable"].includes(value.error)) return { error: value.error };
  const patterns = { vmId: /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i, username: /^[A-Za-z0-9_-]{1,20}$/, domain: /^[A-Za-z0-9_.-]{1,255}$/, password: /^[^\x00-\x1f\x7f]{1,256}$/, certificateFingerprint: /^sha256:(?:[0-9a-f]{2}:){31}[0-9a-f]{2}$/ };
  if (!value || Object.entries(patterns).some(([key, pattern]) => typeof value[key] !== "string" || !pattern.test(value[key])) || typeof value.hostAddress !== "string" || (value.hostAddress !== "" && net.isIP(value.hostAddress) !== 4))
    throw new Error("Console access could not be prepared on this PC. The credential broker returned invalid connection data.");
  return Object.fromEntries([...Object.keys(patterns), "hostAddress", "rotated"].map(key => [key, value[key]]));
}
const forwardFailure = "The console forward is not open on this PC. Reconnect to the VM (the Forwards card must show port 6080 as open) and try again.";
function mapFailure(step, result = {}) {
  if (result.code === -1 || result.code === -2 || /ssh:/i.test(result.stderr || "")) return "The VM did not answer over SSH. Start or connect the VM, then click Console again.";
  const status = step === "ensure" ? parseEnsureOutput(result.stdout) : result.error;
  const messages = {
    "no-docker": "Docker is not installed on the VM, so the console gateway cannot run. Reprovision the VM, or make this PC a Construct host.",
    "missing-source": "The Construct checkout is missing on the VM (/opt/construct/repo). Reprovision the VM.",
    "install-failed": "Installing the console gateway failed. Run bash /opt/construct/repo/console-viewer/install.sh on the VM to see the full error.",
    "vm-not-running": "The VM is not running on this PC's Hyper-V.",
    "vmconnect-unreachable": "Hyper-V's console service (port 2179) did not answer on this PC. Check that the Hyper-V Virtual Machine Management service is running.",
  };
  if (messages[status]) return messages[status];
  if (result.setupRequired) return "Console setup did not finish. Check the administrator PowerShell window, then click Console again.";
  if (step === "mint" && result.code === 6) return "No Construct client is attached to the VM, so the viewer port cannot be forwarded. Connect this window to the VM and retry.";
  if (step === "mint" && result.code === 9) return "This VM's construct CLI is older than the panel. Reprovision the VM.";
  // Only fixed gateway errors may be surfaced. Raw SSH output can contain a ticket or password.
  const known = String(result.stderr || "").match(/Host refused console operation \(HTTP \d{3}\)|Browser console is unavailable on this host\.?|Too many viewer links/);
  return known ? known[0] : "Could not create a console link. Check that the primary VM and Construct client are connected.";
}
function probeLink(link) {
  const url = new URL(link);
  return new Promise(resolve => {
    const socket = net.connect({ host: "127.0.0.1", port: Number(url.port || (url.protocol === "https:" ? 443 : 80)) });
    const finish = ok => { socket.destroy(); resolve(ok); };
    socket.setTimeout(1500, () => finish(false)); socket.once("error", () => finish(false)); socket.once("connect", () => finish(true));
  });
}
async function mintLiveLink(mint, run, probe = probeLink) {
  for (let attempt = 0; attempt < 2; attempt++) {
    const result = await mint();
    if (result.code !== 0) throw new Error(mapFailure("mint", result));
    const link = parseBrowserLink(result.stdout);
    if (await probe(link)) return link;
    if (attempt === 0) {
      const url = new URL(link);
      const closed = await run(buildCloseForwardScript(Number(url.port || (url.protocol === "https:" ? 443 : 80))));
      if (closed.code !== 0) throw new Error(forwardFailure);
    }
  }
  throw new Error(forwardFailure);
}
function buildHandoffScript(scriptsDir) {
  const quote = s => "'" + String(s).replace(/'/g, "''") + "'";
  return "$ErrorActionPreference='Stop'; $u=New-Object Text.UTF8Encoding($false); [Console]::OutputEncoding=$u; [Console]::InputEncoding=$u; " +
    `. ${quote(path.join(scriptsDir, "lib", "AgentVm.Remote.ps1"))}; . ${quote(path.join(scriptsDir, "lib", "AgentVm.Console.ps1"))}; ` +
    "$p=[Console]::In.ReadToEnd() | ConvertFrom-Json; Invoke-ConstructConsoleHandoff -InstanceName $p.instanceName -VmName $p.vmName";
}
function runHostBroker(instance, scriptsDir) {
  return new Promise((resolve, reject) => {
    const child = cp.spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Buffer.from(buildHandoffScript(scriptsDir), "utf16le").toString("base64")], { windowsHide: true });
    let stdout = "";
    const timer = setTimeout(() => { child.kill(); reject(new Error("Console access could not be prepared on this PC. The credential broker timed out.")); }, 30000);
    child.stdout.on("data", data => { if (stdout.length < 8192) stdout += data; });
    child.stderr.resume(); child.stdin.on("error", () => {});
    child.on("error", () => { clearTimeout(timer); reject(new Error("Console access could not be prepared on this PC. Check that Windows PowerShell is available.")); });
    child.on("close", code => { clearTimeout(timer); if (code !== 0) reject(new Error("Console access could not be prepared on this PC. Run Set-AgentVmConsoleAccess.ps1 to repair access.")); else { try { resolve(parseHandoff(stdout)); } catch (e) { reject(e); } } });
    child.stdin.end(JSON.stringify({ instanceName: instance.name, vmName: instance.vmName }));
  });
}
async function open(target, deps = {}) {
  const vscode = deps._vscode || require("vscode"), instance = target.instance;
  const plan = planConsole(instance, deps.platform);
  if (plan.mode === "unsupported") throw new Error(plan.reason);
  const run = (script, opts = {}) => (deps._ssh || ssh).runRemoteScript(script, { cfg: instances.toSshCfg(instance), timeoutMs: 90000, ...opts });
  const checkTarget = () => { if (deps.targetSuperseded && deps.targetSuperseded(target, "Console")) throw new Error("The selected instance changed. Click Console again."); };
  return vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Opening console for ${instance.name}…`, cancellable: false }, async progress => {
    const report = message => { if (progress) progress.report({ message }); };
    report("Checking the console gateway on the VM; first-time installation can take about a minute");
    const ensured = await run(buildEnsureGatewayScript(), { timeoutMs: 300000 });
    if (ensured.code !== 0 || !["ready", "installed"].includes(parseEnsureOutput(ensured.stdout))) throw new Error(mapFailure("ensure", ensured));
    let handoff;
    if (plan.mode === "local") {
      report("Preparing console access on this PC");
      const broker = deps.runHostBroker || (() => runHostBroker(instance, deps.scriptsDir));
      handoff = await broker();
      if (handoff.setupRequired) {
        checkTarget();
        const accepted = await vscode.window.showWarningMessage(`Console access for ${instance.name} is not set up on this PC. Set it up now? Windows asks for administrator approval once.`, { modal: true }, "Set up console");
        if (accepted !== "Set up console") throw new Error("Console setup was cancelled. Click Console to try again.");
        checkTarget();
        if (await deps.launchSetup(handoff.reason) === false) throw new Error("Console setup could not be launched. Update Construct on this PC and retry.");
        for (let i = 0; i < 60 && handoff.setupRequired; i++) {
          await (deps.delay || (ms => new Promise(resolve => setTimeout(resolve, ms))))(2000);
          checkTarget(); handoff = await broker();
        }
      }
      if (handoff.setupRequired || handoff.error) throw new Error(mapFailure("broker", handoff));
    }
    report("Creating a fresh console link");
    const link = await mintLiveLink(() => run(buildMintScript({ local: plan.mode === "local" }), { stdin: handoff ? JSON.stringify(handoff) + "\n" : undefined }), run, deps.probeLink || probeLink);
    handoff = undefined; checkTarget(); report("Opening your browser");
    try { if (!await vscode.env.openExternal(vscode.Uri.parse(link))) throw new Error(); }
    catch (_) { throw new Error("The browser could not open the console link"); }
  });
}
module.exports = { planConsole, stateFor, buildEnsureGatewayScript, buildCloseForwardScript, parseEnsureOutput, buildMintScript, parseBrowserLink, parseHandoff, mapFailure, probeLink, mintLiveLink, buildHandoffScript, runHostBroker, open };
