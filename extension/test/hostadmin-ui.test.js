"use strict";
// Plain-node tests for src/hostadmin-ui.js — the VS Code adapter of the host-administration
// module — driven with a FAKE vscode API (modals, input boxes, webview panels, clipboard
// are scripted and recorded) and a fake remotehost client. Nothing here touches a socket.
// Run: node hostadmin-ui.test.js
const path = require("path");
const ui = require("../src/hostadmin-ui");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);

function apiErr(status, body, message) {
  const e = new Error(message || `HTTP ${status}`);
  e.status = status; e.body = body == null ? null : body;
  e.code = body && typeof body === "object" && typeof body.code === "string" ? body.code : "";
  return e;
}
const HEALTH = { status: "ok", apiFeatures: ["host-admin", "children", "media", "console", "updates", "network"] };
const ME_ADMIN = { name: "DOMAIN\\alice", known: true, role: "admin", enabled: true };
const ME_USER = { name: "DOMAIN\\bob", known: true, role: "user", enabled: true };
const HOST = { url: "https://buildbox.example.local:7462", auth: "token", fingerprint: "", identity: "DOMAIN\\alice", role: "admin" };
const INST = { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm", service: { url: "https://buildbox.example.local:7462", auth: "token" } };

/** A scripted client (see hostadmin.test.js). */
function fakeClient(answers = {}) {
  const calls = [];
  const c = { host: "buildbox.example.local", calls };
  for (const m of ["health", "whoami", "hostStatus", "hostCapacity", "hostConfig", "putHostConfig", "hostCapabilities", "isoCatalog", "users", "createUser", "updateUser", "deleteUser",
    "putUserAllowance", "userTokens", "issueUserToken", "revokeUserToken", "vms", "children", "lifecycle", "deleteVm", "getJob", "overrides", "putOverrides", "deleteOverrides",
    "rotateVmToken", "revokeVmToken", "media", "deleteMedia", "mediaCleanup", "jobs", "cancelJob", "audit", "updatesStatus", "updatesCheck", "updatesStage", "updatesApply", "updatesCancel", "updatesResolve"]) {
    c[m] = async (...args) => {
      calls.push({ method: m, args });
      const a = answers[m];
      if (a instanceof Error) throw a;
      if (typeof a === "function") return a(...args);
      if (a === undefined) return ["users", "vms", "jobs", "audit", "media", "children", "userTokens"].indexOf(m) >= 0 ? [] : {};
      return a;
    };
  }
  return c;
}

/** The fake vscode API: every dialog answer comes from `script`, every call is recorded. */
function fakeVscode(script = {}) {
  const rec = { warnings: [], infos: [], inputs: [], panels: [], clipboard: [], quickPicks: [] };
  const uri = (...parts) => ({ fsPath: path.join(...parts), toString: () => "vscode-resource:" + parts.join("/") });
  const api = {
    rec,
    ViewColumn: { Active: -1 },
    ProgressLocation: { Notification: 15 },
    Uri: { joinPath: (base, ...rest) => uri(base.fsPath, ...rest), file: (p) => uri(p) },
    env: { clipboard: { writeText: async (t) => { rec.clipboard.push(t); } } },
    window: {
      showWarningMessage: async (message, opts, ...items) => {
        rec.warnings.push({ message, detail: opts && opts.detail, modal: !!(opts && opts.modal), items });
        const a = typeof script.warning === "function" ? script.warning(message, items) : script.warning;
        return a === true ? items[0] : (typeof a === "string" ? a : undefined);
      },
      showInformationMessage: async (message, opts, ...items) => {
        rec.infos.push({ message, detail: opts && opts.detail, items: items.length ? items : (Array.isArray(opts) ? opts : []) });
        const a = typeof script.info === "function" ? script.info(message, items) : script.info;
        return a === true ? items[0] : (typeof a === "string" ? a : undefined);
      },
      showInputBox: async (opts) => {
        rec.inputs.push(opts);
        const v = typeof script.input === "function" ? script.input(opts) : script.input;
        if (v != null && opts && opts.validateInput && opts.validateInput(v)) return undefined;
        return v;
      },
      showQuickPick: async (items, opts) => { rec.quickPicks.push({ items, opts }); return typeof script.pick === "function" ? script.pick(items) : undefined; },
      withProgress: async (_opts, fn) => fn(),
      createWebviewPanel: (viewType, title) => {
        const listeners = [];
        const panel = {
          viewType, title, disposed: false, revealed: 0, posted: [],
          webview: {
            html: "", cspSource: "vscode-webview://x",
            asWebviewUri: (u) => u,
            postMessage: (m) => { panel.posted.push(m); return Promise.resolve(true); },
            onDidReceiveMessage: (cb) => { listeners.push(cb); return { dispose() {} }; },
          },
          reveal: () => { panel.revealed++; },
          dispose: () => { panel.disposed = true; (panel._onDispose || []).forEach((f) => f()); },
          onDidDispose: (cb) => { (panel._onDispose = panel._onDispose || []).push(cb); return { dispose() {} }; },
          send: (m) => Promise.all(listeners.map((cb) => cb(m))),
        };
        rec.panels.push(panel);
        return panel;
      },
    },
  };
  return api;
}

function makeFeature(opts = {}) {
  const vscode = fakeVscode(opts.script);
  const timers = { calls: [], setTimeout: (fn, ms) => { timers.calls.push({ fn, ms }); return { unref() {} }; }, clearTimeout: () => {} };
  const logs = [];
  const client = opts.client || fakeClient({ health: HEALTH, whoami: ME_ADMIN });
  let nowMs = 1000;
  const feature = ui.createHostAdminFeature({
    vscode,
    context: { extensionUri: { fsPath: path.join(__dirname, "..") } },
    log: (l) => logs.push(l),
    remoteHosts: () => opts.hosts || [HOST],
    clientFor: async () => (opts.noClient ? null : client),
    instanceClient: async () => (opts.noClient ? { client: null, problem: "the API token is gone" } : { client, problem: "" }),
    queryChildren: async () => opts.children || { supported: true, items: [], problem: "" },
    registryList: () => opts.instances || [INST],
    activeInstance: () => INST,
    addRemoteHost: async () => { logs.push("addRemoteHost"); },
    newRemoteVm: async (h) => { logs.push("newRemoteVm " + h.url); },
    refreshAll: () => { logs.push("refreshAll"); },
    themeCssFile: () => "themes/native.css",
    nonce: () => "NONCE123",
    now: () => nowMs,
    timers,
    sleep: async () => {},
  });
  return { feature, vscode, client, logs, timers, setNow: (v) => { nowMs = v; } };
}

/** Open the panel and play the webview's `ready`. */
async function openReady(t) {
  const entry = await t.feature.openHostAdmin(HOST);
  await entry.panel.send({ type: "hostadmin.ready" });
  return entry;
}
const lastState = (entry) => [...entry.panel.posted].reverse().find((m) => m.type === "hostadmin.state").state;

(async () => {
  console.log("\n=== the panel ===");
  {
    const t = makeFeature();
    const entry = await openReady(t);
    const html = entry.panel.webview.html;
    ok("panel: the HTML is the media file with every placeholder replaced", html.indexOf("{{") < 0 && html.indexOf("nonce-NONCE123") >= 0);
    ok("panel: it layers panel.css, the chosen theme and hostadmin.css", /panel\.css/.test(html) && /themes\/native\.css/.test(html) && /hostadmin\.css/.test(html));
    const s = lastState(entry);
    eq("panel: ready -> detect -> admin state posted", s.mode, "admin");
    eq("panel: the overview tab was loaded", s.activeTab, "overview");
    ok("panel: the overview data is there", !!s.overview);
    ok("panel: every tab available on a full-feature host", s.tabs.every((x) => x.available));
    eq("panel: no first-VM offer while the host has one of ours", s.offers.createFirstVm, false);
    const again = await t.feature.openHostAdmin(HOST);
    ok("panel: opening again reveals the same panel", again === entry && entry.panel.revealed === 1 && t.vscode.rec.panels.length === 1);
    await entry.panel.send({ type: "hostadmin.tab", tab: "users" });
    eq("panel: a tab message loads that tab", lastState(entry).activeTab, "users");
    ok("panel: ...and the users were read", t.client.calls.some((c) => c.method === "users"));
    const before = t.client.calls.length;
    await entry.panel.send({ type: "hostadmin.tab", tab: "../evil" });
    eq("panel: an unknown tab id is ignored", t.client.calls.length, before);
    ok("panel: the picker offers the host-administration row once the host is known admin",
      t.feature.pickerItems().some((i) => i.kind === "hostAdmin" && i.url === HOST.url));
    entry.panel.dispose();
    ok("panel: dispose forgets the panel", t.feature._panels.size === 0);
    const reopened = await t.feature.openHostAdmin(HOST);
    ok("panel: ...so the next open creates a new one", reopened !== entry && t.vscode.rec.panels.length === 2);
  }
  {
    const t = makeFeature({ client: fakeClient({ health: HEALTH, whoami: ME_USER }), instances: [] });
    const entry = await openReady(t);
    const s = lastState(entry);
    eq("panel: an ordinary user sees the user state", s.mode, "user");
    ok("panel: ...nothing was loaded", !t.client.calls.some((c) => c.method === "hostStatus"));
    eq("panel: ...with the first-VM offer when they own nothing on the host", s.offers.createFirstVm, true);
    await entry.panel.send({ type: "hostadmin.action", action: "createFirstVm", args: {} });
    ok("panel: the offer runs the existing New VM flow for THAT host", t.logs.some((l) => l === "newRemoteVm " + HOST.url));
    ok("panel: the picker offers 'Create first VM' for the host", t.feature.pickerItems().some((i) => i.kind === "createFirstVm"));
    ok("panel: ...and no host-administration row for a non-admin", !t.feature.pickerItems().some((i) => i.kind === "hostAdmin"));
  }
  {
    const t = makeFeature({ noClient: true });
    const entry = await openReady(t);
    const s = lastState(entry);
    eq("panel: a missing credential is the unavailable state", s.mode, "unavailable");
    ok("panel: ...naming the enrolment command", /Add Remote Host/.test(s.message));
    await entry.panel.send({ type: "hostadmin.signIn" });
    ok("panel: Sign in runs the enrolment flow and re-detects", t.logs.indexOf("addRemoteHost") >= 0);
  }
  {
    const t = makeFeature({ client: fakeClient({ health: apiErr(404, null), whoami: ME_ADMIN }) });
    const entry = await openReady(t);
    eq("panel: an old service is the old-service state", lastState(entry).mode, "old-service");
  }

  console.log("\n=== maintenance ===");
  {
    const t = makeFeature({ client: fakeClient({ health: { ...HEALTH, status: "maintenance", maintenance: { phase: "draining", retryAfterSeconds: 5 } }, whoami: ME_ADMIN }) });
    const entry = await openReady(t);
    ok("maintenance: the banner rides the state", lastState(entry).maintenance && lastState(entry).maintenance.phase === "draining");
    ok("maintenance: a 5 s re-probe is scheduled", t.timers.calls.some((c) => c.ms === 5000));
    const r = await entry.panel.send({ type: "hostadmin.action", action: "mediaCleanup", args: {} });
    ok("maintenance: mutations are refused without a call", !t.client.calls.some((c) => c.method === "mediaCleanup"));
    ok("maintenance: ...and the refusal is a notice", /updating/.test(lastState(entry).notice && lastState(entry).notice.text));
    entry.panel.dispose();
    const fired = t.timers.calls.length;
    t.timers.calls.forEach((c) => c.fn());
    eq("maintenance: a disposed panel schedules nothing more", t.timers.calls.length, fired);
  }

  console.log("\n=== the cascade dialog (§10.4) ===");
  {
    let deletes = 0;
    const client = fakeClient({
      health: HEALTH, whoami: ME_ADMIN,
      deleteVm: (name, body) => {
        deletes++;
        if (body && body.cascade && body.cascade.token === "TOK2") return { jobId: "j9" };
        if (body && body.cascade && body.cascade.token === "TOK1") {
          throw apiErr(409, { code: "cascade-scope-changed", cascadeToken: "TOK2", expiresAt: "2026-09-07T10:10:00Z", children: [{ name: "child-a", state: "running", sharing: "private", diskGb: 80 }, { name: "child-c", state: "off", sharing: "host", diskGb: 10 }] });
        }
        throw apiErr(409, { code: "cascade-confirmation-required", cascadeToken: "TOK1", expiresAt: "2026-09-07T10:10:00Z", children: [{ name: "child-a", state: "running", sharing: "private", diskGb: 80 }, { name: "child-b", state: "saved", sharing: "host", diskGb: 40, mediaCount: 1 }] });
      },
    });
    const t = makeFeature({ client, script: { warning: true, input: "work-vm" } });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.action", action: "deleteVm", args: { name: "work-vm", kind: "primary" } });
    const w = t.vscode.rec.warnings;
    ok("cascade: the first DELETE (no body) got the confirmation", deletes >= 2);
    const dlg = w.find((x) => /Delete "work-vm" and its 2 child VMs\?/.test(x.message));
    ok("cascade: a modal lists ALL children including shared ones", !!dlg && dlg.modal && /child-a\s+running\s+private\s+80 GB disk/.test(dlg.detail));
    ok("cascade: ...the shared child is highlighted", dlg && /child-b\s+saved\s+SHARED HOST-WIDE \(other users may be using it\)\s+40 GB disk\s+1 dedicated media/.test(dlg.detail));
    ok("cascade: ...and permanent disk removal is stated", dlg && /virtual disks, saved state and dedicated media are removed permanently/.test(dlg.detail));
    ok("cascade: the typed name is required", t.vscode.rec.inputs.length >= 1 && t.vscode.rec.inputs[0].validateInput("work-v") !== null && t.vscode.rec.inputs[0].validateInput("work-vm") === null);
    const bodies = client.calls.filter((c) => c.method === "deleteVm").map((c) => c.args[1]);
    ok("cascade: retried with the token", bodies.some((b) => b && b.cascade && b.cascade.token === "TOK1"));
    const changed = w.find((x) => /children changed since the confirmation/.test(x.message));
    ok("cascade: a scope change re-opens the dialog with the NEW list", !!changed && w.some((x) => /child-c/.test(x.detail || "")));
    ok("cascade: ...and the new token is used", bodies.some((b) => b && b.cascade && b.cascade.token === "TOK2"));
    ok("cascade: accepted in the end", /Deletion of work-vm accepted \(job j9\)/.test(lastState(entry).notice.text));
  }
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN, deleteVm: () => { throw apiErr(409, { code: "cascade-confirmation-required", cascadeToken: "T", children: [{ name: "c" }] }); } });
    const t = makeFeature({ client, script: { warning: true, input: "wrong-name" } });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.action", action: "deleteVm", args: { name: "work-vm", kind: "primary" } });
    eq("cascade: a wrong typed name sends no confirmed delete", client.calls.filter((c) => c.method === "deleteVm" && c.args[1]).length, 0);
  }
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN, vms: [{ name: "work-vm-a1", kind: "child", parent: "work-vm", sharing: "host", state: "running", allowedActions: ["delete"] }] });
    const t = makeFeature({ client, script: { warning: false } });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.tab", tab: "vms" });
    await entry.panel.send({ type: "hostadmin.action", action: "deleteVm", args: { name: "work-vm-a1", kind: "child" } });
    const dlg = t.vscode.rec.warnings.find((x) => /Delete the child VM "work-vm-a1"\?/.test(x.message));
    ok("child delete: the smaller modal names the child, its sharing and the permanent removal",
      !!dlg && /SHARED HOST-WIDE/.test(dlg.detail) && /disk, saved state and dedicated media are removed permanently/.test(dlg.detail));
    ok("child delete: cancelling sends nothing", !client.calls.some((c) => c.method === "deleteVm"));
  }

  console.log("\n=== shutdown is graceful and honest ===");
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN, lifecycle: { jobId: "j1" }, getJob: { id: "j1", state: "failed", error: "guest-shutdown-unavailable" } });
    const t = makeFeature({ client, script: { warning: true } });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.action", action: "shutdownVm", args: { name: "work-vm-a1" } });
    const call = client.calls.find((c) => c.method === "lifecycle");
    ok("shutdown: the lifecycle route with action shutdown", call && call.args[0] === "work-vm-a1" && call.args[1].action === "shutdown");
    ok("shutdown: never /power", !client.calls.some((c) => c.method === "power"));
    ok("shutdown: an unavailable guest shutdown is reported as a failure, not a success",
      t.vscode.rec.warnings.some((x) => /could NOT be shut down gracefully/.test(x.message)) && !t.vscode.rec.infos.some((x) => /shut down \(guest observed off\)/.test(x.message)));
  }

  console.log("\n=== secrets ===");
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN, issueUserToken: { id: "t1", token: "PLAINTEXT-ONE" }, rotateVmToken: { vmToken: "VM-PLAINTEXT", kind: "primary" } });
    const t = makeFeature({ client, script: { warning: true, info: "Copy to clipboard" } });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.action", action: "issueToken", args: { name: "bob", label: "laptop" } });
    const shown = t.vscode.rec.infos.find((x) => /API token for bob/.test(x.message));
    ok("secrets: the token is shown once in a modal", !!shown && shown.detail.indexOf("PLAINTEXT-ONE") >= 0 && /shown once/.test(shown.detail));
    eq("secrets: Copy puts it on the clipboard", t.vscode.rec.clipboard[0], "PLAINTEXT-ONE");
    await entry.panel.send({ type: "hostadmin.action", action: "rotateVmToken", args: { name: "work-vm" } });
    ok("secrets: rotation asks first and shows the VM token once", t.vscode.rec.warnings.some((x) => /Rotate the VM token of "work-vm"/.test(x.message)) && t.vscode.rec.infos.some((x) => (x.detail || "").indexOf("VM-PLAINTEXT") >= 0));
    const everything = JSON.stringify(entry.panel.posted) + t.logs.join("\n") + JSON.stringify(entry.model.state);
    ok("secrets: neither plaintext reached the webview, the log or the state", everything.indexOf("PLAINTEXT-ONE") < 0 && everything.indexOf("VM-PLAINTEXT") < 0);
  }

  console.log("\n=== refusal flips the module ===");
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN, users: apiErr(403, null) });
    const t = makeFeature({ client });
    const entry = await openReady(t);
    await entry.panel.send({ type: "hostadmin.tab", tab: "users" });
    eq("refusal: a 403 on a load posts the user state", lastState(entry).mode, "user");
    ok("refusal: ...and the offers cache follows (no admin row in the picker)", !t.feature.pickerItems().some((i) => i.kind === "hostAdmin"));
  }

  console.log("\n=== the control panel's minimal user view ===");
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_USER, lifecycle: { jobId: "j5" }, getJob: { id: "j5", state: "succeeded", result: { outcome: "completed" } }, deleteVm: { jobId: "j6" } });
    const t = makeFeature({
      client, script: { warning: true },
      children: { supported: true, items: [{ name: "work-vm-a1", state: "running", sharing: "host", lease: { state: "active", expiresAt: "2026-09-07T12:00:00Z" }, allowedActions: ["shutdown", "delete"] }], problem: "" },
    });
    const card = await t.feature.childrenStateFor(INST);
    ok("children: the card state for a supporting host", card && card.visible && card.items.length === 1 && card.items[0].shared);
    eq("children: a local instance gets null", await t.feature.childrenStateFor({ name: "agent-vm", backend: "hyperv-local" }), null);
    let handled = await t.feature.handlePanelCommand("childShutdown", { child: "not-listed" }, INST);
    ok("children: an unlisted child name is refused (untrusted webview) and handled", handled && t.vscode.rec.warnings.some((x) => /not a child VM of work-vm/.test(x.message)) && !client.calls.some((c) => c.method === "lifecycle"));
    handled = await t.feature.handlePanelCommand("childShutdown", { child: "work-vm-a1" }, INST);
    ok("children: Shut down asks, then requests a graceful shutdown", handled && client.calls.some((c) => c.method === "lifecycle" && c.args[1].action === "shutdown"));
    ok("children: ...and reports the observed outcome", t.vscode.rec.infos.some((x) => /shut down \(guest observed off\)/.test(x.message)));
    ok("children: ...never a power action", !client.calls.some((c) => c.method === "power"));
    handled = await t.feature.handlePanelCommand("childDelete", { child: "work-vm-a1" }, INST);
    const dlg = t.vscode.rec.warnings.find((x) => /Delete the child VM "work-vm-a1"\?/.test(x.message));
    ok("children: Delete confirms with the child's sharing and the permanent removal", handled && dlg && /SHARED HOST-WIDE/.test(dlg.detail) && /removed permanently/.test(dlg.detail));
    ok("children: ...then DELETEs the child and refreshes", client.calls.some((c) => c.method === "deleteVm" && c.args[0] === "work-vm-a1") && t.logs.indexOf("refreshAll") >= 0);
    eq("children: other command ids are not ours", await t.feature.handlePanelCommand("reprovision", {}, INST), false);
    ok("children: openHostAdmin opens the instance's host panel", await t.feature.handlePanelCommand("openHostAdmin", {}, INST) && t.vscode.rec.panels.length === 1);
  }
  {
    const t = makeFeature({ noClient: true, children: { supported: false, items: [], problem: "old" } });
    eq("children: no credential hides the card", await t.feature.childrenStateFor(INST), null);
  }
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_USER });
    const t = makeFeature({ client, children: { supported: true, items: null, problem: "HTTP 500" } });
    const card = await t.feature.childrenStateFor(INST);
    ok("children: a failed read on a supporting host keeps the card with the problem", card && card.visible && card.problem === "HTTP 500");
  }

  console.log("\n=== the Host offer is per host, cached, re-evaluated ===");
  {
    const client = fakeClient({ health: HEALTH, whoami: ME_ADMIN });
    const t = makeFeature({ client });
    const offer = await t.feature.hostAdminOfferFor(INST);
    ok("offer: an admin identity yields the offer", offer && offer.host === "buildbox.example.local");
    const probes = client.calls.filter((c) => c.method === "whoami").length;
    await t.feature.hostAdminOfferFor(INST);
    eq("offer: cached inside the TTL", client.calls.filter((c) => c.method === "whoami").length, probes);
    t.setNow(1000 + ui.OFFER_TTL_MS + 1);
    await t.feature.hostAdminOfferFor(INST);
    eq("offer: re-probed after the TTL", client.calls.filter((c) => c.method === "whoami").length, probes + 1);
    eq("offer: a local instance has none", await t.feature.hostAdminOfferFor({ name: "agent-vm", backend: "hyperv-local" }), null);
    eq("offer: an instance on an un-enrolled host has none", await t.feature.hostAdminOfferFor({ ...INST, service: { url: "https://other:7462" } }), null);
  }
  {
    const t = makeFeature({ client: fakeClient({ health: HEALTH, whoami: ME_USER }) });
    eq("offer: a user identity yields none (the module is absent)", await t.feature.hostAdminOfferFor(INST), null);
  }
  {
    // The background probe must be SILENT: it uses offerClient, never the prompting
    // clientFor — a missing token is "no offer", not a toast every minute.
    const vscode = fakeVscode({});
    let offerCalls = 0, userCalls = 0;
    const feature = ui.createHostAdminFeature({
      vscode, context: { extensionUri: { fsPath: path.join(__dirname, "..") } },
      remoteHosts: () => [HOST], registryList: () => [INST],
      clientFor: async () => { userCalls++; vscode.window.showWarningMessage("The API token is no longer stored"); return null; },
      offerClient: async () => { offerCalls++; return null; },
      queryChildren: async () => ({ supported: false, items: [], problem: "" }),
      now: () => 1000, timers: { setTimeout: () => ({ unref() {} }), clearTimeout() {} },
    });
    eq("offer: a missing token yields no offer", await feature.hostAdminOfferFor(INST), null);
    ok("offer: ...through the silent factory only", offerCalls === 1 && userCalls === 0);
    eq("offer: ...and no warning was shown", vscode.rec.warnings.length, 0);
    eq("offer: the children read never uses the prompting factory either", await feature.childrenStateFor(INST), null);
    eq("offer: ...still no warning", vscode.rec.warnings.length, 0);
  }
  {
    const t = makeFeature({ client: fakeClient({ health: apiErr(0, null, "down") }) });
    eq("offer: an unreachable host yields none", await t.feature.hostAdminOfferFor(INST), null);
  }

  console.log("\n=== wiring in extension.js ===");
  {
    const fs = require("fs");
    const extSrc = fs.readFileSync(path.join(__dirname, "..", "extension.js"), "utf8");
    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));
    ok("wiring: the command is registered and contributed",
      extSrc.indexOf('registerCommand("construct.openHostAdmin"') >= 0 && pkg.contributes.commands.some((c) => c.command === "construct.openHostAdmin"));
    ok("wiring: one feature block builds the adapter with injected deps", /hostadminui\.createHostAdminFeature\(\{/.test(extSrc) && /clientFor: \(entry\) => remoteClientFor\(entry\)/.test(extSrc));
    ok("wiring: the offer probe gets the silent driverOpts-based factory", /offerClient: async \(entry\) => \{[\s\S]*?await driverOpts\(inst\)[\s\S]*?hypervRemote\.resolveClient\(inst/.test(extSrc));
    ok("wiring: the state push carries children + hostAdminOffer like idlePolicy", /extra\.children = cachedChildren;/.test(extSrc) && /extra\.hostAdminOffer = cachedHostAdminOffer;/.test(extSrc));
    ok("wiring: the refresh reads them after the idle policy", /await readHostAdminExtras\(inst\);/.test(extSrc));
    ok("wiring: the four panel commands are forwarded to the feature", /id === "openHostAdmin" \|\| id === "createFirstVm" \|\| id === "childShutdown" \|\| id === "childDelete"/.test(extSrc));
    ok("wiring: the picker appends the feature's rows and lets it handle them", /\.concat\(hostRows\)/.test(extSrc) && /handlePickerItem\(pick\)/.test(extSrc));
    ok("wiring: the forwarder transport gets the service's features", /createRemoteTransport\(\{ ssh, cfg, client, features \}\)/.test(extSrc));
    ok("wiring: a switch clears the caches", /cachedChildren = null; cachedHostAdminOffer = null; cachedHostAdminInstance = null;/.test(extSrc));
    ok("wiring: deactivate disposes the feature", /hostAdmin\.dispose\(\)/.test(extSrc));
  }

  console.log(`\n  host-administration adapter tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
