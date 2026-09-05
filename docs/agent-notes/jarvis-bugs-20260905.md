# Jarvis bug repair: T3 Node ownership (2026-09-05)

## Task outcome

`construct-node-upgrade-breaks-native-npx.md` (triage package P2): repaired in
source; activation deferred. The local T3 build driver previously installed
NodeSource Node 24 over the profile SDK, even for cache hits and late Desktop
packaging. Native npx trees populated under Node 22 then retained ABI 127
binaries while the interpreter changed. No other triage task is part of this
worktree.

## Ownership and scope

Local builds now use a private Node 26.8.1 distribution under
`/opt/construct/toolchains/node-v26.8.1-linux-{x64,arm64}`. The pin and official
archive SHA-256 digests live in the artifact recipe, so changing them invalidates
old source/native build artifacts. The version matches the current
`construct-t3-builds/.node-version` and `config.json` publisher pin: its packager
bundles the publisher's Node executable, so coordinate future pin changes in both
repositories. This repair does not change that publisher or activate a build.

The driver selects the private runtime before resolving npm channels, on cache
hits, and when packaging a previously prepared server. pnpm installs explicitly
into that private prefix, and build npm/npx uses the compiler cache's `npm/`
subdirectory. The project SDK, user npm prefix, npm configuration (including
release-age quarantine), and `~/.npm/_npx` are left alone. Downloads are checked
against pinned digests, staged, locked, then renamed into place. An incomplete
existing runtime fails closed; old runtime directories are retained for installed
servers. `T3CODE_NODE_ROOT` is an isolation override; its absolute path must fit
a shebang and contain no whitespace.

The compiled server keeps the existing launcher/symlink layout but gets an
absolute interpreter line. It therefore loads its own native dependencies under
the build runtime while child agents inherit the ordinary project PATH. The
recipe hash changes once on adoption; late packaging of an old recipe refuses
until the server is provisioned with the new recipe. Normal cache reuse and
separate server/Desktop stages remain intact.

Stable patched installs still default to prebuilt and never silently fall back
to compilation after download failure. Explicit local, nightly, and a stable
source version using the complete nightly inventory all share this runtime
selection. No upstream source transforms, project pins, SDK installer policy,
stock npm T3 installer, provisioning order, or user-data migration was changed.

## Tested result

- `bash test/t3-build-node.test.sh`: PASS. Downloads verified Node 22.22.0 and
  the recipe's Node 26.8.1 into a disposable fixture; compiles real V8 native
  addons; warms an actual offline npx package under Node 22; first proves a
  cross-major load fails with `NODE_MODULE_VERSION`. Runs the real driver and
  prepare/compile recipe with mocked apt/git/pnpm/Windows packaging boundaries.
  Local, nightly, stable-with-nightly-inventory, repeated provisioning, and late
  Desktop packaging preserve Node 22 and the cached addon byte-for-byte. The
  server runs its native addon under Node 26 and launches an npx child under
  Node 22. User global pnpm survives; incomplete private runtime fails closed.
  This test requires Linux x64, curl, g++, and Node distribution downloads.
- `node test/t3-build-cache.test.mjs`: PASS, including private Node/npm selection,
  profile/cache preservation, cache hits, explicit version/inventory pins,
  source/recipe changes, missing outputs, and failure preserving the launcher.
- `bash test/t3-build-diskcheck.test.sh`: 89 passed.
- `python3 test/t3-install-source.test.py`: 5 passed.
- `python3 test/t3-prebuilt.test.py`: 5 passed.
- Bash syntax, ShellCheck at warning/error severity, and `git diff --check`: PASS.
  The driver retains pre-existing informational ShellCheck findings.
- The relevant regressions are wired into the existing T3 CI workflow.

These are isolated fixtures, not a full upstream T3 compilation, Windows UI
installation, ARM64 execution, or affected-machine field verification. No live
T3 state, launchers, services, global Node installation, or project cache was
modified by verification.

## Activation / existing damage

Commit only after native Omniloop reviewer approval; no push or merge in this
session. Adoption requires a later merge and provisioning/build from an external
maintenance context. Do not restart `t3code-serve` from its own session. No
production service was restarted and no build was activated here.

This prevents T3 from changing the profile ABI. It does not guess which Node an
already-broken npx tree needs, delete caches, or silently downgrade an existing
system Node. An affected machine that already has the wrong system SDK or a
mismatched native cache still needs explicit field repair during maintenance;
preserve the profile pin and user data when doing so. The existing per-tool
Omniloop workaround is not removed.
