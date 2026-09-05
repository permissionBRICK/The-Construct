# Construct's T3 Code source inventories

`bin/build-t3code.sh` clones the upstream `t3code` tag published on the selected npm
channel and applies the inventory of that channel before building the VM server and
the Windows Desktop installer:

- `t3code-release/` — written against the **latest stable release** only.
- `t3code-nightly/` — written against the **latest nightly** only.

Each inventory holds `overlays/` (files Construct adds, copied verbatim; an upstream
file with the same path is a conflict) and `source-transforms.json` (the guarded edits
to upstream files, applied by `bin/apply-t3code-source.mjs apply|status --source
<checkout> --manifest <file> --overlays <dir>`).

## The rule for every transform, and for every repair

**As small and as narrow as possible, fixed on exactly the thing it changes, so that
any other upstream change avoids breaking it. Never any logic for more than one state
of the upstream code base.**

Concretely:

- An inventory targets the latest tag of its channel and nothing else. There are no
  version checks, no anchor alternatives, no fallbacks for older or newer trees. When
  upstream changes the code a transform hooks into, that is a conflict, and the repair
  rewrites the transform for the new shape. It does not keep the old one around.
- The two inventories are maintained separately. They will mostly look alike; that is
  fine. A fix for one is ported to the other by editing that inventory against its own
  tag, not by making one transform serve both.
- Do not add context "for safety": every extra byte in an anchor is one more way for
  an unrelated upstream edit to break the patch.

## Transform format (manifest version 2)

```jsonc
{ "path": "a.ts", "after":  "<anchor line>", "insert": "<full lines>\n" }
{ "path": "a.ts", "before": "<anchor line>", "insert": "<full lines>\n" }
{ "path": "a.ts", "find":   "<exact text>",  "replace": "<text>" }
```

- **Line-based inserts.** `after`/`before` name a line; the insert text (complete lines
  carrying their own indentation) goes after or before that line.
- **Indentation-free anchors.** Leading whitespace on every anchor line is ignored when
  matching, so re-indented upstream code still matches. Multi-line anchors are allowed
  but should be the exception.
- **Exactly one match** for an anchor or `find`, unless a `scope` is given: then the
  first match *after* the scope text is used (`"scope": "function Foo"` for an anchor
  such as `</>` that repeats in the file).
- **`every: true`** inserts next to every occurrence (a prop passed at several identical
  call sites).
- **Idempotent.** An insert whose text is already present, or a `replace` text already
  present, is a no-op, so an interrupted build can re-apply the same tree.
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
  Independent inserts stay independent.

`status` prints JSON with `compatible`, the per-transform `results`
(`applied|pending|skipped|conflict`, with `detail: "lenient"` when the anchor matched
through re-indentation) and the list of `conflicts`.

## What the inventories add to T3 Code

- **Voice input** (`voiceInput.*` RPCs, composer mic button, client or host capture).
- **Public base URL** (`T3CODE_PUBLIC_BASE_URL` for pairing URLs behind the TLS proxy).
- **Construct updates** in the Desktop app (update Construct / reprovision the VM).
- **Disk-space warning** (`construct.diskSpace` RPC, capability `constructDiskSpace`):
  a prompt when the VM disk is almost full or only the root reserve is left.
- **Omniloop tab** (`construct.omniloop*` RPCs, capability `constructOmniloop`, proxy
  route `/construct/omniloop/<ticket>/*`): the omniloop dashboard of the thread's own
  VM in the right panel, served through the T3 server behind a ticket, plus a composer
  banner and tab badge for the workflows the thread started.

## Build cache and Windows handoff

`bin/t3code-build-recipe.sh` owns artifact-producing commands. Its contents, the
source transformer, the selected channel inventory, and the two server patchers
form `patchHash`. The upstream T3 version plus that recipe identifies a build;
the Construct commit and the orchestration code in `bin/build-t3code.sh` do not.
Hash inputs use repository-relative paths and LF checkouts on Windows and Linux.
Changes to build semantics belong in the recipe so they invalidate artifacts.

Windows provisioning sets `T3CODE_BUILD_MODE=server`. The guest compiles shared
server/web/Desktop JavaScript once and writes `server-manifest.json` under
`/var/lib/construct/t3code-desktop`. It skips all compilation on an unchanged
build. The host compares that identity with its per-user, cross-VM record at
`%LOCALAPPDATA%\The-Construct\artifacts\t3code\installed.json` before requesting
Windows packaging or downloading an EXE.

Only a host that needs an install requests `T3CODE_BUILD_MODE=desktop`. This stage
uses the prepared server's exact version/channel/recipe (no second npm channel
lookup), keeps its compiled bundle untouched, and writes the installer plus
`manifest.json`. A matching cached installer with a valid SHA-256 skips packaging.
Calling the driver directly without a mode retains the combined build behavior
(`all`), which only activates a new server after packaging succeeds.

The source cache is `/var/cache/construct/t3code-source`. pnpm's shared package
store survives source changes; Windows Cargo outputs and Wine setup now also
survive them in `/var/cache/construct/t3code-compiler`. Superseded source cleanup
keeps the previous active server until provisioning can restart it. These caches
survive reprovisioning, but a VM rebuild/reinstall removes them. JavaScript build
outputs are reused for an identical build; changed source still recompiles.

The new recipe identity requires one rebuild per VM on first upgrade, and one
Desktop installation per Windows user. Subsequent VMs with the same T3 version,
channel, and recipe skip Windows packaging, download, and installation.

Focused verification (all use temporary fixtures, with no live service restart):

```sh
node test/t3-build-cache.test.mjs
pwsh -NoProfile -File test/t3-desktop-handoff.test.ps1
bash test/t3-build-diskcheck.test.sh
node extension/test/t3code.test.js
pwsh -NoProfile -File test/host-lib.test.ps1
```

CI can set `T3CODE_SOURCE_VERSION` to pin the exact upstream tag and
`T3CODE_INVENTORY=release|nightly` to select a whole inventory independently of
the output channel. This lets the separate publisher test a prepared nightly
inventory against a new stable tag without adding compatibility branches to any
patch. Desktop packaging retains the inventory from `server-manifest.json`.
The service restart key includes the upstream version as well as the patch hash.
