using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Constructd.Tests.Windows;
using Constructd.Windows.Iso;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

public class IsoCatalogApiTests
{
    [Fact]
    public async Task CatalogIsAdminOnlyAndNeverBuildsMedia()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/host/iso-catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/host/iso-catalog")).StatusCode);
        var job = await owner.CreateVmAsync("parent");
        using var primary = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Forbidden, (await primary.GetAsync("/api/v1/host/iso-catalog")).StatusCode);
        var rotation = await owner.PostAsJsonAsync("/api/v1/vms/parent/token", new { kind = "legacy" });
        using var legacy = app.CreateVmTokenClient((await rotation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("vmToken").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/host/iso-catalog")).StatusCode);
        var empty = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/iso-catalog");
        Assert.Empty(empty.GetProperty("entries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("current").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectsCatalogAndSourceWithoutUrlSecrets(bool local)
    {
        var files = new FakeIsoFileSystem();
        using var app = new TestApp(new Dictionary<string, string?> {
            ["Constructd:Iso:CacheDir"] = @"C:\catalog",
            ["Constructd:Iso:SourcePath"] = local ? @"C:\source.iso" : "",
            ["Constructd:Iso:SourceUrl"] = "https://secret-user:secret-password@example.org/ubuntu.iso?secret-query#secret-fragment",
            ["Constructd:Iso:Sha256"] = new string('a', 64)
        }, services => services.AddSingleton<IIsoFileSystem>(files));
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var catalog = app.Service<IIsoCatalog>();
        var path = catalog.NextMediaPath(); files.WithBinary(path, 1024);
        catalog.Publish(path, new(app.Clock.UtcNow, "not-projected", new string('a', 64), "seed", "SHA256:public", "hyperv-kvp", "script"));
        files.WithBinary(@"C:\catalog\construct-autoinstall-unreadable.iso", 64);
        files.WithBinary(local ? @"C:\source.iso" : @"C:\catalog\ubuntu.iso", 512);
        var response = await admin.GetAsync("/api/v1/host/iso-catalog");
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("prebuilt", body.GetProperty("mode").GetString());
        Assert.Equal("https://example.org/ubuntu.iso", body.GetProperty("source").GetProperty("url").GetString());
        Assert.True(body.GetProperty("source").GetProperty("present").GetBoolean());
        Assert.True(body.GetProperty("source").GetProperty("sha256Configured").GetBoolean());
        Assert.Equal(512, body.GetProperty("source").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1024, body.GetProperty("current").GetProperty("sizeBytes").GetInt64());
        Assert.Equal("SHA256:public", body.GetProperty("current").GetProperty("bootstrapKeyFingerprint").GetString());
        Assert.Contains(body.GetProperty("entries").EnumerateArray(), e => !e.GetProperty("sidecarReadable").GetBoolean());
        Assert.DoesNotContain("secret-", text);
        Assert.DoesNotContain("not-projected", text);
        Assert.DoesNotContain("secret-", app.Logs.AllText());
    }
}
