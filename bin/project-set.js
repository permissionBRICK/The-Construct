"use strict";
// project-set.js — validate + write a project profile in canonical form.
//
// Used by `construct project set <name>` on the VM. Requires node (the shared
// validateProfile / canonicalProfileJson from extension/src/projects.js run in
// node). Reads the profile JSON from --file <path> or stdin, validates it
// strictly, and writes the canonical form atomically into PROJECTS_STORE.
//
// Exit codes:
//   0  success
//   1  general error (missing file, I/O)
//   2  validation or parse error
//   3  reserved name
//
// Environment:
//   PROJECTS_STORE  override for /opt/construct/projects
//
// Run: node bin/project-set.js <name> [--file <path>]
//      echo '{"name":"x",...}' | node bin/project-set.js x

const fs = require("fs");
const path = require("path");

// Locate extension/src/projects.js relative to this script's directory — works
// both in the repo checkout (/opt/construct/repo/bin/project-set.js ->
// ../extension/src/projects.js) and during tests (same relative layout).
const projects = require(path.join(__dirname, "..", "extension", "src", "projects"));

const STORE = process.env.PROJECTS_STORE || "/opt/construct/projects";

// -- argument parsing ---------------------------------------------------------

function die(msg, code) {
  process.stderr.write(msg + "\n");
  process.exit(code);
}

const args = process.argv.slice(2);
if (args.length < 1) die("Usage: project-set.js <name> [--file <path>]", 1);

const name = args[0];
let filePath = null;
for (let i = 1; i < args.length; i++) {
  if (args[i] === "--file") {
    i++;
    if (i >= args.length) die("--file requires a path", 1);
    filePath = args[i];
  }
}

// -- reserved name gate -------------------------------------------------------

if (projects.isReservedProfileName(name)) {
  die('"' + name + '" is a reserved name (default and project.schema cannot be used)', 3);
}

// -- read input ---------------------------------------------------------------

function readInput() {
  if (filePath) {
    try {
      return fs.readFileSync(filePath, "utf8");
    } catch (e) {
      die("Cannot read file: " + filePath + " (" + e.message + ")", 1);
    }
  }
  // Read from stdin (pipe/redirect — not interactive).
  try {
    return fs.readFileSync(0, "utf8");
  } catch (e) {
    die("Cannot read stdin: " + e.message, 1);
  }
}

const raw = readInput();

// -- parse --------------------------------------------------------------------

let obj;
try {
  obj = JSON.parse(raw);
} catch (e) {
  die("JSON parse error: " + e.message, 2);
}

if (typeof obj !== "object" || obj === null || Array.isArray(obj)) {
  die("Profile must be a JSON object", 2);
}

// Inject name when the body omits it (convenience for piped input).
if (!obj.name || (typeof obj.name === "string" && obj.name.trim() === "")) {
  obj.name = name;
}

// -- validate -----------------------------------------------------------------

const result = projects.validateProfile(name, obj);
if (!result.ok) {
  for (const e of result.errors) {
    process.stderr.write(e + "\n");
  }
  process.exit(2);
}

// -- write canonical form atomically ------------------------------------------

const canonical = projects.canonicalProfileJson(name, obj);
if (canonical == null) {
  die("canonicalProfileJson returned null (empty name?)", 2);
}

// mkdir -p equivalent for the store directory.
try {
  fs.mkdirSync(STORE, { recursive: true });
} catch (e) {
  // Already exists is fine.
  if (e.code !== "EEXIST") die("Cannot create store: " + STORE + " (" + e.message + ")", 1);
}

const target = path.join(STORE, name + ".json");
const tmp = target + ".tmp-" + process.pid;
try {
  fs.writeFileSync(tmp, canonical, "utf8");
  fs.renameSync(tmp, target);
} catch (e) {
  // Clean up the temp file on failure.
  try { fs.unlinkSync(tmp); } catch (_) { /* ignore */ }
  die("Write failed: " + e.message, 1);
}

process.stdout.write(target + "\n");
