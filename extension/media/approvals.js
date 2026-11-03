// SHARED WEBVIEW MEDIA, used by Construct Companion only: the key vault's tray pop-out (Core/Vault/VaultApprovals.cs).
/* global acquireVsCodeApi */
// Renders `approvals.state` and posts `approvals.<action>` requests. Nothing here decides anything: the
// Companion lists the pending approvals, says when each item's Approve is armed, and checks every answer
// against its records again. The texts come from VMs and hosts, so every one reaches the DOM through
// textContent. Items are updated in place (keyed by id), so the once-a-second refresh of the time left
// never moves a button under the pointer.
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);
  const post = (msg) => vscode.postMessage(msg);
  // id -> { root, left, error, approve, deny, armed, busy, timer }
  const items = new Map();
  let reported = 0;

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = String(text);
    return e;
  }
  function sync(entry) {
    entry.approve.disabled = entry.busy || !entry.armed;
    entry.deny.disabled = entry.busy;
  }
  function decide(id, decision) {
    const entry = items.get(id);
    if (!entry || entry.busy || (decision === "approve" && !entry.armed)) return;
    entry.busy = true; entry.error.textContent = ""; sync(entry);
    post({ type: "approvals.decide", id, decision });
  }
  // The request in parts (lead, secrets, facts, note, reason, source): "VM asks for", each secret's name in
  // bold with its description, then what access, why and who. Without them (an older host), its message.
  function details(item) {
    const parts = [];
    const who = el("div", "ap-who");
    const vm = el("b", null, item.vm);
    who.append(vm, document.createTextNode(" · " + item.where + " " + item.lead));
    who.title = who.textContent;
    parts.push(who);
    const list = el("ul", "ap-secrets");
    for (const secret of item.secrets) {
      const row = el("li", "ap-secret");
      const line = el("div", "ap-secret-line");
      line.append(el("span", "ap-secret-name", secret.name));
      if (secret.hasUsername) line.append(el("span", "ap-user", "with username"));
      row.append(line);
      if (secret.description) row.append(el("div", "ap-desc", secret.description));
      list.append(row);
    }
    parts.push(list);
    const facts = (Array.isArray(item.facts) ? item.facts : []).map((f) => [f.label, f.value]);
    if (item.reason) facts.push(["Reason", "\u201c" + item.reason + "\u201d"]);
    if (item.source) facts.push(["Requested by", item.source]);
    if (facts.length) {
      const grid = el("dl", "ap-facts");
      for (const [label, value] of facts) grid.append(el("dt", null, label), el("dd", null, value));
      parts.push(grid);
    }
    if (item.note) parts.push(el("div", "ap-note", item.note));
    return parts;
  }
  function structured(item) { return Array.isArray(item.secrets) && item.secrets.length > 0 && typeof item.lead === "string"; }
  function create(item) {
    const root = el("section", "lcard ap-item");
    root.dataset.id = item.id;
    const head = el("div", "ap-head");
    const left = el("span", "ap-left");
    let body;
    if (structured(item)) {
      // The window says KEY VAULT already: the title's kind is enough ("Access request").
      const kind = String(item.title || "").replace(/^key vault\s*[\u2014-]\s*/i, "");
      head.append(el("span", "ap-kind", kind ? kind.charAt(0).toUpperCase() + kind.slice(1) : "Request"), left);
      body = details(item);
    } else {
      const vm = el("span", "lcard-h ap-vm", item.vm + " · " + item.where);
      vm.title = vm.textContent;
      head.append(vm, left);
      const names = el("div", "ap-names");
      for (const name of item.names || []) names.append(el("span", "ap-name", name));
      body = [el("div", "ap-title", item.title), names, el("div", "ap-message", item.message)];
    }
    const error = el("div", "ap-error");
    error.setAttribute("role", "status");
    const actions = el("div", "ap-actions");
    const deny = el("button", "btn", item.deny || "Deny");
    deny.type = "button"; deny.dataset.decision = "deny";
    const approve = el("button", "btn start", item.action || "Approve");
    approve.type = "button"; approve.dataset.decision = "approve";
    approve.title = "Becomes available a second after the request appears";
    actions.append(deny, approve);
    root.append(head, ...body, error, actions);
    const entry = { root, left, error, approve, deny, armed: false, busy: false, timer: 0 };
    deny.addEventListener("click", () => decide(item.id, "deny"));
    approve.addEventListener("click", () => decide(item.id, "approve"));
    return entry;
  }
  function arm(entry, armIn) {
    if (entry.armed || entry.timer) return;
    if (!(armIn > 0)) { entry.armed = true; sync(entry); return; }
    entry.timer = setTimeout(() => { entry.timer = 0; entry.armed = true; sync(entry); }, armIn);
  }
  function render(state) {
    const list = $("aList");
    const wanted = Array.isArray(state.items) ? state.items : [];
    const ids = new Set(wanted.map((i) => i.id));
    for (const [id, entry] of items) if (!ids.has(id)) { clearTimeout(entry.timer); entry.root.remove(); items.delete(id); }
    let previous = null;
    for (const item of wanted) {
      let entry = items.get(item.id);
      if (!entry) { entry = create(item); items.set(item.id, entry); }
      entry.left.textContent = item.left || "";
      arm(entry, item.armIn);
      sync(entry);
      // Keep the Companion's order without re-inserting nodes that are already in place.
      const expected = previous ? previous.nextSibling : list.firstChild;
      if (expected !== entry.root) list.insertBefore(entry.root, expected);
      previous = entry.root;
    }
    $("aEmpty").hidden = wanted.length > 0;
    $("aStatus").textContent = wanted.length === 1 ? "1 request waits for your approval"
      : wanted.length > 1 ? wanted.length + " requests wait for your approval" : "nothing waits";
    report();
  }
  function done(message) {
    const entry = items.get(message.id);
    if (!entry) return;
    entry.busy = false;
    entry.error.textContent = message.notice ? String(message.notice) : "";
    sync(entry);
    report();
  }
  // The window sizes itself to the page, up to the launcher popup's height.
  function report() {
    const height = Math.ceil(document.querySelector(".lshell").getBoundingClientRect().height);
    if (height !== reported) { reported = height; post({ type: "approvals.size", height }); }
  }

  $("aHide").addEventListener("click", () => post({ type: "approvals.hide" }));
  $("aVault").addEventListener("click", () => post({ type: "approvals.openVault" }));
  window.addEventListener("message", (event) => {
    const message = event.data;
    if (!message || typeof message !== "object") return;
    if (message.type === "approvals.state") render(message);
    else if (message.type === "approvals.done") done(message);
  });
  post({ type: "approvals.ready" });
})();
