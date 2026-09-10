"use strict";

const fs = require("fs");
const path = require("path");
const os = require("os");
const cp = require("child_process");
const crypto = require("crypto");
const ssh = require("./ssh");
const instances = require("./instances");
const remotehost = require("./remotehost");

const SECRET_PREFIX = "construct.hostConversion.";
let running = false;
const quote = s => "'" + String(s).replace(/'/g, "''") + "'";
const encode = s => Buffer.from(s, "utf16le").toString("base64");
const html = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const validHost = s => typeof s === "string" && /^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$/i.test(s) && !s.includes("..");
function eligible(instance, connected, platform = process.platform) {
  return platform === "win32" && connected === true && instance && instance.backend === "hyperv-local";
}
function pendingPath(env = process.env) {
  if (!env.LOCALAPPDATA) throw new Error("LOCALAPPDATA is unavailable on this PC.");
  return path.join(env.LOCALAPPDATA, "The-Construct", "host-conversion.json");
}
function readPending() {
  try { return JSON.parse(fs.readFileSync(pendingPath(), "utf8")); } catch (_) { return null; }
}
function powershell(script, input = "") {
  return new Promise((resolve, reject) => {
    const encoding = "$u=New-Object Text.UTF8Encoding($false); [Console]::OutputEncoding=$u; [Console]::InputEncoding=$u; ";
    const child = cp.spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encode(encoding + script)], { windowsHide: true });
    let output = "", error = "";
    child.stdout.on("data", d => { if (output.length < 65536) output += d; });
    child.stderr.on("data", d => { if (error.length < 4096) error += d; });
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve(output.trim()) : reject(new Error(error.trim() || "The host setup console did not finish successfully.")));
    child.stdin.on("error", () => {});
    child.stdin.end(input);
  });
}
function launchScript(plan) {
  const script = path.join(plan.scriptsDir, "service", "host", "ConvertTo-ConstructHost.ps1");
  const b64 = Buffer.from(JSON.stringify(plan), "utf8").toString("base64");
  const inner = encode(`& ${quote(script)} -PlanB64 '${b64}'; exit $LASTEXITCODE`);
  // The outer process remains unelevated and observes UAC rejection/exit status.
  // Only the detached, visible child is elevated. Its -NonInteractive forbids late prompts.
  return `$ErrorActionPreference='Stop'; try { $p=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ${inner}'; exit $p.ExitCode } catch { Write-Error 'Host setup could not start. The UAC request may have been cancelled.'; exit 1 }`;
}
function review(vscode, defaults) {
  return new Promise(resolve => {
    const panel = vscode.window.createWebviewPanel("construct.hostConversion", "Make this PC a Construct host", vscode.ViewColumn.Active, { enableScripts: true });
    const nonce = crypto.randomBytes(16).toString("hex");
    let settled = false;
    const finish = value => { if (settled) return; settled = true; resolve(value); panel.dispose(); };
    panel.onDidDispose(() => finish(null));
    panel.webview.onDidReceiveMessage(message => {
      if (message.type === "cancel") finish(null);
      if (message.type === "install" && validHost(message.host)) finish({ publicHost: message.host, keepAwake: message.keepAwake === true });
    });
    panel.webview.html = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'"><style>
      body{font-family:var(--vscode-font-family);color:var(--vscode-foreground);padding:24px;max-width:650px;margin:auto;line-height:1.6}h1{font-size:24px}label{display:block;margin:20px 0}input[type=text]{display:block;width:95%;padding:10px;background:var(--vscode-input-background);color:var(--vscode-input-foreground);border:1px solid var(--vscode-input-border)}button{padding:10px 18px;margin-right:10px;border:0;cursor:pointer;background:var(--vscode-button-background);color:var(--vscode-button-foreground)}small{display:block;opacity:.8}
      </style></head><body><h1>Make this PC a Construct host</h1>
      <p>Install the host service and adopt <b>${html(defaults.name)}</b> with its existing data and settings.</p>
      <p>Host administrator: <b>${html(defaults.adminUser)}</b><br>Your VM stays private. Add other users from Host administration after setup.</p>
      <label>Address other users will connect to<input id="host" type="text" value="${html(defaults.publicHost)}" ${defaults.locked ? "readonly" : ""}><small>A name or IPv4 address for this PC on your LAN or VPN. Service port: 7462.</small></label>
      <label><input id="awake" type="checkbox" ${defaults.keepAwake ? "checked" : ""} ${defaults.locked ? "disabled" : ""}> Keep this PC awake while plugged in<small>Unchecked: retain the current power settings. The host also prevents idle sleep while managed VMs run.</small></label>
      <p>Setup opens an administrator PowerShell window. After Windows asks for approval, installation and VM conversion run automatically. Your VM remains running.</p>
      <button id="install">${defaults.resume ? "Retry setup" : "Install host and adopt VM"}</button><button id="cancel">Cancel</button><p id="error" role="alert"></p>
      <script nonce="${nonce}">const api=acquireVsCodeApi();document.getElementById('install').onclick=()=>{const host=document.getElementById('host').value.trim();if(!/^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$/i.test(host)||host.includes('..')){document.getElementById('error').textContent='Enter a hostname or IPv4 address.';return;}api.postMessage({type:'install',host,keepAwake:document.getElementById('awake').checked});};document.getElementById('cancel').onclick=()=>api.postMessage({type:'cancel'});</script></body></html>`;
  });
}
function convertedRegistry(registry, plan, result) {
  if (!result.ok || result.id !== plan.id || result.name !== plan.name || result.owner !== plan.adminUser ||
      result.url !== `https://${plan.publicHost}:7462` || !Number.isInteger(result.sshPort) || result.sshPort < 1 || result.sshPort > 65535)
    throw new Error("The conversion result does not match this VM's setup request.");
  const current = registry.byName[plan.name];
  const already = current && current.backend === "hyperv-remote" && current.service && remotehost.sameServiceUrl(current.service.url, result.url);
  if (!current || (!already && instances.targetFingerprint(current) !== plan.fingerprint))
    throw new Error("The instance changed during setup. Host adoption succeeded; client conversion needs its original instance restored.");
  // The initiating PC keeps its verified direct SSH endpoint and alias, so open VS Code
  // windows stay connected. Other users receive the public forward from the host API.
  return instances.updateInstance(registry, plan.name, {
    backend: "hyperv-remote", vmName: plan.name, sshHost: current.vmHost, sshPort: current.sshPort,
    owner: result.owner, publicHost: result.publicHost,
    service: { url: result.url, auth: "token" },
  });
}
async function complete(opts, plan) {
  const result = JSON.parse(fs.readFileSync(plan.resultPath, "utf8"));
  if (!result.ok) throw new Error(result.error || "Host setup stopped. Retry from Settings.");
  convertedRegistry(instances.load(), plan, result); // validate before storing credentials
  const privateKey = await opts.context.secrets.get(SECRET_PREFIX + plan.id);
  if (!privateKey) throw new Error("The conversion's client key is unavailable. Reopen setup in the original VS Code profile.");
  const token = crypto.privateDecrypt({ key: privateKey, padding: crypto.constants.RSA_PKCS1_OAEP_PADDING, oaepHash: "sha1" }, Buffer.from(result.encryptedToken, "base64")).toString("utf8");
  const pin = remotehost.readPin(result.url);
  if (pin && !remotehost.fingerprintsMatch(pin, result.fingerprint)) throw new Error("The host certificate differs from this PC's saved pin.");
  const client = remotehost.createClient({ baseUrl: result.url, pin: result.fingerprint, auth: { kind: "token", token } });
  const me = await client.whoami();
  if (String(me.name).toLowerCase() !== result.owner.toLowerCase() || String(me.role).toLowerCase() !== "admin") throw new Error("The host did not verify the expected administrator.");
  const vm = await client.getState(plan.name);
  if (remotehost.mapVmState(vm.state) !== "running") throw new Error("The adopted VM is not reporting running; client settings have been preserved.");
  await opts.context.secrets.store(remotehost.tokenSecretKey(result.url), token);
  remotehost.writePin(result.url, result.fingerprint);
  // Lifecycle consoles read the existing per-user DPAPI store, not VS Code secrets.
  const lib = path.join(plan.scriptsDir, "lib", "AgentVm.Remote.ps1");
  await powershell(`$ErrorActionPreference='Stop'; . ${quote(lib)}; $v=[Console]::In.ReadToEnd() | ConvertFrom-Json; Save-ConstructRemoteToken -BaseUrl $v.url -Token $v.token`, JSON.stringify({ url: result.url, token }));
  await opts.saveRemoteHost({ url: result.url, fingerprint: result.fingerprint, auth: "token", identity: me.name, role: me.role, maxVms: me.maxVms, addedAt: new Date().toISOString() });
  const registry = instances.load();
  instances.save(registry.path, convertedRegistry(registry, plan, result));
  fs.unlinkSync(pendingPath());
  fs.unlinkSync(plan.resultPath);
  await opts.context.secrets.delete(SECRET_PREFIX + plan.id);
  opts.refresh();
  opts.vscode.window.showInformationMessage(`This PC is now a Construct host: ${result.url}. ${plan.name} is adopted and you are its host administrator.`);
}
async function run(opts) {
  if (running) return;
  if (!eligible(opts.instance, opts.connected)) throw new Error("Connect to this PC's local Construct VM to convert it.");
  running = true;
  try {
    let plan = readPending();
    if (plan && plan.name !== opts.instance.name) throw new Error(`Finish the pending host setup for ${plan.name} first.`);
    if (plan && fs.existsSync(plan.resultPath) && JSON.parse(fs.readFileSync(plan.resultPath, "utf8")).ok)
      return await opts.vscode.window.withProgress({ location: opts.vscode.ProgressLocation.Notification, title: "Finishing host conversion…", cancellable: false }, () => complete(opts, plan));
    if (!plan) {
      const cfg = instances.toSshCfg(opts.instance);
      const machine = await ssh.runRemote("cat /etc/machine-id", { cfg });
      if (machine.code !== 0 || !/^[a-f0-9]{32}$/.test(machine.stdout.trim())) throw new Error("The VM must answer SSH before conversion.");
      const pc = JSON.parse(await powershell(`$ErrorActionPreference='Stop'; $fqdn=$env:COMPUTERNAME; try { $fqdn=[Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName } catch {}; $lan=Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1; @{adminUser=[Security.Principal.WindowsIdentity]::GetCurrent().Name;hostName=$fqdn;ip=($lan.IPv4Address.IPAddress | Select-Object -First 1)} | ConvertTo-Json -Compress`));
      let publicHost = validHost(pc.hostName) ? pc.hostName : os.hostname();
      const dns = await ssh.runRemote(`getent ahostsv4 ${quote(publicHost)}`, { cfg });
      if (dns.code !== 0 && validHost(pc.ip)) publicHost = pc.ip;
      if (!fs.existsSync(ssh.keyPath(cfg))) throw new Error("The local VM's saved SSH key could not be found.");
      plan = { id: crypto.randomBytes(16).toString("hex"), name: opts.instance.name, vmName: opts.instance.vmName,
        scriptsDir: opts.scriptsDir, adminUser: pc.adminUser, publicHost, keepAwake: false,
        machineId: machine.stdout.trim(), sshHost: opts.instance.vmHost, sshPort: opts.instance.sshPort,
        keyPath: ssh.keyPath(cfg), knownHosts: path.join(os.homedir(), ".ssh", "known_hosts"),
        fingerprint: instances.targetFingerprint(opts.instance), ubuntu: opts.ubuntu || "24.04" };
    }
    let locked = false;
    try { locked = JSON.parse(fs.readFileSync(plan.resultPath, "utf8")).hostInstalled === true; } catch (_) {}
    const choices = await review(opts.vscode, { ...plan, resume: !!readPending(), locked });
    if (!choices || !opts.stillCurrent()) return;
    Object.assign(plan, choices);
    if (!plan.publicKeyXml) {
      const pair = crypto.generateKeyPairSync("rsa", { modulusLength: 2048 });
      const jwk = pair.publicKey.export({ format: "jwk" });
      plan.publicKeyXml = `<RSAKeyValue><Modulus>${Buffer.from(jwk.n, "base64url").toString("base64")}</Modulus><Exponent>${Buffer.from(jwk.e, "base64url").toString("base64")}</Exponent></RSAKeyValue>`;
      plan.resultPath = path.join(path.dirname(pendingPath()), `host-conversion-${plan.id}.result.json`);
      await opts.context.secrets.store(SECRET_PREFIX + plan.id, pair.privateKey.export({ format: "pem", type: "pkcs8" }));
      fs.mkdirSync(path.dirname(pendingPath()), { recursive: true });
      fs.writeFileSync(pendingPath(), JSON.stringify(plan), { flag: "wx" });
    } else {
      const temp = pendingPath() + ".tmp." + process.pid;
      fs.writeFileSync(temp, JSON.stringify(plan));
      fs.renameSync(temp, pendingPath());
    }
    if (fs.existsSync(plan.resultPath)) fs.unlinkSync(plan.resultPath);
    await opts.vscode.window.withProgress({ location: opts.vscode.ProgressLocation.Notification, title: "Installing Construct host and adopting your VM…", cancellable: false }, async () => {
      try { await powershell(launchScript(plan)); }
      catch (error) {
        if (!fs.existsSync(plan.resultPath)) throw error;
      }
      await complete(opts, plan);
    });
  } finally { running = false; }
}
function pendingStatus(name) {
  const plan = readPending();
  if (!plan || plan.name !== name) return null;
  try {
    const result = JSON.parse(fs.readFileSync(plan.resultPath, "utf8"));
    return result.ok ? { ready: true, message: "Host installation finished. Finish conversion to verify access and connect this window." }
      : { ready: false, message: result.error || "Host setup stopped. Review and retry setup." };
  } catch (_) { return { ready: false, message: "Host setup is pending. Its PowerShell window shows installation progress." }; }
}
function watchPending(opts, runtime = {}) {
  if ((runtime.platform || process.platform) !== "win32") return { dispose() {} };
  let attempted = "";
  const timer = (runtime.setInterval || setInterval)(async () => {
    if (running) return;
    const plan = readPending();
    if (!plan || !fs.existsSync(plan.resultPath)) return;
    let result;
    try { result = JSON.parse(fs.readFileSync(plan.resultPath, "utf8")); } catch (_) { return; }
    if (attempted === plan.id + ":" + String(result.ok)) return;
    attempted = plan.id + ":" + String(result.ok);
    opts.refresh();
    const action = result.ok ? "Finish host conversion" : "Review host setup";
    const text = result.ok ? `Host installation finished for ${plan.name}. Finish conversion to verify access and connect VS Code.`
      : `Host setup for ${plan.name} stopped: ${result.error || "See the installer console for details."}`;
    const pick = await opts.vscode.window[result.ok ? "showInformationMessage" : "showErrorMessage"](text, action);
    if (pick !== action || running) return;
    if (!result.ok) { if (opts.review) await opts.review(); return; }
    running = true;
    try { await opts.vscode.window.withProgress({ location: opts.vscode.ProgressLocation.Notification, title: "Finishing host conversion…", cancellable: false }, () => complete(opts, plan)); }
    catch (error) { opts.vscode.window.showErrorMessage("Host setup needs attention: " + error.message); }
    finally { running = false; }
  }, 2000);
  return { dispose() { (runtime.clearInterval || clearInterval)(timer); } };
}
module.exports = { eligible, validHost, launchScript, convertedRegistry, pendingPath, pendingStatus, run, watchPending };
