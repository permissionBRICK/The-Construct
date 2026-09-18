using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Windows;

public class WindowsApiTests
{
    [Fact]
    public async Task Key_pool_requires_admin_and_never_returns_or_logs_plaintext()
    {
        await using var app = new TestApp();
        using var user = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        const string secret = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY";
        var input = new { product = "win11", edition = "pro", kind = "retail", key = secret };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/host/windows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/host/windows/keys", input)).StatusCode);
        using var added = await admin.PostAsJsonAsync("/api/v1/host/windows/keys", input);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.DoesNotContain(secret, await added.Content.ReadAsStringAsync());
        var pool = await admin.GetStringAsync("/api/v1/host/windows");
        Assert.DoesNotContain(secret, pool); Assert.Contains("UVWXY", pool); Assert.Contains("hostId", pool);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(await app.Service<IAuditLog>().QueryAsync(100, default)));
    }

    [Fact]
    public async Task Shared_media_is_readable_to_users_but_only_admin_can_delete()
    {
        await using var app = new TestApp();
        using var user = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var files = app.Service<IMediaFiles>();
        var id = new string('a', 32);
        var item = new MediaItem(id, "host", "Windows", MediaRole.Install, MediaSource.Url, null,
            files.PathFor(id), MediaState.Ready, 40000, 0, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null, true);
        await app.Service<IMediaStore>().AddAsync(item, default);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/media/" + id)).StatusCode);
        var listed = await user.GetFromJsonAsync<JsonElement>("/api/v1/media");
        Assert.Equal(id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await user.DeleteAsync("/api/v1/media/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/media/" + id)).StatusCode);
    }
}
