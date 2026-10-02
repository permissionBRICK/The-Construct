// SHARED WEBVIEW MEDIA, used by Construct Companion only: the Key Vault window (Core/Vault/VaultView.cs).
/* global acquireVsCodeApi */
// Renders `vault.state` and posts `vault.<action>` requests. Nothing here decides anything and nothing
// here ever holds a value: the state carries names, descriptions, usernames, counts and access metadata;
// adding, editing and copying a secret, the vault key and phone pairing all happen in native dialogs.
// Every value that reaches the DOM goes through textContent.
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);
  const post = (msg) => vscode.postMessage(msg);

  let state = null;
  let tab = "secrets";
  // Requests in flight ("action" or "action:key"): their controls stay disabled until vault.done.
  const busy = new Set();
  let noticeTimer = null;

  // ── DOM helpers ─────────────────────────────────────────────────────────────
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = String(text);
    return e;
  }
  function show(node, on) { if (node) node.hidden = !on; }
  function text(id, value) { const n = $(id); if (n) n.textContent = value == null ? "" : String(value); }
  function plural(n, one, many) { return n + " " + (n === 1 ? one : many); }
  // One request; `key` names the row it is about, so only that row's control waits.
  function request(action, fields, key) {
    const id = key ? action + ":" + key : action;
    if (busy.has(id)) return;
    busy.add(id);
    post(Object.assign({ type: "vault." + action }, fields || {}));
    render();
  }
  function button(label, cls, action, fields, key, title) {
    const b = el("button", "btn " + (cls || ""), label);
    b.type = "button";
    if (title) b.title = title;
    const id = key ? action + ":" + key : action;
    const waiting = busy.has(id);
    b.disabled = waiting || (state && state.unavailable != null && action !== "reset");
    b.classList.toggle("v-busy", waiting);
    b.setAttribute("aria-busy", String(waiting));
    b.addEventListener("click", () => request(action, fields, key));
    return b;
  }

  // ── time ────────────────────────────────────────────────────────────────────
  function span(ms) {
    const minutes = Math.round(Math.abs(ms) / 60000);
    if (minutes < 60) return Math.max(1, minutes) + " min";
    if (minutes < 60 * 36) return Math.round(minutes / 60) + " h";
    return plural(Math.round(minutes / 1440), "day", "days");
  }
  function ago(at) {
    if (at == null) return "never";
    const d = state.now - at;
    if (d < 45000) return "just now";
    if (d > 7 * 86400000) return date(at);
    return span(d) + " ago";
  }
  function inTime(at) { const d = at - state.now; return d <= 30000 ? "now" : "in " + span(d); }
  function date(at) { return new Date(at).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }); }
  function full(at) { return at == null ? "" : new Date(at).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }); }
  function clock(at) { return new Date(at).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }); }
  function day(at) {
    const d = new Date(at), now = new Date(state.now);
    const midnight = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const diff = Math.round((midnight(now) - midnight(d)) / 86400000);
    return diff === 0 ? "Today" : diff === 1 ? "Yesterday" : d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" });
  }

  // ── tabs ────────────────────────────────────────────────────────────────────
  const tabs = Array.from(document.querySelectorAll("#vTabs .utab"));
  function selectTab(next) {
    tab = next;
    tabs.forEach((t) => {
      const on = t.dataset.tab === tab;
      t.classList.toggle("sel", on);
      t.setAttribute("aria-selected", String(on));
      t.tabIndex = on ? 0 : -1;
      show($("tab-" + t.dataset.tab), on);
    });
  }
  tabs.forEach((t, i) => {
    t.addEventListener("click", () => selectTab(t.dataset.tab));
    t.addEventListener("keydown", (e) => {
      if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
      const next = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
      selectTab(next.dataset.tab); next.focus();
    });
  });

  // Static controls (header refresh, add, sync, key, reset) carry data-vault.
  document.querySelectorAll("[data-vault]").forEach((b) => b.addEventListener("click", () => request(b.dataset.vault)));
  $("vFilter").addEventListener("input", () => renderSecrets());
  $("vNoticeClose").addEventListener("click", () => notice(null));

  function notice(n) {
    clearTimeout(noticeTimer);
    show($("vNotice"), !!n);
    if (!n) return;
    $("vNotice").classList.toggle("error", !!n.error);
    text("vNoticeText", n.text);
    // A confirmation fades on its own; an error stays until dismissed.
    if (!n.error) noticeTimer = setTimeout(() => notice(null), 8000);
  }

  // ── render ──────────────────────────────────────────────────────────────────
  function render() {
    if (!state) return;
    const unavailable = state.unavailable != null;
    show($("vUnavailable"), unavailable);
    text("vUnavailableText", state.unavailable || "");
    document.querySelectorAll("[data-vault]").forEach((b) => {
      const waiting = busy.has(b.dataset.vault);
      b.disabled = waiting || (unavailable && b.dataset.vault !== "reset" && b.dataset.vault !== "refresh");
      b.classList.toggle("v-busy", waiting);
      b.setAttribute("aria-busy", String(waiting));
    });
    $("vFilter").disabled = unavailable;
    renderStrip(); renderSecrets(); renderAccess(); renderActivity(); renderHosts();
  }

  function renderStrip() {
    const secrets = state.secrets.length;
    text("vPillSecrets", unavailableText() || plural(secrets, "secret", "secrets"));
    const vms = new Set(state.leases.map((l) => l.host + "\u0000" + l.vm)).size;
    const pill = $("vPillAccess");
    pill.classList.toggle("v-live", vms > 0);
    pill.querySelector(".dot").classList.toggle("live", vms > 0);
    text("vPillAccessText", vms === 0 ? "no VM holds a secret" : plural(vms, "VM holds access", "VMs hold access"));
    const scrubs = state.scrubs.length;
    show($("vPillScrubs"), scrubs > 0);
    text("vPillScrubs", plural(scrubs, "VM waits for a scrub", "VMs wait for a scrub"));
    const hosts = state.hosts.length, attention = state.hosts.filter((h) => h.level === "warn").length;
    const hostPill = $("vPillHosts");
    hostPill.classList.toggle("warn", attention > 0);
    text("vPillHosts", hosts === 0 ? "no hosts" : attention > 0 ? plural(attention, "host needs attention", "hosts need attention")
      : plural(hosts, "host", "hosts") + (state.hosts.every((h) => h.level === "ok") ? " in sync" : ""));
    text("vCountSecrets", secrets || "");
    text("vCountAccess", state.leases.length || "");
    text("vCountHosts", hosts || "");
  }
  function unavailableText() { return state.unavailable != null ? "vault unreadable" : ""; }

  function renderSecrets() {
    if (!state) return;
    const host = $("vSecrets");
    host.textContent = "";
    const filter = $("vFilter").value.trim().toLowerCase();
    const shown = state.secrets.filter((s) => !filter || s.name.toLowerCase().includes(filter) || s.description.toLowerCase().includes(filter) || s.username.toLowerCase().includes(filter));
    show($("vSecretsEmpty"), state.secrets.length === 0 && state.unavailable == null);
    show($("vSecretsNoMatch"), state.secrets.length > 0 && shown.length === 0);
    shown.forEach((s) => {
      const row = el("div", "v-item v-secret");
      row.dataset.name = s.name;
      const main = el("div", "v-main");
      const line = el("div", "v-line");
      line.appendChild(el("span", "v-name", s.name));
      if (s.username) {
        const user = el("span", "v-user");
        user.appendChild(el("span", "v-k", "user"));
        user.appendChild(document.createTextNode(s.username));
        line.appendChild(user);
      }
      if (s.holders > 0) {
        const held = el("span", "tag upd", "held by " + plural(s.holders, "VM", "VMs"));
        held.title = "VMs that may read it now: see the Access tab";
        line.appendChild(held);
      }
      main.appendChild(line);
      main.appendChild(el("div", "v-desc" + (s.description ? "" : " none"), s.description || "No description: agents cannot tell what it is for."));
      const meta = el("div", "v-meta", "updated " + ago(s.updatedAt) + (s.addedBy ? " · added by an agent on " + s.addedBy : ""));
      meta.title = full(s.updatedAt);
      main.appendChild(meta);
      row.appendChild(main);
      const actions = el("div", "v-actions");
      actions.appendChild(button("Copy secret", "", "copySecret", { name: s.name }, s.name, "Copy the value; it leaves the clipboard again after 30 seconds"));
      if (s.username) actions.appendChild(button("Copy username", "ghost", "copyUsername", { name: s.name }, s.name));
      actions.appendChild(button("Edit…", "ghost", "edit", { name: s.name }, s.name));
      actions.appendChild(button("Delete", "ghost v-danger", "delete", { name: s.name }, s.name, "Delete it; VMs that hold it lose access and are scrubbed of it"));
      row.appendChild(actions);
      host.appendChild(row);
    });
  }

  const ORIGIN = { approved: "approved", once: "one-time", added: "added by the VM" };
  function renderAccess() {
    const leases = $("vLeases");
    leases.textContent = "";
    show($("vLeasesEmpty"), state.leases.length === 0);
    text("vAccessMeta", state.leases.length ? plural(state.leases.length, "lease", "leases") : "");
    state.leases.slice().sort((a, b) => a.expiresAt - b.expiresAt).forEach((l) => {
      const row = el("div", "v-item v-lease");
      const main = el("div", "v-main");
      const line = el("div", "v-line");
      line.appendChild(el("span", "v-vm", l.vm));
      line.appendChild(el("span", "v-holds", "holds"));
      line.appendChild(el("span", "v-name", l.name));
      const where = el("span", "tag", l.host ? "on " + l.hostName : "this PC");
      where.title = l.host ? "A VM of the host service " + l.hostName + "; the host keeps this lease" : "A VM this PC's Companion serves";
      line.appendChild(where);
      main.appendChild(line);
      const uses = l.usesLeft == null ? "unlimited uses" : plural(l.usesLeft, "use", "uses") + " left";
      const meta = el("div", "v-meta", uses + " · ends " + inTime(l.expiresAt) + " · " + (ORIGIN[l.origin] || l.origin));
      meta.title = "Ends " + full(l.expiresAt);
      main.appendChild(meta);
      if (l.reason) main.appendChild(el("div", "v-reason", "“" + l.reason + "”"));
      row.appendChild(main);
      const actions = el("div", "v-actions");
      actions.appendChild(button("Revoke", "danger", "revokeLease", l.host ? { id: l.id, host: l.host } : { id: l.id }, l.host + "/" + l.id, "End this access now and scrub the VM"));
      row.appendChild(actions);
      leases.appendChild(row);
    });

    const scrubs = $("vScrubs");
    scrubs.textContent = "";
    show($("vScrubsEmpty"), state.scrubs.length === 0);
    text("vScrubsMeta", state.scrubs.length ? plural(state.scrubs.length, "VM", "VMs") : "");
    state.scrubs.forEach((c) => {
      const row = el("div", "v-item v-scrub");
      const main = el("div", "v-main");
      const line = el("div", "v-line");
      line.appendChild(el("span", "v-vm", c.vm));
      c.names.forEach((n) => line.appendChild(el("span", "v-name v-chipname", n)));
      main.appendChild(line);
      const overdue = state.now - c.dueAt;
      const due = overdue < 0 ? "due " + inTime(c.dueAt) : overdue < 60000 ? "due now" : "waiting for " + span(overdue);
      const meta = el("div", "v-meta", due + " · runs when the VM is online with the Companion connected");
      meta.title = "Due " + full(c.dueAt);
      main.appendChild(meta);
      row.appendChild(main);
      const actions = el("div", "v-actions");
      actions.appendChild(button("Forget…", "ghost v-danger", "discardScrubs", { vm: c.vm }, c.vm, "Forget the pending scrubs for this VM"));
      row.appendChild(actions);
      scrubs.appendChild(row);
    });
  }

  function renderActivity() {
    const list = $("vActivity");
    list.textContent = "";
    show($("vActivityEmpty"), state.activity.length === 0);
    const warnings = state.activity.filter((a) => a.warning).length;
    text("vActivityMeta", state.activity.length ? plural(state.activity.length, "event", "events") + (warnings ? " · " + plural(warnings, "warning", "warnings") : "") : "");
    let heading = null;
    state.activity.forEach((a) => {
      const label = day(a.at);
      if (label !== heading) { heading = label; list.appendChild(el("li", "v-day", label)); }
      const item = el("li", "v-event" + (a.warning ? " warn" : ""));
      const when = el("time", "v-when", clock(a.at));
      when.dateTime = new Date(a.at).toISOString();
      when.title = full(a.at);
      item.appendChild(when);
      item.appendChild(el("span", "v-where", a.host ? (a.vm ? a.vm + " · " + a.host : a.host) : a.vm || "this PC"));
      item.appendChild(el("span", "v-what", a.text));
      list.appendChild(item);
    });
  }

  const MODE = {
    available: ["always available", "The host keeps the vault key, so requests, approvals from a phone and scrubs work while this PC is off."],
    locked: ["locked to my PC", "The host gets the key only while this PC starts or connects to one of your VMs, and only for that VM. VMs started without this PC cannot read secrets."],
    "": ["not set up yet", "The first sync with this host sets it up; switch this on first to keep it locked to this PC."],
  };
  function renderHosts() {
    const list = $("vHosts");
    list.textContent = "";
    show($("vNoHosts"), state.hosts.length === 0);
    const key = $("vKeyState");
    key.textContent = state.hasKey ? "on this PC" : "none yet";
    key.className = "tag" + (state.hasKey ? " ok" : "");
    key.title = state.hasKey ? "" : "The first sync with a host creates it, unless your hosts already use one: then import it.";
    state.hosts.forEach((h) => {
      const card = el("section", "module v-host");
      card.dataset.host = h.slug;
      const head = el("div", "mhead");
      head.appendChild(el("h2", "seclabel", h.host));
      head.appendChild(el("span", "tag " + (h.level === "ok" ? "ok" : h.level === "warn" ? "upd" : ""), h.level === "ok" ? "in sync" : h.level === "warn" ? "attention" : "not synced"));
      card.appendChild(head);
      const row = (k, v, title) => {
        const r = el("div", "row");
        r.appendChild(el("span", "k", k));
        const value = el("span", "v", v);
        if (title) value.title = title;
        r.appendChild(value);
        card.appendChild(r);
      };
      row("Last sync", ago(h.lastSyncAt), full(h.lastSyncAt));
      row("Your VMs", h.instances.length ? h.online + " of " + h.instances.length + " online · " + h.instances.join(", ") : "none registered");
      if (h.level === "warn") card.appendChild(el("p", "v-problem", h.status));

      const mode = MODE[h.mode] || MODE[""];
      const toggle = el("div", "toggle-row v-mode");
      const label = el("div");
      label.appendChild(el("div", "label", "Lock to my PC"));
      label.appendChild(el("div", "state", mode[0]));
      toggle.appendChild(label);
      const sw = el("div", "switch");
      sw.appendChild(el("span", "knob"));
      const locked = h.mode === "locked", waiting = busy.has("setMode:" + h.slug);
      sw.setAttribute("role", "switch");
      sw.setAttribute("aria-checked", String(locked));
      sw.setAttribute("aria-label", "Lock the vault on " + h.host + " to this PC");
      sw.tabIndex = 0;
      sw.classList.toggle("busy", waiting);
      const flip = () => { if (!waiting && state.unavailable == null) request("setMode", { host: h.slug, mode: locked ? "available" : "locked" }, h.slug); };
      sw.addEventListener("click", flip);
      sw.addEventListener("keydown", (e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); flip(); } });
      toggle.appendChild(sw);
      card.appendChild(toggle);
      card.appendChild(el("p", "v-mode-note", mode[1]));

      const sub = el("div", "v-sub");
      const subhead = el("div", "v-subhead");
      subhead.appendChild(el("span", null, "Paired phones"));
      subhead.appendChild(button("Pair a phone…", "ghost", "pairPhone", { host: h.slug }, h.slug, "Show a code a phone scans to approve requests (and log in to T3 Code)"));
      sub.appendChild(subhead);
      if (!h.devices.length) sub.appendChild(el("p", "cost-note v-empty", "No phone approves requests for this host yet."));
      h.devices.forEach((d) => {
        const item = el("div", "v-device");
        item.appendChild(el("span", "v-label", d.label));
        const meta = el("span", "v-meta", "paired " + (d.createdAt != null ? ago(d.createdAt) : "—") + " · used " + ago(d.lastUsedAt));
        meta.title = d.lastUsedAt != null ? "Last used " + full(d.lastUsedAt) : "Never used";
        item.appendChild(meta);
        item.appendChild(button("Revoke", "ghost v-danger", "revokeDevice", { host: h.slug, id: d.id }, h.slug + "/" + d.id, "This phone can no longer approve requests"));
        sub.appendChild(item);
      });
      card.appendChild(sub);
      list.appendChild(card);
    });
  }

  window.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m || typeof m.type !== "string") return;
    if (m.type === "vault.state" && m.state) { state = m.state; render(); }
    else if (m.type === "vault.done") {
      const action = String(m.action || "");
      // An answer without an action (a request the Companion could not read) releases everything.
      for (const id of Array.from(busy)) if (!action || id === action || id.startsWith(action + ":")) busy.delete(id);
      if (m.notice) notice(m.notice);
      render();
    }
  });

  selectTab(tab);
  post({ type: "vault.ready" });
})();
