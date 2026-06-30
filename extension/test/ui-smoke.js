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
  check("save posts saveSettings carrying the form", savePosted.some((m) => m.type === "saveSettings" && m.settings && m.settings.gitName === "Neo"));

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

  // panel degrades without horizontal overflow when dragged narrow
  await page.setViewportSize({ width: 300, height: 1400 });
  await page.waitForTimeout(80);
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
