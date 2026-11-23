using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Windows.Iso;

namespace Constructd.Api.Endpoints;

public static class IsoCatalogEndpoints
{
    public static RouteGroupBuilder MapIsoCatalogEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/host/iso-catalog", Read).RequireAuthorization(Policies.Admin).Produces<IsoCatalogResponse>().WithName("HostIsoCatalog");
        return group;
    }

    private static IResult Read(IIsoCatalog catalog, IIsoFileSystem files, ConstructdOptions options)
    {
        var iso = options.Iso;
        var path = string.IsNullOrWhiteSpace(iso.SourcePath) ? null : iso.SourcePath;
        string? url = null;
        var sourceFile = path;
        if (Uri.TryCreate(iso.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // Never expose credentials, query tokens or fragments from an operator's source URL.
            url = new UriBuilder(uri.Scheme, uri.Host, uri.Port, uri.AbsolutePath).Uri.AbsoluteUri;
            var name = Path.GetFileName(uri.AbsolutePath);
            if (sourceFile is null && !name.Contains("..", StringComparison.Ordinal) && !name.Contains('\\'))
                sourceFile = iso.CacheDir.TrimEnd('\\', '/') + "\\" + (name.Length == 0 ? "source.iso" : name);
        }
        var present = sourceFile is not null && files.FileExists(sourceFile);
        var entries = catalog.List();
        var current = entries.FirstOrDefault(e => e.IsCurrent && e.SizeBytes > 0);
        return Results.Ok(new IsoCatalogResponse(
            iso.Mode,
            new IsoCatalogSource(path, url, !string.IsNullOrWhiteSpace(iso.Sha256), present, present ? files.FileLength(sourceFile!) : null),
            current is null ? null : new IsoCatalogCurrent(current.FileName, current.SizeBytes, current.Sidecar?.BuiltAt,
                current.Sidecar?.SourceSha256, current.Sidecar?.BootstrapKeyFingerprint, current.Sidecar?.HostnameSource),
            [.. entries.Select(e => new IsoCatalogItem(e.FileName, e.SizeBytes, e.IsCurrent, e.Sidecar?.BuiltAt, e.Sidecar is not null))]));
    }
}
