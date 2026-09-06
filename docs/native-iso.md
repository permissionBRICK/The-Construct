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
2. Otherwise read the immutable release tag and SHA-256 hashes in
   `config/iso-builder.json`. Reuse an executable whose hash matches; otherwise download
   its release zip, verify the zip and executable hashes, and publish the executable.

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

To adopt a new tool version, wait for its release workflow, then update the tag, zip hash
and executable hash in `config/iso-builder.json` from that release's `SHA256SUMS`.
Commit that pin update in The Construct. A Construct update alone never silently selects
a newer tool release.

## Try it

Build only, without creating a VM:

```powershell
.\Auto-Install.ps1 -IsoPath C:\ISOs\ubuntu-24.04.4-live-server-amd64.iso -SkipCreateVm -Force
```

Omit `-SkipCreateVm` for a normal local installation. `-Force` rebuilds existing media;
the tool replaces it only after a complete new image is ready. If Hyper-V holds the old
ISO open, replacement fails and preserves the old file.

For an existing remote host, first publish/deploy the updated service so its native
builder adapter is available, then rebuild its catalog media:

```powershell
dotnet publish .\service\src\Constructd.Api -c Release -r win-x64 --self-contained true -o .\service\publish
.\service\host\Install-ConstructHost.ps1 -IsoBuildOnly
```

The normal host installer writes `Iso:Mode=Native` and `Iso:NativeBuilderPath`. ISO-only
rebuilds also select the native tool when upgrading an older configuration. `Native` and
`Prebuilt` consume versioned catalog images and use the native producer; existing VMs keep
their attached image. The catalog's `BuildScriptSha256` provenance field records the
executable hash for native builds.

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
