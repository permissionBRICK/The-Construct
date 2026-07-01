"use strict";
// Plain-node unit tests for the Construct update check. The HTTP fetch is injected
// (opts.fetchJson) so no network is touched. No deps. Run: node updates.test.js
const updates = require("../src/updates");
const { EventEmitter } = require("events");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

(async () => {
  // ── readMarkers ─────────────────────────────────────────────────────────────
  const def = updates.readMarkers({});
  ok("markers: default repo/ref", def.repo === updates.DEFAULT_REPO && def.ref === updates.DEFAULT_REF);
  ok("markers: no installedCommit by default", def.installedCommit === "");
  const m = updates.readMarkers({ constructRepo: " me/fork ", constructRef: " dev ", installedCommit: " abc123 " });
  ok("markers: honors + trims overrides", m.repo === "me/fork" && m.ref === "dev" && m.installedCommit === "abc123");
  ok("markers: null raw -> defaults", updates.readMarkers(null).repo === updates.DEFAULT_REPO);
  // A cleared marker (installedCommit:"" — what Set-ConstructInstalledMarker writes when
  // the SHA lookup fails) must read as "no marker" while repo/ref are still honored,
  // so a refreshed install never compares against a stale commit.
  const cleared = updates.readMarkers({ installedCommit: "", constructRepo: "me/fork", constructRef: "dev" });
  ok("markers: empty installedCommit -> no marker (banner hidden), repo/ref kept",
    cleared.installedCommit === "" && cleared.repo === "me/fork" && cleared.ref === "dev");

  // ── constructUpdateFromCompare ──────────────────────────────────────────────
  ok("compare: ahead_by>0 -> available", (() => { const r = updates.constructUpdateFromCompare({ ahead_by: 6 }); return r.available === true && r.count === 6; })());
  ok("compare: ahead_by 0 -> not available", (() => { const r = updates.constructUpdateFromCompare({ ahead_by: 0 }); return r.available === false && r.count === 0; })());
  ok("compare: missing field -> null", updates.constructUpdateFromCompare({}) === null);
  ok("compare: null -> null", updates.constructUpdateFromCompare(null) === null);

  // ── behindText ──────────────────────────────────────────────────────────────
  ok("behindText: positive", updates.behindText(6) === "6 behind");
  ok("behindText: zero -> empty", updates.behindText(0) === "");

  // ── checkConstruct (injected fetch) ─────────────────────────────────────────
  let calledUrl = null;
  const fakeFetch = (json) => async (url) => { calledUrl = url; return json; };

  calledUrl = null;
  const noMarker = await updates.checkConstruct({ repo: "a/b", ref: "main", installedCommit: "" }, { fetchJson: fakeFetch({ ahead_by: 9 }) });
  ok("check: no installedCommit -> null without fetching", noMarker === null && calledUrl === null);

  calledUrl = null;
  const hit = await updates.checkConstruct({ repo: "a/b", ref: "main", installedCommit: "deadbeef" }, { fetchJson: fakeFetch({ ahead_by: 3 }) });
  ok("check: builds compare URL base...head", calledUrl === "https://api.github.com/repos/a/b/compare/deadbeef...main", calledUrl);
  ok("check: returns update info", hit && hit.available === true && hit.count === 3);

  const netFail = await updates.checkConstruct({ repo: "a/b", ref: "main", installedCommit: "deadbeef" }, { fetchJson: async () => null });
  ok("check: network failure -> null", netFail === null);

  // ── augment ─────────────────────────────────────────────────────────────────
  const base = { online: true, host: "h" };

  const a1 = await updates.augment(base, { installedCommit: "abc1234567", constructRef: "main" }, { fetchJson: fakeFetch({ ahead_by: 6 }), noCache: true });
  ok("augment: folds update.available + behind", a1.update && a1.update.available === true && a1.update.behind === "6 behind");
  ok("augment: sets constructRev label from marker", a1.constructRev === "main@abc1234");
  ok("augment: does not mutate input", base.update === undefined && a1 !== base);

  const a2 = await updates.augment(base, { installedCommit: "abc1234567", constructRef: "main" }, { fetchJson: fakeFetch({ ahead_by: 0 }), noCache: true });
  ok("augment: up-to-date -> available false, blank behind", a2.update.available === false && a2.update.behind === "");

  const a3 = await updates.augment(base, {}, { fetchJson: fakeFetch({ ahead_by: 6 }), noCache: true });
  ok("augment: no marker -> unchanged (same ref, no update/constructRev)", a3 === base);

  // ── provisionStale (installedCommit vs provisionedCommit) ────────────────────
  ok("stale: installed != provisioned -> stale", updates.isProvisionStale({ installedCommit: "aaa", provisionedCommit: "bbb" }) === true);
  ok("stale: installed == provisioned -> not stale", updates.isProvisionStale({ installedCommit: "aaa", provisionedCommit: "aaa" }) === false);
  ok("stale: missing provisioned -> not stale (conservative)", updates.isProvisionStale({ installedCommit: "aaa", provisionedCommit: "" }) === false);
  ok("stale: missing installed -> not stale", updates.isProvisionStale({ installedCommit: "", provisionedCommit: "bbb" }) === false);
  ok("markers: reads provisionedCommit", updates.readMarkers({ provisionedCommit: " ccc " }).provisionedCommit === "ccc");
  // augment folds provisionStale ONLY when stale (so the no-marker fast path above holds).
  const aStale = await updates.augment(base, { installedCommit: "aaaaaaa", provisionedCommit: "bbbbbbb", constructRef: "main" }, { fetchJson: fakeFetch({ ahead_by: 0 }), noCache: true });
  ok("augment: stale VM -> provisionStale true", aStale.provisionStale === true);
  const aFresh = await updates.augment(base, { installedCommit: "aaaaaaa", provisionedCommit: "aaaaaaa", constructRef: "main" }, { fetchJson: fakeFetch({ ahead_by: 0 }), noCache: true });
  ok("augment: in-sync VM -> no provisionStale key", aFresh.provisionStale === undefined);

  const a4 = await updates.augment(base, { installedCommit: "abc1234567" }, { fetchJson: async () => null, noCache: true });
  ok("augment: network fail still sets constructRev, no update", a4.constructRev === "main@abc1234" && a4.update === undefined);

  // constructRev must reflect the MARKER's ref, not the default — a non-default ref
  // catches a regression that hardcodes "main".
  const a5 = await updates.augment(base, { installedCommit: "abc1234567", constructRef: "dev" }, { fetchJson: fakeFetch({ ahead_by: 0 }), noCache: true });
  ok("augment: constructRev uses the marker's (non-default) ref", a5.constructRev === "dev@abc1234");

  // ── cache: negative results expire fast; successes are trusted longer ────────
  // Inject a clock (opts.now) and a per-test marker key so the module cache doesn't
  // bleed between cases.
  let calls = 0;
  const countingFetch = (json) => async () => { calls++; return json; };
  const cm = { repo: "c/d", ref: "main", installedCommit: "cachekey1" };

  calls = 0;
  const r1 = await updates.checkConstructCached(cm, { fetchJson: async () => null, now: () => 1000 });
  ok("cache: first call returns (and stores) the null failure", r1 === null);
  const r2 = await updates.checkConstructCached(cm, { fetchJson: countingFetch({ ahead_by: 5 }), now: () => 1000 + 30 * 1000 });
  ok("cache: failure served within negative TTL (no refetch at +30s)", r2 === null && calls === 0);
  const r3 = await updates.checkConstructCached(cm, { fetchJson: countingFetch({ ahead_by: 5 }), now: () => 1000 + 90 * 1000 });
  ok("cache: failure expires after negative TTL (refetch at +90s)", r3 && r3.available === true && calls === 1);

  const cm2 = { repo: "c/d", ref: "main", installedCommit: "cachekey2" };
  let calls2 = 0;
  const succFetch = async () => { calls2++; return { ahead_by: 2 }; };
  const s1 = await updates.checkConstructCached(cm2, { fetchJson: succFetch, now: () => 5000 });
  const s2 = await updates.checkConstructCached(cm2, { fetchJson: succFetch, now: () => 5000 + 5 * 60 * 1000 });
  ok("cache: success served for the full TTL (no refetch at +5min)", s1.count === 2 && s2.count === 2 && calls2 === 1);
  const s3 = await updates.checkConstructCached(cm2, { fetchJson: succFetch, now: () => 5000 + 11 * 60 * 1000 });
  ok("cache: success refetched after TTL (+11min)", s3.count === 2 && calls2 === 2);

  // ── fetchJson redirect following + per-host Accept (mocked https.get) ────────
  // routes: { url: { statusCode, headers?, body? } }; seenAccept records the Accept
  // header sent per requested URL.
  const seenAccept = {};
  const mockGet = (routes) => (u, options, cb) => {
    seenAccept[u] = options && options.headers && options.headers.Accept;
    const req = new EventEmitter(); req.destroy = () => {};
    process.nextTick(() => {
      const spec = routes[u];
      if (!spec) { req.emit("error", new Error("no route: " + u)); return; }
      const res = new EventEmitter();
      res.statusCode = spec.statusCode; res.headers = spec.headers || {};
      res.setEncoding = () => {}; res.resume = () => {};
      cb(res);
      if (spec.body != null) res.emit("data", spec.body);
      res.emit("end");
    });
    return req;
  };

  // acceptFor: npm needs application/json (vnd.github+json -> 406); GitHub keeps its type.
  ok("acceptFor: npm -> application/json", updates.acceptFor("https://registry.npmjs.org/@anthropic-ai/claude-code/latest") === "application/json");
  ok("acceptFor: github -> vnd.github+json", updates.acceptFor("https://api.github.com/repos/openai/codex/releases/latest") === "application/vnd.github+json");
  ok("fetchJson: 200 parses JSON", (await updates.fetchJson("https://x/a", { _get: mockGet({ "https://x/a": { statusCode: 200, body: '{"v":"1.2.3"}' } }) })).v === "1.2.3");
  ok("fetchJson: follows a 301 to the moved repo", (await updates.fetchJson("https://x/old", { _get: mockGet({
    "https://x/old": { statusCode: 301, headers: { location: "https://x/new" } },
    "https://x/new": { statusCode: 200, body: '{"tag_name":"v9.9.9"}' },
  }) })).tag_name === "v9.9.9");
  ok("fetchJson: non-2xx -> null", (await updates.fetchJson("https://x/m", { _get: mockGet({ "https://x/m": { statusCode: 404 } }) })) === null);
  ok("fetchJson: stops after maxRedirects -> null", (await updates.fetchJson("https://x/a", { maxRedirects: 1, _get: mockGet({
    "https://x/a": { statusCode: 302, headers: { location: "https://x/b" } },
    "https://x/b": { statusCode: 302, headers: { location: "https://x/c" } },
    "https://x/c": { statusCode: 200, body: "{}" },
  }) })) === null);
  ok("fetchJson: bad JSON -> null", (await updates.fetchJson("https://x/a", { _get: mockGet({ "https://x/a": { statusCode: 200, body: "{not json" } }) })) === null);
  ok("fetchJson: transport error -> null", (await updates.fetchJson("https://x/err", { _get: mockGet({}) })) === null);

  // agent latest works end-to-end through the redirect-following fetchJson (real-ish
  // path, not just injected fetchJson): opencode's source 301s to its new home.
  const ocLatest = await updates.fetchAgentLatest("opencode", { noCache: true, _get: mockGet({
    [updates.AGENT_LATEST.opencode.url]: { statusCode: 301, headers: { location: "https://api.github.com/repos/NEW/opencode/releases/latest" } },
    "https://api.github.com/repos/NEW/opencode/releases/latest": { statusCode: 200, body: '{"tag_name":"v1.18.0"}' },
  }) });
  ok("fetchAgentLatest: opencode resolves through a 301 redirect", ocLatest === "1.18.0");

  // Claude/npm source end-to-end through the REAL fetchJson, asserting the npm-
  // compatible Accept header is sent (the bug: vnd.github+json -> 406 -> no badge).
  const claudeUrl = updates.AGENT_LATEST["claude-code"].url;
  const claudeLatest = await updates.fetchAgentLatest("claude-code", { noCache: true, _get: mockGet({ [claudeUrl]: { statusCode: 200, body: '{"version":"2.1.210"}' } }) });
  ok("fetchAgentLatest: claude from npm via real fetchJson", claudeLatest === "2.1.210");
  ok("fetchAgentLatest: npm request used an npm-compatible Accept", seenAccept[claudeUrl] === "application/json");

  // ── constructRefreshArgs ────────────────────────────────────────────────────
  const args = updates.constructRefreshArgs({ repo: "me/fork", ref: "dev", installedCommit: "x" });
  ok("refreshArgs: -Repo -Ref (no -RefreshOnly; Update-Construct.ps1 is the refresh)", args.join(" ") === "-Repo me/fork -Ref dev");

  // ── semver compare ──────────────────────────────────────────────────────────
  ok("isNewer: patch bump", updates.isNewer("2.1.197", "2.1.196") === true);
  ok("isNewer: minor bump beats higher patch", updates.isNewer("2.2.0", "2.1.999") === true);
  ok("isNewer: equal -> false", updates.isNewer("2.1.196", "2.1.196") === false);
  ok("isNewer: older -> false", updates.isNewer("2.1.195", "2.1.196") === false);
  ok("isNewer: unparseable -> false (best-effort)", updates.isNewer("", "2.1.196") === false && updates.isNewer("2.1.196", "nope") === false);
  ok("semverParts: extracts core", JSON.stringify(updates.semverParts("v2.1.196-beta")) === "[2,1,196]");

  // ── fetchAgentLatest (injected fetch) ───────────────────────────────────────
  const gh = (tag) => async () => ({ tag_name: tag });
  ok("agentLatest: codex from GitHub tag", await updates.fetchAgentLatest("codex", { fetchJson: gh("rust-v0.143.0"), noCache: true }) === "0.143.0");
  ok("agentLatest: claude from npm version", await updates.fetchAgentLatest("claude-code", { fetchJson: async () => ({ version: "2.1.210" }), noCache: true }) === "2.1.210");
  ok("agentLatest: unknown agent -> empty", await updates.fetchAgentLatest("bogus", { fetchJson: gh("9.9.9"), noCache: true }) === "");
  ok("agentLatest: network fail -> empty", await updates.fetchAgentLatest("opencode", { fetchJson: async () => null, noCache: true }) === "");

  // ── augmentAgents ───────────────────────────────────────────────────────────
  const agentsIn = [
    { id: "claude-code", name: "Claude Code", version: "2.1.196", updateAvailable: false },
    { id: "opencode", name: "OpenCode", version: "1.17.11", updateAvailable: false },
    { id: "codex", name: "Codex", version: "—", updateAvailable: false }, // no version -> skipped
  ];
  const fakeByUrl = (map) => async (url) => map[url] || null;
  const aug = await updates.augmentAgents(agentsIn, {
    noCache: true,
    fetchJson: fakeByUrl({
      [updates.AGENT_LATEST["claude-code"].url]: { version: "2.1.210" },
      [updates.AGENT_LATEST.opencode.url]: { tag_name: "v1.17.11" },
    }),
  });
  ok("augmentAgents: claude flagged updateAvailable + latest", aug[0].updateAvailable === true && aug[0].latest === "2.1.210");
  ok("augmentAgents: up-to-date agent returned UNCHANGED (same ref, enables skip-re-push)", aug[1] === agentsIn[1]);
  ok("augmentAgents: agent without a version is left untouched", aug[2] === agentsIn[2]);

  // A v-prefixed GitHub tag that IS genuinely newer must flag an update (proves the
  // tag -> extractVersion -> isNewer path, not just the equal-version case).
  const augV = await updates.augmentAgents(
    [{ id: "opencode", name: "OpenCode", version: "1.17.11", updateAvailable: false }],
    { noCache: true, fetchJson: async () => ({ tag_name: "v1.18.0" }) }
  );
  ok("augmentAgents: v-prefixed newer tag flags update", augV[0].updateAvailable === true && augV[0].latest === "1.18.0");

  // ── augment folds agent updates into state ──────────────────────────────────
  const st = await updates.augment(
    { online: true, agents: [{ id: "codex", name: "Codex", version: "0.142.4", updateAvailable: false }] },
    {},
    { noCache: true, fetchJson: fakeByUrl({ [updates.AGENT_LATEST.codex.url]: { tag_name: "0.143.0" } }) }
  );
  ok("augment: folds agent updateAvailable into state.agents", st.agents[0].updateAvailable === true && st.agents[0].latest === "0.143.0");

  const stOffline = await updates.augment({ online: false, agents: [{ id: "codex", version: "0.142.4" }] }, {}, { noCache: true, fetchJson: gh("9.9.9") });
  ok("augment: offline state leaves agents unchanged", stOffline.agents[0].updateAvailable === undefined && stOffline.agents[0].latest === undefined);

  // When everything is up to date and there's no Construct marker, augment must
  // return the SAME object reference so the extension can skip the redundant re-push.
  const upToDate = { online: true, agents: [{ id: "codex", name: "Codex", version: "0.143.0", updateAvailable: false }] };
  const same = await updates.augment(upToDate, {}, { noCache: true, fetchJson: fakeByUrl({ [updates.AGENT_LATEST.codex.url]: { tag_name: "0.143.0" } }) });
  ok("augment: no changes -> same state ref (skip re-push)", same === upToDate);

  // ── buildAgentUpdateScript ──────────────────────────────────────────────────
  const all = updates.buildAgentUpdateScript();
  ok("agentScript: set -uo pipefail + rc=0 preamble + exit $rc", all.startsWith("set -uo pipefail\nrc=0\n") && /\nexit \$rc\n$/.test(all));
  ok("agentScript: guards each on command -v", /command -v claude/.test(all) && /command -v codex/.test(all) && /command -v opencode/.test(all));
  ok("agentScript: every agent's failure is captured into rc", /claude update \|\| rc=1/.test(all) && (all.match(/else rc=1/g) || []).length === 2);
  ok("agentScript: claude self-update + installers", /claude update/.test(all) && /chatgpt\.com\/codex\/install\.sh/.test(all) && /opencode\.ai\/install/.test(all));
  const onlyClaude = updates.buildAgentUpdateScript(["claude-code"]);
  ok("agentScript: subset only includes requested agents", /claude update/.test(onlyClaude) && !/opencode/.test(onlyClaude) && !/codex/.test(onlyClaude));
  ok("agentScript: subset still aggregates exit code", onlyClaude.startsWith("set -uo pipefail\nrc=0\n") && /\nexit \$rc\n$/.test(onlyClaude));

  console.log(`\n  updates (Construct check) unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
