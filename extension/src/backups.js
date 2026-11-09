"use strict";
// CONFIG BACKUPS — the pure half of the panel's Backups module (docs/plans/host-config-backups.md
// §3.7, §3.8). The Companion's port is companion/src/Construct.Companion.Core/Backups/BackupProtocol.cs;
// both are checked against the "config-backups" parity fixture (extension/test/export-parity-fixtures.js).
//
//   toPanelBackups    host replies + this PC's key file -> the module's model (null hides it)
//   parseOverride     the per-VM override form -> the PUT …/backups/settings body, or problems
//   backupWarning     the once-per-failure-streak warning (Companion toast, extension message)
//   backupFileName    the default name of a backup saved to this PC
//   pcKeyPath         where the PowerShell client keeps this PC's key description (§3.2)
//
// No vscode, no I/O: the callers read the host and the key file and hand the results in.

const path = require("path");
const { formatBytes, formatWhen } = require("./hostadmin");

/** The `/health` apiFeatures entry that turns the remote module on. */
const FEATURE = "config-backups";
/** The per-instance state key that remembers the failure streak already warned about. */
const STREAK_KEY = "backupWarnedFailingSince";
const MAX_INTERVAL_HOURS = 720;
/** The contract's ceiling for `maxKeep`; the host's own value replaces it when known. */
const MAX_KEEP = 50;
const KEY_FILE_NAME = "construct-backup-key.json";

const str = (v) => (v == null ? "" : String(v)).trim();
const isObj = (v) => !!v && typeof v === "object" && !Array.isArray(v);
const obj = (v) => (isObj(v) ? v : {});
const int = (v) => (Number.isSafeInteger(v) ? v : null);

/** A backup id as the host spells it: 32 lowercase hex. Pure. */
function isBackupId(value) {
  return typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
}

/** One line of printable text, at most `max` characters. Pure. */
function clean(value, max) {
  const s = (value == null ? "" : String(value)).replace(/[\u0000-\u001f\u007f-\u009f]+/g, " ").replace(/ {2,}/g, " ").trim();
  return s.length > max ? s.slice(0, max - 1) + "…" : s;
}

const REASONS = new Map([
  ["vm-disk-space", "not enough free disk space on the VM"],
  ["no-recipients", "no PC backup key is enrolled on the VM"],
  ["export-failed", "the config export failed on the VM"],
  ["upload-failed", "the upload to the host failed"],
  ["host-refused", "the host refused the backup"],
  ["verify-failed", "the backup could not be verified"],
]);

/** A reported failure (`lastFailure`) as one sentence fragment. Pure. */
function failureText(failure) {
  const f = obj(failure);
  const reason = str(f.reason);
  const detail = clean(f.detail, 200);
  const text = REASONS.get(reason) || (reason ? clean(reason, 60) : "an unknown error");
  return detail ? `${text} (${detail})` : text;
}

const TRIGGERS = new Map([["scheduled", "scheduled"], ["manual", "manual"], ["pre-reinstall", "before reinstall"]]);

/** One row of the host's backup list. Pure. */
function toRow(backup, now) {
  const b = obj(backup);
  const until = Date.parse(str(b.pinnedUntil));
  const pinned = Number.isFinite(until) && until > now;
  return {
    id: str(b.id),
    when: formatWhen(b.createdAt),
    trigger: TRIGGERS.get(str(b.trigger)) || clean(b.trigger, 40) || "unknown",
    size: formatBytes(b.sizeBytes),
    pinned,
    pinnedUntil: pinned ? formatWhen(b.pinnedUntil) : "",
  };
}

/** This PC's backup key as the panel shows it, from pc-key.json (§3.2). Pure. */
function toKeyView(key) {
  const k = obj(key);
  const fingerprint = str(k.fingerprint).toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(fingerprint)) return { present: false, exported: false, label: "", fingerprint: "" };
  return { present: true, exported: str(k.exportedAt) !== "", label: clean(k.label, 64), fingerprint: fingerprint.slice(0, 16) };
}

/** The per-VM override as the form shows it: "" is the host default. Pure. */
function toOverride(settings) {
  if (!isObj(settings)) return null;
  const s = settings, host = obj(s.host);
  const maxKeep = int(host.maxKeep);
  return {
    intervalHours: int(s.intervalHours) === null ? "" : String(s.intervalHours),
    keep: int(s.keep) === null ? "" : String(s.keep),
    hostIntervalHours: int(host.intervalHours),
    hostKeep: int(host.keep),
    maxKeep: maxKeep !== null && maxKeep >= 1 ? maxKeep : MAX_KEEP,
  };
}

/**
 * The Backups module's model, or null to hide the module.
 *
 * `input`: { remote, supported, status, list, settings, key, problem }
 *   remote     the instance lives on a host service
 *   supported  that host's /health lists `config-backups` (remote only)
 *   status     GET …/backups/status, list GET …/backups, settings GET …/backups/settings
 *              (each null when its read failed; `problem` then says why)
 *   key        this PC's pc-key.json, or null
 * A local instance gets the file and key buttons only; a remote one on a host without
 * the feature gets nothing (the module stays hidden, like the idle policy). Pure.
 */
function toPanelBackups(input, now) {
  const i = obj(input);
  const at = typeof now === "number" ? now : Date.now();
  const key = toKeyView(i.key);
  const keyWarning = key.present && !key.exported
    ? "This PC's backup key is not exported yet. Without an exported copy, a lost or reinstalled PC cannot open these backups."
    : "";
  if (i.remote !== true) {
    return { remote: false, enabled: false, statusLine: "", warning: "", warningReason: "", pending: false,
      items: [], override: null, key, keyWarning, problem: "" };
  }
  if (i.supported !== true) return null;
  const status = isObj(i.status) ? i.status : null;
  const settings = isObj(i.settings) ? i.settings : null;
  const effective = obj(settings && settings.effective);
  const parts = [];
  let enabled = true;
  if (status) {
    enabled = status.enabled !== false;
    if (!enabled) parts.push("Backups are turned off on this host");
    else if (int(effective.intervalHours) === 0) parts.push("Scheduled backups are off for this VM");
    parts.push(str(status.lastSuccessAt) ? "Last backup " + formatWhen(status.lastSuccessAt) : "No backup yet");
    const count = int(status.count);
    if (count !== null && count > 0) parts.push(`${count} stored (${formatBytes(status.totalBytes)})`);
    if (enabled && str(status.nextDueAt)) parts.push("next " + formatWhen(status.nextDueAt));
    if (status.requestPending === true) parts.push("backup requested, it starts at the VM's next check-in");
  }
  const failing = status ? str(status.failingSince) : "";
  const failure = obj(status && status.lastFailure);
  const list = obj(i.list);
  return {
    remote: true,
    enabled,
    statusLine: parts.join(" · "),
    warning: failing ? `Backups failing since ${formatWhen(failing)}: ${failureText(failure)}.` : "",
    warningReason: failing ? str(failure.reason) : "",
    pending: !!(status && status.requestPending === true),
    items: (Array.isArray(list.backups) ? list.backups : []).filter((b) => b && isBackupId(b.id)).map((b) => toRow(b, at)),
    override: toOverride(settings),
    key,
    keyWarning,
    problem: clean(i.problem, 300),
  };
}

/** One override field: "" / null = host default, else a whole number in [min, max]. */
function overrideValue(value, min, max) {
  if (value === null || value === undefined) return { ok: true, value: null };
  if (typeof value === "number") return Number.isInteger(value) && value >= min && value <= max ? { ok: true, value } : { ok: false };
  if (typeof value !== "string") return { ok: false };
  const s = value.trim();
  if (s === "") return { ok: true, value: null };
  if (!/^[0-9]+$/.test(s)) return { ok: false };
  const n = Number(s);
  return n >= min && n <= max ? { ok: true, value: n } : { ok: false };
}

/**
 * The override form -> the `PUT …/backups/settings` body. `maxKeep` is the host's
 * ceiling (the contract's 50 when unknown). Refused here with a reason, so a bad value
 * never becomes a 400. Pure.
 */
function parseOverride(form, maxKeep) {
  const f = obj(form);
  const ceiling = Number.isSafeInteger(maxKeep) && maxKeep >= 1 ? maxKeep : MAX_KEEP;
  const problems = [];
  const interval = overrideValue(f.intervalHours, 0, MAX_INTERVAL_HOURS);
  if (!interval.ok) problems.push({ field: "intervalHours", reason: `hours between backups: a whole number from 0 to ${MAX_INTERVAL_HOURS}, or empty for the host default` });
  const keep = overrideValue(f.keep, 1, ceiling);
  if (!keep.ok) problems.push({ field: "keep", reason: `backups kept: a whole number from 1 to ${ceiling}, or empty for the host default` });
  return problems.length
    ? { ok: false, body: null, problems }
    : { ok: true, body: { intervalHours: interval.value, keep: keep.value }, problems };
}

/**
 * The warning for one instance's status, once per failure streak (§3.7): `remember` is the
 * value to keep for the instance (the streak's `failingSince`, or null once it ended), and
 * `notify` is true only for a streak not warned about yet. Pure.
 */
function backupWarning(instance, status, warned) {
  const s = obj(status);
  const since = str(s.failingSince);
  if (!since) return { notify: false, remember: null, title: "", body: "" };
  if (since === str(warned)) return { notify: false, remember: since, title: "", body: "" };
  return {
    notify: true, remember: since, title: "Config backups failing",
    body: `Backups of “${clean(instance, 64)}” are failing since ${formatWhen(since)}: ${failureText(s.lastFailure)}. Open its control panel for details.`,
  };
}

/** `<instance>-<UTC timestamp>.cbk`, safe as a Windows file name. Pure. */
function backupFileName(instance, now) {
  const name = str(instance).replace(/[^A-Za-z0-9._-]+/g, "-").replace(/^[-.]+|[-.]+$/g, "") || "construct";
  const d = new Date(typeof now === "number" ? now : Date.now());
  const p = (n) => String(n).padStart(2, "0");
  return `${name}-${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}T${p(d.getUTCHours())}${p(d.getUTCMinutes())}${p(d.getUTCSeconds())}Z.cbk`;
}

/** %LOCALAPPDATA%\The-Construct\backup\pc-key.json, or null without a local app data dir. */
function pcKeyPath(localAppData) {
  return localAppData ? path.join(localAppData, "The-Construct", "backup", "pc-key.json") : null;
}

module.exports = {
  FEATURE, STREAK_KEY, MAX_INTERVAL_HOURS, MAX_KEEP, KEY_FILE_NAME,
  isBackupId, failureText, toKeyView, toOverride, toPanelBackups, parseOverride, backupWarning, backupFileName, pcKeyPath,
};
