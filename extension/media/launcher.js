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

    // Power controls (set before the offline early-return below). "Open on VM"
    // shows when reachable + not already connected here; "Start & connect" replaces
    // it when the VM is installed but stopped; "Shutdown" shows whenever reachable.
    // "Open on VM" is hidden for now: the "only when this window isn't already
    // connected" gate (s.connected) isn't reliable, so keep it out of the UI.
    const conn = $("lConnect");
    if (conn) conn.hidden = true;
    // Offline + not known-absent → offer Start (see panel.js for the full rationale:
    // the non-elevated Get-VM probe is permission-gated, so a stopped VM usually reads
    // "unknown"; the elevated Start-VM works anyway, so show it for off/unknown alike).
    const start = $("lStart");
    if (start) start.hidden = !(!online && s.vmState !== "absent" && s.vmState !== "running");
    const sd = $("lShutdown");
    if (sd) sd.hidden = !online;

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
