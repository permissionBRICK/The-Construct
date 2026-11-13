# Release flow and channels

This covers the new stack. The old clients (`install.ps1`, `Update-Construct.ps1`, `lib/`,
`companion/`, `extension/`) keep following `main` and `releases/latest` as described in
[Host releases](host-release.md).

## Branches and builds

| Branch | A push publishes |
|---|---|
| `main` | The stable release `host-<sha>`, which becomes GitHub's latest release, as before. |
| `nightly` | A prerelease `host-<sha>`, and its `manifest.json` replaces the one on the release tagged `channel-nightly`. |
| `rework` | The same as `nightly`, until the branch is merged into `main` and `nightly` is created. |

Every PR merges into `nightly` with a merge commit. `main` takes release PRs only, so it is
always a subset of `nightly`; nothing merges back. Prereleases never move `latest`.

Builds report `X.Y.Z+<sha>`. `X.Y.Z` is the highest `vX.Y.Z` tag or the `VERSION` file of the
commit, whichever is higher, else `1.0.0` (`scripts/release.py version`). A release cut writes
`VERSION`; the stable build of that commit also gets the tag `vX.Y.Z` and the title
`Construct vX.Y.Z`.

The Companion workflow follows the same branches; off `main` it publishes `companion-<sha>`
as a prerelease, which the old Companion discovery ignores.

## Channel manifests

| Channel | Manifest |
|---|---|
| `stable` | `https://github.com/<owner>/<repo>/releases/latest/download/manifest.json` |
| `nightly` | `https://github.com/<owner>/<repo>/releases/download/channel-nightly/manifest.json` |
| A URL | That HTTPS URL, for a mirror or a company server |

The channel manifest is the release manifest with these keys added:

| Key | Value |
|---|---|
| `channel` | `stable` or `nightly` |
| `version` | `X.Y.Z+<commit>` |
| `guestPayloadHash` | SHA-256 of the source archive, which is what a VM receives |
| `packages` | Per package (`payload`, `frameworkDependent`, `linux`, `source`): `asset` and `sha256` |

Manifests are fetched over HTTPS and not signed. New code in `service/` resolves a channel
and reads a manifest through `ReleaseChannel` (`service/src/Constructd.Core/Logic/ReleaseChannel.cs`).
The host setting `updates.channel` takes `stable`, `nightly` or a URL; `main` still means
stable.

## Cutting a release

```sh
git fetch origin
python3 scripts/release.py status               # PRs on nightly not yet on main
python3 scripts/release.py cut --dry-run        # version, notes and held-back list only
python3 scripts/release.py cut                  # everything: merges nightly
python3 scripts/release.py cut --only 412 418   # or a subset: cherry-picks those merges
python3 scripts/release.py cut --exclude 415
git switch release/vX.Y.Z && python3 scripts/release.py verify
```

`status` lists each pending PR with its labels and touched files, and names earlier pending
PRs that touched the same files: a hint that the PR may depend on them.

`cut` creates `release/vX.Y.Z` from `origin/main`, commits `VERSION`, pushes the branch and
opens the release PR into `main`. Without `--only` or `--exclude` it merges `origin/nightly`.
With them it runs `git cherry-pick -m 1 -x` on the chosen PR merge commits, in nightly's
order. `--base` and `--head` change the two branches (before the merge: `--head origin/rework`).

The version follows the labels of the included PRs: `breaking` bumps the major version,
`enhancement` the minor one, anything else the patch. The PR body carries the version, the
release notes and the held-back PRs. Notes have one line per PR, grouped by label (breaking,
enhancement, bug, other). A `## Release notes` section in a PR body replaces its title line.
Nightly prereleases carry the notes of everything not yet on `main`.

`verify` runs `bash test/run-local-checks.sh all` on a clean checkout and posts the result as
the `local-checks` commit status of `HEAD`. Push the branch first, so GitHub knows the commit.

A PR counts as on `main` when its merge commit is reachable from `main` or a commit on `main`
names it in a `(cherry picked from commit …)` line.

A hotfix lands on `nightly` like any PR and ships alone with `cut --only <PR>`.

## Resolving a promotion conflict (agent procedure)

The normal release promotes everything and does not conflict. Picking a subset can. An agent
runs the promotion; the owner merges the release PR.

1. Dry run. On a scratch branch from `origin/main`, cherry-pick the chosen merges in order:
   `git switch -c promote-check origin/main`, then `git cherry-pick -m 1 -x <merge>` for each.
   Classify each PR:
   - `Clean`: it applies.
   - `Overlap`: git conflicts, but the PR does not need held-back code.
   - `Depends`: it needs code from a held-back PR (a function, a table, a migration whose tables
     another PR creates). Ask the owner once: ship the dependency too, or hold this PR.

   `git cherry-pick --abort` and `git branch -D promote-check` when done.
2. Run `python3 scripts/release.py cut --only …` with the final list. On a conflict it stops on
   `release/vX.Y.Z`. For an `Overlap`, put the PR's change onto main's code; never copy
   held-back code. Then `git add`, `git cherry-pick --continue`, and run the same `cut` again:
   picks already on the branch are skipped.
3. For each adapted PR, show `git range-diff <merge>^1..<merge> <picked>^..<picked>` to a second
   agent for review.
4. Every file whose nightly changes are all promoted must equal nightly:
   `git diff release/vX.Y.Z origin/nightly -- <file>` is empty.
5. Keep stable migration IDs: the host detects applied migrations by their integer `id`
   (`service/src/Constructd.Sqlite/Migrations/SqliteMigrationRunner.cs`). Never renumber one
   while promoting.
6. Add an "Adapted during promotion" section to the release PR with each PR and its verdict
   (`gh pr edit <n> --body-file …`), then run `release.py verify`.

