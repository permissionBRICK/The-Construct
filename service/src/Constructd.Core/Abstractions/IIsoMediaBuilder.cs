namespace Constructd.Core.Abstractions;

/// <summary>
/// Builds GENERIC install media — one ISO that serves every VM — at a path the caller chooses, and
/// describes what went into it so an <see cref="IIsoCatalog"/> can publish it.
///
/// The other half of the ISO seam. <see cref="IIsoBuilder"/> is the CONSUMING side: the
/// job asks for the media a new VM boots from. This is the PRODUCING side: whichever strategy can
/// build media on this host does it here, once, and publishes the result. A strategy implements
/// whichever halves it can — today <c>WslIsoBuilder</c> implements both and <c>PrebuiltIsoBuilder</c>
/// only consumes; a future <c>InGuest</c> or <c>HypervisorHost</c> builder plugs in here without the
/// admin CLI or the catalog changing. The strategies are listed in <c>service/README.md</c>.
///
/// Per-VM identity is deliberately NOT an input: generic media carries a placeholder hostname and the
/// guest adopts its real name at first boot from <see cref="IsoMediaRequest.HostnameSource"/>.
/// </summary>
public interface IIsoMediaBuilder
{
    Task<IsoMediaResult> BuildMediaAsync(
        IsoMediaRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>What to build, and where to put it.</summary>
/// <param name="OutputPath">Where the finished ISO goes, in the host's file-system namespace.</param>
/// <param name="SeedUser">The user the unattended install creates.</param>
/// <param name="SeedPassword">
/// Its password. A secret: it exists only for the unattended install, nobody is meant to log in with
/// it, and it must not reach a log, an exception or durable state.
/// </param>
/// <param name="BootstrapPublicKeyPath">The key the client provisioner later authenticates with.</param>
/// <param name="HostnameSource">
/// The guest's identity source (<c>hyperv-kvp</c> today). Passed straight through to the build
/// script's <c>VM_HOSTNAME_SOURCE</c>.
/// </param>
public sealed record IsoMediaRequest(
    string OutputPath,
    string SeedUser,
    string SeedPassword,
    string BootstrapPublicKeyPath,
    string HostnameSource);

/// <summary>
/// The finished media plus everything the catalog records about it, so that "what is this ISO?" can
/// be answered a year later without rebuilding it.
/// </summary>
/// <param name="IsoPath">Where the ISO actually landed.</param>
/// <param name="SourceIsoPath">The stock ISO it was remastered from.</param>
/// <param name="SourceSha256">That ISO's SHA-256, lowercase hex.</param>
/// <param name="BootstrapKeyFingerprint">
/// The bootstrap key baked into it, as <c>SHA256:…</c> — the one value that tells an admin whether
/// their client can still get in.
/// </param>
/// <param name="BuildScriptSha256">SHA-256 of the build script that produced it.</param>
public sealed record IsoMediaResult(
    string IsoPath,
    string SourceIsoPath,
    string SourceSha256,
    string BootstrapKeyFingerprint,
    string BuildScriptSha256);
