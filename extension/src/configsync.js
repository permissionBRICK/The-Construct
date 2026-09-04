"use strict";
// Config-sync engine: the host-side git-backed sync described in
// docs/config-sync.md §6. Reads VM profiles over SSH, commits them onto a `vm`
// branch INSIDE the host config repo, merges into `main`, validates, and writes
// back the merged result — all without touching the working tree's checkout
// branch (the vm branch uses a temp index). Process execution is injected
// (makeGitRunner) so tests run REAL git in throwaway dirs without any fakes.
//
// The VM-side branch name is a PARAMETER (`vmBranch`, default `vm`): with more
// than one VM instance, each instance owns its own branch in the one host config
// repo (`vm` for the default instance, `vm-<instance>` otherwise — see
// docs/config-sync.md §6 "Multiple instances"). `main` stays the host truth for
// all of them. Omitting the option reproduces the single-VM behavior exactly.
//
// The module also owns:
//   - the bash scripts that read/write the VM store (buildReadStoreScript /
//     buildWriteStoreScript) — same base64-as-data, END-sentinel idiom as
//     projects.js buildScanScript;
//   - remote/upstream helpers (manifest, staging clones, import planning,
//     mergeFile, pushUpstream) per D16/D17/D19;
//   - ensureConfigTree for the config-dir bootstrap, and the cross-process
//     sync lock (acquireSyncLock/releaseSyncLock) that serializes ticks across
//     VS Code windows and the PowerShell engine.

const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");

const projects = require("./projects");
// The profile-name rule lives in host.js and is reused here (never re-implemented):
// one authoritative rule per engine, shared by every profile read/write and publish.
const hostNames = require("./host");

// ── Git runner (injectable spawn) ────────────────────────────────────────────

/**
 * Create a git command runner with an injectable spawn. Tests inject
 * child_process.spawn directly (no fakes — tests run real git); the extension
 * injects a spawn that adds windowsHide:true.
 *
 * Returns `runGit(args, {cwd, timeoutMs?, env?})` → Promise<{code, stdout, stderr}>.
 * Never rejects. code < 0 on spawn error or timeout.
 */
function makeGitRunner({ spawn, gitCmd = "git" } = {}) {
  if (!spawn) spawn = require("child_process").spawn;
  return function runGit(args, opts = {}) {
    const cwd = opts.cwd || process.cwd();
    const timeoutMs = opts.timeoutMs || 30000;
    return new Promise((resolve) => {
      let child;
      try {
        child = spawn(gitCmd, args, {
          cwd,
          stdio: ["pipe", "pipe", "pipe"],
          windowsHide: true,
          env: opts.env ? { ...process.env, ...opts.env } : process.env,
        });
      } catch (err) {
        return resolve({ code: -1, stdout: "", stderr: String(err.message || err) });
      }
      const chunks = { out: [], err: [] };
      child.stdout.on("data", (d) => chunks.out.push(d));
      child.stderr.on("data", (d) => chunks.err.push(d));
      let killed = false;
      const timer = setTimeout(() => { killed = true; child.kill("SIGKILL"); }, timeoutMs);
      child.on("error", (err) => {
        clearTimeout(timer);
        resolve({ code: -1, stdout: "", stderr: String(err.message || err) });
      });
      child.on("close", (code) => {
        clearTimeout(timer);
        resolve({
          code: killed ? -1 : (code == null ? -1 : code),
          stdout: Buffer.concat(chunks.out).toString("utf8"),
          stderr: Buffer.concat(chunks.err).toString("utf8"),
        });
      });
    });
  };
}

/**
 * Detect whether git is available and its version.
 */
async function detectGit(runGit) {
  const r = await runGit(["--version"], { cwd: os.tmpdir() });
  if (r.code !== 0) return { present: false, version: null };
  const m = r.stdout.match(/(\d+\.\d+[\d.]*)/);
  return { present: true, version: m ? m[1] : null };
}

// ── Config tree bootstrap ────────────────────────────────────────────────────

/**
 * Ensure the config dir subdirectories exist: projects/, manifest/, bases/.
 */
function ensureConfigTree(configDir) {
  for (const sub of ["projects", "manifest", "bases"]) {
    fs.mkdirSync(path.join(configDir, sub), { recursive: true });
  }
}

// ── Cross-process sync lock ──────────────────────────────────────────────────
// Serializes whole sync ticks across every engine that can touch the config
// repo: each VS Code window runs its own extension host (extensionKind "ui"),
// and the PowerShell engine (Invoke-ConstructConfigSync) runs during provisions
// — none of them share a process, so an in-process flag is not enough. Without
// this, two concurrent ticks interleave read-store → commit → merge → write-back
// and a tick holding a stale store read commits spurious deletions of files the
// other tick just added. The lock file lives at <configDir>/.sync.lock and both
// engines use the same name and stale rule (Lock-ConstructConfigSync in
// AgentVm.Common.ps1 must match).

const SYNC_LOCK_FILE = ".sync.lock";
const PROVISION_SYNC_INTENT_FILE = ".sync.provisioning";
// A tick is seconds of work (two 30s-capped SSH calls + local git). A lock older
// than this belongs to a crashed/killed process and may be broken.
const SYNC_LOCK_STALE_MS = 5 * 60 * 1000;
const PROVISION_SYNC_INTENT_STALE_MS = 5 * 60 * 1000;

/** True when the pid recorded by another local host process is definitely gone. */
function ownerProcessIsDead(lockPath) {
  try {
    const owner = JSON.parse(fs.readFileSync(lockPath, "utf8"));
    const pid = Number(owner && owner.pid);
    if (!Number.isSafeInteger(pid) || pid <= 0) return false;
    try { process.kill(pid, 0); return false; }
    catch (e) { return !!e && e.code === "ESRCH"; }
  } catch (_) { return false; }
}

/**
 * A provisioning PowerShell process writes this short-lived intent before it
 * waits for the ordinary sync lock. Extension ticks yield while that live
 * intent exists, which prevents timer/watcher ticks from repeatedly winning
 * the lock and starving an unattended reprovision.
 */
function provisionSyncPending(configDir, opts) {
  const staleMs = (opts && opts.staleMs) || PROVISION_SYNC_INTENT_STALE_MS;
  const intentPath = path.join(configDir, PROVISION_SYNC_INTENT_FILE);
  let st;
  try { st = fs.statSync(intentPath); } catch (_) { return false; }
  if (ownerProcessIsDead(intentPath) || Date.now() - st.mtimeMs > staleMs) {
    try { fs.unlinkSync(intentPath); } catch (_) { /* lost a cleanup race */ }
    return false;
  }
  return true;
}

/**
 * Try to take the sync lock. Atomic create (O_EXCL); a lock whose mtime is
 * older than the stale threshold is treated as abandoned and broken. Returns
 * an ownership token (string) when acquired, null when another live engine
 * holds it. Pass the token to releaseSyncLock — release is a no-op unless the
 * lock file still carries it, so a holder that outlived the stale threshold
 * and was broken cannot delete the next holder's lock on its way out.
 */
function acquireSyncLock(configDir, opts) {
  const staleMs = (opts && opts.staleMs) || SYNC_LOCK_STALE_MS;
  const lockPath = path.join(configDir, SYNC_LOCK_FILE);
  const token = crypto.randomBytes(16).toString("hex");
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const fd = fs.openSync(lockPath, "wx");
      try { fs.writeSync(fd, JSON.stringify({ token, pid: process.pid, at: new Date().toISOString() })); }
      finally { fs.closeSync(fd); }
      return token;
    } catch (e) {
      if (!e || e.code !== "EEXIST") return null;
      let st;
      try { st = fs.statSync(lockPath); }
      catch (_) { continue; } // holder released between open and stat — retry
      if (!ownerProcessIsDead(lockPath) && Date.now() - st.mtimeMs <= staleMs) return null;
      try { fs.unlinkSync(lockPath); } catch (_) { /* lost the break race */ }
      // retry the create once; if a rival re-created it first, report busy
    }
  }
  return null;
}

/**
 * Release the sync lock, but only if it is still ours: the file must exist and
 * carry the given token. A missing/unreadable/foreign-token file is left alone
 * (best-effort — a break-then-recreate between the read and the unlink is
 * theoretically possible but needs a second stale-break inside the window).
 */
function releaseSyncLock(configDir, token) {
  const lockPath = path.join(configDir, SYNC_LOCK_FILE);
  try {
    const cur = JSON.parse(fs.readFileSync(lockPath, "utf8"));
    if (!token || cur.token !== token) return; // not ours (or ownership unknown) — leave it
    fs.unlinkSync(lockPath);
  } catch (_) { /* already gone or unreadable — leave it */ }
}

// ── VM-side branch name ──────────────────────────────────────────────────────
// One host config repo can carry several VM instances, one branch each. The
// default instance keeps the historical name `vm`, so a single-VM install sees
// no new refs. A slash form (`vm/<name>`) is deliberately NOT used: git cannot
// hold refs/heads/vm and refs/heads/vm/<x> at the same time.

const DEFAULT_VM_BRANCH = "vm";

// Windows' reserved device stems. A loose ref IS a file — refs/heads/<name>, plus the
// refs/heads/<name>.lock git writes next to it — and on Windows none of these names can
// be created as one, with or without an extension ("CON.txt" is the device too). The host
// config repo lives on Windows, so a hand-authored `configBranch` of "CON" passes every
// registry check here and then fails at the first `git branch`, leaving the instance on
// the fallback branch. Same list, same rule as isKeyFileName() in instances.js — that one
// guards ~/.ssh/<keyName>, this one guards .git/refs/heads/<branch>.
const WINDOWS_DEVICE_NAMES = new Set([
  "con", "prn", "aux", "nul",
  "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
  "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
]);

// Names that are syntactically fine but semantically wrong here, compared
// case-insensitively because Windows' loose-ref files are case-insensitive:
//   • `main` is the host-truth branch — using it as an instance branch would
//     merge main into itself and destroy the isolation this parameter exists for
//     (same for `master`, the other conventional trunk name);
//   • git's pseudo-refs (`HEAD` and friends) cannot be created as branches
//     (`git branch HEAD` is refused) yet READ specially — a bare `HEAD` resolves
//     to the checked-out branch, so a tick would silently sync against main
//     while writing to a bogus refs/heads/HEAD.
// This is an explicit list, not a shape rule: an instance legitimately named
// `WORK` must get its own `WORK` branch rather than silently sharing `vm`.
const RESERVED_VM_BRANCHES = new Set([
  "main", "master",
  "head", "fetch_head", "orig_head", "merge_head", "cherry_pick_head",
  "revert_head", "bisect_head", "rebase_head", "auto_merge", "stash",
]);

/**
 * A branch name git will accept as refs/heads/<name> without surprises, and
 * that is safe to hand to git as a BARE ref operand: starts alphanumeric, then
 * alphanumerics / dot / underscore / hyphen, no "..", no trailing ".", no
 * ".lock" suffix (git reserves it), and not one of RESERVED_VM_BRANCHES.
 * Deliberately stricter than check-ref-format — these names come from instance
 * names (^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$) and end up in file paths and ssh aliases
 * too — but never LOOSER: everything this accepts, `git check-ref-format
 * --branch` accepts (configsync.test.js drives the fixture list through real
 * git, and config-sync.test.ps1 drives the same list through the PS twin).
 *
 * The shape rule above already covers most of check-ref-format's list — a
 * leading "-", control characters, "@{", a "/." component and the space/tilde/
 * caret/colon/question/asterisk/bracket/backslash set are all outside the
 * character class, and there are no slashes, so ".lock" only has to be checked
 * on the whole name. The two that survive it are spelled out:
 *   • ".."   — git refuses it anywhere in a ref;
 *   • a TRAILING "." — `git check-ref-format --branch foo.` fails, so accepting
 *     it meant a hand-authored `configBranch` of "foo." passed validation here
 *     and then failed at `git branch` (or silently synced on the fallback
 *     branch), which is exactly the promise this function exists to keep.
 *
 * The last rule is not git's at all, it is WINDOWS': a loose ref is a file, and
 * the host config repo is a loose-ref repo on Windows, where `refs/heads/CON`
 * (and the `refs/heads/CON.lock` git writes beside it) cannot be created. git
 * on Linux accepts those names, so only this check keeps the promise for them.
 * The shape rule leaves the name with no "/" in it, so there is exactly one path
 * component to test — its stem, extension or not.
 */
function isValidVmBranch(name) {
  if (typeof name !== "string" || !name) return false;
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name)) return false;
  if (name.includes("..")) return false;
  if (name.endsWith(".")) return false;
  if (name.endsWith(".lock")) return false;
  if (WINDOWS_DEVICE_NAMES.has(name.split(".")[0].toLowerCase())) return false;
  const lower = name.toLowerCase();
  if (RESERVED_VM_BRANCHES.has(lower)) return false;
  // Any other spelling of the default branch is the SAME loose-ref file on
  // Windows (refs/heads/VM == refs/heads/vm), so it would hijack the default
  // instance's store. Only the exact default name is allowed.
  if (lower === DEFAULT_VM_BRANCH && name !== DEFAULT_VM_BRANCH) return false;
  return true;
}

/**
 * Resolve the branch to sync on. Empty/absent means the default instance;
 * anything git could choke on falls back to `vm` with a warning rather than
 * failing the tick (a broken registry entry must not stop config sync).
 */
function resolveVmBranch(name, onWarn) {
  if (name === undefined || name === null || name === "") return DEFAULT_VM_BRANCH;
  if (isValidVmBranch(name)) return name;
  if (onWarn) {
    onWarn(`invalid config-sync branch name "${String(name)}"; falling back to "${DEFAULT_VM_BRANCH}"`);
  }
  return DEFAULT_VM_BRANCH;
}

// ── Git identity args (per-invocation, never global config) ──────────────────

// Identity + hardening flags prefixed onto every git invocation that may create a
// commit. The config dir is a machine-local bookkeeping repo created with
// `git init`, so it inherits the user's GLOBAL git settings — and two of those
// routinely break the engine's headless commits on Windows:
//   • commit.gpgsign=true (a "verified commits" setup) makes every `git commit`
//     fail with a non-zero exit when no signing key is reachable. A cleanly
//     auto-merged `merge --no-ff --no-commit` is then left uncommitted, and the
//     panel reports a phantom "unresolved merge" the user has to commit by hand
//     even though nothing actually conflicted.
//   • a global core.hooksPath pointing at a failing pre-commit hook does the same.
// `-c commit.gpgsign=false -c core.hooksPath=` neutralises both per-invocation, so
// they can't wedge the sync tick regardless of the host's global git config. The
// two flags are inert on non-commit operations (add/merge/reset), so prefixing
// them everywhere is safe.
const GIT_IDENTITY = [
  "-c", "user.name=The Construct",
  "-c", "user.email=construct@construct.local",
  "-c", "commit.gpgsign=false",
  "-c", "core.hooksPath=",
];

// ── Repo init (lazy, idempotent) ─────────────────────────────────────────────

/**
 * Make the config repo commit hermetic and line-ending-stable, idempotently.
 * The GIT_IDENTITY prefix already disables signing/hooks for the ENGINE's own
 * commits; this persists the same repo-locally so the user's manual commits
 * (e.g. resolving a merge in VS Code) in this bookkeeping repo behave the same,
 * and pins LF so the canonical-LF profiles never round-trip through CRLF (which
 * would make every unchanged file look modified and churn the write-back).
 * Best-effort: every step is allowed to fail silently (git absent mid-run, a
 * read-only FS, …) — the per-invocation GIT_IDENTITY flags are the hard guarantee.
 */
async function hardenConfigRepo(runGit, configDir) {
  await runGit(["config", "commit.gpgsign", "false"], { cwd: configDir });
  // Empty hooksPath bypasses any inherited (global) hooks — so the user's own
  // manual commits in this repo (e.g. resolving a merge in VS Code) also can't be
  // broken by a global pre-commit hook, matching the engine's per-invocation guard.
  await runGit(["config", "core.hooksPath", ""], { cwd: configDir });
  await runGit(["config", "core.autocrlf", "false"], { cwd: configDir });
  const ga = path.join(configDir, ".gitattributes");
  // A .gitattributes present in the working tree is honoured by git even while
  // untracked/ignored — it just pins LF as a belt to core.autocrlf=false's braces.
  try { fs.accessSync(ga); }
  catch (_) { try { fs.writeFileSync(ga, "* text=auto eol=lf\n", "utf8"); } catch (_) { /* best-effort */ } }
  // Keep the machine-local bookkeeping files (.gitattributes + the PS engine's
  // .migrated sentinel) out of `git status` and — crucially — out of git's
  // untracked-overwrite guard. Left un-ignored, an untracked root file makes
  // `git merge` refuse ("untracked working tree files would be overwritten"),
  // which the tick then reports as a blocked/phantom "merge conflict". Ignoring
  // them via .git/info/exclude (local, itself untracked) fixes both.
  ensureRepoExclude(configDir, [".gitattributes", ".migrated", SYNC_LOCK_FILE, PROVISION_SYNC_INTENT_FILE]);
}

/** Append the given names to <configDir>/.git/info/exclude if absent. Best-effort. */
function ensureRepoExclude(configDir, names) {
  try {
    const dir = path.join(configDir, ".git", "info");
    const p = path.join(dir, "exclude");
    let cur = "";
    try { cur = fs.readFileSync(p, "utf8"); } catch (_) { /* may not exist yet */ }
    const have = new Set(cur.split(/\r?\n/).map((s) => s.trim()));
    const missing = names.filter((n) => !have.has(n));
    if (!missing.length) return;
    fs.mkdirSync(dir, { recursive: true });
    const prefix = cur && !cur.endsWith("\n") ? "\n" : "";
    fs.appendFileSync(p, prefix + missing.join("\n") + "\n", "utf8");
  } catch (_) { /* best-effort */ }
}

/**
 * Lazy git init per D1: init, add -A, commit, rename branch to main, create the
 * VM branch. Idempotent — if a repo already exists returns {repo:true,
 * initialized:false}. Returns {repo:false} when git is absent.
 *
 * `vmBranch` (default "vm") names the instance's VM-side branch.
 */
async function ensureRepo(runGit, configDir, vmBranch) {
  const branch = resolveVmBranch(vmBranch);
  // Check if repo already exists. We must verify that the discovered repo's
  // toplevel actually IS configDir — `git rev-parse --git-dir` matches any
  // ancestor repo (e.g. a dotfiles repo in %USERPROFILE% that contains
  // %LOCALAPPDATA%), and silently adopting that parent repo would commit config
  // files into the user's unrelated repository.
  const st = await runGit(["rev-parse", "--git-dir"], { cwd: configDir });
  // A repo owned by another Windows account makes git refuse every command
  // ("fatal: detected dubious ownership"). Without this check the failed
  // rev-parse falls through to `git init`, whose later add/commit fail too,
  // and the tick ends with the misleading "git not available" warning. Detect
  // it and report a repairable, named condition instead.
  if (st.code !== 0 && /dubious ownership/i.test(st.stderr || "")) {
    return { repo: false, dubiousOwnership: true };
  }
  if (st.code === 0) {
    const tl = await runGit(["rev-parse", "--show-toplevel"], { cwd: configDir });
    const toplevel = tl.stdout.trim().replace(/\/$/, "");
    const target = configDir.replace(/\/$/, "");
    // On Windows git may return forward-slash paths; normalise for comparison.
    if (path.resolve(toplevel) === path.resolve(target)) {
      // Re-apply the repo-local hardening every run so a repo created before this
      // fix (or one the user re-created) is repaired: signing/hooks off for the
      // user's own commits too, and LF line endings pinned.
      await hardenConfigRepo(runGit, configDir);
      // A NON-default instance branch may not exist yet in a repo that was
      // created for the default VM — create it at main so the tick's ls-tree /
      // read-tree / merge steps have a ref to work with. The default branch is
      // deliberately left alone: when `vm` is missing, today's tick self-heals
      // through the seed path (update-ref refs/heads/vm = main), and that
      // behavior stays exactly as-is for single-VM installs.
      if (branch !== DEFAULT_VM_BRANCH) {
        const have = await runGit(["rev-parse", "--verify", "refs/heads/" + branch], { cwd: configDir });
        if (have.code !== 0) await runGit(["branch", branch, "main"], { cwd: configDir });
      }
      return { repo: true, initialized: false };
    }
    // The repo belongs to an ancestor directory — ignore it and init our own.
  }

  // Try to init.
  const init = await runGit(["init"], { cwd: configDir });
  if (init.code !== 0) {
    return { repo: false, dubiousOwnership: /dubious ownership/i.test(init.stderr || "") };
  }

  // Harden BEFORE the initial commit: sets core.autocrlf=false and drops the
  // .gitattributes into the tree so the initial `add -A` versions it.
  await hardenConfigRepo(runGit, configDir);

  // Initial commit with whatever is in the tree.
  await runGit([...GIT_IDENTITY, "add", "-A"], { cwd: configDir });
  // Exclude reserved profile names from the initial commit (D1/D5), matching
  // the PS Initialize-ConstructConfigRepo behavior (AgentVm.Common.ps1:1756-1758).
  for (const rn of projects.RESERVED_PROFILE_NAMES) {
    await runGit(["reset", "HEAD", "--", "projects/" + rn + ".json"], { cwd: configDir });
  }
  await runGit([...GIT_IDENTITY, "commit", "--allow-empty", "-m", "initial config"], { cwd: configDir });
  // Rename whatever default branch to main.
  await runGit(["branch", "-M", "main"], { cwd: configDir });
  // Create the instance's VM branch at the same point.
  await runGit(["branch", branch], { cwd: configDir });
  return { repo: true, initialized: true };
}

/**
 * Query the current repo state: whether it's a repo, whether there's a
 * conflict or an in-progress merge.
 */
async function repoState(runGit, configDir) {
  const st = await runGit(["rev-parse", "--git-dir"], { cwd: configDir });
  if (st.code !== 0) return { repo: false, conflict: false, conflictFiles: [], mergeInProgress: false };
  // Check for unmerged files (conflict).
  const ls = await runGit(["ls-files", "-u", "--error-unmatch", "--"], { cwd: configDir });
  const unmerged = ls.stdout.trim().split("\n").filter(Boolean);
  const conflict = unmerged.length > 0;
  // Check for MERGE_HEAD (merge in progress).
  const mh = await runGit(["rev-parse", "--verify", "MERGE_HEAD"], { cwd: configDir });
  const mergeInProgress = mh.code === 0;
  // Extract conflicted file names from ls-files -u output.
  const conflictFiles = [];
  const seen = new Set();
  for (const line of unmerged) {
    // Format: mode sha stage\tpath
    const tabIdx = line.indexOf("\t");
    if (tabIdx >= 0) {
      const f = line.slice(tabIdx + 1).trim();
      if (f && !seen.has(f)) { seen.add(f); conflictFiles.push(f); }
    }
  }
  return { repo: true, conflict, conflictFiles, mergeInProgress };
}

/**
 * If a previous tick left a merge in progress but there are no unmerged paths,
 * finish the merge with Construct's per-command git identity. This covers the
 * "clean merge left uncommitted" and "user resolved files but did not commit"
 * cases without depending on VS Code's Git UI or the host's global git identity.
 */
async function completePendingMerge(runGit, configDir, vmBranch) {
  const branch = resolveVmBranch(vmBranch);
  const state = await repoState(runGit, configDir);
  if (!state.repo || !state.mergeInProgress) {
    return { ok: true, completed: false, conflict: false, blocked: false, reason: "" };
  }
  if (state.conflict) {
    return { ok: false, completed: false, conflict: true, blocked: false, reason: "merge conflict in config repo" };
  }

  const valid = validateWorkingTreeProfiles(configDir);
  if (!valid.ok) {
    return {
      ok: false, completed: false, conflict: false, blocked: true,
      reason: "post-merge validation failed: " + valid.errors.join("; "),
    };
  }

  await runGit([...GIT_IDENTITY, "add", "-A"], { cwd: configDir });
  const commit = await runGit([...GIT_IDENTITY, "commit", "-m", "sync merge " + branch], { cwd: configDir });
  if (commit.code !== 0) {
    return {
      ok: false, completed: false, conflict: false, blocked: true,
      reason: "merge commit failed: " + (commit.stderr || commit.stdout || "").trim(),
    };
  }
  return { ok: true, completed: true, conflict: false, blocked: false, reason: "" };
}

// ── VM store read/write scripts ──────────────────────────────────────────────

/**
 * Build the bash script that reads every .json file under the VM store and emits
 * name<TAB>base64 lines, followed by an END sentinel. The sentinel proves
 * completeness (same idiom as buildScanScript). `root` overrides the store path
 * for tests.
 */
function buildReadStoreScript(root) {
  const r = root || "/opt/construct/projects";
  return [
    "set -u",
    "store='" + String(r).replace(/'/g, "'\\''") + "'",
    'if [ -d "$store" ]; then',
    '  for f in "$store"/*.json; do',
    '    [ -f "$f" ] || continue',
    '    name=$(basename "$f" .json)',
    '    data=$(base64 < "$f" | tr -d "\\n")',
    "    printf '%s\\t%s\\n' \"$name\" \"$data\"",
    "  done",
    "else",
    // Emit an explicit marker so the parser can distinguish "store dir absent"
    // (wiped/fresh VM) from "store dir exists but is empty". D13 requires seeding
    // when the store dir does not exist, but treating an existing-but-empty store
    // as a real deletion when the last sync had files.
    "  printf 'STORE_ABSENT\\n'",
    "fi",
    "printf 'END\\n'",
  ].join("\n");
}

/**
 * Parse the read-store script's stdout. Returns an object
 * `{ entries: [{name, content}], storeAbsent: bool }` or null when the END
 * sentinel is missing (partial/failed capture — must not treat as authoritative).
 *
 * `storeAbsent` is true when the script emitted the STORE_ABSENT marker,
 * meaning the store directory itself does not exist on the VM (as opposed to
 * existing but being empty). D13 uses this to distinguish a fresh/wiped VM
 * from a deliberate delete-all.
 */
function parseReadStore(stdout) {
  const lines = String(stdout == null ? "" : stdout).split("\n");
  let sawEnd = false;
  let storeAbsent = false;
  const results = [];
  for (const line of lines) {
    if (line === "END") { sawEnd = true; continue; }
    if (line === "STORE_ABSENT") { storeAbsent = true; continue; }
    const tab = line.indexOf("\t");
    if (tab < 0) continue;
    const name = line.slice(0, tab).trim();
    if (!name) continue;
    const b64 = line.slice(tab + 1).trim();
    let content;
    try { content = Buffer.from(b64, "base64").toString("utf8"); }
    catch (_) { continue; }
    results.push({ name, content });
  }
  return sawEnd ? { entries: results, storeAbsent } : null;
}

/**
 * Plan the write-back from main to the VM store (D6 step 7). Given the current
 * main files and the VM files read this tick, produce the operations needed so
 * that the VM matches main — with guards so each write only proceeds when the
 * VM file hasn't changed since we read it.
 *
 * mainFiles: {name -> content string}
 * vmFiles:   {name -> content string}  (the raw content read this tick; a name
 *            absent here means the file was not on the VM)
 *
 * Returns an array of ops: [{name, action:'write'|'delete', content?, expect}].
 * - write: write content to the VM, guarded by expect (the raw VM content we
 *   read, or null if the file was absent).
 * - delete: remove from the VM, guarded by expect (the raw content at read time).
 */
function planWriteBack({ mainFiles, vmFiles }) {
  const main = mainFiles || {};
  const vm = vmFiles || {};
  const ops = [];
  const allNames = new Set([...Object.keys(main), ...Object.keys(vm)]);
  for (const name of allNames) {
    const onMain = name in main;
    const onVm = name in vm;
    if (onMain && !onVm) {
      // New on main, absent on VM: write with expect=null (absent guard).
      ops.push({ name, action: "write", content: main[name], expect: null });
    } else if (onMain && onVm) {
      // Both exist: write only if main differs from VM.
      if (main[name] !== vm[name]) {
        ops.push({ name, action: "write", content: main[name], expect: vm[name] });
      }
    } else if (!onMain && onVm) {
      // Deleted on main: guarded delete.
      ops.push({ name, action: "delete", expect: vm[name] });
    }
  }
  return ops;
}

/**
 * Build the bash script that performs guarded writes/deletes on the VM store.
 * Each operation checks that the current file content matches the expected
 * value (or that the file is absent when expect is null) before writing.
 * Prints name<TAB>done|skipped per operation, then END.
 *
 * `ops` is the output of planWriteBack. `root` overrides the store path.
 */
function buildWriteStoreScript(ops, root) {
  const r = root || "/opt/construct/projects";
  const lines = [
    "set -u",
    "store='" + String(r).replace(/'/g, "'\\''") + "'",
    'mkdir -p "$store"',
  ];
  for (const op of (ops || [])) {
    const safeName = String(op.name).replace(/'/g, "'\\''");
    const file = '"$store"' + "/'" + safeName + ".json'";
    if (op.action === "write") {
      const dataB64 = Buffer.from(op.content || "", "utf8").toString("base64");
      if (op.expect === null) {
        // Guard: file must be absent.
        lines.push("if [ ! -f " + file + " ]; then");
        lines.push("  printf '%s' '" + dataB64.replace(/'/g, "'\\''") + "' | base64 -d > " + file);
        lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'done'");
        lines.push("else");
        lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'skipped'");
        lines.push("fi");
      } else {
        // Guard: file content must match expected.
        const expectB64 = Buffer.from(op.expect, "utf8").toString("base64");
        lines.push("cur=$(base64 < " + file + " 2>/dev/null | tr -d '\\n')");
        lines.push("if [ \"$cur\" = '" + expectB64.replace(/'/g, "'\\''") + "' ]; then");
        lines.push("  printf '%s' '" + dataB64.replace(/'/g, "'\\''") + "' | base64 -d > " + file);
        lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'done'");
        lines.push("else");
        lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'skipped'");
        lines.push("fi");
      }
    } else if (op.action === "delete") {
      // Guard: file content must match expected before deletion.
      const expectB64 = Buffer.from(op.expect, "utf8").toString("base64");
      lines.push("cur=$(base64 < " + file + " 2>/dev/null | tr -d '\\n')");
      lines.push("if [ \"$cur\" = '" + expectB64.replace(/'/g, "'\\''") + "' ]; then");
      lines.push("  rm -f " + file);
      lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'done'");
      lines.push("else");
      lines.push("  printf '%s\\t%s\\n' '" + safeName + "' 'skipped'");
      lines.push("fi");
    }
  }
  lines.push("printf 'END\\n'");
  return lines.join("\n");
}

/**
 * Parse the write-store script's stdout. Returns {done, skipped} arrays of
 * names, or null when the END sentinel is missing.
 */
function parseWriteResult(stdout) {
  const lines = String(stdout == null ? "" : stdout).split("\n");
  let sawEnd = false;
  const done = [];
  const skipped = [];
  for (const line of lines) {
    if (line === "END") { sawEnd = true; continue; }
    const tab = line.indexOf("\t");
    if (tab < 0) continue;
    const name = line.slice(0, tab).trim();
    const status = line.slice(tab + 1).trim();
    if (!name) continue;
    if (status === "done") done.push(name);
    else if (status === "skipped") skipped.push(name);
  }
  return sawEnd ? { done, skipped } : null;
}

// ── Sync tick (D6 steps 1-8) ─────────────────────────────────────────────────

/**
 * The sync tick, serialized by the cross-process lock. When another engine
 * (a second VS Code window, or the PowerShell engine during a provision) holds
 * the lock, the tick is skipped — `{ok:true, ran:false, lockBusy:true}` — and
 * the next timer tick simply retries. Never throws on lock contention.
 *
 * readStore:  () => Promise<string|null>  (raw stdout; null = SSH unreachable)
 * writeStore: (script) => Promise<string|null>
 * log:        (level, msg) => void
 * vmBranch:   optional VM-side branch (default "vm" = the default instance)
 */
async function syncTick(opts) {
  if (provisionSyncPending(opts.configDir)) {
    if (opts.log) opts.log("info", "provisioning sync is pending; extension tick yielded");
    return {
      ok: true, ran: false, lockBusy: true, conflict: false, blocked: false,
      blockedReason: null, reason: "provision-pending", skippedInvalid: [], merged: false,
      seeded: false, writeBack: { done: [], skipped: [] }, warnings: [], vmReadOk: null,
    };
  }
  const lockToken = acquireSyncLock(opts.configDir);
  if (!lockToken) {
    if (opts.log) opts.log("info", "sync lock held by another window/engine; tick skipped");
    return {
      ok: true, ran: false, lockBusy: true, conflict: false, blocked: false,
      blockedReason: null, reason: "lock-busy", skippedInvalid: [], merged: false, seeded: false,
      writeBack: { done: [], skipped: [] }, warnings: [], vmReadOk: null,
    };
  }
  try { return await syncTickLocked(opts); }
  finally { releaseSyncLock(opts.configDir, lockToken); }
}

/** The core tick body. Implements D6 steps 1-8 exactly. Callers go through
 *  syncTick (the lock); tests may call this directly to bypass it. */
async function syncTickLocked({ runGit, configDir, readStore, writeStore, log, storeRoot, vmBranch }) {
  const warn = (msg) => log && log("warn", msg);
  const info = (msg) => log && log("info", msg);
  // vmReadOk: did the VM-store read succeed this tick? true/false once the read
  // was attempted, null when the tick never got that far (lock busy, repo init
  // failure, conflict). Provisioning treats false as fatal when the host has
  // profiles to seed — a silent read failure is how a fresh install ends up
  // with an empty store and zero cloned repos.
  const result = {
    ok: false, ran: false, conflict: false, blocked: false, blockedReason: null,
    skippedInvalid: [], merged: false, seeded: false,
    writeBack: { done: [], skipped: [] }, warnings: [], vmReadOk: null,
  };
  const addWarning = (msg) => { result.warnings.push(msg); warn(msg); };

  // The instance's VM-side branch. Every ref this tick touches is derived from
  // it; an unusable name degrades to the default rather than failing the tick.
  const branch = resolveVmBranch(vmBranch, addWarning);

  // Step 1: ensure repo.
  const repo = await ensureRepo(runGit, configDir, branch);
  if (!repo.repo) {
    if (repo.dubiousOwnership) {
      // Surface as blocked (not a tooltip warning): the panel's banner shows
      // blockedReason, and this state is persistent + user-fixable.
      result.blocked = true;
      result.blockedReason =
        "config folder is owned by another Windows account, so git refuses to sync it — " +
        'reprovision (it repairs ownership automatically) or run from an elevated prompt: icacls "' +
        configDir + '" /setowner "%USERNAME%" /T';
      addWarning(result.blockedReason);
      return result;
    }
    addWarning("git not available; sync skipped");
    return result;
  }

  // Check for existing conflict/merge state. If the merge is already resolved
  // (or was clean but left uncommitted), complete it automatically before the
  // tick continues to write-back/advance refs.
  const pending = await completePendingMerge(runGit, configDir, branch);
  if (pending.completed) result.merged = true;
  if (!pending.ok) {
    result.conflict = pending.conflict;
    result.blocked = pending.blocked || !pending.conflict;
    result.blockedReason = pending.reason || "unresolved merge in config repo";
    return result;
  }
  const state = await repoState(runGit, configDir);
  if (state.conflict || state.mergeInProgress) {
    result.conflict = state.conflict;
    result.blocked = true;
    result.blockedReason = "unresolved merge in config repo";
    return result;
  }

  result.ran = true;

  // Step 2: commit host-side dirty files under projects/ onto main.
  await commitHostDirtyFiles(runGit, configDir, result);

  // Step 3: read VM store.
  const rawStdout = await readStore();
  const vmParsed = parseReadStore(rawStdout);
  if (vmParsed === null) {
    // SSH unreachable or truncated — skip VM side, return partial success.
    addWarning("could not read VM store (SSH unreachable or truncated)");
    result.vmReadOk = false;
    result.ok = true;
    return result;
  }
  result.vmReadOk = true;

  const vmStoreAbsent = vmParsed.storeAbsent;

  // Step 4: validate each VM file. Build maps of valid entries.
  // vmRaw: name->raw content (for write-back guards).
  // vmValid: name->canonicalized content (for committing to the vm branch).
  const vmRaw = {};
  const vmValid = {};
  for (const entry of vmParsed.entries) {
    if (projects.isReservedProfileName(entry.name)) {
      // Reserved names are dead files by design (default always resolves to the
      // shipped copy) and pre-v2 provisions seeded default.json into the store,
      // so a leftover is the normal state on upgraded VMs — log it, but don't
      // raise a permanent panel warning the user can't act on.
      info(`reserved name "${entry.name}" in VM store ignored`);
      continue;
    }
    let obj;
    try { obj = JSON.parse(entry.content); } catch (_) {
      result.skippedInvalid.push({ name: entry.name, reason: "invalid JSON" });
      addWarning(`invalid JSON in VM profile "${entry.name}"; skipped`);
      continue;
    }
    const v = projects.validateProfile(entry.name, obj);
    if (!v.ok) {
      result.skippedInvalid.push({ name: entry.name, reason: v.errors.join("; ") });
      addWarning(`invalid VM profile "${entry.name}": ${v.errors.join("; ")}; skipped`);
      continue;
    }
    vmRaw[entry.name] = entry.content;
    // Canonicalize for the vm branch commit.
    vmValid[entry.name] = projects.canonicalProfileJson(entry.name, obj);
  }

  // D13: fresh-VM seed path. Implements the spec literally:
  //   - Store dir absent (wiped/fresh VM): ALWAYS seed from main.
  //   - vm branch tip has 0 profiles: seed (first-ever sync).
  //   - Store dir EXISTS but is empty: only seed when the vm tip also has 0
  //     profiles (first sync). Otherwise treat as a real deletion — the user
  //     deleted all profiles on the VM intentionally (the store dir still exists
  //     and the last sync had files on the vm branch).
  //
  // The previous heuristic (vmRef === mainRef) was wrong: after any host-only
  // commit (e.g. the P2 add-remote flow calling writeRemotes + commitAll) main
  // advances past vm, making the predicate false, and the next tick with an
  // absent store would commit a mass-deletion vm commit and merge it into main.
  const vmTipProfiles = await countVmBranchProfiles(runGit, configDir, branch);
  const noValidVmFiles = Object.keys(vmValid).length === 0;
  const freshVm = vmStoreAbsent
    ? noValidVmFiles    // store dir absent: seed unless valid files were somehow read (shouldn't happen)
    : (noValidVmFiles && vmTipProfiles === 0); // store dir exists but empty: seed only on first-ever sync

  if (freshVm) {
    info("fresh VM detected; seeding from main");
    result.seeded = true;
    // Build main file map.
    const mainFiles = readMainProfiles(configDir);
    // Write all main profiles with expect-absent guard.
    const ops = Object.keys(mainFiles).map((name) => ({
      name, action: "write", content: mainFiles[name], expect: null,
    }));
    if (ops.length > 0) {
      const script = buildWriteStoreScript(ops, storeRoot);
      const wbStdout = await writeStore(script);
      const wb = parseWriteResult(wbStdout);
      if (wb) {
        result.writeBack = wb;
        if (wb.skipped.length > 0) {
          addWarning(`seed write-back skipped concurrently changed profile(s): ${wb.skipped.join(", ")}; sync base not advanced`);
          result.ok = true;
          return result;
        }
      } else {
        addWarning("write-back to VM store failed; vm ref not advanced");
        result.ok = true;
        return result;
      }
    }
    await runGit(["update-ref", "refs/heads/" + branch, "refs/heads/main"], { cwd: configDir });
    result.ok = true;
    return result;
  }

  // Mass-deletion guard: the store EXISTS but yielded zero valid profiles while
  // the vm branch has some. The spec read this as "the user deleted every
  // profile on the VM intentionally" — but in practice it is far more likely a
  // half-provisioned store (dir created before seeding finished) or a store
  // whose files all failed validation, and propagating it would wipe main
  // (observed in the field: a reinstall-morning tick mass-deleted profiles that
  // then kept resurrecting). Deleting ALL profiles via sync is never worth that
  // risk: skip the VM side with a warning; individual deletions still propagate
  // normally, and a real delete-all can be done per profile or from the panel.
  if (noValidVmFiles && vmTipProfiles > 0) {
    addWarning(`VM store has no valid profiles but the ${branch} branch has ${vmTipProfiles}; refusing to propagate a mass deletion (delete profiles individually if intended)`);
    // ...but do NOT stall: a store dir that exists yet holds zero valid profiles
    // is, in the field, a rebuilt/wiped VM (provisioning recreates the dir before
    // the first seed can run) — leaving it empty made every subsequent tick
    // refuse here forever, so profiles never returned to the VM at all. Re-seed
    // main's profiles with expect-absent guards: only files missing from the VM
    // are written (a lingering invalid file keeps its warning and is preserved),
    // main is untouched, and the vm ref is NOT advanced, so nothing about the
    // refused deletion is committed.
    const seedFiles = readMainProfiles(configDir);
    const seedOps = Object.keys(seedFiles).map((name) => ({
      name, action: "write", content: seedFiles[name], expect: null,
    }));
    if (seedOps.length > 0) {
      const seedStdout = await writeStore(buildWriteStoreScript(seedOps, storeRoot));
      const seedWb = parseWriteResult(seedStdout);
      if (seedWb) {
        result.writeBack = seedWb;
        result.seeded = seedWb.done.length > 0;
        info(`re-seeded ${seedWb.done.length} profile(s) into the emptied VM store`);
      } else {
        addWarning("re-seed write-back to the VM store failed");
      }
    }
    result.ok = true;
    return result;
  }

  // Step 5: commit VM snapshot onto the vm branch via a temp index.
  // Names that were read from the VM but skipped as INVALID must NOT be treated
  // as deletions: an invalid file (e.g. a half-written agent edit) is skipped and
  // "never enters the repo" (spec §6.2) — the vm branch keeps its last agreed-valid
  // copy for that name, so the merge doesn't propagate a spurious deletion to main
  // and wipe a previously-synced profile. Only names genuinely ABSENT from the VM
  // read are deletions. (A reserved name in the store is also preserved rather than
  // deleted, matching the "ignored, not deleted" rule for a stale default.json.)
  const preserveNames = new Set([
    ...result.skippedInvalid.map((s) => s.name),
    ...vmParsed.entries.filter((e) => projects.isReservedProfileName(e.name)).map((e) => e.name),
  ]);
  const vmChanged = await commitVmBranch(runGit, configDir, vmValid, preserveNames, branch);

  // Step 6: merge vm into main.
  // First check if merge is needed.
  const baseCheck = await runGit(["merge-base", "--is-ancestor", branch, "main"], { cwd: configDir });
  if (baseCheck.code === 0 && !vmChanged) {
    // vm is already an ancestor of main and nothing changed — nothing to merge.
    // Still do write-back for any drifted files.
    const mainFiles = readMainProfiles(configDir);
    const ops = planWriteBack({ mainFiles, vmFiles: vmRaw });
    if (ops.length > 0) {
      const script = buildWriteStoreScript(ops, storeRoot);
      const wbStdout = await writeStore(script);
      const wb = parseWriteResult(wbStdout);
      if (wb) {
        result.writeBack = wb;
        if (wb.skipped.length > 0) {
          addWarning(`write-back skipped concurrently changed profile(s): ${wb.skipped.join(", ")}; sync base not advanced`);
          result.ok = true;
          return result;
        }
      } else {
        addWarning("write-back to VM store failed; vm ref not advanced");
        result.ok = true;
        return result;
      }
    }
    await runGit(["update-ref", "refs/heads/" + branch, "refs/heads/main"], { cwd: configDir });
    result.ok = true;
    return result;
  }

  // Also check: are trees identical? (vm == main tree-wise)
  const mainTree = await runGit(["rev-parse", "main^{tree}"], { cwd: configDir });
  const vmTree = await runGit(["rev-parse", branch + "^{tree}"], { cwd: configDir });
  if (mainTree.stdout.trim() === vmTree.stdout.trim()) {
    // Trees are identical — nothing to merge.
    const mainFiles = readMainProfiles(configDir);
    const ops = planWriteBack({ mainFiles, vmFiles: vmRaw });
    if (ops.length > 0) {
      const script = buildWriteStoreScript(ops, storeRoot);
      const wbStdout = await writeStore(script);
      const wb = parseWriteResult(wbStdout);
      if (wb) result.writeBack = wb;
    }
    result.ok = true;
    return result;
  }

  // Perform the merge.
  const mergeResult = await runGit(
    [...GIT_IDENTITY, "merge", "--no-ff", "--no-commit", branch],
    { cwd: configDir }
  );

  if (mergeResult.code !== 0) {
    // Check if it's a conflict.
    const postState = await repoState(runGit, configDir);
    if (postState.conflict) {
      result.conflict = true;
      addWarning("merge conflict in config repo");
      return result;
    }
    // D6 step 2: when an invalid host file was left uncommitted and the merge
    // touches it, git refuses with "local changes would be overwritten". This
    // must surface as blocked so P2's UI can show the blocked state.
    const mergeStderr = mergeResult.stderr || "";
    if (mergeStderr.includes("would be overwritten") || mergeStderr.includes("not possible because you have unmerged files")) {
      result.blocked = true;
      result.blockedReason = "merge refused: uncommitted changes in projects/ would be overwritten — fix or remove the invalid file and retry";
      addWarning(result.blockedReason);
      return result;
    }
    // Some other merge failure (maybe already up to date with a non-zero code?).
    // Try to abort and report.
    await runGit(["merge", "--abort"], { cwd: configDir });
    addWarning("merge failed: " + mergeResult.stderr.trim());
    result.blocked = true;
    result.blockedReason = "merge failed: " + mergeResult.stderr.trim();
    return result;
  }

  // Post-merge validation gate (step 6 cont.): validate every projects/*.json
  // in the working tree. Any invalid => leave uncommitted, return blocked.
  const postMergeValid = validateWorkingTreeProfiles(configDir);
  if (!postMergeValid.ok) {
    result.blocked = true;
    result.blockedReason = "post-merge validation failed: " + postMergeValid.errors.join("; ");
    addWarning(result.blockedReason);
    return result;
  }

  // Commit the merge.
  const mergeCommit = await runGit([...GIT_IDENTITY, "commit", "-m", "sync merge " + branch], { cwd: configDir });
  if (mergeCommit.code !== 0) {
    result.blocked = true;
    result.blockedReason = "merge commit failed: " + (mergeCommit.stderr || mergeCommit.stdout || "").trim();
    addWarning(result.blockedReason);
    return result;
  }
  result.merged = true;

  // Step 7: guarded write-back.
  let writeBackRan = true;
  const mainFiles = readMainProfiles(configDir);
  const ops = planWriteBack({ mainFiles, vmFiles: vmRaw });
  if (ops.length > 0) {
    const script = buildWriteStoreScript(ops, storeRoot);
    const wbStdout = await writeStore(script);
    const wb = parseWriteResult(wbStdout);
    if (wb) {
      result.writeBack = wb;
      if (wb.skipped.length > 0) {
        writeBackRan = false;
        addWarning(`write-back skipped concurrently changed profile(s): ${wb.skipped.join(", ")}; sync base not advanced`);
      }
    } else {
      writeBackRan = false;
      addWarning("write-back to VM store failed; vm ref not advanced");
    }
  }

  // Step 8: fast-forward vm to main — only when write-back succeeded. Mirrors
  // the PS engine's gate (AgentVm.Common.ps1:3043-3050): if write-back failed
  // the vm ref stays behind so the next tick retries the merge+write-back
  // instead of silently losing the merged content.
  if (writeBackRan) {
    await runGit(["update-ref", "refs/heads/" + branch, "refs/heads/main"], { cwd: configDir });
  }

  result.ok = true;
  return result;
}

// ── Sync tick helpers ────────────────────────────────────────────────────────

/** Commit any dirty files under projects/ on main. Invalid files are left
 *  uncommitted with a warning. */
async function commitHostDirtyFiles(runGit, configDir, result) {
  const addWarning = (msg) => { result.warnings.push(msg); };
  // Check for dirty projects/ files.
  const diff = await runGit(["diff", "--name-only", "--", "projects/"], { cwd: configDir });
  const untracked = await runGit(["ls-files", "--others", "--exclude-standard", "--", "projects/"], { cwd: configDir });
  const allDirty = [...diff.stdout.trim().split("\n"), ...untracked.stdout.trim().split("\n")].filter(Boolean);
  if (allDirty.length === 0) return;

  // Validate each dirty file; stage only valid ones.
  const toStage = [];
  for (const relPath of allDirty) {
    if (!relPath.startsWith("projects/") || !relPath.endsWith(".json")) continue;
    const base = path.basename(relPath, ".json");
    if (projects.isReservedProfileName(base)) continue;
    const absPath = path.join(configDir, relPath);
    let content;
    try { content = fs.readFileSync(absPath, "utf8"); } catch (_) { continue; }
    let obj;
    try { obj = JSON.parse(content); } catch (_) {
      addWarning(`invalid JSON in host file "${base}"; left uncommitted`);
      continue;
    }
    const v = projects.validateProfile(base, obj);
    if (!v.ok) {
      addWarning(`invalid host profile "${base}": ${v.errors.join("; ")}; left uncommitted`);
      continue;
    }
    toStage.push(relPath);
  }
  // Also check for deleted files to stage.
  const deleted = await runGit(["diff", "--name-only", "--diff-filter=D", "--", "projects/"], { cwd: configDir });
  const deletedFiles = deleted.stdout.trim().split("\n").filter(Boolean);
  toStage.push(...deletedFiles);

  if (toStage.length > 0) {
    await runGit([...GIT_IDENTITY, "add", "--", ...toStage], { cwd: configDir });
    // Only commit if there are staged changes.
    const staged = await runGit(["diff", "--cached", "--name-only"], { cwd: configDir });
    if (staged.stdout.trim()) {
      await runGit([...GIT_IDENTITY, "commit", "-m", "host config update"], { cwd: configDir });
    }
  }
}

/** Count the number of projects/*.json files on the VM branch tip. */
async function countVmBranchProfiles(runGit, configDir, vmBranch) {
  const branch = resolveVmBranch(vmBranch);
  const ls = await runGit(["ls-tree", "--name-only", branch, "projects/"], { cwd: configDir });
  if (ls.code !== 0) return 0;
  const files = ls.stdout.trim().split("\n").filter((f) => f && f.endsWith(".json"));
  return files.length;
}

/**
 * Commit the VM snapshot onto the instance's VM branch using a temp index so the
 * working tree (checked out on main) is never disturbed. Returns true if a new
 * commit was created (the tree differs from the branch-tip tree).
 */
async function commitVmBranch(runGit, configDir, vmValid, preserveNames, vmBranch) {
  const branch = resolveVmBranch(vmBranch);
  const preserve = preserveNames instanceof Set ? preserveNames : new Set(preserveNames || []);
  const tmpIndex = path.join(configDir, ".git", "tmp-vm-index");
  try { fs.unlinkSync(tmpIndex); } catch (_) { /* ok */ }

  const envOverride = { GIT_INDEX_FILE: tmpIndex };

  // Read the current vm tree into the temp index.
  await runGit(["read-tree", branch], { cwd: configDir, env: envOverride });

  // Remove projects/* entries from the temp index so the tree is rebuilt from the
  // fresh VM read — EXCEPT names in `preserve` (skipped-invalid or reserved), whose
  // last agreed-valid vm-branch blob is kept as-is so they aren't misread as
  // deletions. We use `git update-index --force-remove` per entry instead of
  // `git rm --cached -r` because git-rm's staged-content safety check can refuse
  // when a blob differs from both HEAD and the working tree (e.g. after conflict
  // resolution), silently dropping VM-side deletions and resurrecting deleted profiles.
  const lsIdx = await runGit(["ls-files", "--cached", "--", "projects/"], { cwd: configDir, env: envOverride });
  const existingEntries = lsIdx.stdout.trim().split("\n").filter(Boolean);
  for (const entry of existingEntries) {
    // entry looks like "projects/<name>.json"; map back to the profile name.
    const base = entry.slice("projects/".length);
    const entryName = base.endsWith(".json") ? base.slice(0, -".json".length) : base;
    if (preserve.has(entryName)) continue; // keep the last valid copy; not a deletion
    const rmRes = await runGit(["update-index", "--force-remove", "--", entry], { cwd: configDir, env: envOverride });
    if (rmRes.code !== 0) {
      // Should not happen — force-remove unconditionally drops the entry — but
      // if it does, the tree will be wrong and the commit comparison will catch it.
    }
  }

  // Add each valid VM file.
  for (const name of Object.keys(vmValid)) {
    // Defense-in-depth: reject any name whose basename differs from the raw name
    // (i.e. it contains path separators or traversal sequences). validateProfile
    // already rejects such names, but this prevents filesystem damage if a future
    // caller bypasses validation.
    if (path.basename(name) !== name || name === "." || name === "..") continue;
    // Write the blob via a temp file. Use a random filename instead of
    // interpolating the untrusted profile name into the path (security: prevents
    // VM-to-host arbitrary file overwrite via crafted name with ../ sequences).
    const tmpFile = path.join(configDir, ".git", "tmp-blob-" + crypto.randomBytes(8).toString("hex"));
    fs.writeFileSync(tmpFile, vmValid[name], "utf8");
    const ho = await runGit(["hash-object", "-w", "--", tmpFile], { cwd: configDir });
    try { fs.unlinkSync(tmpFile); } catch (_) { /* ok */ }
    const sha = ho.stdout.trim();
    if (!sha) continue;
    await runGit(
      ["update-index", "--add", "--cacheinfo", "100644," + sha + ",projects/" + name + ".json"],
      { cwd: configDir, env: envOverride }
    );
  }

  // Write the tree.
  const wt = await runGit(["write-tree"], { cwd: configDir, env: envOverride });
  const newTree = wt.stdout.trim();

  // Compare with the vm-tip tree.
  const vmTipTree = await runGit(["rev-parse", branch + "^{tree}"], { cwd: configDir });
  if (newTree === vmTipTree.stdout.trim()) {
    // No change.
    try { fs.unlinkSync(tmpIndex); } catch (_) { /* ok */ }
    return false;
  }

  // Commit the new tree as a child of the vm tip.
  const vmTip = await runGit(["rev-parse", branch], { cwd: configDir });
  const ct = await runGit(
    [...GIT_IDENTITY, "commit-tree", newTree, "-p", vmTip.stdout.trim(), "-m", branch + " sync"],
    { cwd: configDir }
  );
  const newCommit = ct.stdout.trim();
  await runGit(["update-ref", "refs/heads/" + branch, newCommit], { cwd: configDir });

  try { fs.unlinkSync(tmpIndex); } catch (_) { /* ok */ }
  return true;
}

/** Read all projects/*.json from the working tree (main) as {name->content}. */
function readMainProfiles(configDir) {
  const dir = path.join(configDir, "projects");
  const map = {};
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); }
  catch (_) { return map; }
  for (const e of entries) {
    if (!e.isFile() || !e.name.endsWith(".json")) continue;
    const base = e.name.slice(0, -5);
    if (projects.isReservedProfileName(base)) continue;
    if (e.name === "project.schema.json") continue;
    try { map[base] = fs.readFileSync(path.join(dir, e.name), "utf8"); }
    catch (_) { /* skip */ }
  }
  return map;
}

/**
 * Return repository identities belonging to profiles that were intentionally
 * deleted from the host/VM history and are still absent now. Auto-discovery
 * must not recreate these merely because their checkout directory remains on
 * the VM. Re-creating a profile explicitly removes it from this effective
 * tombstone set because `currentNames` then contains the name again.
 */
async function deletedProfileIdentities(runGit, configDir, currentNames) {
  const current = new Set(Array.from(currentNames || [], (n) => String(n).toLowerCase()));
  const paths = new Set();
  for (const args of [
    ["diff", "--name-only", "--diff-filter=D", "--", "projects/"],
    ["log", "--all", "--name-only", "--pretty=format:", "--diff-filter=D", "--", "projects/"],
  ]) {
    const r = await runGit(args, { cwd: configDir });
    for (const line of String(r.stdout || "").split(/\r?\n/)) {
      const rel = line.trim().replace(/\\/g, "/");
      if (/^projects\/[^/]+\.json$/.test(rel)) paths.add(rel);
    }
  }

  const names = new Set();
  const urls = new Set();
  for (const rel of paths) {
    const name = path.basename(rel, ".json");
    if (current.has(name.toLowerCase()) || projects.isReservedProfileName(name)) continue;
    names.add(name);

    // Recover the most recent valid pre-deletion content so renamed profiles
    // also suppress the same repository URL. Names alone cover the common
    // auto-import case, so history-read failures remain harmless.
    const commits = await runGit(["log", "--all", "--max-count=20", "--format=%H", "--", rel], { cwd: configDir });
    let found = false;
    for (const commit of String(commits.stdout || "").split(/\r?\n/).filter(Boolean)) {
      for (const spec of [commit + ":" + rel, commit + "^:" + rel]) {
        const shown = await runGit(["show", spec], { cwd: configDir });
        if (shown.code !== 0 || !shown.stdout) continue;
        try {
          const profile = JSON.parse(shown.stdout);
          for (const repo of (Array.isArray(profile.repos) ? profile.repos : [])) {
            if (repo && typeof repo.url === "string" && repo.url.trim()) urls.add(repo.url.trim());
          }
          found = true;
        } catch (_) { /* try the next historical blob */ }
        if (found) break;
      }
      if (found) break;
    }
  }
  return { names, urls };
}

/** Validate every projects/*.json in the working tree. Returns {ok, errors}. */
function validateWorkingTreeProfiles(configDir) {
  const dir = path.join(configDir, "projects");
  const errors = [];
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); }
  catch (_) { return { ok: true, errors }; }
  for (const e of entries) {
    if (!e.isFile() || !e.name.endsWith(".json")) continue;
    const base = e.name.slice(0, -5);
    if (projects.isReservedProfileName(base)) continue;
    if (e.name === "project.schema.json") continue;
    let content;
    try { content = fs.readFileSync(path.join(dir, e.name), "utf8"); }
    catch (_) { errors.push(`cannot read ${e.name}`); continue; }
    let obj;
    try { obj = JSON.parse(content); }
    catch (_) { errors.push(`${base}: invalid JSON`); continue; }
    const v = projects.validateProfile(base, obj);
    if (!v.ok) errors.push(`${base}: ${v.errors.join("; ")}`);
  }
  return { ok: errors.length === 0, errors };
}

// ── Remotes (D16/D17/D19) ───────────────────────────────────────────────────

/**
 * Read the linked remote repos list from manifest/remotes.json.
 * Returns [{url}] or [] if absent.
 */
function readRemotes(configDir) {
  const p = path.join(configDir, "manifest", "remotes.json");
  try {
    const raw = fs.readFileSync(p, "utf8");
    const arr = JSON.parse(raw);
    return Array.isArray(arr) ? arr.filter((e) => e && typeof e.url === "string") : [];
  } catch (_) { return []; }
}

/**
 * Write the linked remotes list to manifest/remotes.json.
 */
function writeRemotes(configDir, list) {
  const dir = path.join(configDir, "manifest");
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, "remotes.json"), JSON.stringify(list, null, 2) + "\n", "utf8");
}

/**
 * Slug a URL for use as a cache directory name: replace every character
 * outside [A-Za-z0-9._-] with a hyphen. Identical rule in JS and PS (D2).
 */
function remoteSlug(url) {
  return String(url || "").replace(/[^A-Za-z0-9._-]/g, "-");
}

/**
 * The cache root for staging clones: <LOCALAPPDATA||TEMP>/The-Construct/cache/config-remotes.
 */
function stagingRoot(env) {
  const base = (env || process.env).LOCALAPPDATA || (env || process.env).TEMP || "";
  return base ? path.join(base, "The-Construct", "cache", "config-remotes") : "";
}

/**
 * Clone or fetch+hard-reset a staging clone for a remote config repo (D2).
 * Returns {dir, ok, error?}.
 */
async function ensureStagingClone(runGit, stagingRootDir, url) {
  const slug = remoteSlug(url);
  const dir = path.join(stagingRootDir, slug);
  fs.mkdirSync(dir, { recursive: true });

  // Check if it's already a repo. Guard against ancestor repos: verify that the
  // discovered repo's toplevel equals our target dir — otherwise a parent repo
  // (e.g. a dotfiles git repo) would be fetched/reset instead of our staging clone.
  const check = await runGit(["rev-parse", "--git-dir"], { cwd: dir });
  if (check.code === 0) {
    const tl = await runGit(["rev-parse", "--show-toplevel"], { cwd: dir });
    const toplevel = tl.stdout.trim().replace(/\/$/, "");
    const target = dir.replace(/\/$/, "");
    if (path.resolve(toplevel) !== path.resolve(target)) {
      // The repo belongs to an ancestor directory — treat as a fresh clone target.
      const parentDir = path.dirname(dir);
      const cloneResult = await runGit(["clone", url, slug], { cwd: parentDir, timeoutMs: 60000 });
      if (cloneResult.code !== 0) return { dir, ok: false, error: "clone failed (ancestor repo detected): " + cloneResult.stderr.trim() };
      return { dir, ok: true };
    }
    // Fetch and hard-reset to the default branch.
    const fetch = await runGit(["fetch", "origin"], { cwd: dir, timeoutMs: 60000 });
    if (fetch.code !== 0) return { dir, ok: false, error: "fetch failed: " + fetch.stderr.trim() };
    // Determine default branch.
    const symref = await runGit(["symbolic-ref", "refs/remotes/origin/HEAD"], { cwd: dir });
    let branch = "main";
    if (symref.code === 0) {
      const ref = symref.stdout.trim();
      branch = ref.replace(/^refs\/remotes\/origin\//, "");
    }
    const reset = await runGit(["reset", "--hard", "origin/" + branch], { cwd: dir });
    if (reset.code !== 0) return { dir, ok: false, error: "reset failed: " + reset.stderr.trim() };
    return { dir, ok: true };
  }

  // Fresh clone.
  const parentDir = path.dirname(dir);
  const cloneResult = await runGit(["clone", url, slug], { cwd: parentDir, timeoutMs: 60000 });
  if (cloneResult.code !== 0) return { dir, ok: false, error: "clone failed: " + cloneResult.stderr.trim() };
  return { dir, ok: true };
}

/**
 * List import candidates in a directory (D16): files matching projects/*.json
 * if that subdir exists, else top-level *.json. Excludes reserved names and
 * *.sample. Returns [{name, relPath}].
 */
function listImportCandidates(dir) {
  const results = [];
  const projDir = path.join(dir, "projects");
  let useProjects = false;
  try { useProjects = fs.statSync(projDir).isDirectory(); } catch (_) { /* nope */ }

  const scanDir = useProjects ? projDir : dir;
  const prefix = useProjects ? "projects/" : "";
  let entries;
  try { entries = fs.readdirSync(scanDir, { withFileTypes: true }); }
  catch (_) { return results; }
  for (const e of entries) {
    if (!e.isFile()) continue;
    const lower = e.name.toLowerCase();
    if (!lower.endsWith(".json")) continue;
    if (lower.endsWith(".sample")) continue;
    const base = e.name.slice(0, -5);
    if (projects.isReservedProfileName(base)) continue;
    if (e.name === "project.schema.json") continue;
    results.push({ name: base, relPath: prefix + e.name });
  }
  return results;
}

/**
 * Read all manifest/<name>.json files (except remotes.json) as
 * {name -> manifestEntry}.
 */
function readImportManifest(configDir) {
  const dir = path.join(configDir, "manifest");
  const map = {};
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); }
  catch (_) { return map; }
  for (const e of entries) {
    if (!e.isFile() || !e.name.endsWith(".json")) continue;
    if (e.name === "remotes.json") continue;
    const base = e.name.slice(0, -5);
    try {
      const raw = fs.readFileSync(path.join(dir, e.name), "utf8");
      map[base] = JSON.parse(raw);
    } catch (_) { /* skip */ }
  }
  return map;
}

/**
 * Plan an upstream import (D17): given the selected files, the existing
 * manifest, and the existing profile names, decide which are creates, updates
 * (same provenance), or collisions (name taken, different provenance).
 *
 * selected: [{remoteUrl, ref, relPath, name, content}]
 * manifest: {name -> manifestEntry}
 * existingNames: string[] of profile names already on disk
 *
 * Returns { creates, updates, collisions }.
 */
function planUpstreamImport({ selected, manifest, existingNames }) {
  const creates = [];
  const updates = [];
  const collisions = [];
  const existing = new Set(existingNames || []);
  const man = manifest || {};

  for (const sel of (selected || [])) {
    const name = sel.name;
    // Check if there's an existing manifest entry with same provenance.
    if (man[name] && man[name].remoteUrl === sel.remoteUrl && man[name].pathInRemote === sel.relPath) {
      // Same provenance → update with 3-way merge.
      updates.push({
        name,
        baseContent: null, // caller reads from bases/<name>.json
        theirsContent: sel.content,
        manifestEntry: {
          remoteUrl: sel.remoteUrl, ref: sel.ref, pathInRemote: sel.relPath,
          importedAs: name,
        },
      });
    } else if (existing.has(name)) {
      // Name collision with different provenance.
      let suggested = name + "-2";
      let n = 2;
      while (existing.has(suggested)) { n++; suggested = name + "-" + n; }
      collisions.push({ name, suggested });
    } else {
      // New profile.
      creates.push({
        name,
        content: sel.content,
        manifestEntry: {
          remoteUrl: sel.remoteUrl, ref: sel.ref, pathInRemote: sel.relPath,
          importedAs: name,
        },
      });
      existing.add(name); // Prevent duplicate creates in the same batch.
    }
  }
  return { creates, updates, collisions };
}

/**
 * 3-way merge via `git merge-file -p` using temp files. Returns
 * {ok, content, conflict}. content is null on conflict.
 */
async function mergeFile(runGit, { ours, base, theirs }) {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-merge-"));
  try {
    const oursPath = path.join(tmpDir, "ours");
    const basePath = path.join(tmpDir, "base");
    const theirsPath = path.join(tmpDir, "theirs");
    fs.writeFileSync(oursPath, ours || "", "utf8");
    fs.writeFileSync(basePath, base || "", "utf8");
    fs.writeFileSync(theirsPath, theirs || "", "utf8");
    const r = await runGit(["merge-file", "-p", oursPath, basePath, theirsPath], { cwd: tmpDir });
    if (r.code === 0) return { ok: true, content: r.stdout, conflict: false };
    if (r.code > 0) return { ok: false, content: null, conflict: true }; // conflict markers
    return { ok: false, content: null, conflict: false }; // unexpected error
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

/**
 * Stage + commit everything in the config dir. Returns {ok, committed, output}.
 *
 * Every step is checked. An unchecked `add` followed by an unchecked
 * `diff --cached` reads a broken repo as "nothing to commit" and reports success,
 * which is exactly how a caller ends up telling the user their work was recorded
 * when it was not. `output` carries the (redacted) git message on any failure, so
 * a caller pointing at the log has something to show there.
 */
async function commitAll(runGit, configDir, message) {
  const add = await runGit([...GIT_IDENTITY, "add", "-A"], { cwd: configDir });
  if (add.code !== 0) return { ok: false, committed: false, output: redactGitOutput("git add failed: " + add.stderr) };
  const staged = await runGit(["diff", "--cached", "--name-only"], { cwd: configDir });
  if (staged.code !== 0) return { ok: false, committed: false, output: redactGitOutput("git diff --cached failed: " + staged.stderr) };
  if (!staged.stdout.trim()) return { ok: true, committed: false, output: "" };
  const c = await runGit([...GIT_IDENTITY, "commit", "-m", message], { cwd: configDir });
  if (c.code !== 0) return { ok: false, committed: false, output: redactGitOutput("git commit failed: " + c.stderr + "\n" + c.stdout) };
  return { ok: true, committed: true, output: "" };
}

/**
 * Resolve a remote URL that came BACK from the webview to the real linked URL.
 * The panel is only ever handed display-safe URLs (a legacy remotes.json entry may
 * still carry a credential), so a round-tripped string can be the redacted form;
 * match it exactly first, then by display form. Returns "" when nothing matches.
 * Pure.
 */
function resolveRemoteUrl(remotes, fromWebview) {
  const wanted = String(fromWebview == null ? "" : fromWebview);
  if (!wanted) return "";
  const list = (remotes || []).map((r) => String(r && r.url ? r.url : "")).filter(Boolean);
  if (list.includes(wanted)) return wanted;
  const hit = list.find((u) => displayRemoteUrl(u) === wanted);
  return hit || "";
}

/**
 * Push local changes from a staging clone to the upstream remote on a named
 * branch (D19). Copies the source files into the staging clone at their
 * pathInRemote locations, commits, and pushes.
 *
 * files: [{absSource, pathInRemote}]
 */
async function pushUpstream(runGit, { stagingDir, files, branch, message }) {
  // Copy files into staging.
  const resolvedStaging = path.resolve(stagingDir);
  for (const f of (files || [])) {
    const dest = path.join(stagingDir, f.pathInRemote);
    // Path containment: reject any pathInRemote that escapes the staging
    // directory via traversal sequences (e.g. "projects/../../../.bashrc").
    const resolvedDest = path.resolve(dest);
    if (!resolvedDest.startsWith(resolvedStaging + path.sep) && resolvedDest !== resolvedStaging) {
      throw new Error("pathInRemote escapes staging directory: " + f.pathInRemote);
    }
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.copyFileSync(f.absSource, dest);
  }
  // Stage + commit.
  await runGit([...GIT_IDENTITY, "checkout", "-B", branch], { cwd: stagingDir });
  await runGit([...GIT_IDENTITY, "add", "-A"], { cwd: stagingDir });
  const staged = await runGit(["diff", "--cached", "--name-only"], { cwd: stagingDir });
  if (!staged.stdout.trim()) return { ok: true, branch, output: "nothing to push" };
  const c = await runGit([...GIT_IDENTITY, "commit", "-m", message || "construct config update"], { cwd: stagingDir });
  if (c.code !== 0) return { ok: false, branch, output: c.stderr.trim() };
  // Push.
  const push = await runGit(["push", "origin", branch], { cwd: stagingDir, timeoutMs: 60000 });
  return { ok: push.code === 0, branch, output: push.code === 0 ? push.stdout.trim() : push.stderr.trim() };
}

// ── Publish local profiles upstream (plan 4.13 / B15) ───────────────────────

/**
 * Redact URL userinfo ("https://user:token@host/..." -> "https://***@host/...").
 * Pure; twin of Protect-ConstructGitOutput / Format-ConstructRemoteUrlForDisplay
 * in lib/AgentVm.Common.ps1. Used for BOTH captured git output and every URL that
 * reaches a toast, a log line or a picker title: a publish target on the owner's
 * own git host is authenticated with a PAT carried IN the URL, so any display
 * string built from the raw URL is a credential leak.
 */
function redactGitOutput(text) {
  if (!text) return "";
  return String(text).replace(/(?<=:\/\/)[^/@\s]+(?=@)/g, "***");
}

/** The display form of a remote URL: never printed, logged or shown raw. */
function displayRemoteUrl(url) {
  return redactGitOutput(String(url == null ? "" : url));
}

/**
 * True when a profile name is usable AS IS as a bare file name. It REUSES the one
 * name rule (host.safeProfileName) rather than restating it, and adds the single
 * thing publish needs on top: the name must already BE its canonical form, because
 * here the string IS the on-disk file name -- a name that only becomes safe after
 * trimming would target a different file than the one it came from. Twin of
 * Test-ConstructSafeProfileName in lib/AgentVm.Common.ps1. Pure.
 */
function isSafeProfileName(name) {
  const s = String(name == null ? "" : name);
  return s !== "" && hostNames.safeProfileName(s) === s;
}

/**
 * True when a remote URL carries credentials in its userinfo -- user:secret. A bare
 * user name is legitimate for every scheme (https://alice@host selects the stored
 * credential; ssh://git@host). Pure; twin of Test-ConstructUrlHasCredentials.
 */
function urlHasCredentials(url) {
  // TRIM first: every caller trims the value before storing or handing it to git,
  // so a check anchored at the untrimmed first character would pass a padded
  // " https://alice:secret@host/x.git" that is then persisted without the padding.
  const m = String(url == null ? "" : url).trim().match(/^([A-Za-z][A-Za-z0-9+.-]*):\/\/([^/@\s]+)@/);
  if (!m) return false;
  // A bare user NAME (https://alice@host/x.git, ssh://git@host) carries no secret -- it
  // is how a credential helper is told WHICH stored credential to use (GitGudLab project
  // tokens require one). user:secret is refused for every scheme, and so is a bare user
  // that is plainly a token (a known token prefix, or 32+ characters).
  if (m[2].includes(":")) return true;
  return tokenLikeUser(m[2]);
}

/**
 * A userinfo value that is a TOKEN rather than a name: a known access-token prefix
 * (GitHub, GitLab, GitGudLab) or the length no human account name has. Pure; twin of
 * Test-ConstructTokenLikeUser.
 */
function tokenLikeUser(user) {
  const u = String(user == null ? "" : user);
  if (!u) return false;
  if (/^gitgud-project(\.|$)/.test(u)) return false; // GitGudLab project-token user names
  // Tokens are long and dot-free; account names that long carry dots (project users).
  if (u.length >= 32 && !u.includes(".")) return true;
  return /^(ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|glpat-|glptt-|gldt-|glrt-|ggpat_|ggpt_|ggjt_|oauth2$|x-access-token$)/.test(u);
}

/**
 * THE validation for a remote-config-repo URL typed by the user, shared by every
 * input path (add remote, add remote & publish) so none of them can drift. The
 * git-URL shape check is injected (remote.isLikelyGitUrl) to keep this module free
 * of the vscode-aware layer. Validates the NORMALIZED value -- the same string the
 * caller goes on to store. Returns null when acceptable, else the message to show.
 */
function validateConfigRemoteUrl(url, isLikelyGitUrl) {
  const s = String(url == null ? "" : url).trim();
  const SHAPE = "Enter an https://, ssh:// or git@host:path git URL.";
  if (!s) return SHAPE;
  if (typeof isLikelyGitUrl === "function" && !isLikelyGitUrl(s)) return SHAPE;
  if (urlHasCredentials(s)) {
    return "Remove the credentials from the URL -- let your git credential helper supply them.";
  }
  return null;
}

/**
 * The provenance manifest entry a publish writes -- deliberately the SAME shape
 * and key ORDER an import writes, because publishing IS adopting the file as if
 * it had been imported from projects/<name>.json of that remote. Serialized with
 * JSON.stringify(entry, null, 2) + "\n", byte-identical to the PowerShell
 * writer's ConvertTo-ConstructJsonValue output. Pure.
 */
function publishManifestEntry({ remoteUrl, ref, name, baseCommit, baseBlobSha }) {
  return {
    remoteUrl: String(remoteUrl || ""),
    ref: String(ref || ""),
    pathInRemote: "projects/" + String(name) + ".json",
    importedAs: String(name),
    baseCommit: String(baseCommit || ""),
    baseBlobSha: String(baseBlobSha || ""),
  };
}

/**
 * True when the name is safe as an upstream branch AND as a bare git ref
 * operand. Looser than isValidVmBranch on purpose -- that one governs THIS
 * repo's vm-<name> branches and reserves "main"/"master", which are exactly the
 * names an upstream default branch has. Pure; twin of
 * Test-ConstructPublishBranchName.
 */
function isValidPublishBranch(name) {
  const n = String(name == null ? "" : name);
  if (!n) return false;
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(n)) return false;
  if (n.includes("..")) return false;
  if (n.includes("//")) return false;
  if (n.endsWith(".")) return false;
  if (n.endsWith("/")) return false;
  if (n.endsWith(".lock")) return false;
  return true;
}

/**
 * Canonicalize on-disk profile text through the SAME gate every other config
 * write uses: parse -> projects.validateProfile -> projects.canonicalProfileJson.
 * Returns {ok, content} or {ok:false, reason}. Pure.
 *
 * The validate step is NOT optional decoration: canonicalProfileJson is
 * COERCIVE (sanitizeProfile drops unknown keys and replaces wrong-typed ones
 * with empty defaults), so canonicalizing an invalid profile silently repairs it
 * into something the user never wrote -- and a publish would then push that
 * repaired value AND write it back over the local file. Invalid profiles are
 * reported, never repaired.
 */
function canonicalizeProfileText(name, raw) {
  let obj;
  try { obj = JSON.parse(raw); }
  catch (e) { return { ok: false, reason: "cannot be parsed as JSON (" + e.message + ")" }; }
  const v = projects.validateProfile(name, obj);
  if (!v.ok) return { ok: false, reason: "is not a valid profile: " + v.errors.join("; ") };
  const content = projects.canonicalProfileJson(name, obj);
  if (!content) return { ok: false, reason: "could not be canonicalized" };
  return { ok: true, content };
}

/**
 * Plan a publish (B15). Pure: no IO, no git. The decision core BOTH engines run --
 * lib/AgentVm.Common.ps1's Get-ConstructPublishPlan is its twin, and
 * test/fixtures/publish-plan-cases.json is the shared behaviour fixture they are
 * both measured against.
 *
 *   profiles      [{name, raw}] -- local profiles as they are ON DISK (raw text)
 *   manifest      {name -> manifestEntry} (config/manifest, minus remotes.json)
 *   remoteFiles   {name -> content} for projects/<name>.json already upstream
 *                 ({} when the remote is empty or brand new)
 *   selected      optional string[] narrowing the selection (null = all)
 *
 * Rules:
 *   - reserved names, *.sample names and names that are not safe bare filenames
 *     are never published (the first two silently, the last as invalid)
 *   - a profile that fails the parse/validate/canonicalize gate is INVALID and is
 *     reported, never repaired
 *   - a profile with ANY manifest entry is already tracked -> skipTracked
 *     (it goes through Push back, not Publish)
 *   - the remote already has projects/<name>.json with DIFFERENT content
 *     -> refuse (import it first, then push back). The comparison is
 *     canonical-vs-canonical when the upstream file is itself a valid profile,
 *     so a merely reformatted copy is not a conflict
 *   - an upstream file whose name differs only by CASE is the same file on
 *     Windows -> refuse, whatever its content
 *   - an upstream copy with identical content is adopted (published with
 *     adopt:true -- nothing to commit, everything to track)
 *
 * Returns {publish, skipTracked, refuse, invalid, reasons}; reasons is a flat
 * {name -> reason} map for logging and the picker. Every reason is display-safe
 * (URLs redacted).
 */
function planPublish({ profiles, manifest, remoteFiles, selected }) {
  const publish = [];
  const skipTracked = [];
  const refuse = [];
  const invalid = [];
  const reasons = {};
  const man = manifest || {};
  const remote = remoteFiles || {};
  const want = Array.isArray(selected) ? new Set(selected.map((n) => String(n))) : null;

  // Case-insensitive index of the upstream listing: on Windows projects/Alpha.json
  // and projects/alpha.json are ONE file, so a case variant is a collision.
  const remoteByLower = new Map();
  for (const key of Object.keys(remote)) remoteByLower.set(key.toLowerCase(), key);

  for (const p of (profiles || [])) {
    const name = String(p && p.name != null ? p.name : "");
    if (!name) continue;
    if (projects.isReservedProfileName(name)) continue;
    if (name.toLowerCase().endsWith(".sample")) continue;
    if (want && !want.has(name)) continue;

    if (!isSafeProfileName(name)) {
      const reason = "is not a safe profile file name";
      reasons[name] = reason;
      invalid.push({ name, reason });
      continue;
    }
    if (man[name]) {
      const url = displayRemoteUrl(man[name].remoteUrl || "");
      const reason = url
        ? "already tracked by " + url + " -- use Push back"
        : "already tracked -- use Push back";
      reasons[name] = reason;
      // The record carries NO raw URL: a plan is returned to callers that print,
      // log and serialize it whole, and a legacy manifest entry can hold a PAT.
      // Everything downstream needs the name and the (redacted) reason.
      skipTracked.push({ name, reason });
      continue;
    }

    const gate = canonicalizeProfileText(name, p.raw);
    if (!gate.ok) {
      reasons[name] = gate.reason;
      invalid.push({ name, reason: gate.reason });
      continue;
    }

    const upstreamKey = remoteByLower.get(name.toLowerCase());
    if (upstreamKey != null && upstreamKey !== name) {
      const reason = "the remote already has projects/" + upstreamKey +
        ".json, which is the SAME file as projects/" + name +
        ".json on Windows -- rename one of them, or import it first";
      reasons[name] = reason;
      refuse.push({ name, reason });
      continue;
    }
    const upstream = upstreamKey != null ? remote[upstreamKey] : null;
    if (upstream != null) {
      // Compare canonical-vs-canonical when the upstream file is itself a valid
      // profile, so reformatting alone is never a conflict.
      const upGate = canonicalizeProfileText(name, upstream);
      const upstreamCmp = upGate.ok ? upGate.content : upstream;
      if (upstreamCmp !== gate.content) {
        const reason = "the remote already has projects/" + name +
          ".json with different content -- import it first, then push back";
        reasons[name] = reason;
        refuse.push({ name, reason });
        continue;
      }
    }
    publish.push({
      name,
      pathInRemote: "projects/" + name + ".json",
      content: gate.content,
      adopt: upstream != null,
    });
  }
  return { publish, skipTracked, refuse, invalid, reasons };
}

/**
 * Turn a profile-name listing into the planner's input. The reader is injected, so
 * this is the handler's whole "listing -> plan input" step and is unit-testable.
 *
 * An UNSAFE name is passed through with empty text and is NEVER handed to the
 * reader: the planner rejects it on the name alone (so it is reported as invalid
 * instead of silently vanishing from the picker), and no unsafe name is ever
 * joined onto a path. An unreadable file becomes empty text too, which the gate
 * reports as unparseable rather than dropping.
 */
function buildPublishProfileInputs(names, readRaw) {
  const out = [];
  for (const n of (names || [])) {
    const name = String(n == null ? "" : n);
    if (!name) continue;
    if (!isSafeProfileName(name)) { out.push({ name, raw: "" }); continue; }
    let raw = null;
    try { raw = readRaw(name); } catch (_) { raw = null; }
    out.push({ name, raw: raw == null ? "" : raw });
  }
  return out;
}

/**
 * The QuickPick model for the publish picker. Pure, so the picker's behaviour is
 * unit-tested rather than only clicked: publishable profiles come first and are
 * ALL ticked by default (the mirror image of Import's none-ticked rule -- pulling
 * someone else's config is opt-in, publishing your own is why the button was
 * pressed); tracked and refused/invalid ones follow under their own separator,
 * carry their reason, and are marked blocked so the caller can keep them
 * de-selected. Returns [{label, description, kind, picked, blocked}] where
 * kind === "separator" rows are headings.
 */
function buildPublishPickerItems(plan) {
  const items = [];
  const p = plan || {};
  const pub = p.publish || [];
  const tracked = p.skipTracked || [];
  const bad = (p.refuse || []).concat(p.invalid || []);
  if (pub.length) {
    items.push({ label: "publish", kind: "separator" });
    for (const e of pub) {
      items.push({
        label: e.name,
        description: e.adopt ? "already upstream, identical -- adopt only" : "",
        picked: true, blocked: false,
      });
    }
  }
  if (tracked.length) {
    items.push({ label: "already tracked -- use Push back", kind: "separator" });
    for (const e of tracked) items.push({ label: e.name, description: e.reason, picked: false, blocked: true });
  }
  if (bad.length) {
    items.push({ label: "cannot be published", kind: "separator" });
    for (const e of bad) items.push({ label: e.name, description: e.reason, picked: false, blocked: true });
  }
  return items;
}

/**
 * The selection filter behind the picker's greying: a blocked row that somehow
 * ends up selected is dropped again, so the UI can never publish a tracked or
 * refused profile. Pure; returns the kept items.
 */
function filterPublishSelection(selection) {
  return (selection || []).filter((i) => i && !i.blocked && i.kind !== "separator");
}

/**
 * Ask the REMOTE which branch its HEAD points at. Returns "" when the remote is
 * unreachable or does not exist yet (the push-to-create case); the caller falls
 * back to "main".
 */
async function remoteDefaultBranch(runGit, url) {
  const r = await runGit(["ls-remote", "--symref", url, "HEAD"], { timeoutMs: 60000 });
  if (r.code !== 0) return "";
  const m = String(r.stdout || "").match(/^ref:\s+refs\/heads\/(\S+)\s/m);
  return m ? m[1] : "";
}

// A staging clone created for push-to-create is marked INSIDE .git (never in the
// work tree, so `git add -A` cannot pick it up). Without the marker a first push
// that fails -- the common case while a PAT is still being set up -- would wedge
// the flow forever: the retry finds a local repo whose fetch still fails and
// whose HEAD now exists (the failed attempt's commit), which is exactly the
// signature of "a real remote we cannot reach". The marker tells the two apart,
// and is removed once a push has actually landed.
const PUBLISH_PENDING_MARKER = "construct-publish-pending";
// The marker is an OPAQUE sentinel, never a copy of the remote URL: the URL is the
// one thing that could carry a credential, and this file is a plain unencrypted
// file in the staging cache. Which remote it belongs to is already the directory
// name (the slug), and git itself holds the URL in .git/config.
const PUBLISH_PENDING_SENTINEL = "push-to-create pending\n";

function publishPendingMarkerPath(cloneDir) {
  return path.join(cloneDir, ".git", PUBLISH_PENDING_MARKER);
}

/**
 * The staging clone used by a publish -- the SAME cache dir and slug an import
 * uses, so a publish and a later import/push-back share one clone. The one
 * difference: publishing tolerates a remote that does not exist YET. When the
 * clone fails, the directory is set up locally with git init + remote add and the
 * first push creates the repo on hosts that support push-to-create
 * (GitLab/GitGudLab). A remote that DOES exist but cannot be fetched is an error,
 * not a push-to-create: starting from an empty tree there would publish a history
 * that drops everything already in the repo.
 *
 * Returns {ok, dir, created, output}.
 */
async function ensurePublishClone(runGit, stagingRootDir, url) {
  const slug = remoteSlug(url);
  const dir = path.join(stagingRootDir, slug);
  let isRepo = false;
  try { isRepo = fs.statSync(path.join(dir, ".git")).isDirectory(); } catch (_) { isRepo = false; }

  if (isRepo) {
    const setUrl = await runGit(["remote", "set-url", "origin", url], { cwd: dir });
    if (setUrl.code !== 0) {
      const add = await runGit(["remote", "add", "origin", url], { cwd: dir });
      if (add.code !== 0) {
        return { ok: false, dir, created: false, output: redactGitOutput(setUrl.stderr + "\n" + add.stderr) };
      }
    }
    const fetch = await runGit(["fetch", "origin"], { cwd: dir, timeoutMs: 60000 });
    if (fetch.code === 0) return { ok: true, dir, created: false, output: "" };
    // Unreachable origin. Push-to-create only when THIS clone was created for it
    // (marker), or when it has no commits at all.
    let pending = false;
    try { pending = fs.existsSync(publishPendingMarkerPath(dir)); } catch (_) { pending = false; }
    const head = await runGit(["rev-parse", "--verify", "--quiet", "HEAD"], { cwd: dir });
    if (pending || head.code !== 0) {
      try { fs.writeFileSync(publishPendingMarkerPath(dir), PUBLISH_PENDING_SENTINEL, "utf8"); } catch (_) { /* best effort */ }
      return { ok: true, dir, created: true, output: "" };
    }
    return { ok: false, dir, created: false, output: redactGitOutput(fetch.stderr) };
  }

  fs.mkdirSync(path.dirname(dir), { recursive: true });
  const clone = await runGit(["clone", url, slug], { cwd: path.dirname(dir), timeoutMs: 60000 });
  if (clone.code === 0) return { ok: true, dir, created: false, output: "" };

  // Push-to-create: no repo there yet. Start an empty local one.
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });
  const init = await runGit(["init"], { cwd: dir });
  if (init.code !== 0) return { ok: false, dir, created: false, output: redactGitOutput(clone.stderr + "\n" + init.stderr) };
  const add = await runGit(["remote", "add", "origin", url], { cwd: dir });
  if (add.code !== 0) return { ok: false, dir, created: false, output: redactGitOutput(add.stderr) };
  try { fs.writeFileSync(publishPendingMarkerPath(dir), PUBLISH_PENDING_SENTINEL, "utf8"); } catch (_) { /* best effort */ }
  return { ok: true, dir, created: true, output: "" };
}

/**
 * Position a staging clone on the target branch and return the profiles already
 * upstream there as {name -> content}. An unborn HEAD (fresh push-to-create
 * clone, or an empty repo) points at the branch instead of checking it out.
 * Every git step that CHANGES state is checked -- a failed reset/clean would
 * leave an earlier run's files in the tree and publish them.
 * Returns {ok, remoteFiles, output}.
 */
async function checkoutPublishBranch(runGit, cloneDir, branch) {
  const hasHead = (await runGit(["rev-parse", "--verify", "--quiet", "HEAD"], { cwd: cloneDir })).code === 0;
  const hasRemoteBranch = (await runGit(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/" + branch], { cwd: cloneDir })).code === 0;
  let co;
  if (hasRemoteBranch) co = await runGit(["checkout", "-B", branch, "refs/remotes/origin/" + branch], { cwd: cloneDir });
  else if (hasHead) co = await runGit(["checkout", "-B", branch], { cwd: cloneDir });
  else co = await runGit(["symbolic-ref", "HEAD", "refs/heads/" + branch], { cwd: cloneDir });
  if (co.code !== 0) return { ok: false, remoteFiles: {}, output: redactGitOutput(co.stderr) };
  if (hasRemoteBranch || hasHead) {
    // Drop anything an earlier run left behind so the tree IS the branch.
    const reset = await runGit(["reset", "--hard"], { cwd: cloneDir });
    if (reset.code !== 0) return { ok: false, remoteFiles: {}, output: redactGitOutput(reset.stderr) };
    const clean = await runGit(["clean", "-fd"], { cwd: cloneDir });
    if (clean.code !== 0) return { ok: false, remoteFiles: {}, output: redactGitOutput(clean.stderr) };
  }
  const remoteFiles = {};
  const projDir = path.join(cloneDir, "projects");
  let entries = [];
  try { entries = fs.readdirSync(projDir, { withFileTypes: true }); } catch (_) { entries = []; }
  for (const e of entries) {
    if (!e.isFile() || !e.name.endsWith(".json")) continue;
    const base = e.name.slice(0, -5);
    try { remoteFiles[base] = fs.readFileSync(path.join(projDir, e.name), "utf8"); } catch (_) { /* skip */ }
  }
  return { ok: true, remoteFiles, output: "" };
}

const SHA1_RE = /^[0-9a-f]{40}$/;

/**
 * Write the planned profiles into the staging clone, commit and push them to the
 * remote's DEFAULT branch (the owner publishes into their own repo, so no review
 * branch). Returns {ok, branch, commit, blobShas, output}.
 *
 * EVERY git step is checked. The caller adopts the published files -- writes a
 * manifest entry claiming they are tracked upstream -- purely on the strength of
 * {commit, blobShas}, so a step that silently failed (a failed `add` followed by
 * an empty staged diff and an "Everything up-to-date" push) must never surface as
 * ok:true. ok is returned only with a real 40-hex commit and a real blob sha for
 * every published file.
 *
 * The commit is made with the USER'S configured git identity: it lands in the
 * user's own upstream repo, so the internal bookkeeping identity the local config
 * store uses would be wrong there. `core.hooksPath=` is still forced -- hooks from
 * a cloned repo must not run on this machine.
 */
async function publishToRemote(runGit, { stagingDir, branch, files, message }) {
  const fail = (output) => ({ ok: false, branch, commit: "", blobShas: {}, output: redactGitOutput(output) });
  const NO_HOOKS = ["-c", "core.hooksPath="];
  const projDir = path.join(stagingDir, "projects");
  fs.mkdirSync(projDir, { recursive: true });
  for (const f of (files || [])) {
    // The destination path is REBUILT from a bare profile name, never taken from
    // the caller: a name carrying a separator or ".." is refused outright rather
    // than quietly collapsed to its basename.
    const name = String(f && f.name ? f.name : "");
    if (!isSafeProfileName(name) || name !== path.basename(name)) {
      throw new Error("refusing to publish outside projects/: " + name);
    }
    fs.writeFileSync(path.join(projDir, name + ".json"), f.content, "utf8");
  }
  const add = await runGit([...NO_HOOKS, "add", "-A"], { cwd: stagingDir });
  if (add.code !== 0) return fail("git add failed: " + add.stderr);
  const staged = await runGit(["diff", "--cached", "--name-only"], { cwd: stagingDir });
  if (staged.code !== 0) return fail("git diff --cached failed: " + staged.stderr);
  if (staged.stdout.trim()) {
    const msg = message || ("publish " + (files || []).length + " profiles");
    const c = await runGit([...NO_HOOKS, "commit", "-m", msg], { cwd: stagingDir });
    if (c.code !== 0) {
      const both = c.stderr + "\n" + c.stdout;
      if (/Please tell me who you are|empty ident|unable to auto-detect email/i.test(both)) {
        return fail("git has no configured identity to commit with. Set user.name and user.email " +
          "(git config --global user.name \"...\"; git config --global user.email \"...\") and publish again.\n" + both);
      }
      return fail("git commit failed: " + both);
    }
  }
  const push = await runGit(["push", "origin", "HEAD:refs/heads/" + branch], { cwd: stagingDir, timeoutMs: 60000 });
  if (push.code !== 0) return fail("git push failed: " + push.stderr);

  const head = await runGit(["rev-parse", "HEAD"], { cwd: stagingDir });
  const commit = head.code === 0 ? head.stdout.trim() : "";
  if (!SHA1_RE.test(commit)) return fail("could not resolve the pushed commit: " + (head.stderr || head.stdout));
  const blobShas = {};
  for (const f of (files || [])) {
    const b = await runGit(["rev-parse", "HEAD:projects/" + f.name + ".json"], { cwd: stagingDir });
    const sha = b.code === 0 ? b.stdout.trim() : "";
    if (!SHA1_RE.test(sha)) {
      return fail("\"" + f.name + "\" is not in the pushed commit " + commit.slice(0, 7) + ": " + (b.stderr || b.stdout));
    }
    blobShas[f.name] = sha;
  }
  // The push landed: this clone is a normal staging clone from now on.
  try { fs.rmSync(publishPendingMarkerPath(stagingDir), { force: true }); } catch (_) { /* best effort */ }
  return { ok: true, branch, commit, blobShas, output: redactGitOutput(push.stdout) };
}

module.exports = {
  makeGitRunner, detectGit,
  ensureConfigTree,
  DEFAULT_VM_BRANCH, isValidVmBranch, resolveVmBranch,
  acquireSyncLock, releaseSyncLock, provisionSyncPending,
  SYNC_LOCK_FILE, PROVISION_SYNC_INTENT_FILE,
  ensureRepo, repoState, completePendingMerge,
  buildReadStoreScript, parseReadStore,
  planWriteBack, buildWriteStoreScript, parseWriteResult,
  syncTick, deletedProfileIdentities,
  readRemotes, writeRemotes, remoteSlug, stagingRoot,
  ensureStagingClone, listImportCandidates, readImportManifest,
  planUpstreamImport, mergeFile, commitAll, pushUpstream,
  redactGitOutput, displayRemoteUrl, resolveRemoteUrl, urlHasCredentials, validateConfigRemoteUrl,
  isSafeProfileName, isValidPublishBranch,
  canonicalizeProfileText, publishManifestEntry, planPublish,
  buildPublishProfileInputs, buildPublishPickerItems, filterPublishSelection,
  remoteDefaultBranch, ensurePublishClone, checkoutPublishBranch, publishToRemote,
};
