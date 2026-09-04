import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"

const script = new URL("../bin/apply-t3code-source.mjs", import.meta.url).pathname

const UPSTREAM = [
  'import { a } from "./a";',
  'import { b } from "./b";',
  "",
  "export const methods = {",
  '  one: "one",',
  '  two: "two",',
  "};",
  "",
  "export function run(): string {",
  '  return "stock";',
  "}",
  "",
].join("\n")

function fixture({ source = UPSTREAM, overlayExisting, transforms, version = 2 } = {}) {
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
    version,
    overlays: ["new/construct.ts"],
    transforms: transforms ?? [
      { path: "owned.ts", after: 'import { b } from "./b";', insert: 'import { construct } from "./new/construct";\n' },
      { path: "owned.ts", before: 'two: "two",', insert: '  construct: "construct",\n' },
      { path: "owned.ts", find: 'return "stock";', replace: 'return construct ? "construct" : "stock";' },
    ],
  }))
  return { root, sourceDir, overlays, manifest }
}

function run(f, mode = "apply") {
  return spawnSync(process.execPath, [script, mode, "--source", f.sourceDir, "--manifest", f.manifest, "--overlays", f.overlays], { encoding: "utf8" })
}
const owned = (f) => readFileSync(join(f.sourceDir, "owned.ts"), "utf8")
const status = (f) => { const r = run(f, "status"); return { code: r.status, ...JSON.parse(r.stdout) } }

test("applies inserts, replacements and overlays idempotently", () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  assert.equal(owned(f), [
    'import { a } from "./a";',
    'import { b } from "./b";',
    'import { construct } from "./new/construct";',
    "",
    "export const methods = {",
    '  one: "one",',
    '  construct: "construct",',
    '  two: "two",',
    "};",
    "",
    "export function run(): string {",
    '  return construct ? "construct" : "stock";',
    "}",
    "",
  ].join("\n"))
  assert.equal(readFileSync(join(f.sourceDir, "new", "construct.ts"), "utf8"), "export const construct = true\n")
  assert.equal(run(f).status, 0)
  const s = status(f)
  assert.equal(s.code, 0)
  assert.equal(s.alreadyApplied, 4)
  assert.equal(s.pending, 0)
})

test("anchors ignore indentation and unrelated changes elsewhere in the file", () => {
  const reindented = UPSTREAM
    .replace('  two: "two",', '\t\ttwo: "two", // renamed comment')
    .replace('  return "stock";', '      return "stock";')
    .replace('import { a } from "./a";', 'import { a, a2 } from "./a";\nimport { z } from "./z";')
  const f = fixture({ source: reindented })
  assert.equal(run(f).status, 0)
  const out = owned(f)
  assert.match(out, /import \{ b \} from "\.\/b";\nimport \{ construct \} from "\.\/new\/construct";\n/)
  assert.match(out, /  construct: "construct",\n\t\ttwo: "two", \/\/ renamed comment/)
  assert.match(out, /      return construct \? "construct" : "stock";/)
})

test("an insert already present upstream is a no-op, not a duplicate", () => {
  const f = fixture({ source: UPSTREAM.replace('import { b } from "./b";', 'import { b } from "./b";\nimport { construct } from "./new/construct";') })
  assert.equal(run(f).status, 0)
  assert.equal(owned(f).match(/import \{ construct \}/g).length, 1)
})

test("alternatives and scope pick the spot when a single line is ambiguous", () => {
  const source = UPSTREAM + '\nexport function other(): string {\n  return "stock";\n}\n'
  const f = fixture({
    source,
    transforms: [
      { path: "owned.ts", after: ['import { nope } from "./nope";', 'import { b } from "./b";'], insert: "// alt\n" },
      { path: "owned.ts", scope: "export function other()", find: 'return "stock";', replace: 'return "other";' },
    ],
  })
  assert.equal(run(f).status, 0)
  const out = owned(f)
  assert.match(out, /import \{ b \} from "\.\/b";\n\/\/ alt\n/)
  assert.match(out, /export function run\(\): string \{\n  return "stock";/)
  assert.match(out, /export function other\(\): string \{\n  return "other";/)
})

test("every: true inserts next to each occurrence of the anchor", () => {
  const source = 'a();\nuse(x);\nb();\nuse(x);\n'
  const f = fixture({ source, transforms: [{ path: "owned.ts", after: "use(x);", insert: "extra();\n", every: true }] })
  assert.equal(run(f).status, 0)
  assert.equal(owned(f), 'a();\nuse(x);\nextra();\nb();\nuse(x);\nextra();\n')
  assert.equal(run(f).status, 0)
  assert.equal(status(f).alreadyApplied, 2)
})

test("channel: a transform tagged for the other channel is skipped, not applied", () => {
  const transforms = [
    { path: "owned.ts", channel: "stable", after: "old(x);", insert: "oldExtra();\n" },
    { path: "owned.ts", channel: "nightly", after: "new(x);", insert: "newExtra();\n" },
  ]
  const stable = fixture({ source: "old(x);\n", transforms })
  assert.equal(run(stable).status, 0) // --channel defaults to stable
  assert.equal(owned(stable), "old(x);\noldExtra();\n")
  assert.equal(status(stable).skipped, 1)
  const nightly = fixture({ source: "new(x);\n", transforms })
  const r = spawnSync(process.execPath, [script, "apply", "--source", nightly.sourceDir, "--manifest", nightly.manifest, "--overlays", nightly.overlays, "--channel", "nightly"], { encoding: "utf8" })
  assert.equal(r.status, 0)
  assert.equal(owned(nightly), "new(x);\nnewExtra();\n")
  // The stable half against a nightly tree (wrong channel requested) is a real conflict.
  const wrong = fixture({ source: "new(x);\n", transforms })
  assert.equal(run(wrong).status, 1)
})

test("an ambiguous unscoped anchor is a conflict", () => {
  const source = UPSTREAM + '\nexport function other(): string {\n  return "stock";\n}\n'
  const f = fixture({ source, transforms: [{ path: "owned.ts", find: 'return "stock";', replace: 'return "x";' }] })
  assert.equal(run(f).status, 1)
  assert.equal(owned(f), source)
  const s = status(f)
  assert.equal(s.code, 2)
  assert.match(s.conflicts[0], /not unique/)
})

test("a missing anchor rejects the whole plan without partial writes", () => {
  const f = fixture({ source: "upstream changed\n" })
  assert.equal(run(f).status, 1)
  assert.equal(owned(f), "upstream changed\n")
  assert.equal(run(f, "status").status, 2)
  assert.throws(() => readFileSync(join(f.sourceDir, "new", "construct.ts"), "utf8"))
})

test("optional transforms skip a missing anchor or file instead of failing", () => {
  const f = fixture({
    transforms: [
      { path: "owned.ts", after: "gone", insert: "// never\n", optional: true },
      { path: "missing.test.ts", after: "x", insert: "y\n", optional: true },
      { path: "owned.ts", after: 'import { b } from "./b";', insert: "// kept\n" },
    ],
  })
  assert.equal(run(f).status, 0)
  assert.match(owned(f), /\/\/ kept/)
  const s = status(f)
  assert.equal(s.skipped, 2)
  assert.equal(s.compatible, true)
})

test("an upstream collision with a Construct overlay is rejected", () => {
  const f = fixture({ overlayExisting: "export const upstream = true\n" })
  assert.equal(run(f).status, 1)
  assert.equal(owned(f), UPSTREAM)
})

test("version 1 literal hunks still apply", () => {
  const f = fixture({ version: 1, transforms: [{ path: "owned.ts", old: 'return "stock";', new: 'return "v1";' }] })
  assert.equal(run(f).status, 0)
  assert.match(owned(f), /return "v1";/)
  assert.equal(status(f).alreadyApplied, 2)
})
