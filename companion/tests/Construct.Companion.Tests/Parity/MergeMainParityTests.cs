using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests.Parity;

public sealed class MergeMainParityTests
{
    public static IEnumerable<object[]> ConsoleRows => ParityTests.Rows("guest-console");
    [Theory, MemberData(nameof(ConsoleRows))]
    public void ConsoleScriptsAndBrowserAcceptanceMatchJavaScript(JsonElement row)
    {
        object? result = null;
        try {
            var input = row.GetProperty("input");
            result = row.GetProperty("kind").GetString() switch {
                "script" => GuestConsole.BuildConsoleScript(input.GetString()!),
                "self" => GuestConsole.BuildSelfConsoleScript(input.GetBoolean()),
                "ensure" => GuestConsole.ParseEnsureOutput(input.GetString()!),
                "handoff" => GuestConsole.ParseHandoff(input.GetString()!),
                "failure" => GuestConsole.MapFailure(input.GetProperty("step").GetString()!, new ProcessResult(input.GetProperty("result").GetProperty("code").GetInt32(), input.GetProperty("result").TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : "", input.GetProperty("result").TryGetProperty("stderr", out var stderr) ? stderr.GetString()! : "")),
                _ => GuestConsole.ParseBrowserLink(input.GetString()) };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        StateParityTests.Equal(JsonNode.Parse(row.GetProperty("output").GetRawText()), JsonSerializer.SerializeToNode(result));
    }
    public static IEnumerable<object[]> PairingRows => ParityTests.Scripts.Where(r => ((JsonElement)r[0]).GetProperty("name").GetString() is "t3-pairing" or "t3-pairing-instance");
    [Theory, MemberData(nameof(PairingRows))]
    public void PairingBuilderComposesTheSharedHelper(JsonElement row)
    {
        var name = row.GetProperty("name").GetString();
        if (name is not ("t3-pairing" or "t3-pairing-instance")) return;
        var instance = name == "t3-pairing" ? null : new JsonObject { ["name"] = row.GetProperty("values").GetProperty("instance").GetString() };
        Assert.Equal(row.GetProperty("output").GetString(), T3Code.BuildPairingScript(instance));
        Assert.Contains("construct-t3-pairing-base", GuestScripts.Names);
    }
    public static IEnumerable<object[]> ChildrenRows => ParityTests.Rows("driver-children");
    [Theory, MemberData(nameof(ChildrenRows))]
    public async Task SharedInventoryMergeKeepsTheFirstCaseInsensitiveName(JsonElement row)
    {
        var api = new FakeRemoteApi { Fingerprint = new string('a', 64) };
        api.Responses.Enqueue(new(200, row.GetProperty("children"))); api.Responses.Enqueue(new(200, row.GetProperty("shared")));
        var client = new RemoteHostClient(api, new FakeFileSystem(), new FakeTokenStore(), "https://host:7462", RemoteAuthentication.Negotiate, api.Fingerprint);
        var result = await VmPower.QueryChildrenAsync(client, "primary");
        StateParityTests.Equal(JsonNode.Parse(row.GetProperty("output").GetProperty("items").GetRawText()), result);
        Assert.Equal(row.GetProperty("requests").EnumerateArray().Skip(1).Select(x => x.GetString()), api.Requests.Select(x => x.Url.PathAndQuery));
    }
    [Theory]
    [InlineData("main", "owner/repo", "aaaaaaa", true)]
    [InlineData("dev", "owner/repo", "aaaaaaa", false)]
    [InlineData("refs/heads/main", "owner/repo", "aaaaaaa", false)]
    [InlineData("main", "owner/repo/path", "aaaaaaa", false)]
    [InlineData("main", "owner/repo?x", "aaaaaaa", false)]
    [InlineData("main", "owner/repo\n", "aaaaaaa", false)]
    [InlineData("main", "owner/repo", "", false)]
    public async Task UpdateCheckUsesOnlyPublishedMainForValidRepositories(string reference, string repo, string installed, bool fetch)
    {
        var source = new FakeUpdateSource(); source.Responses.Enqueue(null);
        Assert.Null(await UpdatePlanner.CheckConstructAsync(source, new() { ["repo"] = repo, ["ref"] = reference, ["installedCommit"] = installed }));
        Assert.Equal(fetch ? new[] { "https://github.com/owner/repo/releases/latest/download/manifest.json" } : [], source.Requests.Select(u => u.AbsoluteUri));
    }
    [Fact]
    public async Task ConsoleTransportExceptionCannotExposeATicket()
    {
        var ssh = new FakeSshTransport { ScriptHandler = (_, _) => throw new InvalidOperationException("https://host/#ticket-secret") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GuestConsole.OpenAsync(ssh, new FakeLauncher(), "guest", default));
        Assert.Equal("Could not create a console link. Check that the primary VM and Construct client are connected.", error.Message);
        Assert.DoesNotContain("ticket-secret", error.ToString());
    }
    [Fact]
    public async Task ConsoleFailureDoesNotExposeTicketFromStderr()
    {
        var ssh = new FakeSshTransport { ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(7, "https://host/#ticket-secret", "ticket-secret")) };
        var launcher = new FakeLauncher();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GuestConsole.OpenAsync(ssh, launcher, "guest", default));
        Assert.DoesNotContain("ticket-secret", error.ToString()); Assert.Empty(launcher.Opened);
        Assert.Equal(TimeSpan.FromSeconds(90), Assert.Single(ssh.ScriptTimeouts));
    }
}
