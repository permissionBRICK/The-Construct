using System.Net;
using System.Net.Http.Json;
using Construct.Keys;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Sync.Participation;
using Constructd.Tests.Store;

namespace Constructd.Tests.Support;

/// <summary>
/// One user's devices at a test engine, each signed in there with its own device token: the first enrols in the
/// engine's store, creates the account's keys and grants the engine <c>D</c>; every later one pairs with the first.
/// </summary>
internal sealed class UserDevices : IDisposable
{
    private static readonly Uri StoreBase = new("http://localhost/api/v1/store/");
    private static readonly Uri EngineBase = new("http://localhost/api/v1/");

    private readonly TestApp _app;
    private readonly List<TestDevice> _devices = [];

    private UserDevices(TestApp app) => _app = app;

    /// <summary>The devices' ids, the first device's first.</summary>
    public IReadOnlyList<string> Ids => [.. _devices.Select(d => d.Id)];

    private string EngineId => _app.Service<EngineStoreIdentity>().Id;

    /// <summary><paramref name="user"/>'s first device at <paramref name="app"/>, and its client.</summary>
    public static async Task<(UserDevices Devices, HttpClient First)> StartAsync(TestApp app, string user, string name = "laptop")
    {
        var devices = new UserDevices(app);
        var first = new TestDevice(name, () => app.Server.CreateHandler(), StoreBase);
        devices._devices.Add(first);
        using (var signedIn = await app.CreateTokenClientAsync(user))
        {
            signedIn.BaseAddress = StoreBase;
            Assert.Equal(HttpStatusCode.OK, (await first.EnrollAsync(signedIn)).StatusCode);
        }

        await first.CreateKeysAsync();
        var engine = app.Service<EngineKeyStore>();
        foreach (var grant in EngineGrants.For(first.Keys!, engine.HostKey.PublicKey, devices.EngineId, user, "locked")) engine.PutGrant(user, grant);
        return (devices, await devices.EnrolAsync(first));
    }

    /// <summary>Another device of the same user, paired with the first; its client.</summary>
    public async Task<HttpClient> PairAsync(string name)
    {
        var device = new TestDevice(name, () => _app.Server.CreateHandler(), StoreBase);
        _devices.Add(device);
        var (pairingId, _) = await device.StartPairingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await _devices[0].ApproveAsync(pairingId)).Response.StatusCode);
        await device.CompletePairingAsync(pairingId);
        return await EnrolAsync(device);
    }

    private async Task<HttpClient> EnrolAsync(TestDevice device)
    {
        using var anonymous = new HttpClient(_app.Server.CreateHandler()) { BaseAddress = EngineBase };
        var response = await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest(device.Account!, device.Id, device.EngineEnrollProof(EngineId)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        device.EngineTokens[EngineId] = (await response.Content.ReadFromJsonAsync<DeviceEnrollResponse>())!.Token;
        return device.Engine(() => _app.Server.CreateHandler(), EngineBase, EngineId);
    }

    public void Dispose()
    {
        foreach (var device in _devices) device.Dispose();
    }
}
