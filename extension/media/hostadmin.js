/* global acquireVsCodeApi */
// Host administration webview controller (host-administration contract §10.2).
// Renders the state the extension posts (`hostadmin.state`) and posts intents back
// (`hostadmin.ready` / `hostadmin.tab` / `hostadmin.refresh` / `hostadmin.action`).
// Nothing here decides anything: confirmations, secrets and every API call live on the
// extension side (src/hostadmin-ui.js over src/hostadmin.js). Every value that reaches
// the DOM goes through textContent — no innerHTML with data, ever.
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);
  const post = (msg) => vscode.postMessage(msg);
  const act = (action, args) => post({ type: "hostadmin.action", action, args: args || {} });

  let state = null;
  /** Per-tab "dirty" edits the next render must not clobber. */
  const cfgEdits = new Map();

  // ── DOM helpers ─────────────────────────────────────────────────────────────
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = String(text);
    return e;
  }
  function cell(text, cls) { return el("span", "ha-cell " + (cls || ""), text); }
  function btn(label, cls, onClick, title) {
    const b = el("button", "btn " + (cls || ""), label);
    b.type = "button";
    if (title) b.title = title;
    b.addEventListener("click", onClick);
    return b;
  }
  function clear(node) { if (node) node.textContent = ""; }
  function show(node, on) { if (node) node.hidden = !on; }
  function text(id, value) { const n = $(id); if (n) n.textContent = value == null ? "" : String(value); }
  function headRow(labels) {
    const r = el("div", "ha-row head");
    labels.forEach((l) => r.appendChild(cell(l, l === "" ? "actions" : "")));
    return r;
  }
  function field(label, id, value, opts) {
    const o = opts || {};
    const wrap = el("label", null, label + " ");
    let input;
    if (o.options) {
      input = el("select");
      o.options.forEach(([v, t]) => { const op = el("option", null, t); op.value = v; if (v === String(value == null ? "" : value)) op.selected = true; input.appendChild(op); });
    } else {
      input = el("input");
      input.type = o.type || "text";
      input.value = value == null ? "" : String(value);
      if (o.placeholder) input.placeholder = o.placeholder;
    }
    input.id = id;
    input.dataset.field = o.field || id;
    wrap.appendChild(input);
    if (o.problem) wrap.appendChild(el("div", "problem", o.problem));
    return wrap;
  }
  function readForm(container) {
    const out = {};
    container.querySelectorAll("[data-field]").forEach((i) => { out[i.dataset.field] = i.value; });
    return out;
  }
  const TRI = [["", "inherit"], ["true", "true"], ["false", "false"]];

  // ── Header, banner, notice, state card ──────────────────────────────────────
  function renderHeader(s) {
    text("haHost", (s.host || "host").toUpperCase());
    const id = s.identity;
    text("haIdentity", id && id.name ? `${id.name} · ${id.role || "?"}${s.mode === "admin" ? "" : " · " + s.mode}` : s.mode === "admin" ? "administrator" : s.mode);
    show($("haMaint"), !!s.maintenance);
    text("haMaintPhase", s.maintenance ? `(${s.maintenance.phase})` : "");
    const n = $("haNotice");
    if (n) {
      show(n, !!s.notice);
      n.className = "ha-notice " + (s.notice && s.notice.level === "error" ? "error" : "");
      text("haNoticeText", s.notice ? s.notice.text : "");
    }
    const refresh = $("haRefresh");
    if (refresh) refresh.disabled = !!s.busy;
  }

  const STATE_TITLES = {
    unavailable: "Host unavailable", "old-service": "Older host service", "sign-in": "Sign in again",
    denied: "Access denied", user: "Not an administrator", local: "Local instance",
  };
  function renderStateCard(s) {
    const admin = s.mode === "admin";
    show($("haState"), !admin);
    show($("haAdmin"), admin);
    if (admin) return;
    text("haStateTitle", STATE_TITLES[s.mode] || "Host");
    text("haStateMode", s.mode);
    text("haStateMessage", s.message || "");
    const lk = $("haStateLastKnown");
    show(lk, !!s.lastKnownAt);
    if (lk && s.lastKnownAt) lk.textContent = "Last successful read: " + s.lastKnownAt;
    show($("haRetry"), s.mode === "unavailable" || s.mode === "old-service" || s.mode === "denied");
    show($("haSignIn"), s.mode === "sign-in");
    show($("haCreateFirst"), !!(s.offers && s.offers.createFirstVm) && (s.mode === "user" || s.mode === "unavailable"));
  }

  // ── Tabs ────────────────────────────────────────────────────────────────────
  function renderTabs(s) {
    const strip = $("haTabs");
    if (!strip) return;
    clear(strip);
    (s.tabs || []).forEach((t) => {
      const b = btn(t.label, t.id === s.activeTab ? "sel" : "ghost", () => post({ type: "hostadmin.tab", tab: t.id }),
        t.available ? "" : t.reason);
      b.setAttribute("role", "tab");
      b.setAttribute("aria-selected", t.id === s.activeTab ? "true" : "false");
      b.dataset.tab = t.id;
      if (!t.available) { b.disabled = true; b.textContent = t.label + " · n/a"; }
      strip.appendChild(b);
    });
    document.querySelectorAll(".ha-tab").forEach((sec) => { sec.hidden = sec.id !== "tab-" + s.activeTab; });
  }

  // ── Overview ────────────────────────────────────────────────────────────────
  function renderOverview(s) {
    const o = s.overview;
    show($("ovCreateFirst"), !!(s.offers && s.offers.createFirstVm));
    if (!o) return;
    text("ovMode", "");
    text("ovVersion", `${o.version.packageVersion} · ${o.version.commit} · ${o.version.source}`);
    text("ovInstalled", o.version.installedAt);
    text("ovHealth", o.problems.length ? o.problems.join(", ") : `hypervisor ${o.health.hypervisor} · database ${o.health.database} · media ${o.health.media} · inventory ${o.health.inventory}`);
    text("ovMaint", o.maintenance ? `${o.maintenance.phase} since ${o.maintenance.since}` : "open");
    text("ovOverdue", o.leaseOverdueCount);
    text("ovUnmanaged", o.unmanagedVmCount);
    const mode = $("ovCapMode");
    if (mode) { mode.textContent = o.capacityMode; mode.className = "tag " + (o.capacityMode === "enforce" ? "upd" : "ok"); }
    const bars = $("ovCapacity");
    clear(bars);
    (o.capacity || []).forEach((b) => {
      const wrap = el("div", "ha-bar-wrap");
      const lbl = el("div", "ha-bar-label");
      lbl.appendChild(el("span", null, b.label));
      lbl.appendChild(el("span", null, b.pct + "%"));
      wrap.appendChild(lbl);
      const bar = el("div", "ha-bar " + (b.pct >= 95 ? "full" : b.pct >= 80 ? "hot" : ""));
      const fill = el("span");
      fill.style.width = b.pct + "%";
      bar.appendChild(fill);
      wrap.appendChild(bar);
      wrap.appendChild(el("div", "ha-bar-text", b.text));
      bars.appendChild(wrap);
    });
    text("ovCapEpoch", `Inventory epoch ${o.capacityEpoch.epoch || "—"}, observed ${o.capacityEpoch.observedAt}${o.capacityEpoch.complete ? "" : " — INCOMPLETE"}${o.capacityProblems && o.capacityProblems.length ? " · " + o.capacityProblems.join("; ") : ""}`);
    const jobs = $("ovJobs");
    clear(jobs);
    show($("ovJobsEmpty"), !o.activeJobs.length);
    if (o.activeJobs.length) jobs.appendChild(headRow(["job", "kind", "vm", "owner", "phase", "since"]));
    o.activeJobs.forEach((j) => {
      const r = el("div", "ha-row");
      r.appendChild(cell(j.id, "mono"));
      r.appendChild(cell(j.kind));
      r.appendChild(cell(j.vmName || "—", "mono"));
      r.appendChild(cell(j.owner + (j.initiator ? ` (${j.initiator})` : "")));
      r.appendChild(cell(j.phase || "—"));
      r.appendChild(cell(j.created));
      jobs.appendChild(r);
    });
  }

  // ── VMs ─────────────────────────────────────────────────────────────────────
  function renderVms(s) {
    const v = s.vms;
    const table = $("vmsTable");
    if (!v || !table) return;
    clear(table);
    show($("vmsEmpty"), !v.rows.length);
    text("vmsMeta", `${v.rows.length} VM${v.rows.length === 1 ? "" : "s"}${v.childrenFeature ? "" : " · child VMs not available on this host version"}`);
    if (v.rows.length) table.appendChild(headRow(["name", "kind", "owner", "state", "resources", "lease", "operation", "guest", ""]));
    v.rows.forEach((r) => {
      const row = el("div", "ha-row " + (r.kind === "child" ? "child" : "") + (r.overdue ? " overdue" : "") + (r.deleting ? " disabled" : ""));
      const name = cell(r.name, "name");
      if (r.kind === "child" && r.parent) name.title = "child of " + r.parent;
      row.appendChild(name);
      const kind = cell("", "");
      kind.appendChild(el("span", "ha-badge " + (r.kind === "child" ? "child" : ""), r.kind + (r.tokenKind ? " · " + r.tokenKind : "")));
      if (r.sharing === "host") kind.appendChild(el("span", "ha-badge shared", "shared host-wide"));
      if (r.deleting) kind.appendChild(el("span", "ha-badge off", "deleting"));
      if (r.childCreationClosed) kind.appendChild(el("span", "ha-badge off", "closed"));
      row.appendChild(kind);
      row.appendChild(cell(r.owner || "—"));
      row.appendChild(cell(r.state, "ha-state-cell"));
      row.appendChild(cell(r.resources, ""));
      row.appendChild(cell(r.lease || (r.kind === "child" ? "—" : ""), "ha-lease"));
      row.appendChild(cell(r.operation || "", "mono"));
      // A child guest holds no credential (§2.1) so it can never report; the honest value is
      // "unknown", not a categorical negative.
      const guest = cell(r.kind === "child" ? "unknown — a child guest cannot report to the service" : r.guest, "wide");
      guest.title = r.reservations;
      row.appendChild(guest);
      const actions = cell("", "actions");
      const busy = !!r.operation || r.deleting;
      if (r.allowedActions.indexOf("shutdown") >= 0 && (r.state === "running" || r.state === "paused")) {
        const b = btn("Shut down", "", () => act("shutdownVm", { name: r.name }), "Graceful guest shutdown request");
        b.disabled = busy;
        actions.appendChild(b);
      }
      if (r.allowedActions.indexOf("delete") >= 0) {
        const b = btn("Delete", "danger", () => act("deleteVm", { name: r.name, kind: r.kind }), r.kind === "child" ? "Delete this child VM" : "Delete this primary and ALL its children");
        b.disabled = busy && !r.deleting;
        actions.appendChild(b);
      }
      if (r.kind !== "child") {
        actions.appendChild(btn("Overrides", "ghost", () => act("loadOverrides", { name: r.name }), "Per-VM delegation overrides (restrict-only)"));
        actions.appendChild(btn("Rotate token", "ghost", () => act("rotateVmToken", { name: r.name }), "Issue a new VM token (primary kind); the old one stops working"));
      }
      row.appendChild(actions);
      table.appendChild(row);
    });
  }
  function renderOverrides(m) {
    const card = $("vmOverridesCard");
    if (!card) return;
    show(card, true);
    text("ovrName", m.name);
    const form = $("ovrForm");
    clear(form);
    const f = m.stored || {};
    const p = (m.problems || []).reduce((acc, x) => { acc[x.field] = x.reason; return acc; }, {});
    form.appendChild(field("Allow child creation", "ovr_allowChildCreation", f.allowChildCreation, { options: TRI, field: "allowChildCreation", problem: p.allowChildCreation }));
    form.appendChild(field("Max retained children", "ovr_maxRetainedChildren", f.maxRetainedChildren, { type: "number", field: "maxRetainedChildren", problem: p.maxRetainedChildren }));
    form.appendChild(field("Max child lifetime (12h, 3d)", "ovr_maxChildLifetime", f.maxChildLifetime, { field: "maxChildLifetime", problem: p.maxChildLifetime }));
    form.appendChild(field("Allow 'never' lifetime", "ovr_allowNeverLifetime", f.allowNeverLifetime, { options: TRI, field: "allowNeverLifetime", problem: p.allowNeverLifetime }));
    form.appendChild(field("Allow sharing", "ovr_allowSharing", f.allowSharing, { options: TRI, field: "allowSharing", problem: p.allowSharing }));
    text("ovrEffective", m.effective ? "Effective: " + m.effective : "");
    card.dataset.name = m.name;
  }

  // ── Users ───────────────────────────────────────────────────────────────────
  function renderUsers(s) {
    const u = s.users;
    const table = $("usrTable");
    if (!u || !table) return;
    clear(table);
    if (u.rows.length) table.appendChild(headRow(["name", "role", "primaries / children", "tokens", "effective allowance", ""]));
    u.rows.forEach((r) => {
      const row = el("div", "ha-row " + (r.enabled ? "" : "disabled"));
      row.appendChild(cell(r.name, "name"));
      const role = cell("", "");
      role.appendChild(el("span", "ha-badge", r.role));
      if (!r.enabled) role.appendChild(el("span", "ha-badge off", "disabled"));
      if (!r.allowHostForwards) role.appendChild(el("span", "ha-badge", "no host forwards"));
      row.appendChild(role);
      row.appendChild(cell(`${r.primaries} / ${r.children} (max ${r.maxVms == null ? "—" : r.maxVms})`));
      row.appendChild(cell(r.tokens));
      const eff = cell(r.effective, "wide");
      row.appendChild(eff);
      const actions = cell("", "actions");
      actions.appendChild(btn("Edit", "", () => openUserEditor(r), "Role, enabled, quota, allowance, tokens"));
      row.appendChild(actions);
      table.appendChild(row);
    });
  }
  function openUserEditor(r, extra) {
    const card = $("usrEditCard");
    if (!card) return;
    show(card, true);
    card.dataset.name = r.name;
    text("ueName", r.name);
    const uf = $("ueUserForm");
    clear(uf);
    uf.appendChild(field("Role", "ue_role", r.role, { options: [["user", "user"], ["admin", "admin"]], field: "role" }));
    uf.appendChild(field("Enabled", "ue_enabled", r.enabled ? "true" : "false", { options: [["true", "enabled"], ["false", "disabled"]], field: "enabled" }));
    uf.appendChild(field("Max primaries", "ue_maxVms", r.maxVms == null ? "" : r.maxVms, { type: "number", field: "maxVms" }));
    uf.appendChild(field("Host forwards", "ue_allowHostForwards", r.allowHostForwards ? "true" : "false", { options: [["true", "allowed"], ["false", "refused"]], field: "allowHostForwards" }));
    const af = $("ueAllowanceForm");
    clear(af);
    const a = r.allowance || {};
    const p = (extra && extra.problems || []).reduce((acc, x) => { acc[x.field] = x.reason; return acc; }, {});
    af.appendChild(field("Allow child creation", "ua_allowChildCreation", a.allowChildCreation, { options: TRI, field: "allowChildCreation", problem: p.allowChildCreation }));
    af.appendChild(field("Max retained children", "ua_maxRetainedChildren", a.maxRetainedChildren, { type: "number", field: "maxRetainedChildren", problem: p.maxRetainedChildren }));
    af.appendChild(field("CPU budget (vCPU)", "ua_cpuBudget", a.cpuBudget, { type: "number", field: "cpuBudget", problem: p.cpuBudget }));
    af.appendChild(field("RAM budget (GiB)", "ua_ramBudgetGiB", a.ramBudgetGiB, { field: "ramBudgetGiB", problem: p.ramBudgetGiB }));
    af.appendChild(field("Storage budget (GiB)", "ua_storageBudgetGiB", a.storageBudgetGiB, { field: "storageBudgetGiB", problem: p.storageBudgetGiB }));
    af.appendChild(field("Max child lifetime (12h, 3d)", "ua_maxChildLifetime", a.maxChildLifetime, { field: "maxChildLifetime", problem: p.maxChildLifetime }));
    af.appendChild(field("Allow 'never' lifetime", "ua_allowNeverLifetime", a.allowNeverLifetime, { options: TRI, field: "allowNeverLifetime", problem: p.allowNeverLifetime }));
    af.appendChild(field("Allow sharing", "ua_allowSharing", a.allowSharing, { options: TRI, field: "allowSharing", problem: p.allowSharing }));
    text("ueEffective", "Effective: " + r.effective);
    renderTokens(r.name, (extra && extra.tokens) || null);
    if (!extra || !extra.tokens) act("listTokens", { name: r.name });
  }
  function renderTokens(name, tokens) {
    const t = $("ueTokens");
    if (!t) return;
    clear(t);
    if (!tokens) { t.appendChild(el("div", "ha-row", "loading tokens…")); return; }
    if (!tokens.length) { t.appendChild(el("div", "ha-row", "no tokens")); return; }
    t.appendChild(headRow(["id", "label", "created", "last used", ""]));
    tokens.forEach((k) => {
      const row = el("div", "ha-row");
      row.appendChild(cell(k.id, "mono"));
      row.appendChild(cell(k.label || "—", "wide"));
      row.appendChild(cell(k.created));
      row.appendChild(cell(k.lastUsed));
      const actions = cell("", "actions");
      actions.appendChild(btn("Revoke", "danger", () => act("revokeToken", { name, id: k.id })));
      row.appendChild(actions);
      t.appendChild(row);
    });
  }

  // ── Media ───────────────────────────────────────────────────────────────────
  function renderMedia(s) {
    const m = s.media;
    if (!m) return;
    const c = m.catalog || { mode: "unavailable", source: {}, entries: [] };
    text("isoMode", c.mode);
    text("isoSource", m.catalogProblem || `${c.source.path || c.source.url || "—"} · ${c.source.present ? "present" : "absent"} · ${c.source.size}${c.source.sha256Configured ? " · sha256 configured" : ""}`);
    text("isoCurrent", c.current ? `${c.current.fileName} · ${c.current.size} · built ${c.current.builtAt}` : "none");
    text("isoLastBuild", c.lastBuild ? `${c.lastBuild.outcome} at ${c.lastBuild.at}` : "—");
    const e = $("isoEntries");
    clear(e);
    c.entries.forEach((x) => {
      const row = el("div", "ha-row");
      row.appendChild(cell(x.fileName, "mono"));
      row.appendChild(cell(x.size));
      row.appendChild(cell(x.builtAt));
      if (x.isCurrent) row.appendChild(el("span", "ha-badge child", "current"));
      if (!x.sidecarReadable) row.appendChild(el("span", "ha-badge off", "sidecar unreadable"));
      e.appendChild(row);
    });
    show($("mediaProblem"), !!m.mediaProblem);
    text("mediaProblem", m.mediaProblem);
    const cleanup = $("mediaCleanupBtn");
    if (cleanup) cleanup.disabled = !(s.features && s.features.media);
    const t = $("mediaTable");
    clear(t);
    show($("mediaEmpty"), !m.items.length && !m.mediaProblem);
    if (m.items.length) t.appendChild(headRow(["name", "owner", "role", "state", "size", "refs", "created", ""]));
    m.items.forEach((x) => {
      const row = el("div", "ha-row " + (x.state === "failed" ? "failed" : ""));
      const name = cell(x.name || x.id, "name");
      name.title = x.id + (x.sourceUrl ? " · " + x.sourceUrl : "") + (x.dedicatedTo ? " · dedicated to " + x.dedicatedTo : "");
      row.appendChild(name);
      row.appendChild(cell(x.owner || "—"));
      row.appendChild(cell(x.role));
      row.appendChild(cell(x.state + (x.error ? ": " + x.error : ""), "ha-state-cell"));
      row.appendChild(cell(`${x.size} (reserved ${x.reserved})`));
      row.appendChild(cell(x.references));
      row.appendChild(cell(x.created));
      const actions = cell("", "actions");
      const del = btn("Delete", "danger", () => act("deleteMedia", { id: x.id, name: x.name }), x.deletable ? "Delete this media item" : "Referenced by a VM; detach it first");
      del.disabled = !x.deletable;
      actions.appendChild(del);
      row.appendChild(actions);
      t.appendChild(row);
    });
  }

  // ── Operations ──────────────────────────────────────────────────────────────
  function renderOperations(s) {
    const o = s.operations;
    if (!o) return;
    const t = $("jobsTable");
    clear(t);
    show($("jobsEmpty"), !o.jobs.length);
    text("jobsMeta", `${o.jobs.length} job${o.jobs.length === 1 ? "" : "s"}`);
    if (o.jobs.length) t.appendChild(headRow(["job", "kind", "vm", "owner", "state", "phase / error", "created", ""]));
    o.jobs.forEach((j) => {
      const row = el("div", "ha-row " + (j.state === "failed" ? "failed" : ""));
      row.appendChild(cell(j.id, "mono"));
      row.appendChild(cell(j.kind));
      row.appendChild(cell(j.vmName || "—", "mono"));
      row.appendChild(cell(j.owner + (j.initiator ? ` (${j.initiator})` : "")));
      row.appendChild(cell(j.state, "ha-state-cell"));
      row.appendChild(cell(j.error ? j.error : (j.phase || "—"), "wide"));
      row.appendChild(cell(j.created));
      const actions = cell("", "actions");
      if (j.cancellable) actions.appendChild(btn("Cancel", "danger", () => act("cancelJob", { id: j.id })));
      if (j.retry) {
        actions.appendChild(btn(j.retry.action === "mediaCleanup" ? "Retry cleanup" : "Retry delete", "",
          () => act(j.retry.action, j.retry.action === "deleteVm" ? { name: j.retry.name, kind: j.retry.kind } : {})));
      }
      row.appendChild(actions);
      t.appendChild(row);
    });
    show($("auditProblem"), !!o.auditProblem);
    text("auditProblem", o.auditProblem);
    const a = $("auditTable");
    clear(a);
    if (o.audit.length) a.appendChild(headRow(["at", "actor", "action", "target", "detail"]));
    o.audit.forEach((x) => {
      const row = el("div", "ha-row");
      row.appendChild(cell(x.at));
      row.appendChild(cell(x.actor));
      row.appendChild(cell(x.action, "mono"));
      row.appendChild(cell(x.target, "mono"));
      row.appendChild(cell(x.detail, "wide"));
      a.appendChild(row);
    });
  }

  // ── Configuration ───────────────────────────────────────────────────────────
  function renderConfig(s) {
    const c = s.config;
    const host = $("cfgSections");
    if (!c || !host) return;
    clear(host);
    const problems = c.problems || [];
    c.sections.forEach((sec) => {
      const wrap = el("div", "ha-section" + (cfgEdits.has(sec.key) ? " dirty" : ""));
      wrap.dataset.key = sec.key;
      wrap.dataset.expectedUpdatedAt = sec.expectedUpdatedAt || "";
      const head = el("div", "ha-section-head");
      head.appendChild(el("span", "seclabel", sec.key));
      head.appendChild(el("span", "ha-badge", sec.source + (sec.updatedAt ? " · " + sec.updatedAt : "")));
      wrap.appendChild(head);
      const ta = el("textarea");
      ta.value = cfgEdits.has(sec.key) ? cfgEdits.get(sec.key) : sec.text;
      ta.dataset.original = sec.text;
      ta.spellcheck = false;
      ta.addEventListener("input", () => {
        if (ta.value === ta.dataset.original) cfgEdits.delete(sec.key); else cfgEdits.set(sec.key, ta.value);
        wrap.classList.toggle("dirty", cfgEdits.has(sec.key));
      });
      wrap.appendChild(ta);
      const mine = problems.filter((p) => p.field === sec.key || String(p.field || "").indexOf(sec.key + ".") === 0);
      if (mine.length) wrap.appendChild(el("p", "problem", mine.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("\n")));
      host.appendChild(wrap);
    });
    const general = problems.filter((p) => !p.field || !c.sections.some((sec) => p.field === sec.key || String(p.field).indexOf(sec.key + ".") === 0));
    if (general.length) host.appendChild(el("p", "problem", general.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("\n")));
    const caps = c.capabilities;
    text("capBackend", caps ? caps.backend : "not available");
    const ct = $("capTable");
    clear(ct);
    if (caps) caps.rows.forEach((r) => {
      const row = el("div", "ha-row");
      row.appendChild(cell(r.key, "mono"));
      row.appendChild(cell(r.value, "wide"));
      ct.appendChild(row);
    });
    text("capNotes", caps && caps.notes.length ? "Notes: " + caps.notes.join(" · ") : "");
  }

  // ── Maintenance ─────────────────────────────────────────────────────────────
  function renderMaintenance(s) {
    const u = s.maintenanceTab;
    if (!u) return;
    text("updSigning", u.signingKeyConfigured ? "signing key configured" : "NO SIGNING KEY — updates cannot be verified");
    text("updInstalled", `${u.installed.packageVersion} · ${u.installed.commit} · ${u.installed.source} · ${u.installed.installedAt}${u.installed.previousCommit ? " · previous " + u.installed.previousCommit : ""}`);
    text("updLatest", u.latestKnown ? `${u.latestKnown.packageVersion} · ${u.latestKnown.commit} · published ${u.latestKnown.publishedAt} · checked ${u.latestKnown.checkedAt}` : "not checked yet");
    const cur = u.current;
    text("updCurrent", cur ? `${cur.updateId} · ${cur.state}${cur.phase ? " (" + cur.phase + ")" : ""} · ${cur.commit} · started ${cur.started}${cur.error ? " · " + cur.error : ""}${cur.blockingJobs.length ? " · blocked by " + cur.blockingJobs.join(", ") : ""}` : "none");
    show($("updCheckResult"), !!u.checkResult);
    text("updCheckResult", u.checkResult || "");
    const a = u.actions;
    const set = (id, on) => { const b = $(id); if (b) { b.disabled = !on || !!s.maintenance; b.hidden = false; } };
    set("updCheck", a.check); set("updStage", a.stage); set("updApply", a.apply); set("updResume", a.resume); set("updCancel", a.cancel);
    show($("updResolveRow"), a.resolve);
    document.querySelectorAll("#updResolveRow [data-resolve]").forEach((b) => { b.disabled = !!s.maintenance; });
    const rec = $("updRecovery");
    show(rec, !!u.recoveryRecord);
    if (rec) rec.textContent = u.recoveryRecord || "";
    const ph = $("updPhases");
    clear(ph);
    if (cur && cur.phases.length) {
      ph.appendChild(headRow(["phase", "at", "outcome"]));
      cur.phases.forEach((p) => {
        const row = el("div", "ha-row " + (p.outcome === "failed" ? "failed" : ""));
        row.appendChild(cell(p.name, "mono"));
        row.appendChild(cell(p.at));
        row.appendChild(cell((p.outcome || "—") + (p.error ? ": " + p.error : ""), "ha-state-cell wide"));
        ph.appendChild(row);
      });
    }
    const h = $("updHistory");
    clear(h);
    if (u.history.length) h.appendChild(headRow(["update", "commit", "state", "started", "finished", "actor", "error"]));
    u.history.forEach((r) => {
      const row = el("div", "ha-row");
      row.appendChild(cell(r.updateId, "mono"));
      row.appendChild(cell(r.commit, "mono"));
      row.appendChild(cell(r.state, "ha-state-cell"));
      row.appendChild(cell(r.started));
      row.appendChild(cell(r.finished));
      row.appendChild(cell(r.actor));
      row.appendChild(cell(r.error, "wide"));
      h.appendChild(row);
    });
    document.querySelectorAll("#tab-maintenance .ha-inline-actions .btn").forEach((b) => { if (s.maintenance) b.disabled = true; });
  }

  // ── Render everything ───────────────────────────────────────────────────────
  function render(s) {
    if (!s) return;
    state = s;
    renderHeader(s);
    renderStateCard(s);
    if (s.mode !== "admin") return;
    renderTabs(s);
    renderOverview(s);
    renderVms(s);
    renderUsers(s);
    renderMedia(s);
    renderOperations(s);
    renderConfig(s);
    renderMaintenance(s);
    // Every mutation is disabled while the host is updating (§10.3).
    document.querySelectorAll("#haAdmin .btn, #haAdmin .save-btn").forEach((b) => {
      if (s.maintenance && !b.closest(".ha-tabs")) b.disabled = true;
    });
  }

  // ── Wiring ──────────────────────────────────────────────────────────────────
  document.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", () => {
    const a = b.getAttribute("data-act");
    if (a === "signIn") post({ type: "hostadmin.signIn" });
    else if (a === "refresh") post({ type: "hostadmin.refresh" });
    else act(a, {});
  }));
  $("haRefresh") && $("haRefresh").addEventListener("click", () => post({ type: "hostadmin.refresh" }));
  $("haNoticeClose") && $("haNoticeClose").addEventListener("click", () => show($("haNotice"), false));
  $("ovrClose") && $("ovrClose").addEventListener("click", () => show($("vmOverridesCard"), false));
  $("ovrSave") && $("ovrSave").addEventListener("click", () => act("saveOverrides", { name: $("vmOverridesCard").dataset.name, form: readForm($("ovrForm")) }));
  $("ovrClear") && $("ovrClear").addEventListener("click", () => act("clearOverrides", { name: $("vmOverridesCard").dataset.name }));
  $("usrAddToggle") && $("usrAddToggle").addEventListener("click", () => { const n = $("usrNew"); n.hidden = !n.hidden; });
  $("nuSave") && $("nuSave").addEventListener("click", () => act("createUser", { form: { name: $("nuName").value, role: $("nuRole").value, maxVms: $("nuMaxVms").value, allowHostForwards: $("nuHostForwards").value } }));
  $("ueClose") && $("ueClose").addEventListener("click", () => show($("usrEditCard"), false));
  $("ueSaveUser") && $("ueSaveUser").addEventListener("click", () => act("updateUser", { name: $("usrEditCard").dataset.name, form: readForm($("ueUserForm")) }));
  $("ueSaveAllowance") && $("ueSaveAllowance").addEventListener("click", () => act("saveAllowance", { name: $("usrEditCard").dataset.name, form: readForm($("ueAllowanceForm")) }));
  $("ueIssueToken") && $("ueIssueToken").addEventListener("click", () => act("issueToken", { name: $("usrEditCard").dataset.name, label: $("ueTokenLabel").value }));
  $("ueDeleteUser") && $("ueDeleteUser").addEventListener("click", () => act("deleteUser", { name: $("usrEditCard").dataset.name }));
  $("cfgSave") && $("cfgSave").addEventListener("click", () => {
    const sections = [];
    document.querySelectorAll("#cfgSections .ha-section").forEach((sec) => {
      const ta = sec.querySelector("textarea");
      if (ta && ta.value !== ta.dataset.original) sections.push({ key: sec.dataset.key, text: ta.value, expectedUpdatedAt: sec.dataset.expectedUpdatedAt || null });
    });
    act("saveConfig", { sections });
  });
  $("updCheck") && $("updCheck").addEventListener("click", () => act("updatesCheck", { releaseTag: $("updTag").value }));
  $("updStage") && $("updStage").addEventListener("click", () => act("updatesStage", { releaseTag: $("updTag").value }));
  $("updApply") && $("updApply").addEventListener("click", () => act("updatesApply", { updateId: state && state.maintenanceTab && state.maintenanceTab.current ? state.maintenanceTab.current.updateId : "" }));
  $("updResume") && $("updResume").addEventListener("click", () => act("updatesApply", { updateId: state && state.maintenanceTab && state.maintenanceTab.current ? state.maintenanceTab.current.updateId : "", resume: true }));
  $("updCancel") && $("updCancel").addEventListener("click", () => act("updatesCancel", { updateId: state && state.maintenanceTab && state.maintenanceTab.current ? state.maintenanceTab.current.updateId : "" }));
  document.querySelectorAll("#updResolveRow [data-resolve]").forEach((b) => b.addEventListener("click", () =>
    act("updatesResolve", { updateId: state && state.maintenanceTab && state.maintenanceTab.current ? state.maintenanceTab.current.updateId : "", action: b.getAttribute("data-resolve") })));

  window.addEventListener("message", (e) => {
    const m = e.data;
    if (!m || typeof m.type !== "string") return;
    if (m.type === "hostadmin.state") render(m.state);
    else if (m.type === "hostadmin.tokens") renderTokens(m.name, m.tokens);
    else if (m.type === "hostadmin.overrides") renderOverrides(m);
    else if (m.type === "hostadmin.allowanceProblems") {
      const row = state && state.users && state.users.rows.find((r) => r.name === m.name);
      if (row) openUserEditor(row, { problems: m.problems, tokens: m.tokens || null });
    } else if (m.type === "hostadmin.configSaved") { cfgEdits.clear(); }
  });

  post({ type: "hostadmin.ready" });
})();
