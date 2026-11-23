using System.Net;
using System.Net.Http.Json;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>The device routes over the local API: the token, what a platform lacks, the settings.</summary>
public sealed class DeviceRouteTests
{
    [Fact]
    public async Task Device_routes_need_the_token_and_answer_not_available_for_what_the_platform_lacks()
    {
        await using var agent = await TestAgent.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = agent.Api.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("api/update/everything", null)).StatusCode);

        var features = await agent.GetAsync("api/device/features");
        Assert.Equal("fake", features["platform"]!.GetValue<string>());
        Assert.False(features["microphone"]!.GetValue<bool>());
        Assert.False(features["shell"]!.GetValue<bool>());
        Assert.Empty((await agent.GetAsync("api/connections")).AsArray());
        var shell = await agent.PostAsync("api/shell/open", new { surface = "win" }, HttpStatusCode.NotImplemented);
        Assert.Equal("not-available", shell["code"]!.GetValue<string>());
        await agent.PostAsync("api/connections/e1/work/open", new { what = "vscode" }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Settings_take_a_channel_automatic_updates_and_start_with_the_os()
    {
        await using var agent = await TestAgent.StartAsync();

        using var bad = await agent.Api.PutAsJsonAsync("api/device/settings", new { channel = "http://plain.example/manifest.json" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var good = await agent.Api.PutAsJsonAsync("api/device/settings", new { channel = "nightly", autoUpdate = true, startWithOs = true });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var settings = await agent.GetAsync("api/device/settings");
        Assert.Equal(("nightly", true, true), (settings["channel"]!.GetValue<string>(), settings["autoUpdate"]!.GetValue<bool>(), settings["startWithOs"]!.GetValue<bool>()));
        var update = await agent.GetAsync("api/update");
        Assert.Equal("nightly", update["channel"]!.GetValue<string>());
        Assert.False(update["installed"]!.GetValue<bool>());
    }
}
