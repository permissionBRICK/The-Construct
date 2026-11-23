using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Constructd.Tests.Windows;
using Constructd.Windows.Iso;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// The JSON shape of the host routes the old clients read (<c>/host/status</c>, <c>/host/config</c>,
/// <c>/host/windows</c>, the update history, the ISO catalog) stays as it was when they got typed records:
/// every property path and its JSON kind, compared with the shape the anonymous objects wrote.
/// </summary>
public sealed class HostWireShapeTests
{
    private static void Paths(JsonElement e, string at, SortedSet<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                into.Add(at + ":object");
                foreach (var p in e.EnumerateObject()) Paths(p.Value, at + "." + p.Name, into);
                break;
            case JsonValueKind.Array:
                into.Add(at + ":array");
                foreach (var item in e.EnumerateArray()) Paths(item, at + "[]", into);
                break;
            default:
                into.Add(at + ":" + (e.ValueKind is JsonValueKind.True or JsonValueKind.False ? "bool" : e.ValueKind.ToString().ToLowerInvariant()));
                break;
        }
    }

    private static async Task<string> ShapeAsync(HttpClient client, string url)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        Paths(await client.GetFromJsonAsync<JsonElement>(url), "", set);
        return string.Join("\n", set);
    }

    [Fact]
    public async Task The_host_routes_keep_their_wire_shape()
    {
        var files = new FakeIsoFileSystem();
        await using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:Iso:CacheDir"] = @"C:\catalog",
            ["Constructd:Iso:SourceUrl"] = "https://example.org/ubuntu.iso",
        }, services => services.AddSingleton<IIsoFileSystem>(files));
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var catalog = app.Service<IIsoCatalog>();
        var path = catalog.NextMediaPath(); files.WithBinary(path, 1024);
        catalog.Publish(path, new(app.Clock.UtcNow, "x", new string('a', 64), "seed", "SHA256:public", "hyperv-kvp", "script"));
        files.WithBinary(@"C:\catalog\ubuntu.iso", 512);
        await admin.PostAsJsonAsync("/api/v1/host/windows/keys", new { product = "win11", edition = "pro", kind = "retail", key = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY" });
        await app.Service<IJobStore>().UpsertAsync(new Job("j1", "provision", "vm", "admin", JobState.Running, [], null, null, app.Clock.UtcNow, null, "admin", null, "copy"), default);
        await app.Service<IHostUpdateStore>().TryStartAsync(new HostUpdateRecord("u1", new string('b', 40), "tag", "1.2.3", HostUpdateState.Succeeded, "done",
            [new HostUpdatePhase("check", app.Clock.UtcNow, "ok", null)], app.Clock.UtcNow, app.Clock.UtcNow, null, new string('c', 40), "admin", []), default);

        var shapes = new Dictionary<string, string>();
        foreach (var url in new[] { "/api/v1/host/status", "/api/v1/host/config", "/api/v1/host/windows", "/api/v1/host/updates/status", "/api/v1/host/iso-catalog" })
            shapes[url] = await ShapeAsync(admin, url);
        var actual = string.Join("\n\n", shapes.Select(s => "# " + s.Key + "\n" + s.Value)) + "\n";
        var expectedFile = Path.Combine(AppContext.BaseDirectory, "Api", "host-wire-shape.txt");
        if (Environment.GetEnvironmentVariable("CONSTRUCTD_WRITE_SHAPE") is { Length: > 0 } target) File.WriteAllText(target, actual);
        Assert.Equal(File.ReadAllText(expectedFile), actual);
    }

    [Theory]
    [InlineData(null, "hyperv")]
    [InlineData("external", "external")]
    public async Task Capabilities_name_the_host_kind(string? backend, string kind)
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:Backend"] = backend });
        using var user = await app.CreateUserClientAsync("alice");
        var caps = await user.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities");
        Assert.Equal(kind, caps.GetProperty("kind").GetString());
    }
}
