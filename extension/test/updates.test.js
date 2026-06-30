"use strict";
// Plain-node unit tests for the Construct update check. The HTTP fetch is injected
// (opts.fetchJson) so no network is touched. No deps. Run: node updates.test.js
const updates = require("../src/updates");

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
  // A cleared marker (installedCommit:"" — what install.ps1 -RefreshOnly writes when
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

  // ── constructRefreshArgs ────────────────────────────────────────────────────
  const args = updates.constructRefreshArgs({ repo: "me/fork", ref: "dev", installedCommit: "x" });
  ok("refreshArgs: -RefreshOnly -Repo -Ref", args.join(" ") === "-RefreshOnly -Repo me/fork -Ref dev");

  console.log(`\n  updates (Construct check) unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
