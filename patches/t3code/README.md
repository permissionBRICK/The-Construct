# Construct's T3 Code source inventory

`bin/build-t3code.sh` clones the upstream `t3code` tag published on the selected npm
channel (stable or nightly) and applies this inventory to it before building the VM
server and the Windows Desktop installer. One inventory serves both channels.

- `overlays/` — files Construct adds. They are copied verbatim; an upstream file with
  the same path is a conflict.

What the inventory adds to T3 Code:

- **Voice input** (`voiceInput.*` RPCs, composer mic button, client or host capture).
- **Public base URL** (`T3CODE_PUBLIC_BASE_URL` for pairing URLs behind the TLS proxy).
- **Construct updates** in the Desktop app (update Construct / reprovision the VM).
- **Disk-space warning** (`construct.diskSpace` RPC, capability `constructDiskSpace`):
  the web UI asks each Construct server once a minute and raises a prompt when the VM
  disk is almost full or only the root reserve is left (non-root writes fail there).
- `source-transforms.json` — the guarded edits to upstream files, applied by
  `bin/apply-t3code-source.mjs apply|status --source <checkout>`.

## Transform format (manifest version 2)

Each transform touches the **minimum** it needs. The anchor is the smallest upstream
fragment that identifies the spot — usually a single line — and nothing around it is
checked, so unrelated upstream churn in the same file never breaks the build. Only a
change to the exact code we hook into does, and that is a real conflict that needs a
repair.

```jsonc
{ "path": "a.ts", "after":  "<anchor line>", "insert": "<full lines>\n" }
{ "path": "a.ts", "before": "<anchor line>", "insert": "<full lines>\n" }
{ "path": "a.ts", "find":   "<exact text>",  "replace": "<text>" }
```

Rules the applier implements (`bin/apply-t3code-source.mjs`):

- **Line-based inserts.** `after`/`before` name a line; the insert text (complete lines
  carrying their own indentation) goes after or before that line.
- **Indentation-free anchors.** Leading whitespace on every anchor line is ignored when
  matching, so re-indented or reformatted upstream code still matches. Multi-line
  anchors are allowed but should be the exception.
- **Exactly one match** for an anchor or `find`, unless a `scope` is given: then the
  first match *after* the scope text is used (`"scope": "function Foo"` for an anchor
  such as `</>` that repeats in the file).
- **Alternatives.** `after`/`before`/`find` may be an array; the first alternative that
  matches wins. Use this when stable and nightly shape the same spot differently.
- **Idempotent.** An insert whose text is already present, or a `replace` text already
  present, is a no-op — that is also how an upstream tree that has adopted one of our
  lines (say an import) simply passes.
- **`optional: true`** — a missing file or anchor is skipped instead of failing. Used
  for upstream *test* files only: they never affect the built server/Desktop app, so
  they must never block a build.

Guidelines when writing or repairing a transform:

- Prefer an insert over a replace; prefer a whole-line anchor over a fragment; prefer
  an anchor line that names the thing we integrate with (an import, an RPC method, a
  schema field) over structural lines such as `});` or `}`.
- Names only Construct uses get their **own** import statement (a duplicate module
  import is fine for TypeScript). A name upstream might import itself is merged into
  the upstream import line with `find`/`replace`, so that an upstream change surfaces
  as a conflict instead of a duplicate-identifier build error.
- One import per transform; one union member or one record entry per transform.
  Independent inserts stay independent, so an upstream tree that already has one of
  them still applies the rest.
- Do not add context "for safety": every extra byte in an anchor is another way for an
  unrelated upstream edit to break the patch.

`status` prints JSON with `compatible`, the per-transform `results`
(`applied|pending|skipped|conflict`, with `detail: "lenient"` when the anchor matched
through re-indentation) and the list of `conflicts`. Check a candidate upstream tag
before a build with:

```sh
node bin/apply-t3code-source.mjs status --source /path/to/t3code-checkout \
  --manifest patches/t3code/source-transforms.json --overlays patches/t3code/overlays
```
