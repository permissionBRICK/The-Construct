using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class CreateOptionsTests
{
    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, null, null)]
    [InlineData(true, true, true, true)]
    public async Task ReportsOnlyRequestedUnsupportedOptionsAndReplaysSameResponse(bool checkpoints, bool nesting, bool? requestedCheckpoints, bool? requestedNested)
    {
        using var app = new TestApp();
        using var client = await app.CreateUserClientAsync("admin", Role.Admin);
        app.Driver.Capabilities = app.Driver.Capabilities with { Checkpoints = checkpoints };
        app.Driver.NestedAvailable = nesting;
        client.DefaultRequestHeaders.Add("X-Construct-Operation-Key", "create-options");
        var body = new { name = "options-test", cpu = 2, ramGb = 4, diskGb = 40,
            opts = new { nested = requestedNested, automaticCheckpoints = requestedCheckpoints } };
        var response = await client.PostAsJsonAsync("/api/v1/vms", body);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ignored = accepted.GetProperty("ignoredOptions").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(!checkpoints && requestedCheckpoints == true, ignored.Contains("automaticCheckpoints"));
        Assert.Equal(!nesting && requestedNested == true, ignored.Contains("nested"));
        Assert.Equal(nesting && requestedNested == true, accepted.GetProperty("nested").GetBoolean());
        Assert.Equal(requestedNested is null, accepted.GetProperty("nestedFromHostDefault").GetBoolean());
        Assert.Equal(JobState.Succeeded, (await client.WaitForJobAsync(accepted.GetProperty("jobId").GetString()!)).State);
        Assert.Equal(checkpoints && requestedCheckpoints == true, app.Driver.Descriptors["options-test"].AutomaticCheckpoints);
        var replay = await client.PostAsJsonAsync("/api/v1/vms", body);
        replay.EnsureSuccessStatusCode();
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(accepted.GetProperty("jobId").GetString(), replayed.GetProperty("jobId").GetString());
        Assert.Equal(accepted.GetProperty("ignoredOptions").ToString(), replayed.GetProperty("ignoredOptions").ToString());
        Assert.Equal(accepted.GetProperty("nested").GetBoolean(), replayed.GetProperty("nested").GetBoolean());
    }

    [Fact]
    public async Task CreateReturnsEffectiveHostDefault()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("owner");
        await app.Service<IHostConfigStore>().SetAsync("virtualization", new VirtualizationConfig(true, false), "test", default);
        var response = await owner.PostAsJsonAsync("/api/v1/vms", new { name = "default-test", cpu = 2, ramGb = 4, diskGb = 40 });
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(data.GetProperty("nested").GetBoolean());
        Assert.True(data.GetProperty("nestedFromHostDefault").GetBoolean());
        await owner.WaitForJobAsync(data.GetProperty("jobId").GetString()!);
    }
}
