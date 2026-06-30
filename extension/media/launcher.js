/* global acquireVsCodeApi */
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);

  document.querySelectorAll("[data-cmd]").forEach((el) =>
    el.addEventListener("click", () => vscode.postMessage({ type: "command", id: el.getAttribute("data-cmd") })));
  $("lOpen").addEventListener("click", () => vscode.postMessage({ type: "openPanel" }));

  function render(s) {
    if (!s) return;
    const online = s.online !== false;
    const dot = $("lDot"); if (dot) dot.classList.toggle("offline", !online);
    const st = $("lStatusText");
    if (st) { st.textContent = online ? "VM online" : "VM offline"; st.style.color = online ? "var(--rain)" : "var(--crit)"; }
    if (s.host) $("lHost").textContent = s.host;

    // Offer "Open on VM" only when the VM is reachable and this window isn't
    // already connected to it.
    const conn = $("lConnect");
    if (conn) conn.hidden = !(online && s.connected === false);

    const agentsEl = $("lAgents");
    if (!online) { if (agentsEl) agentsEl.textContent = "unavailable"; $("lMeta").textContent = ""; return; }

    if (Array.isArray(s.agents) && agentsEl) {
      agentsEl.innerHTML = "";
      if (!s.agents.length) { agentsEl.textContent = "—"; }
      else s.agents.forEach((a) => {
        const row = document.createElement("div"); row.className = "lagent";
        const n = document.createElement("span"); n.textContent = a.name;
        const v = document.createElement("span"); v.className = "lver" + (a.updateAvailable ? " upd" : "");
        v.textContent = (a.version || "—") + (a.updateAvailable ? " ↑" : "");
        row.appendChild(n); row.appendChild(v); agentsEl.appendChild(row);
      });
    }
    const meta = [];
    if (s.installed) meta.push("installed " + s.installed);
    if (s.reprovisioned) meta.push("reprovisioned " + s.reprovisioned);
    $("lMeta").textContent = meta.join("  ·  ");
  }

  window.addEventListener("message", (ev) => { const m = ev.data; if (m && m.type === "state") render(m.state); });
  vscode.postMessage({ type: "ready" });
})();
