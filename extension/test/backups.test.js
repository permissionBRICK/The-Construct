"use strict";
// Plain-node unit tests for the panel's Backups module (docs/plans/host-config-backups.md §3.7, §3.8): the
// model builder (hidden without the feature, local vs remote), the per-VM override validation, the once-per-
// streak warning, the PC file name, the backup lifecycle actions and the host routes. The webview side is in
// ui-smoke.js; the Companion's port is checked by the "config-backups" parity fixture.
// Run: node extension/test/backups.test.js
const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const bk = require("../src/backups");
const life = require("../src/lifecycle");
const remotehost = require("../src/remotehost");
const instances = require("../src/instances");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const now = Date.parse("2026-11-08T12:00:00Z");
const id = (c) => c.repeat(32);
const key = { fingerprint: "AB".repeat(32), label: "Work PC", exportedAt: null };
const settings = { intervalHours: null, keep: 6, effective: { enabled: true, intervalHours: 24, keep: 6 }, host: { enabled: true, intervalHours: 24, keep: 4, maxKeep: 10 } };
const failing = { enabled: true, lastSuccessAt: "2026-11-06T09:30:00Z", failingSince: "2026-11-07T10:00:00Z", requestPending: false, count: 2, totalBytes: 3221225472,
  lastFailure: { reason: "vm-disk-space", detail: "need 3 GiB" }, nextDueAt: null };
const list = { backups: [
  { id: id("a"), createdAt: "2026-11-08T09:00:00Z", trigger: "scheduled", sizeBytes: 1610612736, pinnedUntil: null },
  { id: id("b"), createdAt: "2026-11-05T22:15:00Z", trigger: "pre-reinstall", sizeBytes: 1073741824, pinnedUntil: "2026-11-19T22:15:00Z" },
  { id: "../escape", createdAt: "2026-11-01T00:00:00Z" }] };

// ── Visibility: hidden without the feature, local vs remote ──────────────────
ok("model: a remote VM whose host lacks config-backups hides the module", bk.toPanelBackups({ remote: true, supported: false, key }, now) === null);
ok("model: ...and so does an unknown answer", bk.toPanelBackups({ remote: true, key }, now) === null);
const local = bk.toPanelBackups({ remote: false, key }, now);
ok("model: a local VM gets the module without the host part",
  local && local.remote === false && local.items.length === 0 && local.override === null && local.statusLine === "" && local.warning === "");
ok("model: ...and still warns about an unexported key", local.keyWarning.includes("not exported"));
ok("model: an exported key raises no warning", bk.toPanelBackups({ remote: false, key: { ...key, exportedAt: "2026-11-02T08:00:00Z" } }, now).keyWarning === "");
ok("model: no key file means no key and no warning", (() => { const m = bk.toPanelBackups({ remote: false, key: null }, now); return m.key.present === false && m.keyWarning === ""; })());
const remote = bk.toPanelBackups({ remote: true, supported: true, status: failing, list, settings, key, problem: "" }, now);
ok("model: a remote VM on a host with the feature gets the host part", remote.remote === true && remote.enabled === true);
ok("model: the status line", remote.statusLine === "Last backup 2026-11-06 09:30 UTC \u00b7 2 stored (3.0 GiB)", remote.statusLine);
ok("model: the failing streak", remote.warning === "Backups failing since 2026-11-07 10:00 UTC: not enough free disk space on the VM (need 3 GiB).", remote.warning);
ok("model: the reason code rides along for the enrol offer", remote.warningReason === "vm-disk-space");
ok("model: list rows keep the host's order and drop malformed ids", remote.items.map((b) => b.id).join() === [id("a"), id("b")].join());
ok("model: the pre-reinstall backup is pinned", remote.items[1].pinned === true && remote.items[1].pinnedUntil === "2026-11-19 22:15 UTC" && remote.items[1].trigger === "before reinstall");
ok("model: an expired pin is not shown", bk.toPanelBackups({ remote: true, supported: true, list, settings, key }, Date.parse("2026-12-01T00:00:00Z")).items[1].pinned === false);
ok("model: the override: empty = host default, numbers as text",
  remote.override.intervalHours === "" && remote.override.keep === "6" && remote.override.hostIntervalHours === 24 && remote.override.hostKeep === 4 && remote.override.maxKeep === 10);
ok("model: a failed settings read hides the override", bk.toPanelBackups({ remote: true, supported: true, status: failing, list, settings: null, key, problem: "boom" }, now).override === null);
ok("model: a host with backups off says so", bk.toPanelBackups({ remote: true, supported: true, status: { ...failing, enabled: false }, settings, key }, now).statusLine.startsWith("Backups are turned off on this host"));
ok("model: interval 0 for this VM says so", bk.toPanelBackups({ remote: true, supported: true, status: { enabled: true }, settings: { ...settings, intervalHours: 0, effective: { enabled: true, intervalHours: 0, keep: 4 } }, key }, now).statusLine.startsWith("Scheduled backups are off for this VM"));
ok("model: a pending request is flagged", bk.toPanelBackups({ remote: true, supported: true, status: { enabled: true, requestPending: true }, settings, key }, now).pending === true);
ok("model: control characters never reach the panel", bk.toPanelBackups({ remote: true, supported: true, status: { ...failing, lastFailure: { reason: "x\u0007y", detail: "a\nb" } }, settings, key }, now).warning.includes("x y (a b)"));

// ── Override validation: interval 0..720 or empty, keep 1..maxKeep or empty ──
const parse = (intervalHours, keep, maxKeep = 10) => bk.parseOverride({ intervalHours, keep }, maxKeep);
ok("override: empty fields are the host default", JSON.stringify(parse("", "").body) === JSON.stringify({ intervalHours: null, keep: null }));
ok("override: null fields too", JSON.stringify(parse(null, null).body) === JSON.stringify({ intervalHours: null, keep: null }));
ok("override: interval 0 turns scheduled backups off", parse("0", "").ok && parse("0", "").body.intervalHours === 0);
ok("override: interval 720 is the ceiling", parse("720", "").ok && !parse("721", "").ok);
ok("override: negative or fractional intervals are refused", !parse("-1", "").ok && !parse("1.5", "").ok && !parse("1e2", "").ok);
ok("override: keep 1..maxKeep", parse("", "1").ok && parse("", "10").ok && !parse("", "0").ok && !parse("", "11").ok);
ok("override: maxKeep defaults to the contract's 50", bk.parseOverride({ keep: "50" }).ok && !bk.parseOverride({ keep: "51" }).ok);
ok("override: numbers are accepted as numbers", parse(24, 6).ok && parse(24, 6).body.keep === 6 && !parse(24.5, 6).ok);
ok("override: other types are refused", !parse(true, "").ok && !parse("", {}).ok);
ok("override: problems name the field", parse("721", "11").problems.map((p) => p.field).join() === "intervalHours,keep");

// ── The once-per-streak warning ──────────────────────────────────────────────
const first = bk.backupWarning("work-vm", failing, null);
ok("warning: a new streak notifies and is remembered", first.notify === true && first.remember === failing.failingSince && first.body.includes("work-vm"));
ok("warning: the same streak does not notify again", bk.backupWarning("work-vm", failing, failing.failingSince).notify === false);
ok("warning: a new streak after an older one notifies", bk.backupWarning("work-vm", failing, "2026-10-01T00:00:00Z").notify === true);
const healthy = bk.backupWarning("work-vm", { ...failing, failingSince: null }, failing.failingSince);
ok("warning: a success ends the streak (nothing to remember)", healthy.notify === false && healthy.remember === null);
ok("warning: no status means no warning", bk.backupWarning("work-vm", null, null).notify === false);

// ── File name ─────────────────────────────────────────────────────────────────
ok("file name: <instance>-<UTC timestamp>.cbk", bk.backupFileName("work-vm", now) === "work-vm-20261108T120000Z.cbk");
ok("file name: unsafe characters are replaced", bk.backupFileName("a:b/c d", now) === "a-b-c-d-20261108T120000Z.cbk");
ok("file name: an empty name falls back", bk.backupFileName("  ", now) === "construct-20261108T120000Z.cbk");

// ── Backup lifecycle actions (Provision-AgentVM.ps1 -Action backup-…) ────────
const remoteInst = instances.deriveDefaults("dev", { backend: "hyperv-remote", sshHost: "host.example", service: { url: "https://host.example:7462", auth: "token" } });
const localInst = instances.deriveDefaults("work-vm", {});
const inv = (action, opts) => life.buildInvocation(action, { instanceParams: ["InstanceName"], ...opts });
const save = inv("backupSave", { instance: remoteInst, backupFile: "C:\\Users\\someone\\dev 1.cbk", backupId: id("a") });
ok("actions: save targets the instance and passes file and id", save.script === life.PROVISION && save.elevate === false && save.destructive === false
  && save.args.join(" ") === `-FromPanel -InstanceName dev -Action backup-save -BackupFile C:\\Users\\someone\\dev 1.cbk -BackupId ${id("a")}`, save.args.join(" "));
ok("actions: the pair spec is always attached, so a path is quoted as data", Array.isArray(inv("backupKeyExport", { backupKeyFile: "-x.json" }).argSpec));
ok("actions: save without a file is refused", inv("backupSave", { instance: remoteInst }) === null);
ok("actions: a malformed id is refused", inv("backupSave", { instance: remoteInst, backupFile: "C:\\b.cbk", backupId: id("A") }) === null);
ok("actions: a host id on a local VM is blocked", (inv("backupRestore", { instance: localInst, backupId: id("a") }) || {}).blocked === true);
ok("actions: restore takes a file or an id, not both and not neither",
  inv("backupRestore", { instance: remoteInst, backupId: id("a") }).args.includes("-BackupId") && inv("backupRestore", { instance: localInst, backupFile: "C:\\b.cbk" }).args.includes("-BackupFile")
  && inv("backupRestore", { instance: remoteInst, backupId: id("a"), backupFile: "C:\\b.cbk" }) === null && inv("backupRestore", { instance: remoteInst }) === null);
ok("actions: key export/import need a key file", inv("backupKeyExport", { backupKeyFile: "C:\\k.json" }).args.includes("backup-key-export") && inv("backupKeyImport", {}) === null);
ok("actions: enrol takes no operand", inv("backupEnroll", { instance: remoteInst }).args.join(" ") === "-FromPanel -InstanceName dev -Action backup-enroll" && inv("backupEnroll", { backupFile: "x" }) === null);
ok("actions: an older Provision-AgentVM.ps1 is refused, not launched", (inv("backupEnroll", { supportsBackupActions: false }) || {}).blocked === true);
ok("actions: the legacy four-argument targeting works like export",
  life.buildInvocation("backupEnroll", { instance: localInst, instanceParams: ["VmHost", "HostAlias", "SshPort", "LocalKeyName"] }).args.includes("-LocalKeyName"));
ok("actions: the script probe is -BackupFile", (() => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-backup-actions-"));
  try {
    fs.writeFileSync(path.join(dir, life.PROVISION), "param([string]$BackupDir = '')\n");
    const before = life.scriptSupportsBackupActions(dir);
    fs.writeFileSync(path.join(dir, life.PROVISION), "param([string]$BackupFile = '', [string]$BackupId = '')\n");
    return before === false && life.scriptSupportsBackupActions(dir) === true;
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
})());

// ── Host routes ───────────────────────────────────────────────────────────────
(async () => {
  const calls = [];
  const client = remotehost.createClient({ baseUrl: "https://host:7462", pin: "ab".repeat(32), auth: { kind: "token", token: "fixture" },
    fetchImpl: (url, init) => { calls.push({ url, method: init.method, body: init.body ? JSON.parse(init.body) : null }); return Promise.resolve({ status: 200, text: "{}" }); } });
  await client.vmBackupStatus("work vm"); await client.vmBackups("work vm"); await client.requestVmBackup("work vm");
  await client.vmBackupSettings("work vm"); await client.setVmBackupSettings("work vm", { intervalHours: 0, keep: null });
  ok("routes: URL-encoded names under /api/v1/vms/{name}/backups", calls.map((c) => c.method + " " + c.url.replace("https://host:7462/api/v1", "")).join("|") ===
    "GET /vms/work%20vm/backups/status|GET /vms/work%20vm/backups|POST /vms/work%20vm/backups/request|GET /vms/work%20vm/backups/settings|PUT /vms/work%20vm/backups/settings", JSON.stringify(calls));
  ok("routes: the settings body is sent as given", JSON.stringify(calls[4].body) === JSON.stringify({ intervalHours: 0, keep: null }));
  assert.ok(pass > 0);
  console.log(`\n  config backups unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
