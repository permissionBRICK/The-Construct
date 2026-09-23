# XFS root and reflink-seeded worktrees

Agents often work in several git worktrees of one repository at once. Git already shares
the object store between worktrees; the expensive part is everything a worktree needs
before it builds: `node_modules/`, `target/`, `bin/`, `obj/` and similar ignored output.
A fresh `git worktree add` has none of it, so every worktree reinstalls dependencies and
builds from cold, and each copy costs its full size on disk.

New Construct VMs therefore use **XFS** as their root file system, and a git hook seeds every
new worktree with **reflink copies** of the main worktree's ignored files. A reflink copy
shares all blocks with its original until one side writes to them, so a seeded worktree
costs almost no disk and starts warm.

## Measured

Construct's own checkout (844 MB working tree, 21 MB of it tracked), ten worktrees:

| Method | Disk used |
|---|---|
| Ten plain `git worktree add` (source only, no build output) | 214 MB |
| Ten reflink-seeded worktrees including all build output | 30 MB, about 5 s |
| Three independent full copies | 2547 MB |

After seeding, builds reuse what was copied. A Rust crate found every unit fresh in a
worktree at a new path, and a one-line change made 38 MB of a 245 MB `target/` its own
(207 MB stayed shared). `npm install` answered "up to date". `dotnet build` in a seeded
worktree left the main worktree's `bin/` and `obj/` untouched.

## Where XFS comes from

| Platform | How the root becomes XFS |
|---|---|
| Hyper-V (autoinstall ISO: `construct-iso` and `bin/build-autoinstall-iso.sh`) | The seed's storage config: a 1 GB EFI system partition and an XFS root on the largest disk, the same shape as Ubuntu's `direct` layout (which only offers ext4) |
| Proxmox (cloud image) | `install-construct-host.sh` converts Ubuntu's ext4 cloud image once per node with `service/host/xfs-cloud-image.sh` and caches it as `construct-ubuntu-<release>-xfs-amd64.qcow2` ([proxmox-host.md](proxmox-host.md)) |

Existing VMs keep ext4 until they are reinstalled. Nothing breaks on ext4; worktrees just
are not seeded there.

XFS against ext4 for a Construct guest:

- XFS cannot shrink. Construct never shrinks a VM disk.
- XFS has no 5% block reserve for root, so root reaches "disk full" at the same point as
  every other user. The provisioning disk check covers both.
- Growing works online (`xfs_growfs`, which cloud-init's `resizefs` calls).
- Docker's overlay2 driver needs `ftype=1`, which is the XFS default.

## The hook

`provision.sh` installs `construct-worktree-clone.sh` to `/usr/local/bin` and a
`post-checkout` hook that calls it. The hook reaches repositories in two ways:

- **New repositories**: through git's init template. `init.templateDir` in the system git
  config points at `/usr/local/share/construct/git-template`, which is git's stock template
  plus the hook. An `init.templateDir` that is already set is left alone.
- **Existing checkouts** directly under `WORKSPACE_ROOT` get the hook unless they already
  have a `post-checkout` hook of their own or use `core.hooksPath` (husky and similar).

The hook deliberately does not use a global `core.hooksPath`, which would replace every
repository's own hooks.

It runs only for a freshly added worktree (post-checkout with a null previous HEAD, in a
worktree other than the main one) and then:

1. Checks that the main worktree and the new worktree are on the same file system and that
   it supports reflinks. If not, it does nothing. It never makes a real copy.
2. Lists the paths git ignores in the main worktree (`git ls-files --others --ignored
   --exclude-standard --directory`).
3. Reflink-copies each one that does not exist yet in the new worktree, skipping other
   worktrees and nested repositories, Python virtual environments (`pyvenv.cfg`) and CMake
   build directories (`CMakeCache.txt`). Both of those record absolute paths, so a copy
   would keep writing into the source.
4. Prints one line to stderr (`construct: reflinked N ignored path(s) ...`) and always exits 0.

Opt out per repository with `git config construct.worktreeClone false`, or for one command
with `CONSTRUCT_WORKTREE_CLONE=0`.

## Things to know

- `du` counts shared blocks once per worktree, so folder sizes add up to far more than the
  disk holds. `df` shows the real usage.
- Deleting the main worktree frees little while seeded worktrees still share its blocks.
- The copy is a snapshot of the main worktree at `git worktree add` time. A build running
  there at that moment may leave half-written files in the copy, and the next build
  replaces them.

Tests: `test/worktree-clone.test.sh` (needs root for the XFS and ext4 loop mounts),
`test/autoinstall-iso.test.sh`.
