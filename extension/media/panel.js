/* global acquireVsCodeApi */
(function () {
  "use strict";
  const vscode = acquireVsCodeApi();
  const post = (msg) => vscode.postMessage(msg);
  const $ = (id) => document.getElementById(id);

  // ── Matrix rain (header band) ───────────────────────────────────────────────
  (function rain() {
    const canvas = $("rain");
    if (!canvas) return;
    const header = canvas.parentElement;
    const ctx = canvas.getContext("2d");
    const glyphs = "ｱｲｳｴｵｶｷｸ0123456789ABCDEFｦｧｨ$<>/\\|=+*".split("");
    const fontSize = 14;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    let cols = 0, drops = [];
    function resize() {
      const w = header.clientWidth, h = header.clientHeight;
      canvas.width = w * dpr; canvas.height = h * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      cols = Math.ceil(w / fontSize); drops = [];
      for (let i = 0; i < cols; i++) drops[i] = Math.random() * (h / fontSize);
    }
    function draw() {
      const w = header.clientWidth, h = header.clientHeight;
      ctx.fillStyle = "rgba(2, 10, 6, 0.16)"; ctx.fillRect(0, 0, w, h);
      ctx.font = fontSize + "px ui-monospace, 'Cascadia Code', Consolas, monospace";
      for (let i = 0; i < cols; i++) {
        const x = i * fontSize, y = drops[i] * fontSize;
        ctx.fillStyle = "rgba(216, 255, 232, 0.85)"; ctx.fillText(glyphs[(Math.random() * glyphs.length) | 0], x, y);
        ctx.fillStyle = "rgba(0, 255, 102, 0.7)"; ctx.fillText(glyphs[(Math.random() * glyphs.length) | 0], x, y - fontSize);
        if (y > h && Math.random() > 0.975) drops[i] = 0;
        drops[i] += 0.5;
      }
    }
    resize();
    window.addEventListener("resize", resize);
    if (reduce) draw(); else setInterval(draw, 55);
  })();

  // ── Switches ────────────────────────────────────────────────────────────────
  // The mic switches (#voiceSwitch / #setMic) request a real backend change and
  // wait for an 'audio' message to flip; everything else toggles locally and is
  // gathered on Save.
  // Only the main-console switch is the LIVE audio control (posts setAudio and
  // waits for confirmation). The settings #setMic is a saved auto-enable
  // preference and toggles locally like every other settings switch.
  function isMicSwitch(el) { return el.id === "voiceSwitch"; }
  function setSwitch(el, on) { if (el) el.setAttribute("aria-checked", on ? "true" : "false"); }
  function swOn(el) { return !!el && el.getAttribute("aria-checked") === "true"; }

  document.querySelectorAll(".switch").forEach((sw) => {
    function toggle() {
      if (sw.classList.contains("busy")) return;
      const next = !swOn(sw);
      if (isMicSwitch(sw)) {
        sw.classList.add("busy");
        post({ type: "setAudio", enabled: next });
        return; // confirmed via 'audio' message
      }
      setSwitch(sw, next);
    }
    sw.addEventListener("click", toggle);
    sw.addEventListener("keydown", (e) => {
      if (e.key === " " || e.key === "Enter") { e.preventDefault(); toggle(); }
    });
  });

  // ── Backup-mode radios (one-time custom reinstall) ──────────────────────────
  const backupCards = Array.from(document.querySelectorAll(".radio-card[data-backup]"));
  backupCards.forEach((c) => c.addEventListener("click", () => {
    backupCards.forEach((x) => x.classList.remove("sel"));
    c.classList.add("sel");
  }));
  const currentBackupMode = () => {
    const sel = document.querySelector(".radio-card[data-backup].sel");
    return sel ? sel.getAttribute("data-backup") : "save";
  };
  const backupId = () => { const e = $("backupPick"); return e ? e.value : ""; };

  // ── Action buttons ──────────────────────────────────────────────────────────
  document.querySelectorAll("[data-cmd]").forEach((el) => {
    el.addEventListener("click", () => {
      const id = el.getAttribute("data-cmd");
      if (id === "customReinstall" || id === "customRedownload") {
        post({
          type: "customRebuild",
          mode: id === "customReinstall" ? "reinstall" : "redownload",
          backup: currentBackupMode(),
          backupId: backupId(),
        });
      } else {
        post({ type: "command", id });
      }
    });
  });

  // ── View switching + open-as-tab ────────────────────────────────────────────
  const mainView = $("mainView"), settingsView = $("settingsView");
  function showSettings(on) {
    if (settingsView) settingsView.hidden = !on;
    if (mainView) mainView.hidden = on;
    window.scrollTo(0, 0);
  }
  $("gearBtn") && $("gearBtn").addEventListener("click", () => showSettings(true));
  $("backBtn") && $("backBtn").addEventListener("click", () => showSettings(false));
  $("cancelBtn") && $("cancelBtn").addEventListener("click", () => showSettings(false));
  $("openTabBtn") && $("openTabBtn").addEventListener("click", () => post({ type: "openPanel" }));

  // ── Chips ─────────────────────────────────────────────────────────────────--
  function toggleChip(c) {
    c.classList.toggle("on");
    const chk = c.querySelector(".check");
    if (c.classList.contains("on") && !chk) {
      const s = document.createElement("span"); s.className = "check"; s.textContent = "✓ ";
      c.prepend(s);
    } else if (!c.classList.contains("on") && chk) {
      chk.remove();
    }
  }
  document.querySelectorAll("#setAgents .chip, #setProjects .chip").forEach((c) =>
    c.addEventListener("click", () => toggleChip(c)));

  // Main-view project chips open the per-project editor.
  function wireProjectChips() {
    document.querySelectorAll("#projChips .chip").forEach((c) =>
      c.addEventListener("click", () =>
        post({ type: "command", id: "editProject", project: c.dataset.project || c.textContent.trim() })));
  }
  wireProjectChips();

  // ── Save settings ───────────────────────────────────────────────────────────
  function val(id) { const e = $(id); return e ? e.value : ""; }
  function gatherSettings() {
    const agents = Array.from(document.querySelectorAll("#setAgents .chip.on")).map((c) => c.dataset.agent);
    const projects = Array.from(document.querySelectorAll("#setProjects .chip.on")).map((c) => c.textContent.replace("✓", "").trim());
    return {
      gitName: val("setGitName"), gitEmail: val("setGitEmail"), gitCred: swOn($("setGitCred")),
      agents, projects,
      ram: val("setRam"), disk: val("setDisk"), ubuntu: val("setUbuntu"), password: val("setPassword"),
      serveWeb: swOn($("setServeWeb")), tunnel: swOn($("setTunnel")), smb: swOn($("setSmb")), mic: swOn($("setMic")),
    };
  }
  $("saveBtn") && $("saveBtn").addEventListener("click", () => post({ type: "saveSettings", settings: gatherSettings() }));

  // ── Render state pushed from the extension ──────────────────────────────────
  function text(id, v) { const e = $(id); if (e && v != null) e.textContent = v; }

  function setOnline(online) {
    const pill = $("pillStatus");
    if (!pill) return;
    pill.classList.toggle("offline", !online);
    pill.innerHTML = online
      ? '<span class="dot live"></span> VM ONLINE'
      : '<span class="dot"></span> VM OFFLINE';
  }

  // Blank the VM-derived live fields so an offline/failed refresh never leaves
  // stale values from a previous successful probe on screen.
  function clearLiveVmData() {
    text("sysVm", "—"); text("sysResources", "—"); text("sysUbuntu", "—");
    renderAgents([]); renderProjects([]);
  }

  function render(s) {
    if (!s) return;
    // host/hostShort come from local config, so they are known even when the VM
    // is unreachable.
    if (s.hostShort) text("hostShort", s.hostShort);
    if (s.host) { text("pillHost", s.host); text("sysHost", s.host); }

    const online = s.online !== false;
    setOnline(online);

    // Unreachable, or reachable but the probe script failed: we have no trustworthy
    // VM data, so clear it rather than show stale values.
    if (!online || s.probeError) { clearLiveVmData(); return; }

    if (s.vmName != null) text("sysVm", s.vmName || "—");
    if (s.resources != null) text("sysResources", s.resources || "—");
    if (s.ubuntu != null) text("sysUbuntu", s.ubuntu || "—");
    if (s.constructRev) text("constructRev", s.constructRev);
    if (s.installed) text("pillInstalled", "installed " + s.installed);
    if (s.reprovisioned) text("pillReprovisioned", "reprovisioned " + s.reprovisioned);

    const b = $("updateBanner");
    if (s.update && s.update.available) { if (b) b.hidden = false; text("updateBehind", s.update.behind || ""); }
    else if (s.update && b) { b.hidden = true; }

    if (Array.isArray(s.agents)) renderAgents(s.agents);
    if (Array.isArray(s.projects)) renderProjects(s.projects);
    if (s.usage) renderUsage(s.usage);
    if (s.audio) renderAudio(s.audio);
  }

  function renderAgents(agents) {
    const host = $("agentList"); if (!host) return;
    host.innerHTML = "";
    if (!agents || !agents.length) {
      const d = document.createElement("div");
      d.className = "agent";
      d.innerHTML = '<span class="name" style="color:var(--dim)">—</span><span class="ver"></span><span class="tag"></span>';
      host.appendChild(d);
      return;
    }
    agents.forEach((a) => {
      const tagCls = a.updateAvailable ? "tag upd" : "tag ok";
      const tagTxt = a.updateAvailable ? (a.latest || "update") + " ↑" : "up to date";
      const div = document.createElement("div");
      div.className = "agent";
      div.innerHTML =
        '<span class="name"></span><span class="ver"></span><span class="' + tagCls + '"></span>';
      const name = div.querySelector(".name");
      name.appendChild(document.createTextNode(a.name + " "));
      if (a.detail) { const sm = document.createElement("small"); sm.textContent = a.detail; name.appendChild(sm); }
      div.querySelector(".ver").textContent = a.version || "—";
      div.querySelector(".tag").textContent = tagTxt;
      host.appendChild(div);
    });
  }

  function renderProjects(projects) {
    const host = $("projChips"); if (!host) return;
    host.innerHTML = "";
    if (!projects || !projects.length) {
      const s = document.createElement("span"); s.className = "chip"; s.textContent = "—"; host.appendChild(s);
      return;
    }
    projects.forEach((p) => {
      const chip = document.createElement("span");
      chip.className = "chip" + (p.selected ? " on" : "");
      chip.dataset.project = p.name;
      if (p.selected) { const ck = document.createElement("span"); ck.className = "check"; ck.textContent = "✓ "; chip.appendChild(ck); }
      chip.appendChild(document.createTextNode(p.name));
      host.appendChild(chip);
    });
    wireProjectChips();
  }

  function renderUsage(u) {
    const host = $("usageRows"); if (!host || !Array.isArray(u.tools)) return;
    const max = Math.max(1, ...u.tools.map((t) => t.tokens || 0));
    host.innerHTML = "";
    u.tools.forEach((t) => {
      const row = document.createElement("div");
      row.className = "usage-row";
      const pct = Math.round(((t.tokens || 0) / max) * 100);
      row.innerHTML =
        '<span class="ulabel"></span><span class="bar"><span></span></span><span class="utok"></span><span class="ucost"></span>';
      row.querySelector(".ulabel").textContent = t.label;
      row.querySelector(".bar > span").style.width = pct + "%";
      row.querySelector(".utok").textContent = t.tokensText || "—";
      row.querySelector(".ucost").textContent = t.costText || "—";
      host.appendChild(row);
    });
    text("usageTotalTok", u.totalTokensText || "—");
    text("usageTotalCost", u.totalCostText || "—");
  }

  function renderAudio(a) {
    const on = !!a.enabled;
    // Drive only the live console switch; the settings #setMic is an independent
    // saved preference, not a mirror of live audio state.
    const sw = $("voiceSwitch");
    if (sw) { setSwitch(sw, on); sw.classList.remove("busy"); }
    const state = $("voiceState");
    if (state) {
      state.textContent = on ? (a.capturing ? "live · capturing" : "armed · idle") : "disabled";
      state.style.color = on ? "var(--rain)" : "var(--dim)";
    }
    const sub = $("voiceSub"); if (sub) sub.hidden = !on;
    if (a.tunnel) text("voiceTunnel", a.tunnel);
  }

  window.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m) return;
    if (m.type === "state") render(m.state);
    else if (m.type === "audio") renderAudio(m);
    else if (m.type === "settings") applySettings(m.settings);
  });

  function applySettings(s) {
    if (!s) return;
    const setVal = (id, v) => { const e = $(id); if (e && v != null) e.value = v; };
    setVal("setGitName", s.gitName); setVal("setGitEmail", s.gitEmail);
    setVal("setRam", s.ram); setVal("setDisk", s.disk); setVal("setPassword", s.password);
    setVal("setUbuntu", s.ubuntu);
    setSwitch($("setGitCred"), s.gitCred); setSwitch($("setServeWeb"), s.serveWeb);
    setSwitch($("setTunnel"), s.tunnel); setSwitch($("setSmb"), s.smb); setSwitch($("setMic"), s.mic);
  }

  // Ask the extension for the current state once the webview is live.
  post({ type: "ready" });
})();
