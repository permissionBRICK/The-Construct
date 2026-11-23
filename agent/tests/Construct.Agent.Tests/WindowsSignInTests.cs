using System.Net;
using System.Text.Json.Nodes;
using Construct.Agent.Engines;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>
/// Adding an engine with the Windows sign-in instead of a token. Negotiate cannot run here: the fake signs in
/// with the engine's fake-mode stand-in (<c>X-Constructd-Test-Identity</c>), which the engine challenges with
/// <c>TestIdentity</c>; the flow around it (challenge read without a credential, pinned connection, the sign-in
/// dropped at enrolment) is the real one.
/// </summary>
public sealed class WindowsSignInTests
{
    private sealed class FakeSignIn(string scheme, string user) : IWindowsSignIn
    {
        public int Sent;

        public string Scheme => scheme;

        public HttpMessageHandler Apply(SocketsHttpHandler pinned) => new Stamp(this, user) { InnerHandler = pinned };

        private sealed class Stamp(FakeSignIn owner, string user) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Sent);
                request.Headers.Add("X-Constructd-Test-Identity", user);
                return base.SendAsync(request, ct);
            }
        }
    }

    [Fact]
    public async Task Without_a_Windows_sign_in_the_route_says_it_is_not_available()
    {
        using var engine = new EngineHost();
        await using var laptop = await TestAgent.StartAsync();
        var added = await laptop.PostAsync("api/engines", new { address = engine.Url, pin = engine.Pin, windowsSignIn = true }, HttpStatusCode.NotImplemented);
        Assert.Equal("not-available", added["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_first_device_adds_an_engine_and_sets_up_with_the_Windows_user_and_then_uses_its_device_token()
    {
        using var engine = new EngineHost();
        await engine.AddUserAsync("alice");
        var signIn = new FakeSignIn("TestIdentity", "alice");
        await using var laptop = await TestAgent.StartAsync(windowsSignIn: signIn);

        var added = await laptop.PostAsync("api/engines", new { address = engine.Url, pin = engine.Pin, windowsSignIn = true });
        Assert.True(added["signedIn"]!.GetValue<bool>());
        var id = added["id"]!.GetValue<string>();

        await laptop.PostAsync("api/device/setup", new { engine = id, deviceName = "laptop" });
        var state = await laptop.GetAsync("api/state");
        Assert.True(state["device"]!["registered"]!.GetValue<bool>());
        var listed = state["engines"]![0]!;
        Assert.True(listed["enrolled"]!.GetValue<bool>());
        Assert.False(listed["signedIn"]!.GetValue<bool>());

        // From now on the device token alone: the Windows sign-in is not used again.
        var sent = signIn.Sent;
        await laptop.GetAsync($"api/engines/{id}/api/v1/vms");
        Assert.Equal(sent, signIn.Sent);
        Assert.Contains("\"WindowsSignIn\": false", laptop.StateFileText().Replace("\"windowsSignIn\"", "\"WindowsSignIn\""));
    }

    [Fact]
    public async Task An_engine_that_does_not_offer_it_or_a_wrong_pin_gets_no_sign_in()
    {
        using var engine = new EngineHost();
        await engine.AddUserAsync("alice");
        var negotiate = new FakeSignIn("Negotiate", "alice");
        await using var laptop = await TestAgent.StartAsync(windowsSignIn: negotiate);

        // The fake engine challenges with its stand-in, not Negotiate: refused before any sign-in is sent.
        var refused = await laptop.PostAsync("api/engines", new { address = engine.Url, pin = engine.Pin, windowsSignIn = true }, HttpStatusCode.Conflict);
        Assert.Equal("no-windows-sign-in", refused["code"]!.GetValue<string>());
        Assert.Equal(0, negotiate.Sent);

        // Another certificate than the one confirmed: nothing answers, nothing is added.
        await using var other = await TestAgent.StartAsync(windowsSignIn: new FakeSignIn("TestIdentity", "alice"));
        var wrong = await other.PostAsync("api/engines", new { address = engine.Url, pin = new string('A', 64), windowsSignIn = true }, HttpStatusCode.BadGateway);
        Assert.Equal("engine-unreachable", wrong["code"]!.GetValue<string>());
        Assert.Empty((await other.GetAsync("api/state"))["engines"]!.AsArray());

        // A user the engine does not know.
        var stranger = await other.PostAsync("api/engines", new { address = engine.Url, pin = engine.Pin, windowsSignIn = true, userToken = "x" }, HttpStatusCode.BadRequest);
        Assert.Equal("invalid-request", stranger["code"]!.GetValue<string>());
        await using var mallory = await TestAgent.StartAsync(windowsSignIn: new FakeSignIn("TestIdentity", "mallory"));
        var unknown = await mallory.PostAsync("api/engines", new { address = engine.Url, pin = engine.Pin, windowsSignIn = true }, HttpStatusCode.Forbidden);
        Assert.Equal("not-enrolled", unknown["code"]!.GetValue<string>());
    }
}
