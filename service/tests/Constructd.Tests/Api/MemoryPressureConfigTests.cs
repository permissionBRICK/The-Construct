using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class MemoryPressureConfigTests
{
    [Theory]
    [InlineData(0, 90, 50, 60, 10)]
    [InlineData(90, 90, 50, 60, 10)]
    [InlineData(91, 90, 50, 60, 10)]
    [InlineData(80, 101, 50, 60, 10)]
    [InlineData(80, 90, -1, 60, 10)]
    [InlineData(80, 90, 101, 60, 10)]
    [InlineData(80, 90, 50, 0, 10)]
    [InlineData(80, 90, 50, 60, -1)]
    public void Invalid_limits_are_rejected(int low, int high, int swap, int interval, int cooldown) =>
        Assert.NotNull(HostConfigValidation.Validate(new MemoryPressureConfig(true, high, low, swap, interval, cooldown)));

    [Fact]
    public async Task Defaults_replacement_and_capabilities_use_the_same_section()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var config = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config");
        var section = config.GetProperty("memoryPressure");
        Assert.Equal("default", section.GetProperty("source").GetString());
        Assert.Equal(HostAdminDefaults.MemoryPressure, section.Deserialize<MemoryPressureConfig>(ApiJson.Options));
        var changed = new MemoryPressureConfig(false, 100, 1, 0, 1, 0);
        var response = await admin.PutAsJsonAsync("/api/v1/host/config", new { memoryPressure = changed });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities");
        Assert.Equal(changed, caps.GetProperty("policy").GetProperty("memoryPressure").Deserialize<MemoryPressureConfig>(ApiJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/config",
            new { memoryPressure = new { enabled = false } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/config",
            new { memoryPressure = changed with { LowWaterPercent = 100 } })).StatusCode);
    }
}
