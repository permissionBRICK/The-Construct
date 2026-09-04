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
    // A design (themes/*.css) may hide the rain entirely — don't animate an
    // invisible canvas. A theme change reloads the webview, so this re-evaluates.
    if (getComputedStyle(canvas).display === "none") return;
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
      if (!id || el.disabled) return;
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

  // ── Instance picker ─────────────────────────────────────────────────────────
  // Rendered ONLY when the extension reports more than one instance (state.instances):
  // a single-VM install never sees the control. Selecting one asks the extension to
  // switch; it re-probes and pushes fresh state, which re-renders this list.
  const instanceSelect = $("instanceSelect");
  if (instanceSelect) {
    instanceSelect.addEventListener("change", () => {
      const name = instanceSelect.value;
      if (name) post({ type: "setInstance", name });
    });
  }
  // The instance the LAST full state described. A narrow live update (below) that names a
  // DIFFERENT instance describes a VM this panel is no longer showing, and is dropped.
  // Recorded from every state push, including the ones that carry no `instances` list —
  // a single-VM install pushes `instance` all the same, and its value never changes, so
  // the drop rule can never fire there.
  let shownInstance = null;
  function renderInstances(s) {
    shownInstance = s && s.instance ? s.instance : shownInstance;
    if (!instanceSelect) return;
    const names = Array.isArray(s.instances) ? s.instances.filter(Boolean) : [];
    if (names.length < 2) { instanceSelect.hidden = true; return; }
    // The instance this WINDOW is attached to over Remote-SSH (state.connectedInstance).
    // Not necessarily the selected one: adoption only preselects it and the user can
    // switch away, so the entry that holds this window's terminals and files is labelled.
    const connected = s.connectedInstance || "";
    // Rebuild only when the set or the selection actually changed, so a 30s refresh
    // never yanks an open dropdown shut under the pointer.
    const signature = names.join("\u0000") + "\u0001" + (s.instance || "") + "\u0001" + connected;
    if (instanceSelect.dataset.signature !== signature) {
      instanceSelect.dataset.signature = signature;
      instanceSelect.textContent = "";
      names.forEach((n) => {
        const opt = document.createElement("option");
        opt.value = n; opt.textContent = n === connected ? n + " (connected)" : n;
        if (n === s.instance) opt.selected = true;
        instanceSelect.appendChild(opt);
      });
    }
    instanceSelect.hidden = false;
  }

  // ── Usage period tabs (daily / monthly / total) ─────────────────────────────
  // Three views: daily = usage so far today, monthly = this calendar month, total =
  // all-time lifetime usage. Clicking a tab flips it optimistically, blanks the other
  // view's numbers, and asks the extension to re-collect the scoped usage (pushed back
  // as state.usage + state.usagePeriod). render() keeps the highlight in sync.
  const USAGE_PERIODS = ["daily", "monthly", "total"];
  const USAGE_SUBLABEL = { daily: "today", monthly: "this month", total: "all-time" };
  const normPeriod = (p) => (USAGE_PERIODS.indexOf(p) >= 0 ? p : "daily");
  const usageTabs = Array.from(document.querySelectorAll(".utab"));
  // The period whose numbers are currently displayed in the table (null = none/blanked).
  // render() uses it to blank the table when the active period changes but fresh usage
  // isn't in the push yet — so we never show one period's numbers under the other's
  // heading, on ANY surface, and even if the new period has no data / the collect fails.
  let shownUsagePeriod = null;
  function setUsageTab(period) {
    const p = normPeriod(period);
    usageTabs.forEach((t) => {
      const on = t.getAttribute("data-period") === p;
      t.classList.toggle("sel", on);
      t.setAttribute("aria-selected", on ? "true" : "false");
    });
    const sub = $("usageSub");
    if (sub) sub.innerHTML = " &middot; " + USAGE_SUBLABEL[p] + " &middot; ccusage";
  }
  // Reset the visible rows/total to placeholders so we never show one period's numbers
  // under the other period's tab while the fresh collection is in flight.
  function clearUsageRows() {
    const host = $("usageRows");
    if (host) host.querySelectorAll(".usage-row").forEach((row) => {
      const tok = row.querySelector(".utok"); if (tok) tok.textContent = "—";
      const cost = row.querySelector(".ucost"); if (cost) cost.textContent = "—";
      const bar = row.querySelector(".bar > span"); if (bar) bar.style.width = "0%";
    });
    text("usageTotalTok", "—"); text("usageTotalCost", "—");
  }
  usageTabs.forEach((t) => t.addEventListener("click", () => {
    if (t.classList.contains("sel")) return; // already active
    const period = normPeriod(t.getAttribute("data-period"));
    setUsageTab(period);
    clearUsageRows();
    shownUsagePeriod = null; // nothing valid shown until the scoped collection returns
    post({ type: "setUsagePeriod", period: period });
  }));

  // ── Chips ─────────────────────────────────────────────────────────────────--
  // Main-view project chips open the per-project editor.
  // Reserved profile names (D11): default and project.schema — their chips must NOT
  // open the edit modal. Kept in sync with projects.js RESERVED_PROFILE_NAMES.
  const RESERVED_NAMES = ["default", "project.schema"];
  function isReservedName(name) {
    return RESERVED_NAMES.includes(String(name || "").trim().toLowerCase());
  }

  function wireProjectChips() {
    document.querySelectorAll("#projChips .chip").forEach((c) => {
      // Locked chips (reserved names) do not open the edit modal on click.
      if (c.classList.contains("locked")) return;
      c.addEventListener("click", () =>
        post({ type: "command", id: "editProject", project: c.dataset.project || c.textContent.trim() }));
    });
  }
  wireProjectChips();

  // ── Project edit modal ──────────────────────────────────────────────────────
  // Opened by an ext->webview {type:'editProject', name, profile} message (posted
  // when the extension has read the host-side profile). The modal edits the profile
  // in structured controls, then posts {type:'saveProject', name, profile} back; the
  // extension re-sanitizes to the schema before writing, so this side is free to be
  // lenient (blank rows, whitespace) — anything malformed is coerced/dropped there.
  const modal = $("projModal");
  let editName = ""; // the profile name being edited (the file identity; not renamed here)
  // The `tests` block isn't edited in the modal (it's an open-ended object), so we
  // stash it from the opened profile and carry it back on save — otherwise every
  // edit would silently drop an existing tests config.
  let editTests = null;

  function openModal(on) {
    if (modal) modal.hidden = !on;
    // Focus the first field when opening so keyboard users land inside the dialog.
    if (on) { const f = modal && modal.querySelector("input, textarea"); if (f) f.focus(); }
  }
  function closeModal() { openModal(false); }

  // Build one repo row (url + directory + remove). `repo` may be {} for a blank add.
  function repoRow(repo) {
    repo = repo || {};
    const row = document.createElement("div");
    row.className = "pm-repo";
    row.innerHTML =
      '<input type="text" class="pm-url" placeholder="https://github.com/owner/repo.git" />' +
      '<input type="text" class="pm-dir" placeholder="directory (optional)" />' +
      '<button type="button" class="pm-del" title="Remove repo" aria-label="Remove repo">&times;</button>';
    row.querySelector(".pm-url").value = repo.url || "";
    row.querySelector(".pm-dir").value = repo.directory || "";
    row.querySelector(".pm-del").addEventListener("click", () => row.remove());
    return row;
  }

  // sdks object -> "name = v1, v2" lines. Values may be a string or an array.
  function sdksToText(sdks) {
    if (!sdks || typeof sdks !== "object") return "";
    return Object.keys(sdks).map((k) => {
      const v = sdks[k];
      const vals = Array.isArray(v) ? v.join(", ") : String(v == null ? "" : v);
      return k + " = " + vals;
    }).join("\n");
  }
  // "name = v1, v2" lines -> sdks object. A single value stays a string; multiple
  // become an array (mirrors default.json's {node:["26"]} vs a scalar). Blank/keyless
  // lines are ignored. The extension sanitizes again, so lenient parsing is fine.
  function textToSdks(text) {
    const out = {};
    String(text || "").split("\n").forEach((line) => {
      const eq = line.indexOf("=");
      if (eq < 0) return;
      const key = line.slice(0, eq).trim();
      if (!key) return;
      const vals = line.slice(eq + 1).split(",").map((s) => s.trim()).filter(Boolean);
      if (!vals.length) return;
      out[key] = vals.length === 1 ? vals[0] : vals;
    });
    return out;
  }

  // A textarea's lines -> a trimmed non-empty string array (host packages / commands).
  function linesToArray(text) {
    return String(text || "").split("\n").map((s) => s.trim()).filter(Boolean);
  }

  function populateModal(name, profile) {
    editName = name || "";
    profile = profile || {};
    editTests = (profile.tests && typeof profile.tests === "object" && !Array.isArray(profile.tests)) ? profile.tests : null;
    const t = $("pmTitle"); if (t) t.textContent = "Edit project · " + editName;
    const reposHost = $("pmRepos");
    if (reposHost) {
      reposHost.innerHTML = "";
      const repos = Array.isArray(profile.repos) ? profile.repos : [];
      if (repos.length) repos.forEach((r) => reposHost.appendChild(repoRow(r)));
      else reposHost.appendChild(repoRow({})); // one blank row to start
    }
    const setTa = (id, v) => { const e = $(id); if (e) e.value = v; };
    setTa("pmSdks", sdksToText(profile.sdks));
    // MCP stays as raw JSON: it's the one genuinely complex, open-ended field, so an
    // honest raw-JSON editor beats a half-form that can't express every server shape.
    setTa("pmMcp", JSON.stringify(Array.isArray(profile.mcp) ? profile.mcp : [], null, 2));
    setTa("pmHostPkgs", (Array.isArray(profile.hostPackages) ? profile.hostPackages : []).join("\n"));
    setTa("pmProvision", (Array.isArray(profile.provisionCommands) ? profile.provisionCommands : []).join("\n"));
    const err = $("pmMcpErr"); if (err) err.hidden = true;
    openModal(true);
  }

  // Gather the modal into a profile object. Returns null (and shows the MCP error)
  // when the MCP JSON doesn't parse — the only hard-stop; everything else is lenient.
  function gatherProfile() {
    const repos = [];
    document.querySelectorAll("#pmRepos .pm-repo").forEach((row) => {
      const url = (row.querySelector(".pm-url").value || "").trim();
      const dir = (row.querySelector(".pm-dir").value || "").trim();
      if (!url) return; // a blank/removed row is skipped (url is required)
      const entry = { url };
      if (dir) entry.directory = dir;
      repos.push(entry);
    });
    let mcp = [];
    const mcpRaw = ($("pmMcp") && $("pmMcp").value || "").trim();
    if (mcpRaw) {
      try {
        const parsed = JSON.parse(mcpRaw);
        if (!Array.isArray(parsed)) throw new Error("not an array");
        mcp = parsed;
      } catch (_) {
        const err = $("pmMcpErr"); if (err) err.hidden = false;
        return null;
      }
    }
    const out = {
      name: editName,
      repos: repos,
      sdks: textToSdks($("pmSdks") && $("pmSdks").value),
      mcp: mcp,
      hostPackages: linesToArray($("pmHostPkgs") && $("pmHostPkgs").value),
      provisionCommands: linesToArray($("pmProvision") && $("pmProvision").value),
    };
    // Preserve the un-edited tests block so a save doesn't drop it.
    if (editTests) out.tests = editTests;
    return out;
  }

  $("pmAddRepo") && $("pmAddRepo").addEventListener("click", () => {
    const reposHost = $("pmRepos"); if (reposHost) reposHost.appendChild(repoRow({}));
  });
  $("pmClose") && $("pmClose").addEventListener("click", closeModal);
  $("pmCancel") && $("pmCancel").addEventListener("click", closeModal);
  $("pmDelete") && $("pmDelete").addEventListener("click", () => {
    if (!editName) return;
    post({ type: "command", id: "deleteProject", project: editName });
    closeModal();
  });
  // Click the dimmed backdrop (but not the dialog itself) to dismiss.
  modal && modal.addEventListener("click", (e) => { if (e.target === modal) closeModal(); });
  // Esc closes the modal when it's open.
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && modal && !modal.hidden) closeModal(); });
  $("pmSave") && $("pmSave").addEventListener("click", () => {
    const profile = gatherProfile();
    if (!profile) return; // invalid MCP JSON — error already shown
    post({ type: "saveProject", name: editName, profile: profile });
    closeModal();
  });

  // ── Save settings ───────────────────────────────────────────────────────────
  function val(id) { const e = $(id); return e ? e.value : ""; }
  function gatherSettings() {
    // Coding-agents / project-profile selection isn't persisted yet (it's entered
    // in the console during reprovision/reinstall), so it's intentionally not
    // gathered here — see the deferred note in the settings view.
    return {
      gitName: val("setGitName"), gitEmail: val("setGitEmail"), gitCred: swOn($("setGitCred")),
      ram: val("setRam"), disk: val("setDisk"), cpu: val("setCpu"), ubuntu: val("setUbuntu"),
      autoCheckpoints: swOn($("setAutoCheckpoints")),
      serveWeb: swOn($("setServeWeb")), tunnel: swOn($("setTunnel")), smb: swOn($("setSmb")), mic: swOn($("setMic")),
      partialStreaming: swOn($("setPartialStreaming")),
      opencodeBackgroundWatcher: swOn($("setOpenCodeBackgroundWatcher")),
      t3code: swOn($("setT3")),
      t3codeChannel: val("setT3Channel"),
      t3codeLimitResume: swOn($("setT3Park")),
    };
  }
  $("saveBtn") && $("saveBtn").addEventListener("click", () => post({ type: "saveSettings", settings: gatherSettings() }));

  // ── Render state pushed from the extension ──────────────────────────────────
  function text(id, v) { const e = $(id); if (e && v != null) e.textContent = v; }

  // Disk-pressure flag next to "RAM / disk". Shown above 90% full only; the title
  // carries the actual number (the icon itself stays a glanceable "look at this").
  // pct === null means "no reading" (offline / older VM) -> hide rather than imply
  // a healthy disk.
  function setDiskWarn(pct) {
    const e = $("sysDiskWarn");
    if (!e) return;
    const warn = typeof pct === "number" && pct > 90;
    e.hidden = !warn;
    if (warn) {
      const msg = "VM disk is " + pct + "% full — free space before it breaks provisioning and agent work";
      e.title = msg;
      e.setAttribute("aria-label", msg);
    }
  }

  function setOnline(online) {
    const pill = $("pillStatus");
    if (!pill) return;
    pill.classList.toggle("offline", !online);
    pill.innerHTML = online
      ? '<span class="dot live"></span> VM ONLINE'
      : '<span class="dot"></span> VM OFFLINE';
  }

  function setPowerAction(s) {
    const btn = $("powerBtn");
    if (!btn) return;

    const cls = ["btn"];
    let cmd = "";
    let label = "\u231B Loading";
    let title = "Checking VM state";
    let disabled = true;

    if (s && (s.online === true || s.vmState === "running")) {
      cls.push("danger");
      cmd = "shutdown";
      label = "\u23FB Shutdown";
      title = "Shutdown the VM";
      disabled = false;
    } else if (s && s.vmState === "saved") {
      // The idle policy saved this VM: Hyper-V has its RAM on disk, so the same start
      // call RESUMES it where it left off. Say that, rather than "Start" \u2014 it is a
      // different promise, and the resume is the one that will happen.
      cls.push("start");
      cmd = "startConnect";
      label = "\u25B6 Resume & connect";
      title = "Resume the saved VM, then connect";
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

  // Blank the VM-derived live fields so an offline/failed refresh never leaves
  // stale values from a previous successful probe on screen.
  function clearLiveVmData() {
    text("sysVm", "—"); text("sysResources", "—"); text("sysUbuntu", "—"); setDiskWarn(null);
    // The install/reprovision markers are VM-derived too, so drop them back to the
    // "—" placeholder when we have no trustworthy VM data (offline / probe failed).
    text("pillInstalled", "installed —"); text("pillReprovisioned", "reprovisioned —");
    renderAgents([]); renderProjects([]);
  }

  // Which backend the active instance runs on, and (for a remote one) the host service
  // that owns it. Both rows stay HIDDEN for the local Hyper-V backend — and for a state
  // push that carries no backend at all, which is what an older extension host sends —
  // so a single-VM install sees exactly the card it always did.
  function renderBackend(s) {
    const backendRow = $("sysBackendRow"), serviceRow = $("sysServiceRow");
    const remote = !!s.backend && s.backend !== "hyperv-local";
    if (backendRow) {
      backendRow.hidden = !remote;
      if (remote) text("sysBackend", s.backend);
    }
    if (serviceRow) {
      const svc = remote && s.serviceHost ? s.serviceHost : "";
      serviceRow.hidden = !svc;
      if (svc) text("sysService", svc);
    }
  }

  // ── Forwards (B8) ───────────────────────────────────────────────────────────
  // The ports an agent asked for with `construct expose`, opened on THIS PC. Host-derived
  // in the sense that matters: the tunnels are ours, so the card is rendered before the
  // offline early-return — a VM that stopped answering has not closed the user's ports.
  //
  // The whole card stays hidden when the extension says so (no forwards AND a local
  // instance), which is the zero-change rule: an install that never runs `expose` sees
  // exactly the panel it saw before.
  function renderForwards(fw) {
    const mod = $("fwdModule");
    if (!mod) return;
    if (!fw || !fw.visible) { mod.hidden = true; return; }
    mod.hidden = false;

    const owner = $("fwdOwner");
    if (owner) {
      // Only worth saying when it is NOT this window: otherwise it is noise on every
      // single-window install, which is nearly all of them.
      owner.textContent = fw.owner === false ? "served by another window" : "";
      owner.title = fw.owner === false
        ? "Another VS Code window is serving this VM's forwards. The links still work here — it is the same PC."
        : "";
    }

    const items = Array.isArray(fw.items) ? fw.items : [];
    const empty = $("fwdEmpty");
    if (empty) empty.hidden = items.length > 0;

    const host = $("fwdList");
    if (!host) return;
    host.textContent = "";
    items.forEach((item) => {
      const row = document.createElement("div");
      row.className = "fwd-row " + (item.status === "open" ? "open" : item.status === "error" ? "error" : "queued");

      const port = document.createElement("span");
      port.className = "fwd-port";
      // vm:5173 -> :18800 when the local port had to differ, which is exactly the case
      // the user needs to see (it is why the link is not the number they typed).
      port.textContent = item.localPort && item.localPort !== item.vmPort
        ? "vm:" + item.vmPort + "→" + item.localPort
        : "vm:" + item.vmPort;
      row.appendChild(port);

      // Which kind of forward this is. `host` means the SERVICE published it on the host's
      // LAN address rather than this PC opening it — a different reach and a different
      // owner, so it has to be visible rather than implied.
      const target = document.createElement("span");
      target.className = "fwd-target";
      target.textContent = item.target === "host" ? "host" : "client";
      target.title = item.target === "host"
        ? "Published by the host service on its LAN address (construct expose --to host)"
        : "Opened on this PC over the SSH connection";
      row.appendChild(target);

      const label = document.createElement("span");
      label.className = "fwd-label";
      label.textContent = item.label || (item.status === "error" ? item.message : "");
      if (item.status === "error" && item.message) label.title = item.message;
      row.appendChild(label);

      const state = document.createElement("span");
      state.className = "fwd-state";
      state.textContent = item.status;
      row.appendChild(state);

      const open = document.createElement("button");
      open.type = "button";
      open.className = "fwd-open";
      open.textContent = "▷";
      open.title = item.url ? "Open " + item.url : "Not open yet";
      open.setAttribute("aria-label", "Open forward for VM port " + item.vmPort);
      open.disabled = !item.url;
      open.addEventListener("click", () => post({ type: "command", id: "openForward", forward: item.id }));
      row.appendChild(open);

      const close = document.createElement("button");
      close.type = "button";
      close.className = "fwd-close";
      close.textContent = "✕";
      // A local non-owner may not delete the owner's spool documents (the extension
      // refuses it too) — so don't offer a control that cannot work.
      close.disabled = item.closable === false;
      close.title = close.disabled
        ? "Another VS Code window owns this VM's forwards — close it from there"
        : "Close this forward";
      close.setAttribute("aria-label", "Close forward for VM port " + item.vmPort);
      close.addEventListener("click", () => post({ type: "command", id: "closeForward", forward: item.id }));
      row.appendChild(close);

      host.appendChild(row);
    });
  }

  // ── Idle policy (B8, plan §4.7) ─────────────────────────────────────────────
  // Remote instances only: the host service is what enforces it (which is the point — it
  // works with this PC switched off), so a local VM has nothing to configure and the card
  // stays hidden.
  //
  // The admin's cap is applied HERE as well as in the service, so the number in the box is
  // the number that will take effect instead of one that silently changes after the round
  // trip. The hint says why.
  let idleDirty = false;
  function renderIdlePolicy(p) {
    const mod = $("idleModule");
    if (!mod) return;
    if (!p) { mod.hidden = true; return; }
    mod.hidden = false;

    const timeout = $("idleTimeout"), action = $("idleAction"), hint = $("idleHint");
    // Never yank a value out from under someone mid-edit: a 30s refresh must not undo
    // half a typed number.
    if (timeout && !idleDirty) {
      timeout.value = String(p.timeoutMinutes);
      if (p.maxTimeoutMinutes > 0) timeout.max = String(p.maxTimeoutMinutes);
    }
    if (action && !idleDirty) action.value = p.action || "save";
    if (hint) {
      const parts = [];
      if (p.maxTimeoutMinutes > 0) parts.push("admin cap " + p.maxTimeoutMinutes + " min");
      if (p.clamped) parts.push("clamped to the cap");
      hint.textContent = parts.join(" · ");
    }
  }
  ["idleTimeout", "idleAction"].forEach((id) => {
    const el = $(id);
    if (el) el.addEventListener("input", () => { idleDirty = true; });
  });
  $("idleSave") && $("idleSave").addEventListener("click", () => {
    const timeout = $("idleTimeout"), action = $("idleAction");
    idleDirty = false;
    post({
      type: "saveIdlePolicy",
      policy: {
        timeoutMinutes: Number((timeout && timeout.value) || 0),
        action: (action && action.value) || "save",
      },
    });
  });

  function render(s) {
    if (!s) return;
    // Which VM this window drives. Host-derived (registry), so render it before the
    // offline early-return — the picker must work while the VM is down.
    renderInstances(s);
    // host/hostShort come from local config, so they are known even when the VM
    // is unreachable.
    if (s.hostShort) text("hostShort", s.hostShort);
    if (s.host) { text("pillHost", s.host); text("sysHost", s.host); }
    // Backend + host service: registry-derived, so render them before the offline
    // early-return too — which backend a VM is on doesn't depend on it answering.
    renderBackend(s);

    const online = s.online !== false;
    setOnline(online);

    // Stable power slot: it is present from first paint, then changes label/command
    // as state arrives so the rest of the strip never jumps under the pointer.
    setPowerAction(s);

    // Usage-period tab reflects the extension's shared selection (a local preference,
    // so sync it even on the offline path before the early-return below). If the active
    // period changed but this push carries no fresh usage yet, blank the table so stale
    // numbers never sit under the new heading (covers every surface + an empty/failed
    // collection, not just the webview that was clicked).
    if (s.usagePeriod) setUsageTab(s.usagePeriod);
    if (s.usagePeriod && s.usagePeriod !== shownUsagePeriod && !s.usage) { clearUsageRows(); shownUsagePeriod = null; }

    // Provision-stale nudge: the VM was provisioned with an OLDER Construct than the one
    // now installed on the host, so a reprovision would apply the update to the VM. Colour
    // the Reprovision button yellow + say so in its subtext/tooltip. Marker-based (host
    // settings), so it's known regardless of VM reachability — set before the early-return.
    const reprov = document.querySelector('.action-grid [data-cmd="reprovision"]');
    if (reprov) {
      const stale = !!s.provisionStale;
      reprov.classList.toggle("stale", stale);
      reprov.title = stale
        ? "The VM was provisioned with an older Construct — reprovision to apply the update to the VM."
        : "Reprovision — re-run setup, keep all data";
      const sub = reprov.querySelector("small");
      if (sub) sub.textContent = stale ? "update pending · reprovision to apply" : "re-run setup · keep all data";
    }

    // Config-sync state is host-derived (not VM-derived): render it regardless of
    // online status, and do NOT clear it in clearLiveVmData.
    if (s.configSync) renderConfigSync(s.configSync);

    // Forwards + idle policy are host/service-derived too, and a VM that stopped
    // answering has not closed the user's ports — so both are rendered before the
    // offline early-return and neither is cleared by clearLiveVmData.
    if (s.forwards !== undefined) renderForwards(s.forwards);
    if (s.idlePolicy !== undefined) renderIdlePolicy(s.idlePolicy);

    // Unreachable, or reachable but the probe script failed: we have no trustworthy
    // VM data, so clear it rather than show stale values.
    if (!online || s.probeError) { clearLiveVmData(); return; }

    if (s.vmName != null) text("sysVm", s.vmName || "—");
    if (s.resources != null) text("sysResources", s.resources || "—");
    // The VM's real size backs the VM-resources inputs as placeholders: an empty field
    // means "keep what the VM has" (the extension also records these into the instance's
    // state the first time it sees them), never a fabricated default.
    if (s.vmSpec) {
      const ph = (id, v) => { const e = $(id); if (e && v != null) e.placeholder = String(v); };
      ph("setRam", s.vmSpec.ramGb); ph("setDisk", s.vmSpec.diskGb); ph("setCpu", s.vmSpec.cpus);
    }
    if (s.diskPct != null) setDiskWarn(s.diskPct);
    if (s.ubuntu != null) text("sysUbuntu", s.ubuntu || "—");
    if (s.constructRev) text("constructRev", s.constructRev);
    // Authoritative on the online path: a value shows the date, an absent marker
    // (older VM, or unreadable /etc/construct/provisioned.env) falls back to "—".
    text("pillInstalled", "installed " + (s.installed || "—"));
    text("pillReprovisioned", "reprovisioned " + (s.reprovisioned || "—"));

    const b = $("updateBanner");
    if (s.update && s.update.available) { if (b) b.hidden = false; text("updateBehind", s.update.behind || ""); }
    else if (s.update && b) { b.hidden = true; }

    // "Register this VM": null on every push that has no offer (a local window, a
    // single-VM install, or a VM that was just registered), so the banner appears and
    // disappears with the state rather than sticking around.
    if (s.registerOffer !== undefined) {
      const rb = $("registerBanner");
      if (rb) rb.hidden = !s.registerOffer;
      text("registerHost", s.registerOffer ? s.registerOffer.host : "");
    }

    // "Remove instance": same contract as registerOffer — null on every push where the
    // action does not apply, so the section appears and disappears with the state
    // instead of sticking around after a switch back to the default VM.
    if (s.removeOffer !== undefined) {
      const rs = $("removeInstanceSection");
      if (rs) rs.hidden = !s.removeOffer;
      if (s.removeOffer) {
        text("removeInstanceName", s.removeOffer.name);
        text("removeInstanceList", (s.removeOffer.removes || []).join("; ") + ".");
        text("removeInstanceKeeps", (s.removeOffer.keeps || []).join(" "));
        const rb2 = $("removeInstanceBtn");
        if (rb2) rb2.textContent = s.removeOffer.deletesVm
          ? "\u2716 Remove instance and DELETE its VM"
          : "\u2716 Remove instance";
      }
    }

    if (Array.isArray(s.agents)) renderAgents(s.agents);
    if (Array.isArray(s.projects)) renderProjects(s.projects);
    if (s.usage) { renderUsage(s.usage); shownUsagePeriod = s.usagePeriod || shownUsagePeriod; }
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
      if (a.webui) {
        // Small inline open button for agents with a browser UI (T3 Code): the
        // extension mints a fresh pairing link over SSH and opens it on the host.
        const open = document.createElement("button");
        open.type = "button";
        open.className = "openbtn";
        open.textContent = "▷";
        open.title = "Open the " + a.name + " web UI in your browser";
        open.setAttribute("aria-label", "Open the " + a.name + " web UI in your browser");
        open.addEventListener("click", () => post({ type: "command", id: "openAgentWeb", agent: a.id }));
        name.appendChild(open);
      }
      div.querySelector(".ver").textContent = a.version || "—";
      const tag = div.querySelector(".tag");
      tag.textContent = tagTxt;
      if (a.updateAvailable && a.id) {
        // The ↑ tag doubles as a per-agent update button (the header's
        // "update all" stays for the bulk path).
        tag.dataset.agent = a.id;
        tag.setAttribute("role", "button");
        tag.setAttribute("tabindex", "0");
        tag.title = "Update " + a.name + " now";
        const fire = () => post({ type: "command", id: "updateAgent", agent: a.id });
        tag.addEventListener("click", fire);
        tag.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); fire(); } });
      }
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
      const reserved = isReservedName(p.name);
      const chip = document.createElement("span");
      chip.className = "chip" + (p.selected ? " on" : "") + (reserved ? " locked" : "");
      chip.dataset.project = p.name;
      if (reserved) {
        // D11: locked chip — show lock icon, no edit on click, no open button.
        chip.title = "reserved — create a named profile instead";
        const lock = document.createElement("span");
        lock.className = "check";
        lock.textContent = "🔒 ";
        chip.appendChild(lock);
      } else if (p.selected) {
        const ck = document.createElement("span"); ck.className = "check"; ck.textContent = "✓ "; chip.appendChild(ck);
      }
      chip.appendChild(document.createTextNode(p.name));
      if (!reserved) {
        // Inline open button on non-reserved chips.
        const open = document.createElement("button");
        open.type = "button";
        open.className = "openbtn";
        open.textContent = "▷";
        open.title = "Open " + p.name + " on the VM";
        open.setAttribute("aria-label", "Open " + p.name + " on the VM");
        open.addEventListener("click", (e) => {
          e.stopPropagation();
          post({ type: "command", id: "openProject", project: p.name });
        });
        chip.appendChild(open);
      }
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
    // Honesty: the guard patch is best-effort — the VM's Claude build may not carry the
    // known speech gate, in which case the chat mic button stays hidden. Reflect the
    // real result (gatePatched) rather than always claiming the button is unlocked; when
    // gatePatched is absent (unknown), keep neutral copy that doesn't assert a patch.
    const gate = $("voiceGate"), gnote = $("voiceGateNote"), grow = $("voiceGateRow");
    if (gate && gnote) {
      if (a.gatePatched === true) { gate.textContent = "chat mic button enabled"; gnote.textContent = "(remote-gate patched)"; }
      else if (a.gatePatched === false) { gate.textContent = "chat mic gate not patched"; gnote.textContent = "(unrecognised Claude build)"; }
      else { gate.textContent = "chat mic button"; gnote.textContent = "(gate patched if a known build)"; }
      if (grow) grow.classList.toggle("warn", a.gatePatched === false);
    }
  }

  /** Render the config-sync strip from state.configSync (D9). Host-derived — NOT
   *  cleared by clearLiveVmData. The strip is hidden when configSync is absent
   *  (extension hasn't pushed it yet) or when no cfgDir was resolved. */
  function renderConfigSync(cs) {
    const strip = $("csStrip");
    if (!strip) return;
    if (!cs) { strip.hidden = true; return; }
    strip.hidden = false;
    // Status result line.
    const result = $("csResult");
    if (result) {
      const parts = [];
      if (cs.lastResult === "ok") parts.push("synced");
      else if (cs.lastResult === "conflict") parts.push("conflict");
      else if (cs.lastResult === "blocked") parts.push("blocked");
      else if (cs.lastResult === "error") parts.push("error");
      if (cs.lastSyncAt) {
        try { parts.push(new Date(cs.lastSyncAt).toLocaleTimeString()); } catch (_) {}
      }
      if (cs.warnings && cs.warnings.length) parts.push(cs.warnings.length + " warning(s)");
      result.textContent = parts.join(" · ");
      if (cs.warnings && cs.warnings.length) result.title = cs.warnings.join("\n");
      else result.title = "";
    }
    // Conflict / blocked banner. A true conflict (unmerged paths) gets the
    // resolve-and-commit text; a blocked tick or a pending validation-gate
    // merge (mergeInProgress, NO unmerged paths — opening the repo shows no
    // conflicts, typically just an uncommitted invalid profile) gets the
    // engine's blockedReason so the banner names the actual problem.
    const conflict = $("csConflict");
    if (conflict) {
      const showBanner = !!(cs.conflict || cs.mergeInProgress || cs.lastResult === "blocked");
      conflict.hidden = !showBanner;
      const txt = $("csConflictText");
      if (txt && showBanner) {
        if (cs.conflict) {
          txt.textContent = "Config merge conflict — resolve it in the config repo, then commit.";
        } else {
          txt.textContent = "Config sync blocked — " +
            (cs.blockedReason || "a pending merge in the config repo needs attention");
        }
      }
    }
    // Git-missing notice.
    const gitMissing = $("csGitMissing");
    if (gitMissing) gitMissing.hidden = cs.gitPresent !== false;
    // Remotes list.
    const remoteSec = $("csRemotes");
    const remotesList = $("csRemotesList");
    if (remoteSec && remotesList) {
      const remotes = Array.isArray(cs.remotes) ? cs.remotes : [];
      remoteSec.hidden = remotes.length === 0 && !(cs.gitPresent);
      if (!remoteSec.hidden) {
        remotesList.innerHTML = "";
        remotes.forEach((r) => {
          const row = document.createElement("div");
          row.className = "cs-remote-row";
          const urlSpan = document.createElement("span");
          urlSpan.className = "cs-remote-url";
          urlSpan.textContent = r.url;
          row.appendChild(urlSpan);
          const pubBtn = document.createElement("button");
          pubBtn.type = "button";
          pubBtn.className = "cs-remote-publish";
          pubBtn.textContent = "publish";
          pubBtn.title = "Publish untracked local profiles into this repo";
          pubBtn.addEventListener("click", () => post({ type: "command", id: "publishConfigProfiles", url: r.url }));
          row.appendChild(pubBtn);
          const pushBtn = document.createElement("button");
          pushBtn.type = "button";
          pushBtn.className = "cs-remote-push";
          pushBtn.textContent = "↑";
          pushBtn.title = "Push local changes upstream";
          pushBtn.addEventListener("click", () => post({ type: "command", id: "pushConfigUpstream", url: r.url }));
          row.appendChild(pushBtn);
          const rmBtn = document.createElement("button");
          rmBtn.type = "button";
          rmBtn.className = "cs-remote-rm";
          rmBtn.textContent = "✕";
          rmBtn.title = "Remove remote";
          rmBtn.addEventListener("click", () => post({ type: "command", id: "removeConfigRemote", url: r.url }));
          row.appendChild(rmBtn);
          remotesList.appendChild(row);
        });
      }
    }
  }

  window.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m) return;
    if (m.type === "state") { render(m.state); return; }
    // SCOPE CHECK for the narrow live updates below. A per-instance message that names an
    // instance this panel is not showing describes another VM: a slow idle-policy PUT for
    // A answered after the window switched to B, A's trailing "mic tunnel down", A's last
    // forwards snapshot, A's settings file. Extension-side session gating cannot help any
    // of them — it decides whether to POST, and these are already posted and queued behind
    // B's state push. Generic on purpose: every per-instance producer stamps `instance`
    // (forwards, audio, settings, idlePolicy) and is covered by this one line. A message
    // with no `instance` (today: only `editProject`, a direct reply about the ONE host
    // config repo every instance shares) is deliberately unaffected.
    if (m.instance && shownInstance && m.instance !== shownInstance) return;
    if (m.type === "audio") renderAudio(m);
    // Narrow live updates, like {type:'audio'}: a tunnel coming up or an applied idle
    // policy repaints one card without going through render(), which would read every
    // absent field of a partial state as "no reading" and blank the rest of the panel.
    else if (m.type === "forwards") renderForwards(m.forwards);
    else if (m.type === "idlePolicy") renderIdlePolicy(m.idlePolicy);
    else if (m.type === "settings") applySettings(m.settings);
    else if (m.type === "editProject") populateModal(m.name, m.profile);
  });

  function applySettings(s) {
    if (!s) return;
    const setVal = (id, v) => { const e = $(id); if (e && v != null) e.value = v; };
    // Only drive a switch when the field is an actual boolean: a settings payload
    // that omits a key (e.g. one the installer wrote with just the git fields)
    // must leave that toggle's HTML default alone, not force it off.
    const setSw = (id, v) => { if (typeof v === "boolean") setSwitch($(id), v); };
    setVal("setGitName", s.gitName); setVal("setGitEmail", s.gitEmail);
    setVal("setRam", s.ram); setVal("setDisk", s.disk); setVal("setCpu", s.cpu);
    setVal("setUbuntu", s.ubuntu);
    setSw("setGitCred", s.gitCred); setSw("setServeWeb", s.serveWeb);
    setSw("setTunnel", s.tunnel); setSw("setSmb", s.smb); setSw("setMic", s.mic);
    setSw("setPartialStreaming", s.partialStreaming);
    setSw("setOpenCodeBackgroundWatcher", s.opencodeBackgroundWatcher);
    setSw("setT3", s.t3code);
    if (s.t3codeChannel) setVal("setT3Channel", s.t3codeChannel);
    setSw("setT3Park", s.t3codeLimitResume);
    setSw("setAutoCheckpoints", s.autoCheckpoints);
  }

  // Ask the extension for the current state once the webview is live.
  post({ type: "ready" });
})();
