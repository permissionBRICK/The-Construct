using System.Net;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Console;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Console;

public sealed class InteractiveConsoleTests
{
    private sealed class FakeInteractive : IInteractiveConsole
    {
        public int Connections, Removed, Renewed;
        public Task<ConsoleConnection> ConnectAsync(ConsoleSession s, CancellationToken ct)
        { Connections++; return Task.FromResult(new ConsoleConnection { VmId = "native-vm-guid", Username = "temporary-user", Domain = "HOST", Password = "PRIVATE-CONSOLE-CREDENTIAL", CertificateFingerprint = "sha256:test" }); }
        public Task RenewAsync(ConsoleSession s, CancellationToken ct) { Renewed++; return Task.CompletedTask; }
        public Task RemoveAsync(string id, CancellationToken ct) { Removed++; return Task.CompletedTask; }
        public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Credentials_require_enabled_host_and_matching_authorized_session_and_are_not_logged()
    {
        var backend = new FakeInteractive();
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:BrowserConsoleEnabled"] = "false" },
            configureServices: s => s.AddSingleton<IInteractiveConsole>(backend));
        using var owner = await app.CreateUserClientAsync("owner");
        using var other = await app.CreateUserClientAsync("other");
        await owner.CreateVmAsync("viewer-vm");
        var root = "/api/v1/vms/viewer-vm/console";
        var created = await (await owner.PostJsonAsync(root + "/sessions", new { })).ReadAsync<JsonElement>();
        var path = root + "/sessions/" + created.GetProperty("sessionId").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync(path + "/connection", new { })).StatusCode);
        Assert.Equal(0, backend.Connections);
        app.Service<ConstructdOptions>().BrowserConsoleEnabled = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostJsonAsync(path + "/connection", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostJsonAsync(root + "/sessions/unknown/connection", new { })).StatusCode);
        var result = await owner.PostJsonAsync(path + "/connection", new { });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.Headers.CacheControl!.NoStore);
        Assert.Equal("PRIVATE-CONSOLE-CREDENTIAL", (await result.ReadAsync<JsonElement>()).GetProperty("password").GetString());
        Assert.Equal(1, backend.Connections);
        Assert.DoesNotContain("PRIVATE-CONSOLE-CREDENTIAL", app.Logs.AllText());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostJsonAsync(path + "/connection", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync(path + "/renew", new { })).StatusCode);
        Assert.Equal(1, backend.Renewed);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(path)).StatusCode);
        Assert.Equal(1, backend.Removed);
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostJsonAsync(path + "/connection", new { })).StatusCode);
    }

    [Fact]
    public async Task Browser_console_works_without_an_opt_in_setting()
    {
        var backend = new FakeInteractive();
        using var app = new TestApp(configureServices: s => s.AddSingleton<IInteractiveConsole>(backend));
        using var owner = await app.CreateUserClientAsync("owner");
        await owner.CreateVmAsync("viewer-default");
        var root = "/api/v1/vms/viewer-default/console";
        var created = await (await owner.PostJsonAsync(root + "/sessions", new { })).ReadAsync<JsonElement>();
        var path = root + "/sessions/" + created.GetProperty("sessionId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync(path + "/connection", new { })).StatusCode);
        Assert.Equal(1, backend.Connections);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(path)).StatusCode);
        Assert.Equal(1, backend.Removed);
    }

    [Fact]
    public async Task Temporary_accounts_use_stdin_and_are_reaped_when_the_session_is_revoked()
    {
        var runner = new RecordingProcessRunner().RespondStdout("""{"vmId":"native-id","username":"cvc-test","domain":"HOST","certificateFingerprint":"sha256:test"}""")
            .RespondStdout("{}").RespondStdout("{}").RespondStdout("{}");
        var sessions = new InMemoryConsoleSessionStore();
        var clock = new MutableClock();
        var options = new ConstructdOptions { BrowserConsoleEnabled = true };
        var console = new HyperVInteractiveConsole(runner, options, sessions, clock);
        var s = sessions.TryCreate("viewer-vm", "owner", 1024, 768, TimeSpan.FromSeconds(60), clock.UtcNow)!;
        var connection = await console.ConnectAsync(s, default);
        Assert.Same(connection, await console.ConnectAsync(s, default));
        Assert.DoesNotContain(connection.Password, connection.ToString());
        await console.RenewAsync(s, default);
        sessions.Remove(s.Id);
        await console.ReconcileAsync(default);
        Assert.Equal(4, runner.Calls.Count);
        Assert.DoesNotContain(connection.Password, string.Join(" ", runner.Calls[0].Arguments));
        using var input = JsonDocument.Parse(runner.Calls[0].StandardInput!);
        Assert.Equal(connection.Password, input.RootElement.GetProperty("password").GetString());
        Assert.Contains("\"action\":\"remove\"", runner.Calls[2].StandardInput!);
        Assert.Contains("\"action\":\"sweep\"", runner.Calls[3].StandardInput!);
    }

    [Fact]
    public async Task Failed_account_setup_never_returns_process_output_or_the_password()
    {
        var runner = new RecordingProcessRunner().Respond(new ProcessResult(1, "private output", "private error", false));
        var sessions = new InMemoryConsoleSessionStore(); var clock = new MutableClock();
        var console = new HyperVInteractiveConsole(runner, new ConstructdOptions { BrowserConsoleEnabled = true }, sessions, clock);
        var s = sessions.TryCreate("viewer-vm", "owner", 1, 1, TimeSpan.FromSeconds(60), clock.UtcNow)!;
        var error = await Assert.ThrowsAsync<ConsoleTransportException>(() => console.ConnectAsync(s, default));
        Assert.DoesNotContain("private", error.ToString());
    }
}
