// Headless-Chromium smoke test for the Construct control-panel webview.
// Loads the real media/panel.{html,css,js} with a mocked acquireVsCodeApi, then
// asserts rendering + interactions + the webview<->extension message protocol —
// the part that cannot be exercised from a non-VS-Code environment.
//
// Run:  npm install && npx playwright install chromium && npm test
const { chromium } = require("playwright");
const http = require("http");
const fs = require("fs");
const path = require("path");

const MEDIA = path.join(__dirname, "..", "media");

// The design under test: every theme is a pure CSS layer over the same markup +
// controller, so the FULL suite must pass for any of them. Default classic;
// override with UI_SMOKE_THEME=terminal|native to prove a theme changed no behavior.
const THEME = /^[a-z]+$/.test(process.env.UI_SMOKE_THEME || "") ? process.env.UI_SMOKE_THEME : "classic";

function buildPage(htmlFile, scriptFile) {
  let html = fs.readFileSync(path.join(MEDIA, htmlFile), "utf8");
  // Strip the CSP for the harness (CSP correctness is reviewed separately); this
  // lets us inject the mock vscode API inline.
  html = html.replace(/<meta http-equiv="Content-Security-Policy"[\s\S]*?\/>/, "");
  html = html.replace(/{{cspSource}}/g, "").replace(/{{styleUri}}/g, "panel.css")
             .replace(/{{themeUri}}/g, "themes/" + THEME + ".css")
             .replace(/{{scriptUri}}/g, scriptFile).replace(/{{nonce}}/g, "test");
  const mock =
    '<script>window.__posted=[];window.acquireVsCodeApi=function(){return{' +
    'postMessage:function(m){window.__posted.push(m);},getState:function(){},setState:function(){}};};</script>';
  return html.replace(`<script nonce="test" src="${scriptFile}"></script>`,
    mock + `\n<script src="${scriptFile}"></script>`);
}

function serve() {
  const pages = { "/": buildPage("panel.html", "panel.js"), "/launcher": buildPage("launcher.html", "launcher.js") };
  const types = { ".css": "text/css", ".js": "text/javascript" };
  const server = http.createServer((req, res) => {
    const url = req.url.split("?")[0];
    if (pages[url]) { res.writeHead(200, { "Content-Type": "text/html" }); return res.end(pages[url]); }
    const ext = path.extname(url);
    if (types[ext]) {
      // Resolve under media/ (themes/ lives in a subdir), refusing traversal.
      const file = path.normalize(path.join(MEDIA, url));
      if (!file.startsWith(MEDIA + path.sep)) { res.writeHead(404); return res.end("nf"); }
      try { const body = fs.readFileSync(file); res.writeHead(200, { "Content-Type": types[ext] }); return res.end(body); }
      catch (_) { res.writeHead(404); return res.end("nf"); }
    }
    res.writeHead(404); res.end("nf");
  });
  return new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port })));
}

const results = [];
const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail: detail || "" });

(async () => {
  const { server, port } = await serve();
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const errors = [];
  page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
  page.on("pageerror", (e) => errors.push(String(e)));

  await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "networkidle" });
  await page.waitForTimeout(150);

  check("no console/page errors on load", errors.length === 0, errors.join(" | "));
  check("title mentions Construct", /Construct/.test(await page.title()));
  check("hero title renders", /CONSTRUCT/.test(await page.locator("h1.title").innerText()));
  check("rain canvas present", (await page.locator("#rain").count()) === 1);
  check("panel: power button present on first paint", await page.locator("#powerBtn").isVisible());
  check("panel: power button disabled while loading", await page.locator("#powerBtn").isDisabled());
  // Usage-period tabs default to daily ("today") before any state is pushed.
  check("usage: three period tabs (daily/monthly/total)", (await page.locator(".usage-tabs .utab").count()) === 3);
  check("usage: daily tab active by default", (await page.locator('.utab[data-period="daily"]').getAttribute("aria-selected")) === "true"
    && (await page.locator("#usageSub").textContent()).includes("today"));

  await page.click("#gearBtn");
  check("settings shows on gear", (await page.locator("#settingsView").isVisible()) && !(await page.locator("#mainView").isVisible()));
  await page.click("#backBtn");
  check("back returns to console", (await page.locator("#mainView").isVisible()) && !(await page.locator("#settingsView").isVisible()));

  await page.click("#voiceSwitch");
  let posted = await page.evaluate(() => window.__posted);
  check("voice switch posts setAudio:true", posted.some((m) => m.type === "setAudio" && m.enabled === true));
  check("voice switch becomes busy", (await page.getAttribute("#voiceSwitch", "class")).includes("busy"));

  await page.click('[data-cmd="reprovision"]');
  posted = await page.evaluate(() => window.__posted);
  check("reprovision posts command", posted.some((m) => m.type === "command" && m.id === "reprovision"));

  await page.click("#openTabBtn");
  posted = await page.evaluate(() => window.__posted);
  check("open-tab posts openPanel", posted.some((m) => m.type === "openPanel"));

  await page.click("#gearBtn");
  const before = await page.getAttribute("#setServeWeb", "aria-checked");
  await page.click("#setServeWeb");
  const after = await page.getAttribute("#setServeWeb", "aria-checked");
  check("settings switch toggles locally", before !== after);
  const setAudioCount = await page.evaluate(() => window.__posted.filter((m) => m.type === "setAudio").length);
  check("settings serve-web does NOT post setAudio", setAudioCount === 1, `setAudio count=${setAudioCount}`);

  // settings <- extension: a full payload populates the form...
  await page.evaluate(() => window.postMessage({ type: "settings", settings: {
    gitName: "Trinity", gitEmail: "trin@zion.io", gitCred: false,
    ram: "16", disk: "120", ubuntu: "22.04", serveWeb: false, tunnel: true, smb: false, mic: true,
  } }, "*"));
  await page.waitForTimeout(60);
  check("settings populate: text fields", (await page.inputValue("#setGitName")) === "Trinity" && (await page.inputValue("#setRam")) === "16");
  check("settings populate: switches driven", (await page.getAttribute("#setMic", "aria-checked")) === "true" && (await page.getAttribute("#setSmb", "aria-checked")) === "false");

  // ...and a PARTIAL payload (e.g. a file the installer wrote with just the git
  // keys) must NOT force the switches it omits to off — regression for applySettings.
  await page.evaluate(() => window.postMessage({ type: "settings", settings: { gitName: "Neo" } }, "*"));
  await page.waitForTimeout(60);
  check("settings partial: omitted switch keeps its value", (await page.getAttribute("#setMic", "aria-checked")) === "true");
  check("settings partial: present field updates", (await page.inputValue("#setGitName")) === "Neo");

  // save -> extension: gather the form and post saveSettings.
  await page.click("#saveBtn");
  const savePosted = await page.evaluate(() => window.__posted);
  const savedMsg = savePosted.find((m) => m.type === "saveSettings");
  check("save posts saveSettings carrying the form", savedMsg && savedMsg.settings && savedMsg.settings.gitName === "Neo");
  // Honesty: agents/projects aren't wired yet, so they must NOT be gathered, and
  // the settings view must not present ignored interactive agent/project chips.
  check("save omits unwired agents/projects", savedMsg && !("agents" in savedMsg.settings) && !("projects" in savedMsg.settings));
  check("settings: no ignored agent/project chip controls", (await page.locator("#setAgents, #setProjects").count()) === 0);

  await page.click("#backBtn");

  await page.evaluate(() => window.postMessage({ type: "state", state: {
    vmName: "agent-vm-01", host: "h.example.net", online: true,
    agents: [{ name: "Claude Code", detail: "CLI", version: "2.1.196", updateAvailable: true, latest: "2.1.210" }],
    projects: [{ name: "default", selected: true }, { name: "billing", selected: false }],
    usagePeriod: "total",
    usage: { tools: [
      { label: "Claude Code", tokens: 100, tokensText: "14.2M", costText: "$38.00" },
      { label: "Codex", tokens: 50, tokensText: "6.1M", costText: "$12.00" },
    ], totalTokensText: "20.3M", totalCostText: "$50.00" },
    update: { available: true, behind: "6 behind" },
  } }, "*"));
  await page.waitForTimeout(80);
  check("state render: vm name", (await page.locator("#sysVm").innerText()) === "agent-vm-01");
  check("state render: update banner shown", await page.locator("#updateBanner").isVisible());
  check("state render: agent version", (await page.locator("#agentList .agent").first().innerText()).includes("2.1.196"));
  check("state render: project chips", (await page.locator("#projChips .chip").count()) === 2);

  // provision-stale: Reprovision goes yellow (class "stale") + subtext when the VM was
  // provisioned with an older Construct than the installed one; cleared when in sync.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h", provisionStale: true } }, "*"));
  await page.waitForTimeout(60);
  check("panel: reprovision marked stale when VM behind", await page.locator('.action-grid [data-cmd="reprovision"]').evaluate((el) => el.classList.contains("stale")));
  check("panel: reprovision stale subtext", (await page.locator('.action-grid [data-cmd="reprovision"] small').innerText()).toLowerCase().includes("update pending"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: reprovision not stale when in sync", !(await page.locator('.action-grid [data-cmd="reprovision"]').evaluate((el) => el.classList.contains("stale"))));

  // per-chip open: each chip carries an inline ▷ button that opens that project on
  // the VM; the chip body still opens the edit modal, and ▷ must NOT bubble to it.
  // Default chip is locked (no openbtn); the billing chip has one — so 1 total open
  // button, not 2 (D11: reserved names have no open button).
  check("state render: non-reserved chips have open buttons", (await page.locator("#projChips .chip .openbtn").count()) === 1);
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('#projChips .chip[data-project="billing"] .openbtn');
  posted = await page.evaluate(() => window.__posted);
  check("panel: chip ▷ posts openProject with the project name",
    posted.some((m) => m.type === "command" && m.id === "openProject" && m.project === "billing"));
  check("panel: chip ▷ does not also post editProject (stopPropagation)",
    !posted.some((m) => m.type === "command" && m.id === "editProject"));
  // clicking the chip body (not the ▷) still opens the editor.
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('#projChips .chip[data-project="billing"]');
  posted = await page.evaluate(() => window.__posted);
  check("panel: chip body posts editProject", posted.some((m) => m.type === "command" && m.id === "editProject" && m.project === "billing"));

  // project edit modal: the extension replies with {type:'editProject', name, profile};
  // the modal opens, populates its structured controls, and Save posts saveProject.
  check("modal: hidden before an editProject message", await page.locator("#projModal").isHidden());
  await page.evaluate(() => window.postMessage({ type: "editProject", name: "billing", profile: {
    name: "billing",
    repos: [{ url: "https://h/o/billing.git", directory: "billing" }, { url: "https://h/o/api.git" }],
    sdks: { node: ["22", "24"], python: "3.12" }, mcp: [{ name: "gh", type: "stdio", command: "npx" }],
    hostPackages: ["build-essential"], provisionCommands: ["npm ci", "cp .env.example .env"],
    tests: { web: { runner: "playwright", command: "npm test" } },
  } }, "*"));
  await page.waitForTimeout(80);
  check("modal: opens on editProject message", await page.locator("#projModal").isVisible());
  check("modal: title carries the project name", /billing/i.test(await page.locator("#pmTitle").innerText()));
  check("modal: repo rows populated", (await page.locator("#pmRepos .pm-repo").count()) === 2);
  check("modal: first repo url populated", (await page.inputValue('#pmRepos .pm-repo:first-child .pm-url')) === "https://h/o/billing.git");
  check("modal: sdks rendered as name=values lines", (await page.inputValue("#pmSdks")).includes("node = 22, 24") && (await page.inputValue("#pmSdks")).includes("python = 3.12"));
  check("modal: mcp rendered as JSON", (await page.inputValue("#pmMcp")).includes('"gh"'));
  check("modal: provision commands one per line", (await page.inputValue("#pmProvision")) === "npm ci\ncp .env.example .env");

  // add + remove a repo row.
  await page.click("#pmAddRepo");
  check("modal: add-repo adds a row", (await page.locator("#pmRepos .pm-repo").count()) === 3);
  await page.click('#pmRepos .pm-repo:last-child .pm-del');
  check("modal: remove-repo drops a row", (await page.locator("#pmRepos .pm-repo").count()) === 2);

  // invalid MCP JSON blocks Save (and surfaces an inline error).
  await page.fill("#pmMcp", "{ not json");
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click("#pmSave");
  check("modal: invalid MCP JSON shows an error", await page.locator("#pmMcpErr").isVisible());
  posted = await page.evaluate(() => window.__posted);
  check("modal: invalid MCP JSON does NOT post saveProject", !posted.some((m) => m.type === "saveProject"));
  check("modal: stays open on invalid save", await page.locator("#projModal").isVisible());

  // fix the MCP + Save: posts a well-formed saveProject and closes the modal.
  await page.fill("#pmMcp", '[{"name":"gh","type":"stdio","command":"npx"}]');
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click("#pmSave");
  posted = await page.evaluate(() => window.__posted);
  const saveProjMsg = posted.find((m) => m.type === "saveProject");
  check("modal: valid Save posts saveProject with the name", saveProjMsg && saveProjMsg.name === "billing");
  check("modal: saved profile carries repos + parsed sdks + mcp", saveProjMsg &&
    Array.isArray(saveProjMsg.profile.repos) && saveProjMsg.profile.repos.length === 2 &&
    saveProjMsg.profile.sdks.node && Array.isArray(saveProjMsg.profile.mcp) && saveProjMsg.profile.mcp[0] && saveProjMsg.profile.mcp[0].name === "gh");
  // the un-edited `tests` block must survive the round-trip (not silently dropped).
  check("modal: saved profile preserves the un-edited tests block", saveProjMsg &&
    saveProjMsg.profile.tests && saveProjMsg.profile.tests.web && saveProjMsg.profile.tests.web.runner === "playwright");
  check("modal: closes after a valid save", await page.locator("#projModal").isHidden());

  // Esc + backdrop dismissal.
  await page.evaluate(() => window.postMessage({ type: "editProject", name: "x", profile: { name: "x", repos: [] } }, "*"));
  await page.waitForTimeout(60);
  await page.keyboard.press("Escape");
  check("modal: Escape closes it", await page.locator("#projModal").isHidden());

  // select-profiles action posts the command (the extension then shows the picker).
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('[data-cmd="selectProfiles"]');
  posted = await page.evaluate(() => window.__posted);
  check("panel: select-profiles posts command", posted.some((m) => m.type === "command" && m.id === "selectProfiles"));
  // import-from-VM action posts the command.
  await page.click('[data-cmd="importProjects"]');
  posted = await page.evaluate(() => window.__posted);
  check("panel: import-projects posts command", posted.some((m) => m.type === "command" && m.id === "importProjects"));

  // usage: the token-usage table renders a row per agent (bar + tokens + cost) and a
  // total row from the pushed usage state (renderUsage consumes {tools,totalTokensText,totalCostText}).
  check("usage: renders a row per agent", (await page.locator("#usageRows .usage-row").count()) === 2);
  check("usage: first row label + tokens + cost", (await page.locator("#usageRows .usage-row").first().innerText()).includes("Claude Code")
    && (await page.locator("#usageRows .usage-row .utok").first().innerText()) === "14.2M"
    && (await page.locator("#usageRows .usage-row .ucost").first().innerText()) === "$38.00");
  check("usage: total tokens + estimated cost", (await page.locator("#usageTotalTok").innerText()) === "20.3M"
    && (await page.locator("#usageTotalCost").innerText()) === "$50.00");
  // usage period tabs: the pushed usagePeriod ("total") highlights the matching tab and
  // updates the subheader; clicking another tab flips optimistically, blanks the stale
  // numbers, and posts setUsagePeriod (the extension re-collects the scoped window).
  check("usage: pushed usagePeriod highlights total tab",
    (await page.locator('.utab[data-period="total"]').getAttribute("aria-selected")) === "true"
    && (await page.locator('.utab[data-period="daily"]').getAttribute("aria-selected")) === "false"
    && (await page.locator("#usageSub").textContent()).includes("all-time"));
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('.utab[data-period="monthly"]');
  posted = await page.evaluate(() => window.__posted);
  check("usage: clicking the monthly tab posts setUsagePeriod", posted.some((m) => m.type === "setUsagePeriod" && m.period === "monthly"));
  check("usage: clicked tab activates + subheader updates",
    (await page.locator('.utab[data-period="monthly"]').getAttribute("aria-selected")) === "true"
    && (await page.locator('.utab[data-period="total"]').getAttribute("aria-selected")) === "false"
    && (await page.locator("#usageSub").textContent()).includes("this month"));
  check("usage: switching period blanks stale numbers until re-collect", (await page.locator("#usageTotalTok").innerText()) === "—");
  // Renderer robustness: a period-change state that arrives WITHOUT usage (slow/empty/
  // failed collection, or a second surface that didn't get the local click-clear) must
  // blank the table rather than leave the previous period's numbers under the new heading.
  // First render fresh daily numbers, then push {usagePeriod:'monthly'} with no usage.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, usagePeriod: "daily",
    usage: { tools: [{ label: "Claude Code", tokens: 100, tokensText: "9.9M", costText: "$9.00" }], totalTokensText: "9.9M", totalCostText: "$9.00" } } }, "*"));
  await page.waitForTimeout(60);
  check("usage: daily numbers render (pre-condition)", (await page.locator("#usageTotalTok").innerText()) === "9.9M");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, usagePeriod: "monthly" } }, "*"));
  await page.waitForTimeout(60);
  check("usage: period-change push without usage blanks the table",
    (await page.locator("#usageTotalTok").innerText()) === "—"
    && (await page.locator("#usageRows .usage-row .utok").first().innerText()) === "—");
  check("usage: period-change push still activates the new tab",
    (await page.locator('.utab[data-period="monthly"]').getAttribute("aria-selected")) === "true");
  // A same-period push without usage must NOT wipe the shown numbers.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, usagePeriod: "monthly",
    usage: { tools: [{ label: "Codex", tokens: 5, tokensText: "5.0M", costText: "$5.00" }], totalTokensText: "5.0M", totalCostText: "$5.00" } } }, "*"));
  await page.waitForTimeout(60);
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, usagePeriod: "monthly" } }, "*"));
  await page.waitForTimeout(60);
  check("usage: same-period push without usage keeps the numbers", (await page.locator("#usageTotalTok").innerText()) === "5.0M");
  // export json: the button posts the exportUsage command (the extension then collects
  // over SSH and saves via a Save dialog).
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('[data-cmd="exportUsage"]');
  posted = await page.evaluate(() => window.__posted);
  check("usage: export button posts exportUsage command", posted.some((m) => m.type === "command" && m.id === "exportUsage"));

  // add-project: the Projects action posts the addProject command (the extension
  // then prompts for a URL, clones over SSH, and opens the result in a new window).
  await page.click('[data-cmd="addProject"]');
  posted = await page.evaluate(() => window.__posted);
  check("panel: add-project posts command", posted.some((m) => m.type === "command" && m.id === "addProject"));

  await page.evaluate(() => window.postMessage({ type: "audio", enabled: true, capturing: false, tunnel: "vm:8767" }, "*"));
  await page.waitForTimeout(80);
  check("audio render: voice switch on", (await page.getAttribute("#voiceSwitch", "aria-checked")) === "true");
  check("audio render: substatus shown", await page.locator("#voiceSub").isVisible());
  check("audio render: not busy anymore", !(await page.getAttribute("#voiceSwitch", "class")).includes("busy"));
  // textContent (not innerText) — the label is CSS-uppercased, so read raw case.
  check("audio render: enabled+idle reads 'armed · idle'", /armed/.test(await page.locator("#voiceState").textContent()));
  // honesty: with no gatePatched signal the gate line stays NEUTRAL (doesn't assert a patch).
  check("audio render: unknown gate stays neutral", (await page.locator("#voiceGateNote").textContent()).includes("if a known build"));
  // gate patched -> asserts the mic button is unlocked; not patched -> says so (warns).
  await page.evaluate(() => window.postMessage({ type: "audio", enabled: true, capturing: false, tunnel: "vm:8767", gatePatched: true }, "*"));
  await page.waitForTimeout(60);
  check("audio render: gatePatched=true reads 'enabled'", (await page.locator("#voiceGate").textContent()).includes("enabled")
    && (await page.locator("#voiceGateNote").textContent()).includes("patched"));
  await page.evaluate(() => window.postMessage({ type: "audio", enabled: true, capturing: false, tunnel: "vm:8767", gatePatched: false }, "*"));
  await page.waitForTimeout(60);
  check("audio render: gatePatched=false says 'not patched'", (await page.locator("#voiceGate").textContent()).includes("not patched"));
  check("audio render: gatePatched=false warns", (await page.getAttribute("#voiceGateRow", "class")).includes("warn"));
  // on-demand capture: while the VM shim is connected (Claude recording), state goes live.
  await page.evaluate(() => window.postMessage({ type: "audio", enabled: true, capturing: true, tunnel: "vm:8767", gatePatched: true }, "*"));
  await page.waitForTimeout(60);
  check("audio render: capturing reads 'live · capturing'", /capturing/.test(await page.locator("#voiceState").textContent()));
  check("audio render: still on while capturing", (await page.getAttribute("#voiceSwitch", "aria-checked")) === "true");
  // disable: switch goes off, substatus hidden, state 'disabled'.
  await page.evaluate(() => window.postMessage({ type: "audio", enabled: false, capturing: false }, "*"));
  await page.waitForTimeout(60);
  check("audio render: disabled turns the switch off", (await page.getAttribute("#voiceSwitch", "aria-checked")) === "false");
  check("audio render: disabled hides substatus", !(await page.locator("#voiceSub").isVisible()));
  check("audio render: disabled reads 'disabled'", /disabled/.test(await page.locator("#voiceState").textContent()));

  // stale-data: after a successful probe, an offline/failed refresh must CLEAR the
  // VM-derived fields rather than leave the prior values on screen.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", hostShort: "agent-vm" } }, "*"));
  await page.waitForTimeout(80);
  check("offline clears vm name", (await page.locator("#sysVm").innerText()) === "—");
  check("offline clears resources", (await page.locator("#sysResources").innerText()) === "—");
  check("offline shows OFFLINE pill", /OFFLINE/.test(await page.locator("#pillStatus").innerText()));
  check("offline clears agent versions", (await page.locator("#agentList .agent .ver").first().innerText()).trim() === "");
  check("offline clears project chips", (await page.locator("#projChips .chip").innerText()).trim() === "—");
  check("offline keeps known host", (await page.locator("#sysHost").innerText()) === "h.example.net");

  // power button: one stable slot changes command/label, but never disappears.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("panel: shutdown wins when online + connected false", await page.locator("#powerBtn").innerText() === "⏻ Shutdown");
  await page.click("#powerBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: online power button posts shutdown", posted.some((m) => m.type === "command" && m.id === "shutdown"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: true } }, "*"));
  await page.waitForTimeout(60);
  check("panel: shutdown shows when already connected", await page.locator("#powerBtn").innerText() === "⏻ Shutdown");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("panel: start&connect shows when offline", await page.locator("#powerBtn").innerText() === "▶ Start & connect");

  // power controls: Start & connect (offline + VM stopped) and Shutdown (online).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, vmState: "off" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: power button remains visible when offline + VM stopped", await page.locator("#powerBtn").isVisible());
  check("panel: start&connect shows when offline + VM stopped", await page.locator("#powerBtn").innerText() === "▶ Start & connect");
  await page.click("#powerBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: start&connect posts startConnect", posted.some((m) => m.type === "command" && m.id === "startConnect"));
  // offline + 'unknown' (the non-elevated Get-VM probe was permission-denied) STILL
  // shows Start — the elevated Start-VM works regardless, so the button must not be
  // hidden just because the probe couldn't read the state (regression: it never showed).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, vmState: "unknown" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: start&connect shows when offline + probe unknown", await page.locator("#powerBtn").innerText() === "▶ Start & connect");
  // strict: only a positively-'absent' vmState (a privileged probe said the VM isn't installed) hides Start.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, vmState: "absent" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: power button still present when VM absent", await page.locator("#powerBtn").isVisible());
  check("panel: power button disabled when VM absent", await page.locator("#powerBtn").isDisabled());
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: shutdown wins when vmState is running", await page.locator("#powerBtn").innerText() === "⏻ Shutdown");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: true, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: shutdown shows when online", await page.locator("#powerBtn").innerText() === "⏻ Shutdown");
  await page.click("#powerBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: shutdown posts shutdown", posted.some((m) => m.type === "command" && m.id === "shutdown"));

  // panel degrades without horizontal overflow when dragged narrow — measured with the
  // power slot visible at narrow width (online).
  await page.setViewportSize({ width: 300, height: 1400 });
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(80);
  check("panel: power button visible at 300px", await page.locator("#powerBtn").isVisible());
  const panelColsTop = await page.locator(".cols").evaluate((el) => el.getBoundingClientRect().top);
  await page.evaluate(() => window.postMessage({ type: "state", state: {
    online: true, connected: false, vmState: "running",
    host: "very-long-agent-vm-hostname-that-used-to-wrap-and-shift.mshome.net",
    hostShort: "very-long-agent-vm-hostname",
  } }, "*"));
  await page.waitForTimeout(80);
  const panelColsTopLongHost = await page.locator(".cols").evaluate((el) => el.getBoundingClientRect().top);
  check("panel: long hostname does not shift content at 300px", Math.abs(panelColsTopLongHost - panelColsTop) <= 1,
    `before=${panelColsTop}, after=${panelColsTopLongHost}`);
  const panelOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check("panel: no horizontal overflow at 300px", panelOverflow <= 1, `overflow=${panelOverflow}px`);

  // ── Launcher (sidebar) surface ──────────────────────────────────────────────
  await page.setViewportSize({ width: 300, height: 1000 });
  await page.goto(`http://127.0.0.1:${port}/launcher`, { waitUntil: "networkidle" });
  await page.waitForTimeout(120);
  check("launcher: 3 lifecycle buttons", (await page.locator(".laction").count()) === 3);
  check("launcher: power button present on first paint", await page.locator("#lPowerBtn").isVisible());
  check("launcher: power button disabled while loading", await page.locator("#lPowerBtn").isDisabled());
  const launcherPowerTop = await page.locator("#lPowerBtn").evaluate((el) => el.getBoundingClientRect().top);
  await page.evaluate(() => window.postMessage({ type: "state", state: {
    online: true, host: "very-long-agent-vm-hostname-that-used-to-wrap-and-shift.mshome.net",
  } }, "*"));
  await page.waitForTimeout(80);
  const launcherPowerTopLongHost = await page.locator("#lPowerBtn").evaluate((el) => el.getBoundingClientRect().top);
  check("launcher: hostname render does not shift power button", Math.abs(launcherPowerTopLongHost - launcherPowerTop) <= 1,
    `before=${launcherPowerTop}, after=${launcherPowerTopLongHost}`);
  await page.click("#lOpen");
  let lposted = await page.evaluate(() => window.__posted);
  check("launcher: open posts openPanel", lposted.some((m) => m.type === "openPanel"));
  await page.click('.laction[data-cmd="reinstall"]');
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: reinstall posts command", lposted.some((m) => m.type === "command" && m.id === "reinstall"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", agents: [{ name: "Claude Code", version: "2.1.196", updateAvailable: true }], installed: "2026-06-12", reprovisioned: "1d ago" } }, "*"));
  await page.waitForTimeout(80);
  check("launcher: host rendered", (await page.locator("#lHost").innerText()) === "h.example.net");
  check("launcher: agent version rendered", (await page.locator("#lAgents").innerText()).includes("2.1.196"));
  check("launcher: online dot", !(await page.getAttribute("#lDot", "class")).includes("offline"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: offline dot", (await page.getAttribute("#lDot", "class")).includes("offline"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown wins when online + connected false", await page.locator("#lPowerBtn").innerText() === "⏻ Shutdown");
  await page.click("#lPowerBtn");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: online power button posts shutdown", lposted.some((m) => m.type === "command" && m.id === "shutdown"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: true } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown shows when already connected", await page.locator("#lPowerBtn").innerText() === "⏻ Shutdown");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: start&connect shows when offline", await page.locator("#lPowerBtn").innerText() === "▶ Start & connect");
  // strict ===: undefined `connected` keeps it hidden.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown shows when `connected` is unknown", await page.locator("#lPowerBtn").innerText() === "⏻ Shutdown");

  // launcher power controls: Start & connect (offline + stopped) and Shutdown (online).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", vmState: "off" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: power button remains visible when offline + VM stopped", await page.locator("#lPowerBtn").isVisible());
  check("launcher: start&connect shows when offline + VM stopped", await page.locator("#lPowerBtn").innerText() === "▶ Start & connect");
  await page.click("#lPowerBtn");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: start&connect posts startConnect", lposted.some((m) => m.type === "command" && m.id === "startConnect"));
  // offline + 'unknown' (permission-denied probe) still shows Start (see panel.js rationale).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", vmState: "unknown" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: start&connect shows when offline + probe unknown", await page.locator("#lPowerBtn").innerText() === "▶ Start & connect");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown wins when vmState is running", await page.locator("#lPowerBtn").innerText() === "⏻ Shutdown");
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: true, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown shows when online", await page.locator("#lPowerBtn").innerText() === "⏻ Shutdown");
  await page.click("#lPowerBtn");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: shutdown posts shutdown", lposted.some((m) => m.type === "command" && m.id === "shutdown"));

  // update banner: shows when an update is available, posts updateConstruct, hidden otherwise.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", update: { available: true, behind: "3 behind" } } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: update banner shows when available", await page.locator("#lUpdate").isVisible());
  check("launcher: update banner shows the behind count", (await page.locator("#lUpdateBehind").innerText()).includes("3 behind"));
  await page.click("#lUpdate");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: update banner posts updateConstruct", lposted.some((m) => m.type === "command" && m.id === "updateConstruct"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", update: { available: false } } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: update banner hidden when no update", !(await page.locator("#lUpdate").isVisible()));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", update: { available: true } } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: update banner hidden when offline", !(await page.locator("#lUpdate").isVisible()));

  // provision-stale in the launcher: the Reprovision laction gets the "stale" class.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", provisionStale: true } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: reprovision marked stale when VM behind", await page.locator('.lactions [data-cmd="reprovision"]').evaluate((el) => el.classList.contains("stale")));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: reprovision not stale when in sync", !(await page.locator('.lactions [data-cmd="reprovision"]').evaluate((el) => el.classList.contains("stale"))));

  // measure overflow with the power slot visible.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: power button visible at 300px", await page.locator("#lPowerBtn").isVisible());
  const launcherOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check("launcher: no horizontal overflow at 300px", launcherOverflow <= 1, `overflow=${launcherOverflow}px`);

  // diagnostics: the logs button posts showLogs (opens the Construct Output channel).
  await page.click('[data-cmd="showLogs"]');
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: logs button posts showLogs", lposted.some((m) => m.type === "command" && m.id === "showLogs"));

  check("launcher: no console/page errors", errors.length === 0, errors.join(" | "));

  // ── Config-sync UI checks (panel surface) ────────────────────────────────
  // Navigate back to the panel to test the config-sync strip.
  await page.setViewportSize({ width: 1000, height: 1400 });
  await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "networkidle" });
  await page.waitForTimeout(120);

  // configSync absent: strip should be hidden.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h" } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: strip hidden when configSync absent", !(await page.locator("#csStrip").isVisible()));

  // gitPresent:false -> install-git notice visible.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: false, repoReady: false, conflict: false, conflictFiles: [], mergeInProgress: false, lastSyncAt: null, lastResult: null, warnings: [], remotes: [] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: strip visible with configSync present", await page.locator("#csStrip").isVisible());
  check("config-sync: git-missing notice visible when !gitPresent", await page.locator("#csGitMissing").isVisible());
  check("config-sync: conflict banner hidden when no conflict", !(await page.locator("#csConflict").isVisible()));

  // conflict:true -> banner visible + open-repo button + merge-conflict text.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: true, repoReady: true, conflict: true, conflictFiles: ["projects/foo.json"], mergeInProgress: true, lastSyncAt: Date.now(), lastResult: "conflict", warnings: [], remotes: [] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: conflict banner visible", await page.locator("#csConflict").isVisible());
  check("config-sync: open-repo button in conflict banner", (await page.locator('#csConflict [data-cmd="openConfigRepo"]').count()) === 1);
  check("config-sync: conflict banner says merge conflict", (await page.locator("#csConflictText").textContent()).includes("merge conflict"));
  check("config-sync: git-missing hidden when gitPresent", !(await page.locator("#csGitMissing").isVisible()));

  // blocked (no unmerged paths — e.g. an uncommitted invalid profile): banner
  // shows the engine's blockedReason, NOT "merge conflict".
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: true, repoReady: true, conflict: false, conflictFiles: [], mergeInProgress: false, lastSyncAt: Date.now(), lastResult: "blocked",
      blockedReason: 'invalid host profile "wip" blocks the merge', warnings: [], remotes: [] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: blocked banner visible", await page.locator("#csConflict").isVisible());
  check("config-sync: blocked banner carries the reason", (await page.locator("#csConflictText").textContent()).includes('invalid host profile "wip"'));
  check("config-sync: blocked banner does not claim a merge conflict", !(await page.locator("#csConflictText").textContent()).includes("merge conflict"));

  // mergeInProgress with NO conflict (validation-gate pending merge): blocked
  // wording with the generic fallback, not the conflict wording.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: true, repoReady: true, conflict: false, conflictFiles: [], mergeInProgress: true, lastSyncAt: Date.now(), lastResult: "ok", warnings: [], remotes: [] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: pending-merge banner visible", await page.locator("#csConflict").isVisible());
  check("config-sync: pending-merge banner uses blocked wording", (await page.locator("#csConflictText").textContent()).includes("Config sync blocked"));

  // Remotes list renders rows.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: true, repoReady: true, conflict: false, conflictFiles: [], mergeInProgress: false, lastSyncAt: Date.now(), lastResult: "ok", warnings: ["test warning"],
      remotes: [{ url: "https://github.com/org/config.git" }, { url: "https://github.com/org/config2.git" }] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: remotes section visible with remotes", await page.locator("#csRemotes").isVisible());
  check("config-sync: remote rows rendered", (await page.locator("#csRemotesList .cs-remote-row").count()) === 2);
  check("config-sync: remote URL text", (await page.locator("#csRemotesList .cs-remote-url").first().textContent()).includes("github.com"));
  check("config-sync: remove button on each remote", (await page.locator("#csRemotesList .cs-remote-rm").count()) === 2);
  check("config-sync: push button on each remote", (await page.locator("#csRemotesList .cs-remote-push").count()) === 2);
  check("config-sync: status line shows result + warning count", (await page.locator("#csResult").textContent()).includes("warning"));

  // sync-now button posts syncConfigNow.
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('[data-cmd="syncConfigNow"]');
  posted = await page.evaluate(() => window.__posted);
  check("config-sync: sync-now posts syncConfigNow", posted.some((m) => m.type === "command" && m.id === "syncConfigNow"));

  // installGit button posts installGit.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: false, repoReady: false, conflict: false, conflictFiles: [], mergeInProgress: false, lastSyncAt: null, lastResult: null, warnings: [], remotes: [] }
  } }, "*"));
  await page.waitForTimeout(60);
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('[data-cmd="installGit"]');
  posted = await page.evaluate(() => window.__posted);
  check("config-sync: installGit button posts installGit", posted.some((m) => m.type === "command" && m.id === "installGit"));

  // Default chip is locked (no modal on click).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    projects: [{ name: "default", selected: true }, { name: "billing", selected: false }]
  } }, "*"));
  await page.waitForTimeout(60);
  check("default-chip: default chip has .locked class", await page.locator('#projChips .chip[data-project="default"]').evaluate((el) => el.classList.contains("locked")));
  check("default-chip: default chip has no openbtn", (await page.locator('#projChips .chip[data-project="default"] .openbtn').count()) === 0);
  // Click the default chip and verify NO editProject is posted.
  await page.evaluate(() => { window.__posted.length = 0; });
  await page.click('#projChips .chip[data-project="default"]');
  posted = await page.evaluate(() => window.__posted);
  check("default-chip: clicking default chip does NOT post editProject",
    !posted.some((m) => m.type === "command" && m.id === "editProject"));
  // Non-default chip should still post editProject.
  await page.click('#projChips .chip[data-project="billing"]');
  posted = await page.evaluate(() => window.__posted);
  check("default-chip: clicking billing chip still posts editProject",
    posted.some((m) => m.type === "command" && m.id === "editProject" && m.project === "billing"));

  // configSync survives offline (NOT cleared by clearLiveVmData).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h",
    configSync: { gitPresent: true, repoReady: true, conflict: false, conflictFiles: [], mergeInProgress: false, lastSyncAt: Date.now(), lastResult: "ok", warnings: [], remotes: [{ url: "https://x" }] }
  } }, "*"));
  await page.waitForTimeout(60);
  check("config-sync: strip visible before offline", await page.locator("#csStrip").isVisible());
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h" } }, "*"));
  await page.waitForTimeout(60);
  // The strip should still be visible even though we went offline (configSync is NOT
  // pushed in this state, but the previously rendered strip should remain).
  check("config-sync: strip remains visible after offline (not cleared by clearLiveVmData)", await page.locator("#csStrip").isVisible());

  await browser.close();
  server.close();

  const pass = results.filter((r) => r.ok).length;
  console.log(`\n  Construct webview UI smoke test — ${pass}/${results.length} passed\n`);
  for (const r of results) console.log(`  ${r.ok ? "PASS" : "FAIL"}  ${r.name}${r.ok ? "" : "   << " + r.detail}`);
  console.log("");
  process.exit(results.every((r) => r.ok) ? 0 : 1);
})().catch((e) => { console.error("HARNESS ERROR:", e); process.exit(2); });
