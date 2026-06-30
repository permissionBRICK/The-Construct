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
const { extractVersion } = require("./probe");

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

/** GET a URL and parse JSON, FOLLOWING redirects (up to opts.maxRedirects, default 3)
 *  — GitHub 301-redirects a moved/renamed repo's API path to its canonical owner, so
 *  a relocated source keeps working without hardcoding the new owner. Resolves the
 *  parsed object, or null on ANY problem (network error, timeout, non-2xx, redirect
 *  loop, bad JSON). Never rejects. `opts._get` injects https.get for tests. */
// The npm registry returns 406 for GitHub's vnd.github+json Accept, so pick the
// media type per host (recomputed each redirect hop, since the target host can differ).
function acceptFor(url) {
  try { return new URL(url).hostname === "registry.npmjs.org" ? "application/json" : "application/vnd.github+json"; }
  catch (_) { return "application/json"; }
}

function fetchJson(url, opts = {}) {
  const httpGet = opts._get || https.get;
  const timeout = opts.timeoutMs || 8000;
  const maxRedirects = opts.maxRedirects == null ? 3 : opts.maxRedirects;
  return new Promise((resolve) => {
    let done = false;
    const finish = (v) => { if (!done) { done = true; resolve(v); } };
    const get = (u, redirectsLeft) => {
      const headers = { "User-Agent": "construct-control-panel", Accept: acceptFor(u) };
      let req;
      try {
        req = httpGet(u, { headers, timeout }, (res) => {
          const sc = res.statusCode;
          if (sc >= 300 && sc < 400 && res.headers && res.headers.location && redirectsLeft > 0) {
            res.resume();
            let next;
            try { next = new URL(res.headers.location, u).toString(); } catch (_) { return finish(null); }
            return get(next, redirectsLeft - 1);
          }
          if (sc < 200 || sc >= 300) { res.resume(); return finish(null); }
          let body = "";
          res.setEncoding("utf8");
          res.on("data", (d) => { if (body.length < 4 * 1024 * 1024) body += d; });
          res.on("end", () => { try { finish(JSON.parse(body)); } catch (_) { finish(null); } });
        });
      } catch (_) { return finish(null); }
      req.on("error", () => finish(null));
      req.on("timeout", () => { try { req.destroy(); } catch (_) {} finish(null); });
    };
    get(url, maxRedirects);
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

// Memoize a best-effort lookup. Failures (null) get a short TTL so recovery is
// picked up quickly; real results are trusted for the full TTL. opts.now (clock)
// and opts.noCache are for tests.
const _cache = new Map(); // key -> { at, value }
async function cached(key, produce, opts = {}) {
  if (opts.noCache) return produce();
  const now = opts.now ? opts.now() : Date.now();
  const hit = _cache.get(key);
  if (hit && now - hit.at < (hit.value == null ? NEG_TTL_MS : TTL_MS)) return hit.value;
  const value = await produce();
  _cache.set(key, { at: now, value });
  return value;
}

function checkConstructCached(markers, opts = {}) {
  return cached(`construct:${markers.repo}@${markers.ref}#${markers.installedCommit}`,
    () => checkConstruct(markers, opts), opts);
}

/** Format the behind-count for the banner (matches the webview's small text). */
function behindText(count) { return count > 0 ? `${count} behind` : ""; }

// ── Agent update detection ───────────────────────────────────────────────────
// Where to look up each agent's latest version, and how to pull a version string
// out of that source's JSON. Best-effort: an unknown agent or a failed lookup
// just leaves the agent's probed version unannotated.
const AGENT_LATEST = {
  "claude-code": { url: "https://registry.npmjs.org/@anthropic-ai/claude-code/latest", pick: (j) => j && j.version },
  codex: { url: "https://api.github.com/repos/openai/codex/releases/latest", pick: (j) => j && j.tag_name },
  // sst/opencode was renamed/transferred; the GitHub API 301-redirects this path to
  // the current owner, which fetchJson follows — so we don't hardcode a new owner.
  opencode: { url: "https://api.github.com/repos/sst/opencode/releases/latest", pick: (j) => j && j.tag_name },
};

/** Parse a version into [major,minor,patch], or null if it has no semver core. */
function semverParts(v) {
  const m = String(v == null ? "" : v).match(/(\d+)\.(\d+)\.(\d+)/);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

/** True when `latest` is a strictly newer release than `installed` (major.minor.patch).
 *  Unparseable on either side -> false (best-effort: don't claim an update). */
function isNewer(latest, installed) {
  const L = semverParts(latest), I = semverParts(installed);
  if (!L || !I) return false;
  for (let i = 0; i < 3; i++) { if (L[i] > I[i]) return true; if (L[i] < I[i]) return false; }
  return false;
}

/** Best-effort latest version string for an agent id (cached), or "" if unknown. */
async function fetchAgentLatest(id, opts = {}) {
  const src = AGENT_LATEST[id];
  if (!src) return "";
  const fj = opts.fetchJson || fetchJson;
  const raw = await cached(`agent:${id}`, async () => {
    const picked = src.pick(await fj(src.url, opts));
    return picked ? extractVersion(picked) : null; // null = failure -> short negative TTL
  }, opts);
  return raw || "";
}

/** Annotate each agent with {latest, updateAvailable} ONLY when there's actually a
 *  newer release; an up-to-date or unknown agent is returned UNCHANGED (same object
 *  reference) so augment() can detect "nothing changed" and skip a redundant re-push.
 *  Best-effort + concurrent. */
async function augmentAgents(agents, opts = {}) {
  if (!Array.isArray(agents)) return agents;
  return Promise.all(agents.map(async (a) => {
    if (!a || !a.id || !a.version || a.version === "—" || !AGENT_LATEST[a.id]) return a;
    const latest = await fetchAgentLatest(a.id, opts);
    if (!latest || !isNewer(latest, a.version)) return a;
    return { ...a, latest, updateAvailable: true };
  }));
}

/**
 * A remote bash script that force-updates the installed coding agents. Mirrors
 * install-ai-tools.sh: claude self-updates; codex/opencode re-run their official
 * installers (which fetch the newest release). Only touches agents present on PATH.
 *
 * Exit code is AGGREGATED: `set -o pipefail` makes the `curl | bash` / `curl | sh`
 * pipelines report a real failure, and each agent's failure is OR'd into `rc`, so
 * the script exits non-zero iff any attempted update failed — that's what the
 * caller's success/failure toast keys on. `set -e` is intentionally NOT used, so
 * one agent's failure doesn't skip the others.
 */
function buildAgentUpdateScript(ids) {
  const want = (Array.isArray(ids) && ids.length) ? ids : ["claude-code", "codex", "opencode"];
  const lines = ["set -uo pipefail", "rc=0"];
  if (want.includes("claude-code")) {
    lines.push('if command -v claude >/dev/null 2>&1; then echo "== updating Claude Code =="; claude update || rc=1; fi');
  }
  if (want.includes("codex")) {
    lines.push('if command -v codex >/dev/null 2>&1; then echo "== updating Codex =="; t=$(mktemp); ' +
      'if curl -fsSL https://chatgpt.com/codex/install.sh -o "$t" && printf "n\\n" | CI=1 sh "$t"; then :; else rc=1; fi; rm -f "$t"; fi');
  }
  if (want.includes("opencode")) {
    lines.push('if command -v opencode >/dev/null 2>&1; then echo "== updating opencode =="; ' +
      'if curl -fsSL https://opencode.ai/install | bash; then :; else rc=1; fi; fi');
  }
  lines.push("exit $rc");
  return lines.join("\n") + "\n";
}

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
    // Agent update detection (only when the VM is online with probed agents).
    if (next.online !== false && Array.isArray(next.agents) && next.agents.length) {
      const agents = await augmentAgents(next.agents, opts);
      if (agents.some((a, i) => a !== next.agents[i])) next = { ...next, agents };
    }
  } catch (_) { /* best-effort: leave the state as-is */ }
  return next;
}

/** install.ps1 args for the control-panel "Update Construct" refresh. */
function constructRefreshArgs(markers) {
  return ["-RefreshOnly", "-Repo", markers.repo, "-Ref", markers.ref];
}

module.exports = {
  DEFAULT_REPO, DEFAULT_REF, TTL_MS, NEG_TTL_MS, AGENT_LATEST,
  readMarkers, acceptFor, fetchJson, constructUpdateFromCompare, checkConstruct, checkConstructCached,
  behindText, semverParts, isNewer, fetchAgentLatest, augmentAgents, buildAgentUpdateScript,
  augment, constructRefreshArgs,
};
