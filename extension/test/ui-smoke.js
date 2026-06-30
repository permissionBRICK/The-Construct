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

function buildPage(htmlFile, scriptFile) {
  let html = fs.readFileSync(path.join(MEDIA, htmlFile), "utf8");
  // Strip the CSP for the harness (CSP correctness is reviewed separately); this
  // lets us inject the mock vscode API inline.
  html = html.replace(/<meta http-equiv="Content-Security-Policy"[\s\S]*?\/>/, "");
  html = html.replace(/{{cspSource}}/g, "").replace(/{{styleUri}}/g, "panel.css")
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
    if (types[ext]) { res.writeHead(200, { "Content-Type": types[ext] }); return res.end(fs.readFileSync(path.join(MEDIA, path.basename(url)))); }
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
    usage: { tools: [{ label: "Claude Code", tokens: 100, tokensText: "14.2M", costText: "$38" }], totalTokensText: "14.2M", totalCostText: "$38" },
    update: { available: true, behind: "6 behind" },
  } }, "*"));
  await page.waitForTimeout(80);
  check("state render: vm name", (await page.locator("#sysVm").innerText()) === "agent-vm-01");
  check("state render: update banner shown", await page.locator("#updateBanner").isVisible());
  check("state render: agent version", (await page.locator("#agentList .agent").first().innerText()).includes("2.1.196"));
  check("state render: project chips", (await page.locator("#projChips .chip").count()) === 2);

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

  // connect button: shows only when the VM is online AND this window isn't already on it
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("panel: connect shows when online + not connected", await page.locator("#connectBtn").isVisible());
  await page.click("#connectBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: connect posts command", posted.some((m) => m.type === "command" && m.id === "connect"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: true } }, "*"));
  await page.waitForTimeout(60);
  check("panel: connect hidden when already connected", !(await page.locator("#connectBtn").isVisible()));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("panel: connect hidden when offline", !(await page.locator("#connectBtn").isVisible()));
  // strict ===: an undefined `connected` (legacy/foreign state) must keep it hidden.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true } }, "*"));
  await page.waitForTimeout(60);
  check("panel: connect hidden when `connected` is unknown", !(await page.locator("#connectBtn").isVisible()));

  // power controls: Start & connect (offline + VM stopped) and Shutdown (online).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, vmState: "off" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: start&connect shows when offline + VM stopped", await page.locator("#startBtn").isVisible());
  check("panel: shutdown hidden when offline", !(await page.locator("#shutdownBtn").isVisible()));
  await page.click("#startBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: start&connect posts startConnect", posted.some((m) => m.type === "command" && m.id === "startConnect"));
  // strict: an unknown/absent vmState (offline, not installed or query failed) shows no Start button.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, vmState: "absent" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: start hidden when VM absent / state unknown", !(await page.locator("#startBtn").isVisible()));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: true, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("panel: shutdown shows when online", await page.locator("#shutdownBtn").isVisible());
  check("panel: start hidden when online", !(await page.locator("#startBtn").isVisible()));
  await page.click("#shutdownBtn");
  posted = await page.evaluate(() => window.__posted);
  check("panel: shutdown posts shutdown", posted.some((m) => m.type === "command" && m.id === "shutdown"));

  // panel degrades without horizontal overflow when dragged narrow — measured with the
  // connect AND shutdown buttons VISIBLE (the widest status-strip state: online, not connected).
  await page.setViewportSize({ width: 300, height: 1400 });
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(80);
  check("panel: connect button visible at 300px", await page.locator("#connectBtn").isVisible());
  check("panel: shutdown button visible at 300px", await page.locator("#shutdownBtn").isVisible());
  const panelOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check("panel: no horizontal overflow at 300px", panelOverflow <= 1, `overflow=${panelOverflow}px`);

  // ── Launcher (sidebar) surface ──────────────────────────────────────────────
  await page.setViewportSize({ width: 300, height: 1000 });
  await page.goto(`http://127.0.0.1:${port}/launcher`, { waitUntil: "networkidle" });
  await page.waitForTimeout(120);
  check("launcher: 3 lifecycle buttons", (await page.locator(".laction").count()) === 3);
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
  check("launcher: connect shows when online + not connected", await page.locator("#lConnect").isVisible());
  await page.click("#lConnect");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: connect posts command", lposted.some((m) => m.type === "command" && m.id === "connect"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: true } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: connect hidden when already connected", !(await page.locator("#lConnect").isVisible()));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", connected: false } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: connect hidden when offline", !(await page.locator("#lConnect").isVisible()));
  // strict ===: undefined `connected` keeps it hidden.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: connect hidden when `connected` is unknown", !(await page.locator("#lConnect").isVisible()));

  // launcher power controls: Start & connect (offline + stopped) and Shutdown (online).
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: false, host: "h.example.net", vmState: "off" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: start&connect shows when offline + VM stopped", await page.locator("#lStart").isVisible());
  check("launcher: shutdown hidden when offline", !(await page.locator("#lShutdown").isVisible()));
  await page.click("#lStart");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: start&connect posts startConnect", lposted.some((m) => m.type === "command" && m.id === "startConnect"));
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: true, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: shutdown shows when online", await page.locator("#lShutdown").isVisible());
  check("launcher: start hidden when online", !(await page.locator("#lStart").isVisible()));
  await page.click("#lShutdown");
  lposted = await page.evaluate(() => window.__posted);
  check("launcher: shutdown posts shutdown", lposted.some((m) => m.type === "command" && m.id === "shutdown"));

  // measure overflow with the connect + shutdown buttons visible.
  await page.evaluate(() => window.postMessage({ type: "state", state: { online: true, host: "h.example.net", connected: false, vmState: "running" } }, "*"));
  await page.waitForTimeout(60);
  check("launcher: connect button visible at 300px", await page.locator("#lConnect").isVisible());
  const launcherOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check("launcher: no horizontal overflow at 300px", launcherOverflow <= 1, `overflow=${launcherOverflow}px`);
  check("launcher: no console/page errors", errors.length === 0, errors.join(" | "));

  await browser.close();
  server.close();

  const pass = results.filter((r) => r.ok).length;
  console.log(`\n  Construct webview UI smoke test — ${pass}/${results.length} passed\n`);
  for (const r of results) console.log(`  ${r.ok ? "PASS" : "FAIL"}  ${r.name}${r.ok ? "" : "   << " + r.detail}`);
  console.log("");
  process.exit(results.every((r) => r.ok) ? 0 : 1);
})().catch((e) => { console.error("HARNESS ERROR:", e); process.exit(2); });
