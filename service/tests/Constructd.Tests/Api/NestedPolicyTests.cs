using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class NestedPolicyTests
{
    [Theory]
    [InlineData(false, true, null, null, false, false, false)]
    [InlineData(true, false, null, null, false, false, true)]
    [InlineData(true, true, null, false, false, false, false)]
    [InlineData(false, false, null, true, false, true, false)]
    [InlineData(false, true, false, true, false, true, false)]
    [InlineData(false, false, true, true, false, false, true)]
    [InlineData(false, false, false, true, true, false, true)]
    public async Task CreateResolvesDefaultAndSelectionPolicy(bool hostDefault, bool selectable, bool? allowance,
        bool? requested, bool admin, bool denied, bool expected)
    {
        using var app = new TestApp();
        using var client = await app.CreateUserClientAsync("owner", admin ? Role.Admin : Role.User);
        await app.Users.UpdateAsync((await app.Users.GetAsync("owner", default))! with { AllowNested = allowance }, default);
        await app.Service<IHostConfigStore>().SetAsync("virtualization", new VirtualizationConfig(hostDefault, selectable), "test", default);
        var response = await client.PostAsJsonAsync("/api/v1/vms", new { name = "nested-test", cpu = 2, ramGb = 4, diskGb = 40, opts = new { nested = requested } });
        if (denied)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("policy-denied", await response.Content.ReadAsStringAsync());
            Assert.Empty(app.Driver.Descriptors);
            return;
        }
        response.EnsureSuccessStatusCode();
        var jobId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        Assert.Equal(JobState.Succeeded, (await client.WaitForJobAsync(jobId)).State);
        Assert.Equal(expected, app.Driver.Descriptors["nested-test"].Nested);
    }

    [Fact]
    public async Task ConfigValidatesCapabilityAndDiscoveryReportsPolicy()
    {
        using var app = new TestApp();
        using var client = await app.CreateUserClientAsync("admin", Role.Admin);
        app.Driver.NestedAvailable = false;
        var response = await client.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = true } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("unsupported-on-host", await response.Content.ReadAsStringAsync());
        var caps = (await client.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities")).GetProperty("nested");
        Assert.False(caps.GetProperty("available").GetBoolean());
        Assert.False(caps.GetProperty("default").GetBoolean());
        Assert.True(caps.GetProperty("selectable").GetBoolean());
        app.Driver.NestedAvailable = true;
        (await client.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = false } })).EnsureSuccessStatusCode();
        caps = (await client.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities")).GetProperty("nested");
        Assert.True(caps.GetProperty("default").GetBoolean());
        Assert.False(caps.GetProperty("selectable").GetBoolean());
    }

    [Fact]
    public async Task SqliteUserOverrideDistinguishesOmittedAndNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "construct-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var app = TestApp.WithSqlite(Path.Combine(directory, "test.db"));
            using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
            await app.AddUserAsync("owner");
            foreach (var value in new[] { true, false })
            {
                (await admin.PutAsJsonAsync("/api/v1/users/owner", new { allowNested = value })).EnsureSuccessStatusCode();
                Assert.Equal(value, (await app.Users.GetAsync("owner", default))!.AllowNested);
                (await admin.PutAsJsonAsync("/api/v1/users/owner", new { maxVms = 3 })).EnsureSuccessStatusCode();
                Assert.Equal(value, (await app.Users.GetAsync("owner", default))!.AllowNested);
            }
            (await admin.PutAsJsonAsync("/api/v1/users/owner", new { allowNested = (bool?)null })).EnsureSuccessStatusCode();
            Assert.Null((await app.Users.GetAsync("owner", default))!.AllowNested);
        }
        finally { Directory.Delete(directory, true); }
    }
}
