"use strict";
// Update checks for the control panel.
//
// This batch: Construct self-update. Compare the installed Construct commit
// (recorded in .construct-settings.json by Provision-AgentVM.ps1 at install time /
// by Update-Construct.ps1 on refresh) against the latest commit on the tracked ref via the GitHub API,
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

/** Read the Construct update markers from raw settings, applying defaults.
 *  installedCommit = the installed Construct (extension + scripts; set by install/update).
 *  provisionedCommit = what the VM was last provisioned with (set by Provision). */
function readMarkers(raw) {
  raw = raw || {};
  return {
    repo: (raw.constructRepo && String(raw.constructRepo).trim()) || DEFAULT_REPO,
    ref: (raw.constructRef && String(raw.constructRef).trim()) || DEFAULT_REF,
    installedCommit: raw.installedCommit ? String(raw.installedCommit).trim() : "",
    provisionedCommit: raw.provisionedCommit ? String(raw.provisionedCommit).trim() : "",
  };
}

/** Whether the VM was provisioned with a DIFFERENT commit than the installed Construct
 *  (so a reprovision would apply the update to the VM). Conservative: only true when BOTH
 *  markers are known and differ — an unknown provisionedCommit (a VM provisioned before
 *  this tracking existed) is not flagged until its next reprovision records one. */
function isProvisionStale(markers) {
  return !!(markers && markers.installedCommit && markers.provisionedCommit &&
            markers.installedCommit !== markers.provisionedCommit);
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
  // Default lookup for stable; nightly is handled by t3codeUrl() below.
  t3code: { url: "https://registry.npmjs.org/t3/latest", pick: (j) => j && j.version },
};

/** The npm registry URL for the t3 package by channel. The per-tag endpoint
 *  returns the version manifest whose `.version` field is the resolved version. */
function t3codeUrl(channel) {
  return channel === "nightly"
    ? "https://registry.npmjs.org/t3/nightly"
    : "https://registry.npmjs.org/t3/latest";
}

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

/** Extract the prerelease portion of a version string (everything after the
 *  first `-` that follows the major.minor.patch core). "" when absent. */
function prereleasePart(v) {
  const m = String(v).match(/\d+\.\d+\.\d+-(.*)/);
  return m ? m[1] : "";
}

/** Compare two prerelease strings per semver 2.0 §11: split on `.`, compare
 *  segment-by-segment — numeric segments as integers, string segments lexically,
 *  numeric < string. Returns <0 / 0 / >0 (a < b / equal / a > b). */
function comparePrerelease(a, b) {
  if (a === b) return 0;
  if (!a && !b) return 0;
  if (!a) return 1;  // no prerelease > any prerelease (release beats pre)
  if (!b) return -1;
  const as = a.split("."), bs = b.split(".");
  const len = Math.max(as.length, bs.length);
  for (let i = 0; i < len; i++) {
    if (i >= as.length) return -1;
    if (i >= bs.length) return 1;
    const an = /^\d+$/.test(as[i]), bn = /^\d+$/.test(bs[i]);
    if (an && bn) {
      const d = Number(as[i]) - Number(bs[i]);
      if (d !== 0) return d;
    } else if (an !== bn) {
      return an ? -1 : 1;
    } else {
      if (as[i] < bs[i]) return -1;
      if (as[i] > bs[i]) return 1;
    }
  }
  return 0;
}

/** Nightly builds share the same major.minor.patch across many daily releases
 *  (e.g. 0.0.30-nightly.20260728 vs 0.0.30-nightly.20260729). isNewer strips
 *  the prerelease and calls them EQUAL, so the panel would never show a nightly
 *  update. This compares the FULL version using semver prerelease ordering: a
 *  core bump wins, otherwise the prerelease identifiers are compared segment by
 *  segment (numeric segments as integers, so .932 < .20260728 and dates sort
 *  correctly regardless of build-number length). Only used when the VM's channel
 *  is nightly — stable still uses isNewer. */
function isNewerNightly(latest, installed) {
  if (!latest || !installed) return false;
  const ls = String(latest).trim(), is = String(installed).trim();
  if (ls === is) return false;
  const lPre = prereleasePart(ls), iPre = prereleasePart(is);
  if ((!lPre) !== (!iPre)) return false;
  if (isNewer(ls, is)) return true;
  const L = semverParts(ls), I = semverParts(is);
  if (!L || !I) return false;
  for (let i = 0; i < 3; i++) { if (L[i] !== I[i]) return false; }
  return comparePrerelease(lPre, iPre) > 0;
}

/** Best-effort latest version string for an agent id (cached), or "" if unknown.
 *  `opts.t3codeChannel` ("nightly"|"stable") steers the t3code lookup to the
 *  matching npm dist-tag; the cache key includes the channel so a switch doesn't
 *  serve a stale cross-channel answer. */
async function fetchAgentLatest(id, opts = {}) {
  const src = AGENT_LATEST[id];
  if (!src) return "";
  const fj = opts.fetchJson || fetchJson;
  // t3code: per-channel URL + distinct cache key
  const url = id === "t3code" ? t3codeUrl(opts.t3codeChannel) : src.url;
  const cacheKey = id === "t3code" ? `agent:t3code:${opts.t3codeChannel === "nightly" ? "nightly" : "stable"}` : `agent:${id}`;
  const raw = await cached(cacheKey, async () => {
    const picked = src.pick(await fj(url, opts));
    return picked ? extractVersion(picked) : null; // null = failure -> short negative TTL
  }, opts);
  return raw || "";
}

/** Annotate each agent with {latest, updateAvailable} ONLY when there's actually a
 *  newer release; an up-to-date or unknown agent is returned UNCHANGED (same object
 *  reference) so augment() can detect "nothing changed" and skip a redundant re-push.
 *  Best-effort + concurrent. t3code on the nightly channel uses `isNewerNightly` so
 *  daily builds with the same semver core still show an update badge. */
async function augmentAgents(agents, opts = {}) {
  if (!Array.isArray(agents)) return agents;
  return Promise.all(agents.map(async (a) => {
    if (!a || !a.id || !a.version || a.version === "—" || !AGENT_LATEST[a.id]) return a;
    // Thread the agent's probed channel so fetchAgentLatest hits the right registry tag.
    const agentOpts = a.id === "t3code" && a.channel ? { ...opts, t3codeChannel: a.channel } : opts;
    const latest = await fetchAgentLatest(a.id, agentOpts);
    if (!latest) return a;
    const newer = (a.id === "t3code" && a.channel === "nightly") ? isNewerNightly(latest, a.version) : isNewer(latest, a.version);
    if (!newer) return a;
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
  const want = (Array.isArray(ids) && ids.length) ? ids : ["claude-code", "codex", "opencode", "t3code"];
  const lines = ["set -uo pipefail", "rc=0"];
  if (want.includes("claude-code")) {
    lines.push('if command -v claude >/dev/null 2>&1; then echo "== updating Claude Code =="; claude update || rc=1; fi');
  }
  if (want.includes("codex")) {
    // Update MATCHES the install layout: an npm-managed codex (the shim resolves
    // into node_modules — how the provision fallback installs it) must update via
    // npm; re-running the official installer there fights the npm layout, and that
    // installer has also been broken upstream (GitHub's minified release JSON).
    // For official-installer layouts, still try the installer first and fall back
    // to npm when it fails and npm is available.
    lines.push('if command -v codex >/dev/null 2>&1; then echo "== updating Codex =="; ' +
      'target=$(readlink -f "$(command -v codex)" 2>/dev/null || true); ' +
      'case "$target" in ' +
      '*/node_modules/*) npm install -g @openai/codex@latest || rc=1 ;; ' +
      '*) t=$(mktemp); ' +
      'if curl -fsSL https://chatgpt.com/codex/install.sh -o "$t" && printf "n\\n" | CI=1 sh "$t"; then :; ' +
      'elif command -v npm >/dev/null 2>&1 && npm install -g @openai/codex@latest; then ' +
      'echo "official installer failed; updated via npm instead"; else rc=1; fi; rm -f "$t"; ' +
      // The official installer only moves its `current` symlink. If a previous
      // provision pinned /usr/local/bin/codex to a VERSIONED releases/<v> dir,
      // the update would land invisibly (probe + app-server keep the old
      // binary) -- relink to the stable entry so the chain resolves at exec.
      'if [ -L /usr/local/bin/codex ]; then case "$(readlink /usr/local/bin/codex)" in ' +
      '*/.codex/*releases/*) for s in "$HOME/.local/bin/codex" "$HOME/.codex/bin/codex"; do ' +
      'if [ -x "$s" ]; then ln -sf "$s" /usr/local/bin/codex; echo "relinked /usr/local/bin/codex -> $s"; break; fi; done ;; ' +
      'esac; fi ;; ' +
      'esac; fi');
  }
  if (want.includes("opencode")) {
    // The official installer downloads its archive without curl --fail, so a
    // transient HTTP error page becomes "gzip: stdin: not in gzip format" and
    // kills the install mid-run. Retry with backoff (mirrors
    // install-ai-tools.sh's run_installer_with_retries) instead of failing the
    // whole update on one network blip.
    lines.push('if command -v opencode >/dev/null 2>&1; then echo "== updating opencode =="; ' +
      'oc_ok=1; for oc_i in 1 2 3; do ' +
      'if curl -fsSL https://opencode.ai/install | bash; then oc_ok=0; break; fi; ' +
      'echo "opencode installer failed (attempt $oc_i/3)"; ' +
      'if [ "$oc_i" -lt 3 ]; then sleep $((oc_i * 5)); fi; ' +
      'done; [ "$oc_ok" -eq 0 ] || rc=1; fi');
  }
  if (want.includes("t3code")) {
    // npm-only (how install-ai-tools.sh installs it); restart the serve unit so
    // the running web GUI actually picks up the new version. try-restart is a
    // no-op when the service isn't deployed/running. The channel is read from
    // the VM's config.env (source of truth), mapped to the npm tag the same way
    // install-ai-tools.sh does — so a host/VM channel disagreement can't happen.
    lines.push('if command -v t3 >/dev/null 2>&1; then echo "== updating T3 Code =="; ' +
      '_t3ch="$(sed -n \'s/^T3CODE_CHANNEL=//p\' /etc/construct/config.env 2>/dev/null | head -1)"; ' +
      'case "$_t3ch" in nightly) _t3tag=nightly ;; *) _t3tag=latest ;; esac; ' +
      'if npm install -g "t3@${_t3tag}" --allow-scripts=node-pty,msgpackr-extract; then ' +
      'systemctl try-restart t3code-serve 2>/dev/null || true; else rc=1; fi; fi');
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
    // The VM is behind the installed Construct → the panel flags the Provision button.
    // Only set it when TRUE (keep the "no marker → unchanged" fast path; the webview
    // treats an absent flag as not-stale and re-toggles the class on every render).
    if (isProvisionStale(markers)) next = { ...next, provisionStale: true };
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
  // Args for Update-Construct.ps1 (the self-update script the panel launches): it IS
  // the refresh, so there's no -RefreshOnly flag — just the source repo/ref to pull.
  return ["-Repo", markers.repo, "-Ref", markers.ref];
}

module.exports = {
  DEFAULT_REPO, DEFAULT_REF, TTL_MS, NEG_TTL_MS, AGENT_LATEST,
  readMarkers, acceptFor, fetchJson, constructUpdateFromCompare, checkConstruct, checkConstructCached,
  behindText, semverParts, isNewer, isNewerNightly, prereleasePart, comparePrerelease,
  isProvisionStale, t3codeUrl,
  fetchAgentLatest, augmentAgents, buildAgentUpdateScript,
  augment, constructRefreshArgs,
};
