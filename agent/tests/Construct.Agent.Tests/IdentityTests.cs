using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>
/// The identity flows of plan section 8 through the agent's local API, against the real engine and
/// its built-in store over pinned HTTPS.
/// </summary>
public sealed class IdentityTests
{
    private static readonly Regex CodeShape = new("^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$");

    [Fact]
    public async Task First_device_setup_creates_the_account_and_hands_out_the_recovery_code_once()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();

        var id = await laptop.AddEngineAsync(engine, signIn);
        var before = await laptop.GetAsync("api/state");
        Assert.False(before["device"]!["registered"]!.GetValue<bool>());
        Assert.True(before["engines"]![0]!["signedIn"]!.GetValue<bool>());

        var setup = await laptop.PostAsync("api/device/setup", new { engine = id, deviceName = "laptop" });
        var code = setup["recoveryCode"]!.GetValue<string>();
        Assert.Matches(CodeShape, code);

        var state = await laptop.GetAsync("api/state");
        Assert.True(state["device"]!["registered"]!.GetValue<bool>());
        Assert.Equal("alice", state["device"]!["account"]!.GetValue<string>());
        Assert.Equal("file", state["device"]!["protection"]!.GetValue<string>());
        Assert.True(state["device"]!["protectionWeak"]!.GetValue<bool>());
        var listed = state["engines"]![0]!;
        Assert.True(listed["enrolled"]!.GetValue<bool>());
        Assert.False(listed["signedIn"]!.GetValue<bool>());
        Assert.True(listed["store"]!.GetValue<bool>());
        Assert.NotNull(listed["engineId"]);

        // Neither the code nor any credential is readable afterwards: not in the API, not in clear on disk.
        var everything = state.ToJsonString() + (await laptop.GetAsync("api/devices")).ToJsonString() + (await laptop.GetAsync("api/engines")).ToJsonString();
        var stateFile = laptop.StateFileText();
        foreach (var secret in new[] { code, code.Replace("-", ""), signIn })
        {
            Assert.DoesNotContain(secret, everything);
            Assert.DoesNotContain(secret, stateFile);
        }

        Assert.DoesNotContain("p256v1:", stateFile);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(laptop.Host.Options.StatePath));

        var devices = (await laptop.GetAsync("api/devices")).AsArray();
        var me = Assert.Single(devices)!;
        Assert.True(me["me"]!.GetValue<bool>());
        Assert.Equal("laptop", me["name"]!.GetValue<string>());

        // A second setup is refused; a new code replaces the old one.
        await laptop.PostAsync("api/device/setup", new { engine = id, deviceName = "again" }, HttpStatusCode.Conflict);
        var replaced = (await laptop.PostAsync("api/device/recovery-code", null))["recoveryCode"]!.GetValue<string>();
        Assert.NotEqual(code, replaced);
    }

    [Fact]
    public async Task A_second_agent_pairs_with_the_first_after_both_show_the_same_words()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });

        await using var desktop = await TestAgent.StartAsync();
        Assert.Equal(engineId, await desktop.AddEngineAsync(engine));
        var started = await desktop.PostAsync("api/pairing", new { engine = engineId, deviceName = "desktop" });
        var pairingId = started["pairingId"]!.GetValue<string>();
        var words = started["words"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
        Assert.Equal(6, words.Length);

        var request = await laptop.GetAsync($"api/pairing/requests/{pairingId}");
        Assert.Equal("desktop", request["name"]!.GetValue<string>());
        Assert.Equal(words, request["words"]!.AsArray().Select(w => w!.GetValue<string>()));

        // The approval names the key whose words were compared; another key is refused.
        var otherKey = Construct.Keys.ParticipantKey.Generate().PublicKey.ToString();
        await laptop.PostAsync($"api/pairing/requests/{pairingId}/approve", new { publicKey = otherKey }, HttpStatusCode.Conflict);
        await laptop.PostAsync($"api/pairing/requests/{pairingId}/approve", new { publicKey = request["publicKey"]!.GetValue<string>() }, HttpStatusCode.NoContent);

        var paired = await desktop.WaitForStateAsync(s => s["pairing"]?["state"]?.GetValue<string>() is "done" or "failed");
        Assert.Equal("done", paired["pairing"]!["state"]!.GetValue<string>());
        Assert.True(paired["device"]!["registered"]!.GetValue<bool>());
        Assert.Equal("alice", paired["device"]!["account"]!.GetValue<string>());
        Assert.True(paired["engines"]![0]!["enrolled"]!.GetValue<bool>());

        // The new device works with the engine through its own credential.
        await desktop.GetAsync($"api/engines/{engineId}/api/v1/vms");
        var devices = (await desktop.GetAsync("api/devices")).AsArray();
        Assert.Equal(["desktop", "laptop"], devices.Select(d => d!["name"]!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task A_fresh_agent_recovers_the_account_with_the_code()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        string code;
        string engineId;
        await using (var lost = await TestAgent.StartAsync())
        {
            engineId = await lost.AddEngineAsync(engine, signIn);
            code = (await lost.PostAsync("api/device/setup", new { engine = engineId, deviceName = "lost laptop" }))["recoveryCode"]!.GetValue<string>();
        }

        await using var fresh = await TestAgent.StartAsync();
        await fresh.AddEngineAsync(engine);
        var wrong = Construct.Keys.RecoveryCode.Generate().ToString();
        await fresh.PostAsync("api/device/recover", new { engine = engineId, account = "alice", recoveryCode = wrong, deviceName = "new laptop" }, HttpStatusCode.Forbidden);

        // Typed loosely: lower case, without dashes.
        var device = await fresh.PostAsync("api/device/recover", new { engine = engineId, account = "alice", recoveryCode = code.Replace("-", "").ToLowerInvariant(), deviceName = "new laptop" });
        Assert.True(device["registered"]!.GetValue<bool>());
        var state = await fresh.GetAsync("api/state");
        Assert.True(state["engines"]![0]!["enrolled"]!.GetValue<bool>());
        await fresh.GetAsync($"api/engines/{engineId}/api/v1/vms");
        Assert.DoesNotContain(code.Replace("-", ""), fresh.StateFileText());
    }

    [Fact]
    public async Task A_revoked_agent_is_refused_by_the_engine()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });
        await using var desktop = await TestAgent.StartAsync();
        await desktop.AddEngineAsync(engine);
        var pairingId = (await desktop.PostAsync("api/pairing", new { engine = engineId, deviceName = "desktop" }))["pairingId"]!.GetValue<string>();
        var request = await laptop.GetAsync($"api/pairing/requests/{pairingId}");
        await laptop.PostAsync($"api/pairing/requests/{pairingId}/approve", new { publicKey = request["publicKey"]!.GetValue<string>() }, HttpStatusCode.NoContent);
        var desktopId = (await desktop.WaitForStateAsync(s => s["pairing"]?["state"]?.GetValue<string>() == "done"))["device"]!["id"]!.GetValue<string>();
        await desktop.GetAsync($"api/engines/{engineId}/api/v1/vms");

        using var revoke = await laptop.Api.DeleteAsync("api/devices/" + desktopId);
        var revocation = JsonNode.Parse(await revoke.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.True(revocation["engines"]![0]!["confirmed"]!.GetValue<bool>());

        await desktop.GetAsync($"api/engines/{engineId}/api/v1/vms", HttpStatusCode.Unauthorized);
        await desktop.GetAsync("api/devices", HttpStatusCode.Forbidden);
        var listed = (await laptop.GetAsync("api/devices")).AsArray().Single(d => d!["id"]!.GetValue<string>() == desktopId)!;
        Assert.True(listed["revoked"]!.GetValue<bool>());

        // The laptop cannot revoke itself, and keeps working.
        var laptopId = (await laptop.GetAsync("api/state"))["device"]!["id"]!.GetValue<string>();
        using (var self = await laptop.Api.DeleteAsync("api/devices/" + laptopId)) Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        await laptop.GetAsync($"api/engines/{engineId}/api/v1/vms");
    }
}
