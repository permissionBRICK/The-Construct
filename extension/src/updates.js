"use strict";
// Update checks for the control panel.
//
// This batch: Construct self-update. Compare the installed Construct commit
// (recorded in .construct-settings.json by `install.ps1 -RefreshOnly` / the
// install step) against the latest commit on the tracked ref via the GitHub API,
// and fold {update:{available,behind}} (+ a constructRev label) into the state.
// Agent update detection + the update actions land in the next batch.
//
// Everything network is BEST-EFFORT: any failure (offline, rate-limited, or no
// recorded marker) yields no update info, so the panel simply leaves the banner
// hidden. The HTTP fetch is injectable (opts.fetchJson) so the logic unit-tests
// without a network. No `vscode` dependency.

const https = require("https");

const DEFAULT_REPO = "permissionBRICK/The-Construct";
const DEFAULT_REF = "main";
const GH = "https://api.github.com";
const TTL_MS = 10 * 60 * 1000; // cache a successful result for 10 min (GitHub unauth = 60 req/hr)
const NEG_TTL_MS = 60 * 1000;  // cache a FAILURE (null) only briefly, so a transient
                               // offline/rate-limit blip doesn't hide the banner for 10 min

/** Read the Construct update markers from raw settings, applying defaults. */
function readMarkers(raw) {
  raw = raw || {};
  return {
    repo: (raw.constructRepo && String(raw.constructRepo).trim()) || DEFAULT_REPO,
    ref: (raw.constructRef && String(raw.constructRef).trim()) || DEFAULT_REF,
    installedCommit: raw.installedCommit ? String(raw.installedCommit).trim() : "",
  };
}

/** GET a URL and parse JSON. Resolves the parsed object, or null on ANY problem
 *  (network error, timeout, non-2xx, bad JSON). Never rejects. */
function fetchJson(url, opts = {}) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (v) => { if (!done) { done = true; resolve(v); } };
    let req;
    try {
      req = https.get(url, {
        headers: { "User-Agent": "construct-control-panel", Accept: "application/vnd.github+json" },
        timeout: opts.timeoutMs || 8000,
      }, (res) => {
        if (res.statusCode < 200 || res.statusCode >= 300) { res.resume(); return finish(null); }
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (d) => { if (body.length < 4 * 1024 * 1024) body += d; });
        res.on("end", () => { try { finish(JSON.parse(body)); } catch (_) { finish(null); } });
      });
    } catch (_) { return finish(null); }
    req.on("error", () => finish(null));
    req.on("timeout", () => { try { req.destroy(); } catch (_) {} finish(null); });
  });
}

/** Shape a GitHub compare response (base=installed ... head=ref) into update info.
 *  `ahead_by` = commits the ref has that the installed commit doesn't = how many
 *  we're behind. Returns {available, count} or null when the response is unusable. */
function constructUpdateFromCompare(json) {
  if (!json || typeof json.ahead_by !== "number") return null;
  const count = json.ahead_by;
  return { available: count > 0, count };
}

/** Check the Construct repo for updates. Returns {available, count} or null
 *  (no marker -> null without any network call; network failure -> null). */
async function checkConstruct(markers, opts = {}) {
  if (!markers || !markers.installedCommit) return null;
  const fj = opts.fetchJson || fetchJson;
  const url = `${GH}/repos/${markers.repo}/compare/${markers.installedCommit}...${markers.ref}`;
  return constructUpdateFromCompare(await fj(url, opts));
}

const _cache = new Map(); // key -> { at, value }
async function checkConstructCached(markers, opts = {}) {
  if (opts.noCache) return checkConstruct(markers, opts);
  const key = `${markers.repo}@${markers.ref}#${markers.installedCommit}`;
  const now = opts.now ? opts.now() : Date.now();
  const hit = _cache.get(key);
  // Failures cache as null; give them a short TTL so recovery is picked up quickly,
  // while a real result is trusted for the full TTL.
  if (hit && now - hit.at < (hit.value === null ? NEG_TTL_MS : TTL_MS)) return hit.value;
  const value = await checkConstruct(markers, opts);
  _cache.set(key, { at: now, value });
  return value;
}

/** Format the behind-count for the banner (matches the webview's small text). */
function behindText(count) { return count > 0 ? `${count} behind` : ""; }

/**
 * Return a copy of `state` with Construct update info folded in (and a constructRev
 * label from the installed marker). Best-effort: on any failure or missing marker
 * the state is returned unchanged (no `update` key -> the panel keeps the banner
 * hidden). `opts.fetchJson` / `opts.noCache` are for tests.
 */
async function augment(state, raw, opts = {}) {
  if (!state || typeof state !== "object") return state;
  let next = state;
  try {
    const markers = readMarkers(raw);
    if (markers.installedCommit) {
      next = { ...next, constructRev: `${markers.ref}@${markers.installedCommit.slice(0, 7)}` };
    }
    const c = await checkConstructCached(markers, opts);
    if (c) next = { ...next, update: { available: c.available, behind: behindText(c.count) } };
  } catch (_) { /* best-effort: leave the state as-is */ }
  return next;
}

/** install.ps1 args for the control-panel "Update Construct" refresh. */
function constructRefreshArgs(markers) {
  return ["-RefreshOnly", "-Repo", markers.repo, "-Ref", markers.ref];
}

module.exports = {
  DEFAULT_REPO, DEFAULT_REF, TTL_MS, NEG_TTL_MS,
  readMarkers, fetchJson, constructUpdateFromCompare, checkConstruct, checkConstructCached,
  behindText, augment, constructRefreshArgs,
};
