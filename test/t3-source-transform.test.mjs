import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"

const script = new URL("../bin/apply-t3code-source.mjs", import.meta.url).pathname

function fixture({ source = "before\n", overlayExisting } = {}) {
  const root = mkdtempSync(join(tmpdir(), "t3-transform-test-"))
  const sourceDir = join(root, "source")
  const overlays = join(root, "overlays")
  mkdirSync(sourceDir); mkdirSync(overlays)
  writeFileSync(join(sourceDir, "owned.ts"), source)
  mkdirSync(join(overlays, "new"), { recursive: true })
  writeFileSync(join(overlays, "new", "construct.ts"), "export const construct = true\n")
  if (overlayExisting !== undefined) {
    mkdirSync(join(sourceDir, "new"), { recursive: true })
    writeFileSync(join(sourceDir, "new", "construct.ts"), overlayExisting)
  }
  const manifest = join(root, "manifest.json")
  writeFileSync(manifest, JSON.stringify({
    version: 1,
    overlays: ["new/construct.ts"],
    transforms: [{ path: "owned.ts", old: "before\n", new: "after\n" }],
  }))
  return { root, sourceDir, overlays, manifest }
}

function run(f, mode = "apply") {
  return spawnSync(process.execPath, [script, mode, "--source", f.sourceDir, "--manifest", f.manifest, "--overlays", f.overlays], { encoding: "utf8" })
}

test("applies guarded transforms and overlays idempotently", () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  assert.equal(readFileSync(join(f.sourceDir, "owned.ts"), "utf8"), "after\n")
  assert.equal(readFileSync(join(f.sourceDir, "new", "construct.ts"), "utf8"), "export const construct = true\n")
  assert.equal(run(f).status, 0)
  const status = run(f, "status")
  assert.equal(status.status, 0)
  assert.equal(JSON.parse(status.stdout).alreadyApplied, 2)
  assert.equal(JSON.parse(status.stdout).pending, 0)
})

test("a missing anchor rejects the whole plan without partial writes", () => {
  const f = fixture({ source: "upstream changed\n" })
  assert.equal(run(f).status, 1)
  assert.equal(readFileSync(join(f.sourceDir, "owned.ts"), "utf8"), "upstream changed\n")
  assert.equal(run(f, "status").status, 2)
  assert.throws(() => readFileSync(join(f.sourceDir, "new", "construct.ts"), "utf8"))
})

test("an upstream collision with a Construct overlay is rejected", () => {
  const f = fixture({ overlayExisting: "export const upstream = true\n" })
  assert.equal(run(f).status, 1)
  assert.equal(readFileSync(join(f.sourceDir, "owned.ts"), "utf8"), "before\n")
})
