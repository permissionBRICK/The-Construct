/* global acquireVsCodeApi */
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);

  document.querySelectorAll("[data-cmd]").forEach((el) =>
    el.addEventListener("click", () => {
      const id = el.getAttribute("data-cmd");
      if (!id || el.disabled) return;
      vscode.postMessage({ type: "command", id });
    }));
  $("lOpen").addEventListener("click", () => vscode.postMessage({ type: "openPanel" }));

  function setPowerAction(s) {
    const btn = $("lPowerBtn");
    if (!btn) return;

    const cls = ["lpower"];
    let cmd = "";
    let label = "\u231B Loading";
    let title = "Checking VM state";
    let disabled = true;

    if (s && (s.online === true || s.vmState === "running")) {
      cls.push("shutdown");
      cmd = "shutdown";
      label = "\u23FB Shutdown";
      title = "Shutdown the VM";
      disabled = false;
    } else if (s && s.vmState !== "absent" && s.vmState !== "running") {
      cls.push("start");
      cmd = "startConnect";
      label = "\u25B6 Start & connect";
      title = "Start the VM, then connect";
      disabled = false;
    } else {
      cls.push("loading");
      label = "\u231B Loading";
      title = "Waiting for VM state";
    }

    btn.className = cls.join(" ");
    btn.textContent = label;
    btn.title = title;
    btn.disabled = disabled;
    btn.setAttribute("aria-disabled", disabled ? "true" : "false");
    if (cmd) btn.setAttribute("data-cmd", cmd);
    else btn.removeAttribute("data-cmd");
  }

  function render(s) {
    if (!s) return;
    const online = s.online !== false;
    const dot = $("lDot"); if (dot) dot.classList.toggle("offline", !online);
    const st = $("lStatusText");
    if (st) { st.textContent = online ? "VM online" : "VM offline"; st.style.color = online ? "var(--rain)" : "var(--crit)"; }
    if (s.host) $("lHost").textContent = s.host;

    // Stable power slot: present from first paint, disabled while state is unknown,
    // then updated in-place so the compact dashboard never reflows around it.
    setPowerAction(s);

    // Construct self-update nudge — same banner intent as the full panel, compact here.
    // `s.update` is folded in only when online, so gate on it; clicking runs the same
    // updateConstruct flow (download + reinstall + auto-reload) via the [data-cmd] handler.
    const upd = $("lUpdate");
    if (upd) {
      const showUpd = !!(online && s.update && s.update.available);
      upd.hidden = !showUpd;
      if (showUpd) { const bh = $("lUpdateBehind"); if (bh) bh.textContent = s.update.behind || ""; }
    }

    // Provision-stale nudge (mirrors the panel): the VM was provisioned with an older
    // Construct than the installed one — colour Reprovision yellow. Marker-based, so set
    // before the offline early-return below.
    const lrep = document.querySelector('.lactions [data-cmd="reprovision"]');
    if (lrep) {
      const stale = !!s.provisionStale;
      lrep.classList.toggle("stale", stale);
      lrep.title = stale
        ? "The VM was provisioned with an older Construct — reprovision to apply the update."
        : "Reprovision — re-run setup, keep all data";
    }

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
