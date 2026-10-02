namespace Constructd.Api.Endpoints;

/// <summary>
/// The key vault's phone pages (docs/plans/key-vault-hosted.md, "Phone pairing and the approval page"):
/// static files embedded in the service and served anonymously on the service's own origin, outside
/// <c>/api/v1</c>. They read the device token from <c>localStorage</c> and call the device routes
/// same-origin, under a strict CSP with no inline code and no third-party resources.
/// </summary>
public static class VaultWebEndpoints
{
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
        "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Dictionary<string, (byte[] Body, string Type)> Files = Load();

    public static IEndpointRouteBuilder MapVaultWebEndpoints(this IEndpointRouteBuilder app)
    {
        // Relative links (vault.js, ../api/v1/…) need the trailing slash, also behind a path-mapping proxy.
        app.MapGet("/vault/", (HttpContext http) => http.Request.Path.Value?.EndsWith('/') == true
                ? Serve(http, "index.html") : Results.Redirect("vault/"))
            .AllowAnonymous().WithName("VaultWebIndex");
        app.MapGet("/vault/pair", (HttpContext http) => Serve(http, "pair.html")).AllowAnonymous().WithName("VaultWebPair");
        app.MapGet("/vault/vault.js", (HttpContext http) => Serve(http, "vault.js")).AllowAnonymous().WithName("VaultWebScript");
        app.MapGet("/vault/vault.css", (HttpContext http) => Serve(http, "vault.css")).AllowAnonymous().WithName("VaultWebStyle");
        return app;
    }

    private static IResult Serve(HttpContext http, string name)
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        var (body, type) = Files[name];
        return Results.Bytes(body, type);
    }

    private static Dictionary<string, (byte[], string)> Load()
    {
        var assembly = typeof(VaultWebEndpoints).Assembly;
        var files = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var (name, type) in new[]
                 {
                     ("index.html", "text/html; charset=utf-8"), ("pair.html", "text/html; charset=utf-8"),
                     ("vault.js", "text/javascript; charset=utf-8"), ("vault.css", "text/css; charset=utf-8"),
                 })
        {
            using var stream = assembly.GetManifestResourceStream("VaultWeb." + name)
                ?? throw new InvalidOperationException($"The vault page {name} is not embedded.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[name] = (copy.ToArray(), type);
        }
        return files;
    }
}
