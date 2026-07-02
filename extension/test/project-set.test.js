"use strict";
// Plain-node unit + integration tests for the VM-side project-set helper
// (bin/project-set.js), the construct CLI (bin/construct), and the rewritten
// generate-runtime-config.sh. No deps. Run: node project-set.test.js
//
// Tests spawn real processes (node, bash) against temp directories so they
// exercise the actual code paths, including atomic writes, reserved-name
// rejection, JSON validation, and the runtime-config resolution rules.

const { execSync, execFileSync, spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");

// Resolve paths relative to the repo root (two dirs up from this test file).
const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PROJECT_SET_JS = path.join(REPO_ROOT, "bin", "project-set.js");
const CONSTRUCT_CLI = path.join(REPO_ROOT, "bin", "construct");
const GEN_SCRIPT = path.join(REPO_ROOT, "bin", "generate-runtime-config.sh");
const PROJECTS_JS = path.join(REPO_ROOT, "extension", "src", "projects.js");

// The projects module, for reference canonical output.
const projects = require(PROJECTS_JS);

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function mkTmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "construct-test-"));
}

function cleanup(dir) {
  try { fs.rmSync(dir, { recursive: true, force: true }); } catch (_) { /* ignore */ }
}

// Helper: run project-set.js with PROJECTS_STORE pointing at a temp dir.
function runSetJs(store, args, input) {
  const opts = {
    env: { ...process.env, PROJECTS_STORE: store },
    encoding: "utf8",
    timeout: 10000,
  };
  if (input != null) opts.input = input;
  return spawnSync(process.execPath, [PROJECT_SET_JS, ...args], opts);
}

// Helper: run the construct CLI.
function runCli(store, args, opts) {
  const env = { ...process.env, PROJECTS_STORE: store, CONSTRUCT_REPO_DIR: REPO_ROOT };
  const spawnOpts = { env, encoding: "utf8", timeout: 10000, ...opts };
  return spawnSync("bash", [CONSTRUCT_CLI, ...args], spawnOpts);
}

// ── project-set.js: set from stdin ──────────────────────────────────────────
console.log("\n── project-set.js ──────────────────────────────────────────");

{
  const store = mkTmpDir();
  const profile = { name: "myproj", repos: [{ url: "https://h/r.git" }], sdks: { node: ["22"] }, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  const r = runSetJs(store, ["myproj"], JSON.stringify(profile));
  ok("set stdin: exits 0", r.status === 0, "exit=" + r.status + " stderr=" + r.stderr);
  const written = fs.readFileSync(path.join(store, "myproj.json"), "utf8");
  const expected = projects.canonicalProfileJson("myproj", profile);
  ok("set stdin: canonical output", written === expected,
    "got " + JSON.stringify(written.slice(0, 80)) + " vs " + JSON.stringify((expected || "").slice(0, 80)));
  cleanup(store);
}

// ── set from --file ──────────────────────────────────────────────────────────
{
  const store = mkTmpDir();
  const tmp = mkTmpDir();
  const profile = { name: "fromfile", repos: [], sdks: {}, mcp: [], hostPackages: [], provisionCommands: ["npm ci"], tests: {} };
  const filePath = path.join(tmp, "input.json");
  fs.writeFileSync(filePath, JSON.stringify(profile));
  const r = runSetJs(store, ["fromfile", "--file", filePath]);
  ok("set --file: exits 0", r.status === 0, "exit=" + r.status + " stderr=" + r.stderr);
  ok("set --file: file written", fs.existsSync(path.join(store, "fromfile.json")));
  cleanup(store); cleanup(tmp);
}

// ── canonical output: trailing newline, key order ────────────────────────────
{
  const store = mkTmpDir();
  const profile = { tests: {}, name: "order", provisionCommands: [], mcp: [], hostPackages: [], sdks: { python: "3" }, repos: [{ url: "u" }] };
  runSetJs(store, ["order"], JSON.stringify(profile));
  const written = fs.readFileSync(path.join(store, "order.json"), "utf8");
  ok("canonical: trailing newline", written.endsWith("\n"));
  ok("canonical: not double newline", !written.endsWith("\n\n"));
  // Key order: name, repos, sdks, mcp, hostPackages, provisionCommands, tests
  const parsed = JSON.parse(written);
  const keys = Object.keys(parsed);
  ok("canonical: key order", eq(keys, ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "tests"]),
    "got " + JSON.stringify(keys));
  cleanup(store);
}

// ── name injected when body omits it ─────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["injected"], JSON.stringify({ repos: [] }));
  ok("name inject: exits 0", r.status === 0, "exit=" + r.status + " stderr=" + r.stderr);
  const parsed = JSON.parse(fs.readFileSync(path.join(store, "injected.json"), "utf8"));
  ok("name inject: name is the CLI arg", parsed.name === "injected");
  cleanup(store);
}

// ── name-mismatch rejection (exit 2) ────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["expected"], JSON.stringify({ name: "different", repos: [] }));
  ok("name mismatch: exit 2", r.status === 2, "exit=" + r.status);
  ok("name mismatch: stderr lists error", r.stderr.includes('"name"'), "stderr=" + r.stderr);
  ok("name mismatch: no file written", !fs.existsSync(path.join(store, "expected.json")));
  cleanup(store);
}

// ── invalid profile rejection (exit 2) ──────────────────────────────────────
{
  const store = mkTmpDir();
  // repos is a string instead of an array => validation error
  const r = runSetJs(store, ["bad"], JSON.stringify({ name: "bad", repos: "notarray" }));
  ok("invalid profile: exit 2", r.status === 2, "exit=" + r.status);
  ok("invalid profile: stderr has error", r.stderr.includes("repos"), "stderr=" + r.stderr);
  cleanup(store);
}

// ── invalid JSON parse (exit 2) ─────────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["x"], "{{not json");
  ok("parse error: exit 2", r.status === 2);
  ok("parse error: stderr mentions parse", r.stderr.toLowerCase().includes("parse") || r.stderr.toLowerCase().includes("json"));
  cleanup(store);
}

// ── reserved name: default (exit 3) ─────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["default"], JSON.stringify({ name: "default" }));
  ok("reserved default: exit 3", r.status === 3, "exit=" + r.status);
  ok("reserved default: no file", !fs.existsSync(path.join(store, "default.json")));
}

// ── reserved name: project.schema (exit 3) ──────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["project.schema"], JSON.stringify({ name: "project.schema" }));
  ok("reserved project.schema: exit 3", r.status === 3, "exit=" + r.status);
}

// ── reserved name: case-insensitive ─────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runSetJs(store, ["DEFAULT"], JSON.stringify({ name: "DEFAULT" }));
  ok("reserved DEFAULT (case): exit 3", r.status === 3);
  cleanup(store);
}

// ── get roundtrip ────────────────────────────────────────────────────────────
{
  const store = mkTmpDir();
  const profile = { name: "roundtrip", repos: [{ url: "https://g/r.git" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  runSetJs(store, ["roundtrip"], JSON.stringify(profile));
  // Now use project-set.js isn't needed for get — the CLI does cat. But let's
  // verify the round-trip via the CLI below.
  const content = fs.readFileSync(path.join(store, "roundtrip.json"), "utf8");
  ok("get roundtrip: file parses", (() => { try { JSON.parse(content); return true; } catch (_) { return false; } })());
  ok("get roundtrip: canonical match", content === projects.canonicalProfileJson("roundtrip", profile));
  cleanup(store);
}

// ── get absent (exit 1) ─────────────────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runCli(store, ["project", "get", "nosuch"]);
  ok("get absent: exit 1", r.status === 1, "exit=" + r.status);
  cleanup(store);
}

// ── list ─────────────────────────────────────────────────────────────────────
{
  const store = mkTmpDir();
  fs.writeFileSync(path.join(store, "alpha.json"), "{}");
  fs.writeFileSync(path.join(store, "beta.json"), "{}");
  const r = runCli(store, ["project", "list"]);
  ok("list: exits 0", r.status === 0);
  const names = r.stdout.trim().split("\n").sort();
  ok("list: contains both", eq(names, ["alpha", "beta"]), "got " + JSON.stringify(names));
  cleanup(store);
}

// ── list: empty store ────────────────────────────────────────────────────────
{
  const store = mkTmpDir();
  const r = runCli(store, ["project", "list"]);
  ok("list empty: exits 0", r.status === 0);
  ok("list empty: no output", r.stdout.trim() === "");
  cleanup(store);
}

// ── atomic write: no .tmp leftovers ─────────────────────────────────────────
{
  const store = mkTmpDir();
  const profile = { name: "atomic", repos: [], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  runSetJs(store, ["atomic"], JSON.stringify(profile));
  const files = fs.readdirSync(store);
  ok("atomic: only .json, no .tmp", files.every(f => !f.includes(".tmp")), "files=" + JSON.stringify(files));
  cleanup(store);
}

// ── construct CLI: project list/get ─────────────────────────────────────────
console.log("\n── construct CLI ───────────────────────────────────────────");

{
  const store = mkTmpDir();
  const profile = { name: "cliproj", repos: [{ url: "https://h/c.git" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  // Write via the CLI's set subcommand.
  const rSet = runCli(store, ["project", "set", "cliproj"], { input: JSON.stringify(profile) });
  ok("cli set: exits 0", rSet.status === 0, "exit=" + rSet.status + " stderr=" + rSet.stderr);

  // Read back via get.
  const rGet = runCli(store, ["project", "get", "cliproj"]);
  ok("cli get: exits 0", rGet.status === 0);
  ok("cli get: canonical content", rGet.stdout === projects.canonicalProfileJson("cliproj", profile));

  // List.
  const rList = runCli(store, ["project", "list"]);
  ok("cli list: includes cliproj", rList.stdout.trim().split("\n").includes("cliproj"));
  cleanup(store);
}

// ── construct CLI: node-missing path (exit 4) ───────────────────────────────
{
  const store = mkTmpDir();
  // Create a shim directory that has symlinks to bash utilities but NOT node.
  // This avoids the problem where node and bash share the same directory.
  const shimDir = mkTmpDir();
  const essentials = ["bash", "cat", "basename", "dirname", "install", "printf",
    "sed", "grep", "xargs", "mktemp", "chmod", "mkdir", "rm", "mv", "env",
    "id", "ls", "pwd", "readlink", "realpath", "cd", "shopt", "test"];
  for (const cmd of essentials) {
    const real = spawnSync("which", [cmd], { encoding: "utf8" }).stdout.trim();
    if (real && fs.existsSync(real)) {
      try { fs.symlinkSync(real, path.join(shimDir, cmd)); } catch (_) { /* ignore */ }
    }
  }

  const r = spawnSync(path.join(shimDir, "bash"), [CONSTRUCT_CLI, "project", "set", "test"], {
    env: { PATH: shimDir, PROJECTS_STORE: store, CONSTRUCT_REPO_DIR: REPO_ROOT, HOME: process.env.HOME || "/root" },
    input: '{"name":"test"}',
    encoding: "utf8",
    timeout: 10000,
  });
  ok("cli no-node: exit 4", r.status === 4, "exit=" + r.status + " stderr=" + (r.stderr || ""));
  ok("cli no-node: hint in stderr", (r.stderr || "").includes("node") || (r.stderr || "").includes("directly"),
    "stderr=" + (r.stderr || ""));
  cleanup(store);
  cleanup(shimDir);
}

// ── construct CLI: help ──────────────────────────────────────────────────────
{
  const r = runCli("/tmp", ["--help"]);
  ok("cli help: exits 0", r.status === 0);
  ok("cli help: shows usage", r.stdout.includes("Usage") || r.stdout.includes("construct project"));
}

// ── generate-runtime-config.sh ──────────────────────────────────────────────
console.log("\n── generate-runtime-config.sh ──────────────────────────────");

{
  // Build a self-contained test fixture:
  //   AGENT_HOME/        (temp)
  //     projects/        (PROJECTS_STORE)
  //       custom.json    valid custom profile
  //       badjson.json   invalid JSON
  //       default.json   stale (should be IGNORED)
  //     runtime/         (created by the script)
  //   REPO_DIR/
  //     projects/
  //       default.json   shipped default
  //       project.schema.json
  //       customer-portal.json.sample
  //   CONFIG_FILE        minimal config.env

  const base = mkTmpDir();
  const agentHome = path.join(base, "agent");
  const repoDir = path.join(base, "repo");
  const storeDir = path.join(agentHome, "projects");
  const repoProjects = path.join(repoDir, "projects");
  const configFile = path.join(base, "config.env");

  fs.mkdirSync(storeDir, { recursive: true });
  fs.mkdirSync(repoProjects, { recursive: true });

  // Shipped default.json (the repo's copy).
  const defaultProfile = { name: "default", repos: [], sdks: { node: ["22"] }, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  fs.writeFileSync(path.join(repoProjects, "default.json"), JSON.stringify(defaultProfile, null, 2) + "\n");

  // Schema file.
  fs.copyFileSync(path.join(REPO_ROOT, "projects", "project.schema.json"), path.join(repoProjects, "project.schema.json"));

  // Sample file.
  fs.writeFileSync(path.join(repoProjects, "customer-portal.json.sample"), '{"name":"customer-portal"}');

  // Store: valid custom profile.
  const customProfile = { name: "custom", repos: [{ url: "https://g/c.git" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  fs.writeFileSync(path.join(storeDir, "custom.json"), JSON.stringify(customProfile, null, 2) + "\n");

  // Store: invalid JSON file.
  fs.writeFileSync(path.join(storeDir, "badjson.json"), "{broken");

  // Store: stale default.json (should be ignored with a warning).
  fs.writeFileSync(path.join(storeDir, "default.json"), '{"name":"default","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}');

  // config.env
  fs.writeFileSync(configFile, "AGENT_HOME=" + agentHome + "\n");

  // -- Test 1: resolve custom + default, skip badjson and project.schema ------
  const env1 = {
    PATH: process.env.PATH,
    CONFIG_FILE: configFile,
    AGENT_HOME: agentHome,
    REPO_DIR: repoDir,
    PROJECTS: "default,custom,badjson,project.schema",
    PROJECTS_STORE: storeDir,
    FORCE_COLOR: "",  // disable colour for parsing
  };

  const r1 = spawnSync("bash", [GEN_SCRIPT], { env: env1, encoding: "utf8", timeout: 15000 });
  ok("gen: exits 0 with valid profiles", r1.status === 0, "exit=" + r1.status + " stderr=" + r1.stderr.slice(0, 300));

  // Check generated.json exists and has the right project list.
  const genJson = path.join(agentHome, "runtime", "generated.json");
  ok("gen: generated.json exists", fs.existsSync(genJson));
  const gen = JSON.parse(fs.readFileSync(genJson, "utf8"));
  ok("gen: projects includes default", gen.projects.includes("default"));
  ok("gen: projects includes custom", gen.projects.includes("custom"));
  ok("gen: projects does NOT include badjson", !gen.projects.includes("badjson"),
    "projects=" + JSON.stringify(gen.projects));
  ok("gen: projects does NOT include project.schema", !gen.projects.includes("project.schema"));

  // Check warnings for badjson and project.schema.
  const combined1 = r1.stdout + r1.stderr;
  ok("gen: warns about invalid JSON", combined1.includes("badjson") && combined1.toLowerCase().includes("invalid"));
  ok("gen: warns about project.schema", combined1.includes("project.schema") && combined1.toLowerCase().includes("reserved"));

  // Check stale default.json warning.
  ok("gen: warns about stale store default", combined1.includes("stale") || combined1.includes("Ignoring"),
    "combined=" + combined1.slice(0, 500));

  // Check generated.env exists.
  const genEnv = path.join(agentHome, "runtime", "generated.env");
  ok("gen: generated.env exists", fs.existsSync(genEnv));

  // -- Test 2: all missing -> falls back to shipped default -------------------
  const env2 = { ...env1, PROJECTS: "nosuch,alsomissing" };
  // Clean the runtime dir.
  fs.rmSync(path.join(agentHome, "runtime"), { recursive: true, force: true });
  const r2 = spawnSync("bash", [GEN_SCRIPT], { env: env2, encoding: "utf8", timeout: 15000 });
  ok("gen fallback: exits 0", r2.status === 0, "exit=" + r2.status);
  const gen2 = JSON.parse(fs.readFileSync(genJson, "utf8"));
  ok("gen fallback: falls back to default", gen2.projects.includes("default"),
    "projects=" + JSON.stringify(gen2.projects));

  // -- Test 3: sample hint ---------------------------------------------------
  const env3 = { ...env1, PROJECTS: "customer-portal" };
  fs.rmSync(path.join(agentHome, "runtime"), { recursive: true, force: true });
  const r3 = spawnSync("bash", [GEN_SCRIPT], { env: env3, encoding: "utf8", timeout: 15000 });
  const combined3 = r3.stdout + r3.stderr;
  ok("gen sample: shows rename hint", combined3.includes("sample") && combined3.includes("rename"),
    "combined=" + combined3.slice(0, 300));

  // -- Test 4: only default in PROJECTS (no store access needed) ─────────────
  const env4 = { ...env1, PROJECTS: "default" };
  fs.rmSync(path.join(agentHome, "runtime"), { recursive: true, force: true });
  const r4 = spawnSync("bash", [GEN_SCRIPT], { env: env4, encoding: "utf8", timeout: 15000 });
  ok("gen default-only: exits 0", r4.status === 0, "exit=" + r4.status);
  const gen4 = JSON.parse(fs.readFileSync(genJson, "utf8"));
  ok("gen default-only: exactly default", eq(gen4.projects, ["default"]),
    "projects=" + JSON.stringify(gen4.projects));

  // -- Test 5: path-traversal profile name is rejected, not resolved ----------
  // Plant a secret file OUTSIDE the store (a sibling of PROJECTS_STORE) that a
  // "../secret" name would otherwise resolve to.
  const secretProfile = { name: "secret", repos: [{ url: "https://g/secret.git" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
  fs.writeFileSync(path.join(agentHome, "secret.json"), JSON.stringify(secretProfile, null, 2) + "\n");
  const env5 = { ...env1, PROJECTS: "../secret,custom" };
  fs.rmSync(path.join(agentHome, "runtime"), { recursive: true, force: true });
  const r5 = spawnSync("bash", [GEN_SCRIPT], { env: env5, encoding: "utf8", timeout: 15000 });
  ok("gen traversal: exits 0 (skips unsafe, keeps going)", r5.status === 0, "exit=" + r5.status);
  const gen5 = JSON.parse(fs.readFileSync(genJson, "utf8"));
  ok("gen traversal: '../secret' NOT resolved (secret excluded)", !gen5.projects.includes("secret"),
    "projects=" + JSON.stringify(gen5.projects));
  ok("gen traversal: safe sibling name still resolved", gen5.projects.includes("custom"));
  const combined5 = r5.stdout + r5.stderr;
  ok("gen traversal: warns about the unsafe name", combined5.toLowerCase().includes("unsafe"));

  cleanup(base);
}

// ── construct CLI: path-traversal name is rejected (get/set) ─────────────────
console.log("\n── construct CLI path-traversal guard ──────────────────────");
{
  const base = mkTmpDir();
  const store = path.join(base, "store");
  fs.mkdirSync(store, { recursive: true });
  // A secret profile OUTSIDE the store that `get ../secret` would otherwise read.
  fs.writeFileSync(path.join(base, "secret.json"), '{"name":"secret"}');
  // A legit profile inside the store to prove non-malicious get still works.
  fs.writeFileSync(path.join(store, "ok.json"), '{"name":"ok"}');

  const rGetEvil = runCli(store, ["project", "get", "../secret"]);
  ok("cli get: '../secret' rejected (nonzero exit)", rGetEvil.status !== 0, "exit=" + rGetEvil.status);
  ok("cli get: '../secret' does NOT leak the outside file", !(rGetEvil.stdout || "").includes("secret"),
    "stdout=" + (rGetEvil.stdout || "").slice(0, 120));
  ok("cli get: invalid-name message", (rGetEvil.stderr || "").toLowerCase().includes("invalid profile name"));

  const rGetOk = runCli(store, ["project", "get", "ok"]);
  ok("cli get: a safe name still works", rGetOk.status === 0 && (rGetOk.stdout || "").includes('"ok"'));

  const rSetEvil = runCli(store, ["project", "set", "../evil"], { input: '{"name":"evil"}' });
  ok("cli set: '../evil' rejected before any write", rSetEvil.status !== 0 && !fs.existsSync(path.join(base, "evil.json")));

  cleanup(base);
}

// ── bash -n syntax check ────────────────────────────────────────────────────
console.log("\n── syntax checks ──────────────────────────────────────────");

{
  const r = spawnSync("bash", ["-n", GEN_SCRIPT], { encoding: "utf8", timeout: 5000 });
  ok("bash -n generate-runtime-config.sh", r.status === 0, "stderr=" + r.stderr);
}
{
  const r = spawnSync("bash", ["-n", CONSTRUCT_CLI], { encoding: "utf8", timeout: 5000 });
  ok("bash -n construct", r.status === 0, "stderr=" + r.stderr);
}
{
  const r = spawnSync(process.execPath, ["--check", PROJECT_SET_JS], { encoding: "utf8", timeout: 5000 });
  ok("node --check project-set.js", r.status === 0, "stderr=" + r.stderr);
}

// ── summary ─────────────────────────────────────────────────────────────────
console.log("\n" + (fail ? "FAIL" : "OK") + "  " + pass + " passed, " + fail + " failed");
process.exit(fail ? 1 : 0);
