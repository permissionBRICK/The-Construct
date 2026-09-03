#!/usr/bin/env node
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
const manifestPath = valueAfter("--manifest", join(constructDir, "patches", "t3code-release", "source-transforms.json"))
const overlayDir = valueAfter("--overlays", join(constructDir, "patches", "t3code-release", "overlays"))

if (mode !== "apply" && mode !== "status") {
  console.error("usage: apply-t3code-source.mjs apply|status [--source DIR] [--manifest FILE] [--overlays DIR]")
  process.exit(1)
}

const manifest = JSON.parse(readFileSync(manifestPath, "utf8"))
if (manifest.version !== 1 || !Array.isArray(manifest.overlays) || !Array.isArray(manifest.transforms)) {
  throw new Error(`unsupported T3 source-transform manifest: ${manifestPath}`)
}

function targetPath(root, path) {
  if (isAbsolute(path) || path.split("/").includes("..")) throw new Error(`unsafe manifest path: ${path}`)
  const target = resolve(root, path)
  if (relative(root, target).startsWith("..")) throw new Error(`manifest path escapes source root: ${path}`)
  return target
}

function occurrences(text, needle) {
  if (!needle) return 0
  let count = 0
  for (let offset = 0; (offset = text.indexOf(needle, offset)) !== -1; offset += needle.length) count++
  return count
}

const files = new Map()
const conflicts = []
let pending = 0
let alreadyApplied = 0

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

for (const operation of manifest.transforms) {
  const destination = targetPath(sourceDir, operation.path)
  if (!existsSync(destination)) {
    conflicts.push(`${operation.path}: upstream file is missing`)
    continue
  }
  let content = files.has(destination) ? files.get(destination) : readFileSync(destination, "utf8")
  const oldCount = occurrences(content, operation.old)
  const newCount = occurrences(content, operation.new)
  if (newCount === 1) {
    alreadyApplied++
  } else if (oldCount === 1) {
    content = content.replace(operation.old, operation.new)
    files.set(destination, content)
    pending++
  } else {
    conflicts.push(`${operation.path}: expected one source anchor, found old=${oldCount} new=${newCount}`)
  }
}

if (mode === "status") {
  console.log(JSON.stringify({ compatible: conflicts.length === 0, pending, alreadyApplied, conflicts }))
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
console.log(`t3-source-transform: applied ${pending} operation(s); ${alreadyApplied} already present`)
