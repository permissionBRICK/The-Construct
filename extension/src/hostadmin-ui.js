"use strict";
// HOST ADMINISTRATION — the VS Code ADAPTER (the pattern of src/forwarder-ui.js): the
// ONLY file of the feature that touches the vscode API. Everything it decides comes
// from src/hostadmin.js (pure) and everything it sends goes through a src/remotehost.js
// client the extension layer built for the host (credential from SecretStorage).
//
// What lives here and nowhere else:
//   • the webview panel per enrolled host (media/hostadmin.*) and its message routing
//     (`hostadmin.*` namespace — extension/ARCHITECTURE.md "Host administration");
//   • the MODAL confirmations: child Delete, the §10.4 cascade dialog with its typed
//     name and its retry on `cascade-scope-changed`, user removal, token revocation,
//     update apply/resolve;
//   • the one-time display of a secret (a user token, a rotated VM token): shown in a
//     modal with a Copy button, never logged, never posted to a webview;
//   • the control panel's minimal user view actions (child Shut down / Delete) and its
//     "Host administration" / "Create first Construct VM here" offers;
//   • the 5 s maintenance poll while a host is updating.
//
// `deps` is injected by extension.js (see createHostAdminFeature), and `deps.vscode` may
// be a fake — extension/test/hostadmin-ui.test.js drives the whole adapter that way.

const path = require("path");
const fs = require("fs");
const hostadmin = require("./hostadmin");
const remotehost = require("./remotehost");

/** How long a host's admin/user classification is trusted for the panel offers. */
const OFFER_TTL_MS = 60000;
/** How long a `vm-shutdown` job is followed before the panel stops waiting (2 s × 150). */
const SHUTDOWN_WAIT = { attempts: 150, delayMs: 2000 };

const str = (v) => (v == null ? "" : String(v)).trim();
const errText = (e) => (e && e.message ? String(e.message) : String(e));

/**
 * Build the feature. `deps`:
 *   vscode            the API (or a fake)
 *   context           the ExtensionContext (extensionUri, subscriptions)
 *   log               (line) => void — never receives a secret
 *   remoteHosts       () => enrolment records [{ url, auth, fingerprint, identity, role }]
 *   clientFor         async (hostEntry) => remotehost client | null (token gone → null after a message;
 *                     the USER-INITIATED factory: open panel, refresh, sign-in, actions)
 *   offerClient       async (hostEntry) => remotehost client | null — the SILENT factory the
 *                     background offer probe uses (no prompt, no toast; a missing token is
 *                     simply "no offer"). Falls back to clientFor only when absent.
 *   instanceClient    async (instance) => { client, problem } for a registry instance (silent)
 *   queryChildren     async (instance) => { supported, items, problem } (hyperv-remote.queryChildren)
 *   registryList      () => instances.list(registryNow())
 *   activeInstance    () => the window's active instance
 *   addRemoteHost     async () => void   (the existing enrolment command)
 *   newRemoteVm       async (hostEntry) => void   (the existing create flow)
 *   refreshAll        () => void
 *   themeCssFile      () => "themes/<id>.css"
 *   nonce             () => a CSP nonce
 *   now               () => ms (tests)
 *   timers            { setTimeout, clearTimeout } (tests)
 */
function createHostAdminFeature(deps = {}) {
  const vscode = deps.vscode;
  if (!vscode) throw new Error("hostadmin-ui: the vscode API (or a fake) is required");
  const log = typeof deps.log === "function" ? deps.log : () => {};
  const now = typeof deps.now === "function" ? deps.now : () => Date.now();
  const timers = deps.timers || { setTimeout, clearTimeout };
  const remoteHosts = deps.remoteHosts || (() => []);
  const registryList = deps.registryList || (() => []);

  /** url(lower) -> { panel, model, hostEntry, pollTimer, children: Map } */
  const panels = new Map();
  /** url(lower) -> { mode, at } — the panel offers' cached classification. */
  const offers = new Map();
  /** instance name -> { supported, items, problem, at } — the last children read. */
  const childrenCache = new Map();

  // ── Helpers ───────────────────────────────────────────────────────────────
  const key = (url) => { try { return remotehost.normalizeServiceUrl(url).toLowerCase(); } catch (_) { return str(url).toLowerCase(); } };
  const hostOf = (url) => { try { return remotehost.urlParts(url).host; } catch (_) { return str(url); } };
  const same = (a, b) => remotehost.sameServiceUrl(a, b);

  function hostEntryFor(inst) { return hostadmin.hostEntryFor(inst, remoteHosts(), same); }

  async function modal(message, detail, confirmLabel, kind) {
    const fn = kind === "info" ? vscode.window.showInformationMessage : vscode.window.showWarningMessage;
    const pick = await fn.call(vscode.window, message, { modal: true, detail }, confirmLabel);
    return pick === confirmLabel;
  }

  /** Show a one-time secret. Never logged; the only copy lives in the dialog. */
  async function showSecretOnce(title, secret, note) {
    if (!secret) {
      vscode.window.showWarningMessage(`${title}: the service returned no plaintext. Nothing was changed on this PC.`);
      return;
    }
    const COPY = "Copy to clipboard";
    const pick = await vscode.window.showInformationMessage(title, { modal: true, detail: `${note}\n\n${secret}\n\nThis is shown once and is not stored anywhere by The Construct.` }, COPY);
    if (pick === COPY && vscode.env && vscode.env.clipboard) {
      try { await vscode.env.clipboard.writeText(secret); } catch (e) { vscode.window.showWarningMessage("Could not copy: " + errText(e)); }
    }
  }

  // ── The webview ───────────────────────────────────────────────────────────
  function buildHtml(webview, extensionUri) {
    const mediaUri = (file) => webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file)).toString();
    const html = fs.readFileSync(path.join(extensionUri.fsPath, "media", "hostadmin.html"), "utf8");
    const theme = typeof deps.themeCssFile === "function" ? deps.themeCssFile() : "themes/classic.css";
    return html
      .replace(/{{cspSource}}/g, webview.cspSource)
      .replace(/{{styleUri}}/g, mediaUri("panel.css"))
      .replace(/{{themeUri}}/g, mediaUri(theme))
      .replace(/{{adminStyleUri}}/g, mediaUri("hostadmin.css"))
      .replace(/{{scriptUri}}/g, mediaUri("hostadmin.js"))
      .replace(/{{nonce}}/g, typeof deps.nonce === "function" ? deps.nonce() : "nonce");
  }

  function postState(entry) {
    const s = entry.model.state;
    const state = {
      ...s,
      offers: { createFirstVm: hostadmin.firstVmOffers({ hosts: [entry.hostEntry], instances: registryList(), sameUrl: same }).length > 0 },
    };
    try { entry.panel.webview.postMessage({ type: "hostadmin.state", state }); } catch (_) { /* disposed */ }
    offers.set(key(entry.hostEntry.url), { mode: s.mode, at: now() });
    schedulePoll(entry);
  }

  /** The 5 s `/health` poll while the host is updating (§10.3); cleared otherwise. */
  function schedulePoll(entry) {
    if (entry.pollTimer) { timers.clearTimeout(entry.pollTimer); entry.pollTimer = null; }
    const ms = hostadmin.pollIntervalMs(entry.model.state);
    if (!ms || entry.disposed) return;
    entry.pollTimer = timers.setTimeout(() => {
      entry.pollTimer = null;
      void refreshEntry(entry);
    }, ms);
    if (entry.pollTimer && typeof entry.pollTimer.unref === "function") entry.pollTimer.unref();
  }

  async function refreshEntry(entry) {
    if (entry.disposed) return;
    await entry.model.detect();
    if (entry.model.state.mode === "admin") await entry.model.load(entry.model.state.activeTab);
    postState(entry);
  }

  /** A model bound to a fresh client for the host (the credential is re-read each time). */
  async function buildModel(hostEntry) {
    let client = null, problem = "";
    try { client = await deps.clientFor(hostEntry); } catch (e) { problem = errText(e); }
    if (!client && !problem) problem = `the credential for ${hostOf(hostEntry.url)} is not available on this PC (run "The Construct: Add Remote Host" again)`;
    return hostadmin.createHostAdminModel({ client, host: hostOf(hostEntry.url), url: hostEntry.url, backend: "hyperv-remote", now, log, problem });
  }

  /** Open (or reveal) the administration panel of one enrolled host. */
  async function openHostAdmin(hostEntry) {
    const k = key(hostEntry.url);
    const existing = panels.get(k);
    if (existing) {
      try { existing.panel.reveal(); return existing; } catch (_) { panels.delete(k); }
    }
    const { extensionUri } = deps.context;
    const panel = vscode.window.createWebviewPanel(
      "construct.hostAdmin",
      `Host administration — ${hostOf(hostEntry.url)}`,
      vscode.ViewColumn.Active,
      { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(extensionUri, "media")], retainContextWhenHidden: true }
    );
    if (vscode.Uri.joinPath) panel.iconPath = vscode.Uri.joinPath(extensionUri, "media", "icon.svg");
    const entry = { panel, hostEntry, model: await buildModel(hostEntry), pollTimer: null, disposed: false, busy: null };
    panels.set(k, entry);
    panel.webview.html = buildHtml(panel.webview, extensionUri);
    // The promise is returned for the tests (VS Code ignores it); nothing awaits it live.
    panel.webview.onDidReceiveMessage((m) => handleWebviewMessage(entry, m));
    panel.onDidDispose(() => {
      entry.disposed = true;
      if (entry.pollTimer) timers.clearTimeout(entry.pollTimer);
      if (panels.get(k) === entry) panels.delete(k);
    });
    return entry;
  }

  /** Serialize the panel's work: one detect/load/action at a time per host. */
  function serialize(entry, work) {
    const run = (entry.busy || Promise.resolve()).then(work, work);
    entry.busy = run.catch(() => {});
    return run;
  }

  async function handleWebviewMessage(entry, m) {
    if (!m || typeof m.type !== "string" || entry.disposed) return;
    switch (m.type) {
      case "hostadmin.ready":
        return serialize(entry, () => refreshEntry(entry));
      case "hostadmin.refresh":
        return serialize(entry, async () => {
          // The credential may have been re-entered meanwhile: rebuild the client.
          entry.model = await buildModel(entry.hostEntry);
          await refreshEntry(entry);
        });
      case "hostadmin.tab": {
        const tab = str(m.tab);
        if (!hostadmin.TABS.some((t) => t.id === tab)) return;
        return serialize(entry, async () => { await entry.model.load(tab); postState(entry); });
      }
      case "hostadmin.signIn":
        return serialize(entry, async () => {
          if (deps.addRemoteHost) await deps.addRemoteHost();
          const fresh = remoteHosts().find((h) => same(h.url, entry.hostEntry.url));
          if (fresh) entry.hostEntry = fresh;
          entry.model = await buildModel(entry.hostEntry);
          await refreshEntry(entry);
        });
      case "hostadmin.action":
        return serialize(entry, () => performAction(entry, str(m.action), m.args && typeof m.args === "object" ? m.args : {}));
      default:
        return;
    }
  }

  // ── Actions (confirmations here, decisions in the model) ─────────────────
  async function performAction(entry, action, args) {
    const model = entry.model;
    const hostName = hostOf(entry.hostEntry.url);
    const name = str(args.name);
    let reloadTab = true;
    try {
      switch (action) {
        case "createFirstVm":
          reloadTab = false;
          if (deps.newRemoteVm) await deps.newRemoteVm(entry.hostEntry);
          break;
        case "shutdownVm": {
          if (!name) return;
          if (!(await modal(`Request a graceful shutdown of "${name}"?`, "The guest is asked to shut down (like the instance panel's Shutdown). The service never forces it off; if the guest offers no shutdown integration, that is reported.", "Shut down"))) return;
          const r = await model.perform("shutdownVm", { name });
          postState(entry);
          if (r.ok && r.jobId) await followShutdown(entry, r.jobId, name);
          break;
        }
        case "deleteVm":
          await deleteVmFlow(entry, name, str(args.kind), str(args.cascadeToken));
          break;
        case "cancelJob":
          if (!(await modal(`Cancel job ${str(args.id)}?`, "A job that has already reached a point of no return finishes anyway; the service answers whether it was cancelled.", "Cancel job"))) return;
          await model.perform("cancelJob", { id: str(args.id) });
          break;
        case "deleteMedia":
          if (!(await modal(`Delete the media item "${str(args.name) || str(args.id)}"?`, "The file is removed from the host and its storage reservation released. Items referenced by a VM are refused by the service.", "Delete media"))) return;
          await model.perform("deleteMedia", { id: str(args.id) });
          break;
        case "deleteUser":
          if (!(await modal(`Remove the user "${name}" from ${hostName}?`, "Refused by the service while the user still owns VMs (children included). Their tokens stop working immediately.", "Remove user"))) return;
          await model.perform("deleteUser", { name });
          break;
        case "revokeToken":
          if (!(await modal(`Revoke token ${str(args.id)} of "${name}"?`, "The next request with it is refused.", "Revoke token"))) return;
          await model.perform("revokeToken", { name, id: str(args.id) });
          if (!entry.disposed) await pushTokens(entry, name);
          break;
        case "issueToken": {
          const r = await model.perform("issueToken", { name, label: str(args.label) });
          if (r.ok) await showSecretOnce(`API token for ${name}`, r.secret, `Label: ${r.label || "(none)"}. Hand it to ${name} over a channel you trust.`);
          if (!entry.disposed) await pushTokens(entry, name);
          break;
        }
        case "listTokens":
          reloadTab = false;
          await pushTokens(entry, name);
          break;
        case "rotateVmToken": {
          const kind = str(args.kind).toLowerCase() === "legacy" ? "legacy" : "primary";
          if (!(await modal(`Rotate the VM token of "${name}" (${kind} kind)?`, "The previous token stops working the moment the new one is issued: the guest loses `construct expose` and its heartbeat until the new token is delivered (Provision-AgentVM.ps1 -RotateVmToken, or the instance's Reprovision with credential upgrade).", "Rotate token"))) return;
          const r = await model.perform("rotateVmToken", { name, kind });
          if (r.ok) await showSecretOnce(`VM token of ${name} (${r.kind})`, r.secret, "Deliver it to the guest with Provision-AgentVM.ps1 -RotateVmToken, or reprovision the instance.");
          break;
        }
        case "revokeVmToken":
          if (!(await modal(`Revoke the VM token of "${name}"?`, "The guest loses `construct expose` and its heartbeat until it is reprovisioned.", "Revoke token"))) return;
          await model.perform("revokeVmToken", { name });
          break;
        case "loadOverrides": {
          reloadTab = false;
          const r = await model.perform("loadOverrides", { name });
          if (r.ok) safePost(entry, { type: "hostadmin.overrides", name, stored: r.stored, effective: r.effective, problems: [] });
          break;
        }
        case "saveOverrides": {
          const r = await model.perform("saveOverrides", { name, form: args.form });
          if (!r.ok && r.problems) { reloadTab = false; safePost(entry, { type: "hostadmin.overrides", name, stored: args.form, effective: "", problems: r.problems }); }
          break;
        }
        case "clearOverrides":
          if (!(await modal(`Clear the overrides of "${name}"?`, "The owner's allowance applies again unrestricted.", "Clear overrides"))) return;
          await model.perform("clearOverrides", { name });
          break;
        case "saveAllowance": {
          const r = await model.perform("saveAllowance", { name, form: args.form });
          if (!r.ok && r.problems) { reloadTab = false; safePost(entry, { type: "hostadmin.allowanceProblems", name, problems: r.problems }); }
          break;
        }
        case "updateUser": case "createUser": case "capacityRefresh": case "mediaCleanup": case "updatesCheck": case "updatesStage":
          await model.perform(action, args);
          break;
        case "saveConfig": {
          const r = await model.perform("saveConfig", { sections: args.sections });
          if (r.ok) safePost(entry, { type: "hostadmin.configSaved" });
          reloadTab = r.ok;
          break;
        }
        case "updatesApply": {
          const updateId = str(args.updateId);
          const resume = args.resume === true;
          if (!(await modal(resume ? `Resume the interrupted update ${updateId}?` : `Apply update ${updateId} to ${hostName}?`,
            "The service drains its own host jobs (not guest provisioning), hands off to the updater, stops, is replaced and restarted. Guest VMs keep running. Clients reconnect afterwards.", resume ? "Resume update" : "Apply update"))) return;
          await model.perform("updatesApply", { updateId });
          break;
        }
        case "updatesCancel":
          if (!(await modal(`Cancel update ${str(args.updateId)}?`, "Only possible before the hand-off to the updater.", "Cancel update"))) return;
          await model.perform("updatesCancel", { updateId: str(args.updateId) });
          break;
        case "updatesResolve": {
          const act = str(args.action).toLowerCase();
          if (!(await modal(`Resolve update ${str(args.updateId)} with "${act}"?`, "commit: keep the new build (only when it really runs and verifies). abort: restore the previous build from its complete backup. close: record that the installation is the previous one. The service refuses a resolution its own checks contradict.", `Resolve: ${act}`))) return;
          await model.perform("updatesResolve", { updateId: str(args.updateId), action: act });
          break;
        }
        case "refresh":
          await refreshEntry(entry);
          return;
        default:
          log(`hostadmin: unknown webview action "${action}" ignored`);
          reloadTab = false;
      }
    } catch (e) {
      log(`hostadmin: action ${action} failed — ${errText(e)}`);
      model.state.notice = { level: "error", text: errText(e) };
    }
    if (entry.disposed) return;
    if (reloadTab && model.state.mode === "admin") await model.load(model.state.activeTab);
    postState(entry);
  }

  function safePost(entry, msg) { try { entry.panel.webview.postMessage(msg); } catch (_) { /* disposed */ } }

  async function pushTokens(entry, name) {
    const r = await entry.model.perform("listTokens", { name });
    if (r.ok) safePost(entry, { type: "hostadmin.tokens", name, tokens: r.tokens });
  }

  /** Delete a VM from the admin panel: a child through its own modal, a primary through
   *  the §10.4 cascade dialog, retried on `cascade-scope-changed` with the fresh list. */
  async function deleteVmFlow(entry, name, kind, token) {
    const model = entry.model;
    if (!name) return;
    if (kind === "child") {
      const row = model.state.vms && model.state.vms.rows.find((r) => r.name === name);
      const c = hostadmin.childDeleteConfirmation({ name, sharing: row ? row.sharing : "", state: row ? row.state : "" });
      if (!(await modal(c.title, c.detail, c.confirmLabel))) return;
      await model.perform("deleteVm", { name });
      return;
    }
    let cascadeToken = token;
    for (let round = 0; round < 3; round++) {
      const r = await model.perform("deleteVm", { name, cascadeToken });
      if (r.ok || !r.cascade) {
        if (r.expired) vscode.window.showWarningMessage(r.error);
        return;
      }
      if (r.error) vscode.window.showWarningMessage(`${name}: ${r.error}.`);
      const typed = await cascadeDialog(r.cascade);
      if (!typed) return;
      cascadeToken = r.cascade.cascadeToken;
    }
    vscode.window.showWarningMessage(`The set of children of "${name}" keeps changing; nothing was deleted. Try again when it is stable.`);
  }

  /** The §10.4 modal: the full list, then the typed instance name. Resolves true when
   *  the user typed the exact name. */
  async function cascadeDialog(c) {
    const NEXT = "Type the name to confirm…";
    const pick = await vscode.window.showWarningMessage(c.title, { modal: true, detail: c.detail }, NEXT);
    if (pick !== NEXT) return false;
    const typed = await vscode.window.showInputBox({
      title: c.confirmLabel,
      prompt: `Type "${c.typedName}" to delete it and ALL its children (${c.sharedCount} shared host-wide). Disks, saved state and dedicated media are removed permanently.`,
      placeHolder: c.typedName,
      ignoreFocusOut: true,
      validateInput: (v) => (str(v) === c.typedName ? null : `Type exactly "${c.typedName}".`),
    });
    return str(typed) === c.typedName;
  }

  /** Follow a `vm-shutdown` job and report its end honestly (D1). */
  async function followShutdown(clientOrEntry, jobId, name) {
    const entry = clientOrEntry && clientOrEntry.model ? clientOrEntry : null;
    const client = entry ? await deps.clientFor(entry.hostEntry) : clientOrEntry;
    if (!client) return;
    let job = null;
    try {
      job = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Shutting down "${name}" gracefully…`, cancellable: false },
        () => hostadmin.awaitJob(client, jobId, { ...SHUTDOWN_WAIT, sleep: deps.sleep })
      );
    } catch (e) {
      vscode.window.showWarningMessage(`Could not follow the shutdown of "${name}": ${errText(e)}`);
      return;
    }
    const outcome = hostadmin.shutdownOutcome(job, name);
    if (outcome.ok) vscode.window.showInformationMessage(outcome.text);
    else vscode.window.showWarningMessage(outcome.text);
    if (entry && !entry.disposed && entry.model.state.mode === "admin") { await entry.model.load(entry.model.state.activeTab); postState(entry); }
    if (deps.refreshAll) deps.refreshAll();
  }

  // ── Commands and the picker ───────────────────────────────────────────────
  /** "The Construct: Host Administration" — pick an enrolled host (or the active
   *  instance's), open its panel. Works before any VM exists on the host. */
  async function runOpenHostAdmin(url) {
    const hosts = remoteHosts();
    if (!hosts.length) {
      const ADD = "Add a remote host";
      const pick = await vscode.window.showInformationMessage("No Construct remote host is enrolled on this PC.", ADD);
      if (pick === ADD && deps.addRemoteHost) await deps.addRemoteHost();
      return;
    }
    let entry = null;
    if (url) entry = hosts.find((h) => same(h.url, url)) || null;
    if (!entry) {
      const active = deps.activeInstance ? deps.activeInstance() : null;
      const own = active ? hostEntryFor(active) : null;
      if (hosts.length === 1) entry = hosts[0];
      else {
        const picked = await vscode.window.showQuickPick(
          hosts.map((h) => ({
            label: hostOf(h.url) + (own && same(own.url, h.url) ? " $(check)" : ""),
            description: [h.identity, h.role].filter(Boolean).join(" · "),
            detail: h.url,
            entry: h,
          })),
          { title: "Administer which Construct host?", placeHolder: "Only an administrator of the host sees the module" });
        if (!picked) return;
        entry = picked.entry;
      }
    }
    await openHostAdmin(entry);
  }

  /** Extra quick-pick rows for the instance picker: one "Host administration" row per
   *  enrolled host whose last classification was admin, and one "Create first VM" row
   *  per enrolled host with zero own VMs. Pure over the caches; never probes. */
  function pickerItems() {
    const hosts = remoteHosts();
    const items = [];
    for (const h of hosts) {
      const cached = offers.get(key(h.url));
      if (cached && cached.mode === "admin") {
        items.push({ label: `$(server-environment) Host administration: ${hostOf(h.url)}`, description: h.identity || "", detail: "open the host-administration panel", kind: "hostAdmin", url: h.url });
      }
    }
    for (const o of hostadmin.firstVmOffers({ hosts, instances: registryList(), sameUrl: same })) {
      items.push({ label: `$(add) Create first Construct VM on ${hostOf(o.url)}`, description: o.identity, detail: "this host has no VM of yours yet", kind: "createFirstVm", url: o.url });
    }
    return items;
  }

  /** Act on a picker row from pickerItems(). Returns true when it was one of ours. */
  async function handlePickerItem(item) {
    if (!item || !item.kind) return false;
    if (item.kind === "hostAdmin") { await runOpenHostAdmin(item.url); return true; }
    if (item.kind === "createFirstVm") {
      const h = remoteHosts().find((x) => same(x.url, item.url));
      if (h && deps.newRemoteVm) await deps.newRemoteVm(h);
      return true;
    }
    return false;
  }

  // ── The control panel's minimal user view ─────────────────────────────────
  /**
   * The `state.hostAdminOffer` for an instance: `{ host, url }` when the host's last
   * classification says admin, else null. Re-resolved per host at most every OFFER_TTL_MS
   * and re-evaluated on every host switch (each instance names its own host, and the
   * cache is per host, never per PC). Never throws.
   */
  async function hostAdminOfferFor(inst) {
    const hostEntry = hostEntryFor(inst);
    if (!hostEntry) return null;
    const k = key(hostEntry.url);
    const cached = offers.get(k);
    if (!cached || now() - cached.at > OFFER_TTL_MS) {
      try {
        // SILENT: this runs on every status refresh with nobody watching, so a missing
        // token must yield "no offer", never a prompt or a warning toast.
        const factory = deps.offerClient || deps.clientFor;
        const client = await factory(hostEntry);
        const resolved = await hostadmin.resolveHostState(client, { backend: "hyperv-remote", host: hostOf(hostEntry.url) });
        offers.set(k, { mode: resolved.mode, at: now() });
      } catch (e) {
        log(`hostadmin: offer probe for ${hostOf(hostEntry.url)} failed — ${errText(e)}`);
        offers.set(k, { mode: "unavailable", at: now() });
      }
    }
    const state = offers.get(k);
    return state && state.mode === "admin" ? { host: hostOf(hostEntry.url), url: hostEntry.url } : null;
  }

  /**
   * The `state.children` card for an instance (§10.2): null for a local instance and for
   * a host whose service has no `children` feature (the card is hidden entirely). Never
   * throws; a failed read on a supporting host keeps the card with the problem.
   */
  async function childrenStateFor(inst) {
    const backend = str(inst && inst.backend).toLowerCase();
    if (backend !== "hyperv-remote") { childrenCache.delete(inst && inst.name); return null; }
    let result;
    try {
      // queryChildren resolves the instance's credential itself (silently) and reports a
      // missing one as `supported: false` with the reason — nothing to resolve twice.
      result = await deps.queryChildren(inst);
    } catch (e) {
      result = { supported: false, items: [], problem: errText(e) };
    }
    const previous = childrenCache.get(inst.name);
    // A failed read keeps the last rows on screen (with the problem) rather than blanking.
    const items = result.items == null && previous && previous.items ? previous.items : result.items;
    childrenCache.set(inst.name, { supported: result.supported, items, problem: result.problem, at: now() });
    return hostadmin.childrenCardState({ backend, supported: result.supported, primary: inst.name, items, problem: result.problem, now: now() });
  }

  /** Is `child` a child THIS window listed for `inst`? The webview is untrusted input. */
  function knownChild(inst, child) {
    const cached = childrenCache.get(inst && inst.name);
    if (!cached || !Array.isArray(cached.items)) return null;
    return cached.items.find((c) => c && str(c.name) === str(child)) || null;
  }

  /** The panel's `command` messages this feature owns. Returns true when handled. */
  async function handlePanelCommand(id, message, inst) {
    if (id === "openHostAdmin") {
      const h = inst ? hostEntryFor(inst) : null;
      await runOpenHostAdmin(h ? h.url : "");
      return true;
    }
    if (id === "createFirstVm") {
      const url = str(message && message.url);
      const h = remoteHosts().find((x) => same(x.url, url));
      if (h && deps.newRemoteVm) await deps.newRemoteVm(h);
      return true;
    }
    if (id !== "childShutdown" && id !== "childDelete") return false;
    const childName = str(message && message.child);
    const child = knownChild(inst, childName);
    if (!child) {
      vscode.window.showWarningMessage(`"${childName}" is not a child VM of ${inst ? inst.name : "this instance"} that this window listed. Refresh and try again.`);
      return true;
    }
    const { client, problem } = await deps.instanceClient(inst);
    if (!client) { vscode.window.showWarningMessage(`Cannot reach the host of ${inst.name}: ${problem}`); return true; }
    if (id === "childShutdown") {
      if (!(await modal(`Shut down the child VM "${child.name}"?`, "The guest is asked to shut down gracefully (like this panel's own Shutdown). The service never forces it off: if the guest offers no shutdown integration, the request fails and that is reported here.", "Shut down"))) return true;
      let res;
      try { res = await client.lifecycle(child.name, { action: "shutdown" }); }
      catch (e) { vscode.window.showWarningMessage(`The host refused the shutdown of "${child.name}": ${errText(e)}`); return true; }
      const jobId = str(res && res.jobId);
      if (!jobId) { vscode.window.showWarningMessage(`The host accepted the shutdown of "${child.name}" but returned no job to follow.`); return true; }
      await followShutdown(client, jobId, child.name);
      return true;
    }
    // childDelete
    const c = hostadmin.childDeleteConfirmation(child);
    if (!(await modal(c.title, c.detail, c.confirmLabel))) return true;
    try {
      const res = await client.deleteVm(child.name);
      vscode.window.showInformationMessage(`Deletion of "${child.name}" accepted (job ${str(res && res.jobId) || "?"}${res && res.replayed ? ", already running" : ""}).`);
    } catch (e) {
      vscode.window.showWarningMessage(`The host refused the deletion of "${child.name}": ${errText(e)}`);
    }
    if (deps.refreshAll) deps.refreshAll();
    return true;
  }

  function dispose() {
    for (const entry of panels.values()) {
      entry.disposed = true;
      if (entry.pollTimer) timers.clearTimeout(entry.pollTimer);
      try { entry.panel.dispose(); } catch (_) {}
    }
    panels.clear();
  }

  return {
    openHostAdmin, runOpenHostAdmin, pickerItems, handlePickerItem,
    hostAdminOfferFor, childrenStateFor, handlePanelCommand, knownChild,
    dispose,
    _panels: panels, _offers: offers, _children: childrenCache,
    OFFER_TTL_MS,
  };
}

module.exports = { createHostAdminFeature, OFFER_TTL_MS, SHUTDOWN_WAIT };
