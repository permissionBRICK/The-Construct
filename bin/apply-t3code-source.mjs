#!/usr/bin/env node
// Apply Construct's T3 Code source inventory (overlays + guarded transforms) to an
// upstream checkout, or report whether it would apply ("status").
//
// Manifest version 2 — every transform touches the MINIMUM it needs:
//
//   { "path": "a.ts", "after":  "<anchor>", "insert": "<lines>" }   insert after the anchor line
//   { "path": "a.ts", "before": "<anchor>", "insert": "<lines>" }   insert before the anchor line
//   { "path": "a.ts", "find":   "<text>",   "replace": "<text>" }   replace the text itself
//
//   * An anchor is the smallest fragment that identifies the spot — usually one
//     line. Leading whitespace on every anchor line is ignored, so reformatting or
//     re-indenting upstream never breaks a match; only the code we depend on does.
//   * Inserts are line-based: the anchor picks a line, the insert text (full lines,
//     with their own indentation) goes after/before it.
//   * An anchor/find may be an ARRAY of alternatives: the first one that matches wins
//     (one inventory can cover several upstream versions).
//   * "scope": "<text>" starts the search after that text — only for the rare anchor
//     that occurs more than once in the file.
//   * "optional": true — a missing file or anchor is skipped, not a conflict (used
//     for upstream TEST files: they never affect the built server/Desktop app).
//   * "channel": "stable" | "nightly" — this transform belongs to that channel's
//     build only (--channel). Used when the two live channels need different text
//     at one spot; the stale half is deleted once the channels agree again.
//   * Already applied (insert text / replacement present) is a no-op, so the same
//     tree can be transformed again after an interrupted build.
//
// Version 1 manifests ({ path, old, new } literal hunks) still apply unchanged.

import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs"
import { dirname, isAbsolute, join, relative, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const scriptDir = dirname(fileURLToPath(import.meta.url))
const constructDir = resolve(scriptDir, "..")
const args = process.argv.slice(2)
const mode = args[0]
const valueAfter = (flag, fallback) => {
  const index = args.indexOf(flag)
  return index >= 0 && args[index + 1] ? resolve(args[index + 1]) : fallback
}
const sourceDir = valueAfter("--source", process.cwd())
const manifestPath = valueAfter("--manifest", join(constructDir, "patches", "t3code", "source-transforms.json"))
const overlayDir = valueAfter("--overlays", join(constructDir, "patches", "t3code", "overlays"))
// The T3 channel being built (stable|nightly). A transform carrying "channel" is one
// half of a pair written separately for each channel; the other half is skipped.
const channelIndex = args.indexOf("--channel")
const channel = channelIndex >= 0 && args[channelIndex + 1] === "nightly" ? "nightly" : "stable"

if (mode !== "apply" && mode !== "status") {
  console.error("usage: apply-t3code-source.mjs apply|status [--source DIR] [--manifest FILE] [--overlays DIR] [--channel stable|nightly]")
  process.exit(1)
}

const manifest = JSON.parse(readFileSync(manifestPath, "utf8"))
if (![1, 2].includes(manifest.version) || !Array.isArray(manifest.overlays) || !Array.isArray(manifest.transforms)) {
  throw new Error(`unsupported T3 source-transform manifest: ${manifestPath}`)
}

function targetPath(root, path) {
  if (isAbsolute(path) || path.split("/").includes("..")) throw new Error(`unsafe manifest path: ${path}`)
  const target = resolve(root, path)
  if (relative(root, target).startsWith("..")) throw new Error(`manifest path escapes source root: ${path}`)
  return target
}

// ── matching ───────────────────────────────────────────────────────────────────
// Find `needle` in `text` (after `from`). Exact substring first; failing that, the
// same text with the leading whitespace of every line left flexible. Returns every
// match as { start, end } so callers can insist on exactly one.
function lenientPattern(needle) {
  const lines = needle.split("\n").map((line) => line.replace(/^[ \t]+/, ""))
  const escaped = lines.map((line) => line.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
  return new RegExp(escaped.join("\\n[ \\t]*"), "g")
}

function findAll(text, needle, from = 0) {
  if (!needle) return []
  const hits = []
  for (let offset = from; (offset = text.indexOf(needle, offset)) !== -1; offset += needle.length) {
    hits.push({ start: offset, end: offset + needle.length, how: "exact" })
  }
  if (hits.length) return hits
  const pattern = lenientPattern(needle)
  pattern.lastIndex = from
  for (let match; (match = pattern.exec(text)); ) {
    hits.push({ start: match.index, end: match.index + match[0].length, how: "lenient" })
    if (match[0].length === 0) pattern.lastIndex++
  }
  return hits
}

const asList = (value) => (Array.isArray(value) ? value : [value])

// Locate one of the alternative anchors. Without a scope the anchor must occur exactly
// once; with a scope the first occurrence after the scope text is the one. Returns
// { hit, anchor } or null; `ambiguous` collects anchors that matched several times.
function locate(text, alternatives, scope, ambiguous) {
  let from = 0
  if (scope) {
    const scoped = findAll(text, scope)
    if (!scoped.length) return null
    from = scoped[0].end
  }
  for (const anchor of alternatives) {
    const hits = findAll(text, anchor, from)
    if (hits.length === 1 || (scope && hits.length)) return { hit: hits[0], anchor }
    if (hits.length > 1) ambiguous.push(anchor)
  }
  return null
}

const lineStart = (text, index) => text.lastIndexOf("\n", index - 1) + 1
const lineEnd = (text, index) => {
  const next = text.indexOf("\n", index)
  return next === -1 ? text.length : next + 1
}
const asLines = (text) => (text.endsWith("\n") ? text : `${text}\n`)

// Apply one transform to `content`. Returns { state, content?, detail? } where state is
// applied | pending | skipped | conflict.
function runTransform(operation, content) {
  if (operation.old !== undefined) {
    // v1 literal hunk
    if (findAll(content, operation.new).length === 1) return { state: "applied" }
    const hits = findAll(content, operation.old)
    if (hits.length !== 1) return { state: "conflict", detail: `expected one source anchor, found old=${hits.length}` }
    return { state: "pending", content: content.slice(0, hits[0].start) + operation.new + content.slice(hits[0].end) }
  }
  const ambiguous = []
  if (operation.insert !== undefined) {
    const insert = asLines(operation.insert)
    if (content.includes(insert)) return { state: "applied" }
    const where = operation.after !== undefined ? "after" : "before"
    if (operation.every) {
      // The same insert next to EVERY occurrence (an upstream prop passed at several call sites).
      const hits = asList(operation[where]).map((anchor) => findAll(content, anchor)).find((found) => found.length)
      if (!hits) return { state: operation.optional ? "skipped" : "conflict", detail: `${where} anchor not found` }
      let updated = content
      for (const hit of [...hits].reverse()) {
        const at = where === "after" ? lineEnd(updated, hit.end) : lineStart(updated, hit.start)
        updated = updated.slice(0, at) + insert + updated.slice(at)
      }
      return { state: "pending", content: updated, detail: hits[0].how }
    }
    const found = locate(content, asList(operation[where]), operation.scope, ambiguous)
    if (!found) return { state: operation.optional ? "skipped" : "conflict", detail: describeMiss(where, ambiguous) }
    const at = where === "after" ? lineEnd(content, found.hit.end) : lineStart(content, found.hit.start)
    return { state: "pending", content: content.slice(0, at) + insert + content.slice(at), detail: found.hit.how }
  }
  if (operation.find !== undefined) {
    if (content.includes(operation.replace)) return { state: "applied" }
    const found = locate(content, asList(operation.find), operation.scope, ambiguous)
    if (!found) return { state: operation.optional ? "skipped" : "conflict", detail: describeMiss("find", ambiguous) }
    return {
      state: "pending",
      content: content.slice(0, found.hit.start) + operation.replace + content.slice(found.hit.end),
      detail: found.hit.how,
    }
  }
  return { state: "conflict", detail: "transform has neither insert, find nor old" }
}

function describeMiss(kind, ambiguous) {
  return ambiguous.length ? `${kind} anchor is not unique: ${JSON.stringify(ambiguous[0])}` : `${kind} anchor not found`
}

// ── plan ───────────────────────────────────────────────────────────────────────
const files = new Map()
const conflicts = []
const results = []
let pending = 0
let alreadyApplied = 0
let skipped = 0

for (const path of manifest.overlays) {
  const destination = targetPath(sourceDir, path)
  const expected = readFileSync(targetPath(overlayDir, path), "utf8")
  if (!existsSync(destination)) {
    files.set(destination, expected)
    pending++
  } else if (readFileSync(destination, "utf8") === expected) {
    alreadyApplied++
  } else {
    conflicts.push(`${path}: Construct overlay collides with an upstream file`)
  }
}

manifest.transforms.forEach((operation, index) => {
  const label = `${operation.path}#${index}`
  if (operation.channel !== undefined && operation.channel !== channel) {
    skipped++
    results.push({ transform: label, state: "skipped", detail: `${operation.channel} channel only` })
    return
  }
  const destination = targetPath(sourceDir, operation.path)
  if (!existsSync(destination) && !files.has(destination)) {
    if (operation.optional) {
      skipped++
      results.push({ transform: label, state: "skipped", detail: "file missing" })
      return
    }
    conflicts.push(`${label}: upstream file is missing`)
    results.push({ transform: label, state: "conflict", detail: "file missing" })
    return
  }
  const content = files.has(destination) ? files.get(destination) : readFileSync(destination, "utf8")
  const result = runTransform(operation, content)
  results.push({ transform: label, state: result.state, ...(result.detail ? { detail: result.detail } : {}) })
  if (result.state === "applied") alreadyApplied++
  else if (result.state === "skipped") skipped++
  else if (result.state === "pending") {
    files.set(destination, result.content)
    pending++
  } else conflicts.push(`${label}: ${result.detail}`)
})

if (mode === "status") {
  console.log(JSON.stringify({ compatible: conflicts.length === 0, pending, alreadyApplied, skipped, conflicts, results }))
  process.exit(conflicts.length ? 2 : 0)
}
if (conflicts.length) {
  for (const conflict of conflicts) console.error(`t3-source-transform: ${conflict}`)
  process.exit(1)
}

for (const [destination, content] of files) {
  mkdirSync(dirname(destination), { recursive: true })
  const temporary = `${destination}.construct-tmp-${process.pid}`
  writeFileSync(temporary, content)
  renameSync(temporary, destination)
}
for (const result of results) {
  if (result.state === "skipped") console.log(`t3-source-transform: skipped optional ${result.transform} (${result.detail})`)
  else if (result.detail === "lenient") console.log(`t3-source-transform: ${result.transform} matched with re-indented anchor`)
}
console.log(`t3-source-transform: applied ${pending} operation(s); ${alreadyApplied} already present; ${skipped} optional skipped`)
