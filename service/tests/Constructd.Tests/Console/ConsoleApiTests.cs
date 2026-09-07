using System.Net;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Console;

public sealed class ConsoleApiTests
{
    private const string Root = "/api/v1/vms/probe-vm/console";
    private static async Task<string> Session(HttpClient client, string root = Root)
    {
        using var response = await client.PostJsonAsync(root + "/sessions", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.ReadAsync<JsonElement>();
        return root + "/sessions/" + json.GetProperty("sessionId").GetString();
    }
    [Fact]
    public async Task Session_screenshot_input_renew_close_and_redacted_audit()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner", Role.Admin);
        await owner.CreateVmAsync("probe-vm");
        var caps = await (await owner.GetAsync(Root + "/capabilities")).ReadAsync<JsonElement>();
        Assert.Equal("unsupported", caps.GetProperty("interactive").GetString());
        Assert.False(string.IsNullOrWhiteSpace(caps.GetProperty("interactiveReason").GetString()));
        var path = await Session(owner);
        using var image = await owner.GetAsync(path + "/screenshot");
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1", Assert.Single(image.Headers.GetValues("X-Construct-Screen-Width")));
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, (await image.Content.ReadAsByteArrayAsync())[..4]);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync(path + "/keyboard", new { kind = "text", text = "SECRET-console-password" })).StatusCode);
        var mouse = await (await owner.PostJsonAsync(path + "/mouse", new { kind = "click", button = 1 })).ReadAsync<JsonElement>();
        Assert.False(mouse.GetProperty("applied").GetBoolean());
        Assert.Equal(32768, mouse.GetProperty("unavailable").GetProperty("returnValue").GetInt32());
        app.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync(path + "/renew", new { })).StatusCode);
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(path + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await owner.GetAsync(path + "/screenshot")).StatusCode);
        var audit = await (await owner.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(audit, a => a.Action == "console.keyboard" && a.Detail!.Contains("chars=23"));
        Assert.DoesNotContain("SECRET-console-password", JsonSerializer.Serialize(audit));
        Assert.DoesNotContain("SECRET-console-password", app.Logs.AllText());
    }
    [Fact]
    public async Task Authorization_is_checked_before_any_transport_and_session_is_bound_to_principal_and_vm()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner");
        using var other = await app.CreateUserClientAsync("other"); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("probe-vm"); await owner.CreateVmAsync("second-vm");
        var path = await Session(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Root + "/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostJsonAsync(path + "/keyboard", new { kind = "ctrlAltDel" })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await admin.GetAsync(path + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await owner.GetAsync(path.Replace("probe-vm", "second-vm") + "/screenshot")).StatusCode);
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root + "/capabilities")).StatusCode);
        Assert.Equal(0, app.Service<FakeConsoleTransport>().KeyboardCalls);
        await app.Service<IVmDelegationRepository>().TryFenceAsync("probe-vm", "delete-job", true, default);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync(path + "/screenshot")).StatusCode);
    }
    [Fact]
    public async Task Primary_token_can_console_self_but_legacy_token_cannot()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner");
        var job = await owner.CreateVmAsync("probe-vm"); using var primary = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.OK, (await primary.GetAsync(Root + "/capabilities")).StatusCode);
        var token = await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("probe-vm", VmTokenKind.Legacy, default);
        using var legacy = app.CreateVmTokenClient(token);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync(Root + "/capabilities")).StatusCode);
    }
    [Fact]
    public async Task Limits_and_expiration_are_enforced_without_extra_input_or_captures()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm");
        var path = await Session(owner); for (var i = 0; i < 3; i++) await Session(owner);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostJsonAsync(Root + "/sessions", new { })).StatusCode);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(path + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.GetAsync(path + "/screenshot")).StatusCode);
        Assert.Equal(4, app.Service<FakeConsoleTransport>().ScreenshotCalls);
        for (var i = 0; i < 50; i++) Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync(path + "/keyboard", new { kind = "ctrlAltDel" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostJsonAsync(path + "/mouse", new { kind = "click", button = 1 })).StatusCode);
        Assert.Equal(0, app.Service<FakeConsoleTransport>().MouseCalls);
        app.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostJsonAsync(path + "/renew", new { })).StatusCode);
        await Session(owner);
    }
    [Theory]
    [InlineData("keyboard", "{\"kind\":\"key\",\"keyCode\":256}")]
    [InlineData("keyboard", "{\"kind\":\"scancodes\",\"scancodes\":[256]}")]
    [InlineData("keyboard", "{\"kind\":\"unknown\"}")]
    [InlineData("keyboard", "{\"kind\":\"text\"}")]
    [InlineData("mouse", "{\"kind\":\"click\",\"button\":0}")]
    [InlineData("mouse", "{\"kind\":\"moveAbsolute\",\"x\":1,\"y\":0}")]
    [InlineData("mouse", "{\"kind\":\"moveRelative\",\"dx\":128,\"dy\":0}")]
    [InlineData("keyboard", "malformed")]
    public async Task Invalid_input_is_refused_before_driver(string route, string body)
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm"); var path = await Session(owner);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(path + "/" + route, content)).StatusCode);
        Assert.Equal(0, app.Service<FakeConsoleTransport>().KeyboardCalls + app.Service<FakeConsoleTransport>().MouseCalls);
    }
    [Fact]
    public async Task Bounds_device_failure_and_off_vm_are_truthful()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm"); var path = await Session(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "/screenshot?width=2&height=1")).StatusCode);
        Assert.Equal(0, app.Service<FakeConsoleTransport>().ScreenshotCalls);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostJsonAsync(path + "/keyboard", new { kind = "text", text = new string('a', 513) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostJsonAsync(path + "/keyboard", new { kind = "text", text = new string('a', 17000) })).StatusCode);
        app.Service<FakeConsoleTransport>().KeyboardResult = new(false, 32768, "keyboard", null);
        var failure = await owner.PostJsonAsync(path + "/keyboard", new { kind = "ctrlAltDel" });
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal(32768, (await failure.ReadAsync<JsonElement>()).GetProperty("returnValue").GetInt32());
        app.Driver.SetState("probe-vm", VmState.Off);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync(Root + "/sessions", new { })).StatusCode);
    }
    [Fact]
    public async Task Shared_child_and_parent_token_reauthorize_sharing_owner_and_parent_fences()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner");
        using var shared = await app.CreateUserClientAsync("shared"); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var job = await owner.CreateVmAsync("parent-vm"); using var parent = app.CreateVmTokenClient(job.VmToken());
        var child = (await app.Vms.GetAsync("parent-vm", default))! with { Name = "probe-vm", Kind = VmKind.Child,
            Parent = "parent-vm", TokenKind = VmTokenKind.Legacy, VmTokenHash = null, Sharing = SharingScope.Host };
        await app.Vms.AddAsync(child, 10, default); app.Driver.SetState("probe-vm", VmState.Running);
        var sharedPath = await Session(shared); var parentPath = await Session(parent);
        Assert.Equal(HttpStatusCode.OK, (await shared.GetAsync(sharedPath + "/screenshot")).StatusCode);
        // Represents the next request after sharing is revoked, without relying on a future sharing route.
        await app.Vms.RemoveAsync(child.Name, default); await app.Vms.AddAsync(child with { Sharing = SharingScope.Private }, 10, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await shared.GetAsync(sharedPath + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync(parentPath + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/users/owner", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await parent.GetAsync(parentPath + "/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/users/owner", new { enabled = true })).StatusCode);
        await app.Service<IVmDelegationRepository>().TryFenceAsync("parent-vm", "cascade-job", true, default);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync(Root + "/capabilities")).StatusCode);
    }

    [Fact]
    public async Task Renew_refreshes_mouse_bounds_and_rotation_ends_primary_sessions()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner");
        var job = await owner.CreateVmAsync("probe-vm"); using var primary = app.CreateVmTokenClient(job.VmToken());
        var path = await Session(primary);
        app.Service<FakeConsoleTransport>().Screen = new(20, 10, true, true, true, false);
        Assert.Equal(HttpStatusCode.BadRequest, (await primary.PostJsonAsync(path + "/mouse", new { kind = "moveAbsolute", x = 19, y = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await primary.PostJsonAsync(path + "/renew", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await primary.PostJsonAsync(path + "/mouse", new { kind = "moveAbsolute", x = 19, y = 9 })).StatusCode);
        var rotated = await (await owner.PostJsonAsync("/api/v1/vms/probe-vm/token", new { kind = "primary" })).ReadAsync<JsonElement>();
        using var next = app.CreateVmTokenClient(rotated.GetProperty("vmToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await primary.PostJsonAsync(path + "/renew", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await next.PostJsonAsync(path + "/renew", new { })).StatusCode);
    }

    [Fact]
    public async Task Missing_absolute_mouse_reports_relative_fallback_without_injection()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm"); var path = await Session(owner);
        app.Service<FakeConsoleTransport>().Screen = new(1, 1, true, true, false, true);
        var response = await owner.PostJsonAsync(path + "/mouse", new { kind = "moveAbsolute", x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.ReadAsync<JsonElement>();
        Assert.Equal("syntheticMouse", body.GetProperty("device").GetString());
        Assert.Equal("moveRelative", body.GetProperty("fallback").GetString());
        Assert.Equal(0, app.Service<FakeConsoleTransport>().MouseCalls);
    }

    [Fact]
    public async Task Rate_responses_include_retry_metadata_for_event_and_session_limits()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm"); var path = await Session(owner);
        for (var i = 0; i < 3; i++) await Session(owner);
        var capped = await owner.PostJsonAsync(Root + "/sessions", new { });
        Assert.Equal(60, (await capped.ReadAsync<JsonElement>()).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(TimeSpan.FromSeconds(60), capped.Headers.RetryAfter!.Delta);
        for (var i = 0; i < 4; i++) await owner.GetAsync(path + "/screenshot");
        var limited = await owner.GetAsync(path + "/screenshot");
        Assert.Equal(1, (await limited.ReadAsync<JsonElement>()).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(TimeSpan.FromSeconds(1), limited.Headers.RetryAfter!.Delta);
    }

}
