"use strict";
// Token usage & estimated cost for the control panel.
//
// Roadmap item 5: run the ccusage-over-SSH collector (the same one Get-AgentUsage.ps1
// runs), parse per-agent token counts + cost, and fold a usage table into the state
// so the panel's "Token usage & cost" section can render it. `exportUsage` saves the
// raw combined JSON to disk.
//
// Like updates.js this is BEST-EFFORT and CACHED: the collection is a slow SSH +
// ccusage round-trip (ccusage may even have to install itself on the VM the first
// time), so the extension pushes the base probe state first and folds usage in as a
// SECOND state message once collect() resolves. Any failure (offline, ccusage
// unavailable, malformed/partial JSON, an agent with no usage) degrades gracefully:
// missing tools are simply omitted and, if nothing is collectable, no `usage` key is
// added so the panel keeps its "—" placeholders.
//
// The remote runner (ssh.runRemoteScript) and the clock are injectable (opts.runScript
// / opts.now) so all the parsing/formatting/cache logic unit-tests without a VM. There
// is NO `vscode` dependency here.
//
// SECURITY: the report granularity is validated against a fixed allow-list before it
// reaches the remote script (never interpolate arbitrary text into the bash the VM
// runs), and the collector itself is shipped base64-encoded-as-data via
// ssh.runRemoteScript — so nothing here interpolates untrusted data into a shell.

const ssh = require("./ssh");

// The three coding agents ccusage knows about, in the order the panel lists them.
// `tool` is the ccusage subcommand; `id`/`label` match the probe's agent ids and the
// panel's human labels.
const TOOLS = [
  { id: "claude", tool: "claude", label: "Claude Code" },
  { id: "codex", tool: "codex", label: "Codex" },
  { id: "opencode", tool: "opencode", label: "OpenCode" },
];

// The three VIEWS the panel offers: "daily" = usage so far TODAY, "monthly" = usage this
// calendar month (month-to-date), "total" = all-time lifetime usage (what raw ccusage
// reports). daily and monthly coincide on the 1st of the month; total is always distinct
// (unless every bit of usage is from today). Each uses a ccusage subcommand supported by
// all three agents — we avoid "weekly", which `ccusage codex` doesn't support (that was
// the original "Codex missing" bug). See buildUsageScript. Default to daily (today).
const REPORTS = ["daily", "monthly", "total"];
const DEFAULT_REPORT = "daily";

const TTL_MS = 5 * 60 * 1000;  // trust a real usage result for 5 min (the round-trip is slow)
const NEG_TTL_MS = 60 * 1000;  // cache a FAILURE (null) only briefly so a transient blip recovers fast

/** Validate/normalize a report granularity against the fixed allow-list (defends the
 *  remote script builder — never let arbitrary text reach the VM's bash). */
function normalizeReport(report) {
  return REPORTS.indexOf(report) >= 0 ? report : DEFAULT_REPORT;
}

/** True iff a just-finished collection for `collectedReport` still matches the live
 *  selection `currentReport`. The extension calls this to DISCARD a slow, stale usage
 *  collection (e.g. a daily run that lands after the user switched to monthly) instead of
 *  clobbering the current view with the wrong period's numbers. Both sides are normalized
 *  so an unsupported/blank value compares as the default. */
function isCurrentReport(collectedReport, currentReport) {
  return normalizeReport(collectedReport) === normalizeReport(currentReport);
}

/**
 * The remote bash collector, mirroring Get-AgentUsage.ps1's `$remoteScript`: ensure a
 * ccusage runner is available (existing ccusage / bunx / npx, else install via npm or a
 * self-contained bun runtime), run it for each of the three agents with --json, and
 * assemble ONE combined JSON object on stdout via jq. All installer/diagnostic noise
 * goes to stderr so stdout stays pure JSON; each tool that fails yields a small
 * {error,...} object rather than aborting the whole report.
 *
 * The `daily`/`monthly` views are scoped with ccusage --since/--until (YYYYMMDD) computed
 * from the VM's OWN clock — ccusage's grand `totals` is a LIFETIME sum regardless of
 * granularity, so without a window they'd just equal `total`. `daily` = today, `monthly`
 * = the 1st of this month through today, `total` = NO window (all-time). All use the
 * `daily`/`monthly` subcommands, which all three agents support. The window dates come
 * from `date`, never from caller input, so there's nothing to inject there.
 *
 * `report` is validated by the caller (collect) against REPORTS, but we also normalize
 * here so a direct call can't inject — the value is substituted into a bash string,
 * and the whole script is then shipped base64-as-data by ssh.runRemoteScript.
 */
function buildUsageScript(report) {
  const r = normalizeReport(report);
  return `set -u
REPORT="${r}"

# daily -> today's window; monthly -> from the 1st of this month; total -> no window
# (all-time). Window dates come from the VM's own clock (data, not injectable).
TODAY="\$(date +%Y%m%d)"
case "\$REPORT" in
  total)   ARGS=(monthly) ;;
  monthly) ARGS=(monthly --since "\$(date +%Y%m01)" --until "\$TODAY") ;;
  *)       ARGS=(daily --since "\$TODAY" --until "\$TODAY") ;;
esac

CC=()
ensure_ccusage() {
  if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  if command -v bunx    >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi
  if command -v npx     >/dev/null 2>&1; then CC=(npx -y ccusage@latest); return; fi

  echo "ccusage not found on the VM; attempting to install it..." >&2

  # Preferred: a global npm install when Node is present.
  if command -v npm >/dev/null 2>&1; then
    npm i -g ccusage >&2 2>&1 || true
    if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  fi

  # Otherwise install the self-contained bun runtime and run ccusage via bunx.
  if ! command -v bun >/dev/null 2>&1; then
    command -v unzip >/dev/null 2>&1 || { (apt-get update && apt-get install -y unzip) >&2 2>&1 || true; }
    curl -fsSL https://bun.sh/install | bash >&2 2>&1 || true
  fi
  export BUN_INSTALL="\${BUN_INSTALL:-\$HOME/.bun}"
  export PATH="\$BUN_INSTALL/bin:\$PATH"
  if command -v bunx >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi

  CC=()
}

# Run ccusage for one tool, returning valid JSON either way: the real report on
# success, or a small {error,...} object describing what went wrong.
capture() {
  local tool="\$1" out rc errfile
  if [ "\${#CC[@]}" -eq 0 ]; then
    jq -n --arg t "\$tool" '{error:"no JavaScript runtime available to run ccusage", tool:\$t}'
    return
  fi
  errfile="\$(mktemp)"
  out="\$("\${CC[@]}" "\$tool" "\${ARGS[@]}" --json 2>"\$errfile")"; rc=\$?
  if [ "\$rc" -ne 0 ] || ! printf '%s' "\$out" | jq -e . >/dev/null 2>&1; then
    local detail; detail="\$(tr '\\n' ' ' <"\$errfile" | head -c 500)"
    jq -n --arg t "\$tool" --arg d "\$detail" \\
      '{error:("ccusage failed for "+\$t), detail:\$d}'
  else
    printf '%s' "\$out"
  fi
  rm -f "\$errfile"
}

ensure_ccusage

claude_json="\$(capture claude)"
codex_json="\$(capture codex)"
opencode_json="\$(capture opencode)"

jq -n \\
  --arg host "\$(hostname)" \\
  --arg report "\$REPORT" \\
  --arg window "\${ARGS[*]}" \\
  --argjson claude "\$claude_json" \\
  --argjson codex "\$codex_json" \\
  --argjson opencode "\$opencode_json" \\
  '{
     generatedAt: (now | todate),
     vmHost: \$host,
     report: \$report,
     window: \$window,
     tools: { claude: \$claude, codex: \$codex, opencode: \$opencode }
   }'
`;
}

// ── Number / cost formatting ─────────────────────────────────────────────────

/** Format an exact token count compactly: 12.3M / 456K / 987. Non-finite/negative
 *  or zero -> "0". Token counts are EXACT (the bar/hover can show the raw number);
 *  this is just the compact label the table shows. */
function formatTokens(n) {
  const v = Number(n);
  if (!Number.isFinite(v) || v <= 0) return "0";
  if (v >= 1e9) return trimUnit(v / 1e9) + "B";
  if (v >= 1e6) return trimUnit(v / 1e6) + "M";
  if (v >= 1e3) return trimUnit(v / 1e3) + "K";
  return String(Math.round(v));
}

// One decimal place, but drop a trailing ".0" so "12.0M" reads as "12M".
function trimUnit(x) {
  const s = x.toFixed(1);
  return s.endsWith(".0") ? s.slice(0, -2) : s;
}

/** Format an ESTIMATED dollar cost. "$1,234.56" / "$0.76" / "$0.00" (two decimals,
 *  thousands-separated). Non-finite/negative -> "$0.00". Cost is an estimate — the
 *  panel already says so; this only formats the number. */
function formatCost(n) {
  const v = Number(n);
  if (!Number.isFinite(v) || v < 0) return "$0.00";
  // Thousands separators without relying on a locale (toLocaleString varies by env).
  const fixed = v.toFixed(2);
  const [intPart, frac] = fixed.split(".");
  const withCommas = intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return "$" + withCommas + "." + frac;
}

// ── Parsing a single tool's ccusage report ───────────────────────────────────

/**
 * Extract {tokens, cost} from one tool's ccusage JSON. ccusage puts a `totals`
 * object at the top level of every report (for `daily`/`monthly` it's the today/this-month
 * window; for `total` it's the all-time lifetime sum). Token counts live in
 * `totals.totalTokens`; the COST field name differs between
 * tools — Claude Code and OpenCode use `totals.totalCost`, but Codex uses
 * `totals.costUSD` (the roadmap's "costUSD"). We accept either.
 *
 * Returns null when the JSON has no usable usage: a per-tool {error,...} object (from
 * the collector's capture()), a missing/empty `totals`, a non-object, or a zero-token
 * total (an agent that has simply never run). null -> that agent is omitted from the
 * table rather than shown as a misleading $0 row.
 */
function parseToolUsage(json) {
  if (!json || typeof json !== "object" || json.error) return null;
  const totals = json.totals;
  if (!totals || typeof totals !== "object") return null;
  const tokens = Number(totals.totalTokens);
  if (!Number.isFinite(tokens) || tokens <= 0) return null;
  // Cost is an estimate and may be absent; accept the two field spellings, default 0.
  let cost = totals.totalCost != null ? Number(totals.totalCost) : Number(totals.costUSD);
  if (!Number.isFinite(cost) || cost < 0) cost = 0;
  return { tokens, cost };
}

/**
 * Turn the combined collector JSON ({tools:{claude,codex,opencode}}) into the usage
 * state the webview's renderUsage consumes:
 *   { tools:[{id,label,tokens,tokensText,costText}], totalTokensText, totalCostText }
 * Only agents with real usage appear as rows (in TOOLS order). Totals sum the exact
 * token counts and estimated costs across whatever rows survived.
 *
 * Returns null when NOTHING is parseable (all tools errored/empty/absent) so the
 * caller adds no `usage` key and the panel keeps its placeholders — an empty table
 * would look like "zero usage", which is different from "couldn't collect".
 */
function parseUsage(combined) {
  const bag = combined && typeof combined === "object" && combined.tools && typeof combined.tools === "object"
    ? combined.tools
    : null;
  if (!bag) return null;
  const rows = [];
  let totalTokens = 0, totalCost = 0;
  for (const t of TOOLS) {
    const u = parseToolUsage(bag[t.id]);
    if (!u) continue;
    totalTokens += u.tokens;
    totalCost += u.cost;
    rows.push({
      id: t.id,
      label: t.label,
      tokens: u.tokens,             // exact — drives the bar width + is the source of truth
      tokensText: formatTokens(u.tokens),
      costText: formatCost(u.cost), // estimate
    });
  }
  if (!rows.length) return null;
  return {
    tools: rows,
    totalTokensText: formatTokens(totalTokens),
    totalCostText: formatCost(totalCost),
  };
}

// ── Collection over SSH (best-effort, cached) ────────────────────────────────

// Memoize the (slow) collection, keyed BY REPORT. A success is trusted for TTL_MS; a
// failure (null) is held only briefly so a transient offline/ccusage-installing blip
// recovers quickly. opts.now (clock) and opts.noCache are for tests; a single in-flight
// collection PER report is shared so overlapping refreshes don't launch parallel ccusage
// runs. Keying by report means switching the daily/monthly view collects that period
// fresh (never serving the other period's numbers), while toggling back to a still-fresh
// period is instant. Only two keys ever exist, so the maps stay tiny.
const _cache = new Map();    // report -> { at, value }
const _inflight = new Map(); // report -> Promise

/**
 * Run the collector on the VM and parse it into usage state. Never rejects; resolves
 * the parsed usage object, or null on ANY problem (offline/unreachable, non-zero exit,
 * empty output, malformed JSON, all-tools-errored). `opts.runScript` injects the ssh
 * runner for tests; `opts.report` picks the granularity (validated).
 */
async function collectOnce(opts = {}) {
  const runScript = opts.runScript || ssh.runRemoteScript;
  const script = buildUsageScript(opts.report);
  let r;
  try {
    // ccusage may install itself the first time, so allow a generous timeout.
    r = await runScript(script, { timeoutMs: opts.timeoutMs || 180000 });
  } catch (_) {
    return null; // runRemoteScript never rejects, but stay defensive against an injected runner
  }
  if (!r || r.code !== 0 || !r.stdout) return null;
  let combined;
  try { combined = JSON.parse(r.stdout); } catch (_) { return null; }
  return parseUsage(combined);
}

/**
 * Cached collect(): returns the parsed usage state, or null. Best-effort. Shares one
 * in-flight collection across concurrent callers and honors the success/failure TTLs.
 */
function collect(opts = {}) {
  if (opts.noCache) return collectOnce(opts);
  const report = normalizeReport(opts.report);
  const now = opts.now ? opts.now() : Date.now();
  const hit = _cache.get(report);
  if (hit && now - hit.at < (hit.value == null ? NEG_TTL_MS : TTL_MS)) {
    return Promise.resolve(hit.value);
  }
  const flying = _inflight.get(report);
  if (flying) return flying;
  const p = collectOnce(opts).then(
    (value) => { _cache.set(report, { at: (opts.now ? opts.now() : Date.now()), value }); return value; },
    () => { _cache.set(report, { at: (opts.now ? opts.now() : Date.now()), value: null }); return null; }
  ).then((v) => { if (_inflight.get(report) === p) _inflight.delete(report); return v; },
         (e) => { if (_inflight.get(report) === p) _inflight.delete(report); throw e; });
  _inflight.set(report, p);
  return p;
}

/** Clear the module cache (tests / a forced refresh). */
function clearCache() { _cache.clear(); _inflight.clear(); }

/**
 * Return a copy of `state` with the usage table folded in, mirroring updates.augment:
 * BEST-EFFORT, so on any failure (offline, ccusage unavailable, nothing parseable) the
 * SAME state reference is returned unchanged (no `usage` key) and the caller can skip a
 * redundant re-push. Only collects when the VM is online — an offline VM can't answer.
 */
async function augment(state, opts = {}) {
  if (!state || typeof state !== "object") return state;
  if (state.online === false) return state; // offline: don't even try
  try {
    const usage = await collect(opts);
    if (!usage) return state;
    return { ...state, usage };
  } catch (_) {
    return state; // best-effort: leave the state as-is
  }
}

// ── Export ───────────────────────────────────────────────────────────────────

/**
 * Build the JSON text saved by "export json". Prefers the raw combined collector JSON
 * (the same document Get-AgentUsage.ps1 saves — full per-session/per-model breakdown,
 * no lossy rounding), annotated with a `savedAt` stamp and the parsed `summary` (the
 * table the panel showed) for convenience. When the raw JSON is unavailable/unparseable
 * it still emits a minimal envelope so the export never throws.
 *
 * Returns a pretty-printed JSON STRING ready for fs.writeFile.
 */
function buildExportPayload(rawText, opts = {}) {
  const savedAt = opts.savedAt || new Date().toISOString();
  let combined = null;
  if (rawText != null) {
    try { combined = JSON.parse(String(rawText)); } catch (_) { combined = null; }
  }
  const summary = combined ? parseUsage(combined) : null;
  const payload = {
    savedAt,
    source: "construct-control-panel",
    summary: summary || null,
    report: combined && combined.report ? combined.report : null,
    ccusage: combined || null, // the raw combined collector document, or null if we couldn't parse it
  };
  return JSON.stringify(payload, null, 2);
}

/** Default filename for the save dialog, e.g. construct-usage-weekly-20260701-143005.json. */
function exportFileName(report, at) {
  const d = at || new Date();
  const p2 = (n) => String(n).padStart(2, "0");
  const stamp = `${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}-${p2(d.getHours())}${p2(d.getMinutes())}${p2(d.getSeconds())}`;
  return `construct-usage-${normalizeReport(report)}-${stamp}.json`;
}

/**
 * Collect the RAW combined JSON (unparsed) for export. Returns the raw stdout string,
 * or null on any failure. Separate from collect() because export wants the full
 * document, not the lossy table. Never rejects.
 */
async function collectRaw(opts = {}) {
  const runScript = opts.runScript || ssh.runRemoteScript;
  const script = buildUsageScript(opts.report);
  let r;
  try { r = await runScript(script, { timeoutMs: opts.timeoutMs || 180000 }); }
  catch (_) { return null; }
  if (!r || r.code !== 0 || !r.stdout) return null;
  // Validate it's JSON before handing it back so export never writes garbage.
  try { JSON.parse(r.stdout); } catch (_) { return null; }
  return r.stdout;
}

module.exports = {
  TOOLS, REPORTS, DEFAULT_REPORT, TTL_MS, NEG_TTL_MS,
  normalizeReport, isCurrentReport, buildUsageScript,
  formatTokens, formatCost, parseToolUsage, parseUsage,
  collectOnce, collect, collectRaw, clearCache, augment,
  buildExportPayload, exportFileName,
};
