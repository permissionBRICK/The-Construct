"use strict";
// Config-sync engine: the host-side git-backed sync described in
// docs/config-sync.md §6. Reads VM profiles over SSH, commits them onto a `vm`
// branch INSIDE the host config repo, merges into `main`, validates, and writes
// back the merged result — all without touching the working tree's checkout
// branch (the vm branch uses a temp index). Process execution is injected
// (makeGitRunner) so tests run REAL git in throwaway dirs without any fakes.
//
// The module also owns:
//   - the bash scripts that read/write the VM store (buildReadStoreScript /
//     buildWriteStoreScript) — same base64-as-data, END-sentinel idiom as
//     projects.js buildScanScript;
//   - remote/upstream helpers (manifest, staging clones, import planning,
//     mergeFile, pushUpstream) per D16/D17/D19;
//   - ensureConfigTree / migrateLegacyProfiles for the config-dir bootstrap.

const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");

const projects = require("./projects");

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

/**
 * One-time migration: copy user *.json profiles from a legacy scriptsDir/projects
 * into the config dir's projects/ folder. Skips reserved names, *.sample files,
 * and files that already exist in the target. Returns the list of names copied.
 */
function migrateLegacyProfiles(configDir, legacyProjectsDir) {
  const copied = [];
  if (!legacyProjectsDir || !configDir) return copied;
  const target = path.join(configDir, "projects");
  fs.mkdirSync(target, { recursive: true });
  let entries;
  try { entries = fs.readdirSync(legacyProjectsDir, { withFileTypes: true }); }
  catch (_) { return copied; }
  for (const e of entries) {
    if (!e.isFile()) continue;
    const lower = e.name.toLowerCase();
    if (!lower.endsWith(".json")) continue;
    if (lower.endsWith(".sample")) continue;
    const base = e.name.slice(0, -5);
    if (projects.isReservedProfileName(base)) continue;
    if (e.name === "project.schema.json") continue;
    const dest = path.join(target, e.name);
    try { fs.statSync(dest); continue; } catch (_) { /* does not exist, good */ }
    try {
      fs.copyFileSync(path.join(legacyProjectsDir, e.name), dest);
      copied.push(base);
    } catch (_) { /* skip on error */ }
  }
  return copied;
}

// ── Git identity args (per-invocation, never global config) ──────────────────

const GIT_IDENTITY = ["-c", "user.name=The Construct", "-c", "user.email=construct@construct.local"];

// ── Repo init (lazy, idempotent) ─────────────────────────────────────────────

/**
 * Lazy git init per D1: init, add -A, commit, rename branch to main, create vm
 * branch. Idempotent — if a repo already exists returns {repo:true,
 * initialized:false}. Returns {repo:false} when git is absent.
 */
async function ensureRepo(runGit, configDir) {
  // Check if repo already exists. We must verify that the discovered repo's
  // toplevel actually IS configDir — `git rev-parse --git-dir` matches any
  // ancestor repo (e.g. a dotfiles repo in %USERPROFILE% that contains
  // %LOCALAPPDATA%), and silently adopting that parent repo would commit config
  // files into the user's unrelated repository.
  const st = await runGit(["rev-parse", "--git-dir"], { cwd: configDir });
  if (st.code === 0) {
    const tl = await runGit(["rev-parse", "--show-toplevel"], { cwd: configDir });
    const toplevel = tl.stdout.trim().replace(/\/$/, "");
    const target = configDir.replace(/\/$/, "");
    // On Windows git may return forward-slash paths; normalise for comparison.
    if (path.resolve(toplevel) === path.resolve(target)) {
      return { repo: true, initialized: false };
    }
    // The repo belongs to an ancestor directory — ignore it and init our own.
  }

  // Try to init.
  const init = await runGit(["init"], { cwd: configDir });
  if (init.code !== 0) return { repo: false };

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
  // Create the vm branch at the same point.
  await runGit(["branch", "vm"], { cwd: configDir });
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
 * The core sync tick. Implements D6 steps 1-8 exactly.
 *
 * readStore:  () => Promise<string|null>  (raw stdout; null = SSH unreachable)
 * writeStore: (script) => Promise<string|null>
 * log:        (level, msg) => void
 */
async function syncTick({ runGit, configDir, readStore, writeStore, log, storeRoot }) {
  const warn = (msg) => log && log("warn", msg);
  const info = (msg) => log && log("info", msg);
  const result = {
    ok: false, ran: false, conflict: false, blocked: false, blockedReason: null,
    skippedInvalid: [], merged: false, seeded: false,
    writeBack: { done: [], skipped: [] }, warnings: [],
  };
  const addWarning = (msg) => { result.warnings.push(msg); warn(msg); };

  // Step 1: ensure repo.
  const repo = await ensureRepo(runGit, configDir);
  if (!repo.repo) {
    addWarning("git not available; sync skipped");
    return result;
  }

  // Check for existing conflict/merge state — don't proceed if unresolved.
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
    result.ok = true;
    return result;
  }

  const vmStoreAbsent = vmParsed.storeAbsent;

  // Step 4: validate each VM file. Build maps of valid entries.
  // vmRaw: name->raw content (for write-back guards).
  // vmValid: name->canonicalized content (for committing to the vm branch).
  const vmRaw = {};
  const vmValid = {};
  for (const entry of vmParsed.entries) {
    if (projects.isReservedProfileName(entry.name)) {
      addWarning(`reserved name "${entry.name}" in VM store ignored`);
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
  const vmTipProfiles = await countVmBranchProfiles(runGit, configDir);
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
      if (wb) result.writeBack = wb;
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
  const vmChanged = await commitVmBranch(runGit, configDir, vmValid, preserveNames);

  // Step 6: merge vm into main.
  // First check if merge is needed.
  const baseCheck = await runGit(["merge-base", "--is-ancestor", "vm", "main"], { cwd: configDir });
  if (baseCheck.code === 0 && !vmChanged) {
    // vm is already an ancestor of main and nothing changed — nothing to merge.
    // Still do write-back for any drifted files.
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

  // Also check: are trees identical? (vm == main tree-wise)
  const mainTree = await runGit(["rev-parse", "main^{tree}"], { cwd: configDir });
  const vmTree = await runGit(["rev-parse", "vm^{tree}"], { cwd: configDir });
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
    [...GIT_IDENTITY, "merge", "--no-ff", "--no-commit", "vm"],
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
  await runGit([...GIT_IDENTITY, "commit", "-m", "sync merge vm"], { cwd: configDir });
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
    await runGit(["update-ref", "refs/heads/vm", "refs/heads/main"], { cwd: configDir });
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

/** Count the number of projects/*.json files on the vm branch tip. */
async function countVmBranchProfiles(runGit, configDir) {
  const ls = await runGit(["ls-tree", "--name-only", "vm", "projects/"], { cwd: configDir });
  if (ls.code !== 0) return 0;
  const files = ls.stdout.trim().split("\n").filter((f) => f && f.endsWith(".json"));
  return files.length;
}

/**
 * Commit the VM snapshot onto the vm branch using a temp index so the working
 * tree (checked out on main) is never disturbed. Returns true if a new commit
 * was created (the tree differs from the vm-tip tree).
 */
async function commitVmBranch(runGit, configDir, vmValid, preserveNames) {
  const preserve = preserveNames instanceof Set ? preserveNames : new Set(preserveNames || []);
  const tmpIndex = path.join(configDir, ".git", "tmp-vm-index");
  try { fs.unlinkSync(tmpIndex); } catch (_) { /* ok */ }

  const envOverride = { GIT_INDEX_FILE: tmpIndex };

  // Read the current vm tree into the temp index.
  await runGit(["read-tree", "vm"], { cwd: configDir, env: envOverride });

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
  const vmTipTree = await runGit(["rev-parse", "vm^{tree}"], { cwd: configDir });
  if (newTree === vmTipTree.stdout.trim()) {
    // No change.
    try { fs.unlinkSync(tmpIndex); } catch (_) { /* ok */ }
    return false;
  }

  // Commit the new tree as a child of the vm tip.
  const vmTip = await runGit(["rev-parse", "vm"], { cwd: configDir });
  const ct = await runGit(
    [...GIT_IDENTITY, "commit-tree", newTree, "-p", vmTip.stdout.trim(), "-m", "vm sync"],
    { cwd: configDir }
  );
  const newCommit = ct.stdout.trim();
  await runGit(["update-ref", "refs/heads/vm", newCommit], { cwd: configDir });

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
 * Stage + commit everything in the config dir. Returns {ok, committed}.
 */
async function commitAll(runGit, configDir, message) {
  await runGit([...GIT_IDENTITY, "add", "-A"], { cwd: configDir });
  const staged = await runGit(["diff", "--cached", "--name-only"], { cwd: configDir });
  if (!staged.stdout.trim()) return { ok: true, committed: false };
  const c = await runGit([...GIT_IDENTITY, "commit", "-m", message], { cwd: configDir });
  return { ok: c.code === 0, committed: c.code === 0 };
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

module.exports = {
  makeGitRunner, detectGit,
  ensureConfigTree, migrateLegacyProfiles,
  ensureRepo, repoState,
  buildReadStoreScript, parseReadStore,
  planWriteBack, buildWriteStoreScript, parseWriteResult,
  syncTick,
  readRemotes, writeRemotes, remoteSlug, stagingRoot,
  ensureStagingClone, listImportCandidates, readImportManifest,
  planUpstreamImport, mergeFile, commitAll, pushUpstream,
};
