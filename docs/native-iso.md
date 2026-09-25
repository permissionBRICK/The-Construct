# Native ISO builds on Windows

Both `Auto-Install.ps1` and `service/host/Install-ConstructHost.ps1` use
[construct-iso](https://github.com/permissionBRICK/construct-iso), an independent .NET
application. No WSL, Docker, or installed .NET runtime is needed on the Windows host.
The original `bin/build-autoinstall-iso.sh` remains available for Linux/Proxmox work;
the service's explicit `PerVm` strategy also retains its WSL adapter.

## How the executable is resolved

1. If a sibling `construct-iso` checkout and a .NET 10 SDK exist, publish that checkout
   locally as a self-contained Windows x64 executable. `CONSTRUCT_ISO_SOURCE_DIR` can
   select another checkout. A local build failure stops the installer.
2. Otherwise read `config/iso-builder.json`. With `"tag": "latest"` (the default), ask the
   GitHub API for the newest release and verify its zip against the SHA-256 digest GitHub
   records for the asset. The installed release is noted in
   `.construct-tools/iso/Construct.Iso.release.json`; a newer release replaces the tool,
   and an unreachable API keeps the installed one. A `build-<commit>` tag with `zipSha256`
   and `exeSha256` pins one release instead: an executable whose hash matches is reused,
   otherwise the release zip is downloaded and both hashes are verified.

The result lives at `<Construct checkout>/.construct-tools/iso/Construct.Iso.exe`.
Failed downloads and checksum mismatches preserve the previous executable. The cache is
ignored by Git. Credentials are passed to the tool as UTF-8 JSON on stdin, not command-line
arguments or persistent request files.

The source repository's Actions workflow tests Windows, Linux and macOS, then publishes
`Construct.Iso-win-x64.zip` and `SHA256SUMS` as an Actions artifact and as release assets
under `build-<commit>`. Only pushes to **construct-iso** trigger those binary builds.
The Construct repository runs integration/resolver tests but does not rebuild the tool.
Release assets provide a public download without the login and expiry associated with
[Actions artifacts](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts).

A new tool release is adopted by the next install, reinstall or host update without a
Construct change. The host service rebuilds its published install media when the tool's
hash differs from the one recorded with the media, reusing the cached Ubuntu ISO. To pin a
release, set its tag, zip hash and executable hash from that release's `SHA256SUMS`.

## Try it

Build only, without creating a VM:

```powershell
.\Auto-Install.ps1 -IsoPath C:\ISOs\ubuntu-24.04.4-live-server-amd64.iso -SkipCreateVm -Force
```

Omit `-SkipCreateVm` for a normal local installation. `-Force` rebuilds existing media;
the tool replaces it only after a complete new image is ready. If Hyper-V holds the old
ISO open, replacement fails and preserves the old file.

For an existing remote host, publish and deploy the updated service and run the host
installer to install the native executable. Updating the client scripts alone does not
upgrade the running host service. `-SkipIsoBuild` installs the executable and defers the
first media build until a VM is requested; `-IsoBuildOnly` remains available for an
explicit administrative rebuild.

Both `Native` and the legacy `Prebuilt` configuration now build missing media on demand.
Normal installations reuse the published ISO. Remote **Redownload**, `-Redownload`, or
`-Action redownload` sends `opts.redownload=true` in the create request: the service
fetches `Iso:SourceUrl` again, verifies `Iso:Sha256` when configured, patches a new
versioned ISO with the native tool, publishes it, and then creates the VM. Progress
streams through the existing installation job.

The source URL is chosen by the host administrator; redownload does not change the
configured Ubuntu release. A host configured only with `Iso:SourcePath` must also set
`Iso:SourceUrl` to support redownload. During redownload, the URL takes precedence over
the local source path. Failed downloads or checksum checks preserve the previous source;
failed builds preserve the published ISO. Existing VMs keep their attached version.
The catalog's `BuildScriptSha256` provenance field records the executable hash.

The initial real-image validation uses Ubuntu Server **24.04.4 amd64**. The tool checks
the ISO layout and rejects unsupported images. Its repository contains the real-image
test suite and firmware/install validation details.

## Tests

```powershell
.\test\native-iso-host.test.ps1
.\service\tests\host-installer.test.ps1
dotnet test .\service\tests\Constructd.Tests -c Release
```

These cover resolver precedence, checksum failures, cache repair, atomic executable
replacement, stdin credentials/encoding, native service composition, source checksums,
catalog provenance and retention of the legacy shell strategy.
