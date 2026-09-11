using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Fakes;
using Construct.Companion.Tests.Parity;

namespace Construct.Companion.Tests;

public sealed class RemoteClientTests
{
    public static IEnumerable<object[]> Routes => ParityTests.Rows("remote-routes");
    private static async Task<(RemoteHostClient Client, FakeRemoteApi Api, FakeTokenStore Tokens)> Client(RemoteAuthentication auth = RemoteAuthentication.Token, string? pin = null)
    {
        var api = new FakeRemoteApi { Fingerprint = new string('a', 64) }; var tokens = new FakeTokenStore();
        await tokens.WriteAsync("host_7462", new Secret("fixture-credential"));
        var client = new RemoteHostClient(api, new FakeFileSystem(), tokens, "https://host:7462", auth, pin ?? new string('a', 64)); return (client, api, tokens);
    }
    [Theory, MemberData(nameof(Routes))]
    public async Task EveryRouteMatchesJavaScript(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var (client, api, _) = await Client(); api.Responses.Enqueue(new RemoteResponse(200));
        var method = row["method"]!.GetValue<string>(); var reflected = typeof(RemoteHostClient).GetMethod(char.ToUpperInvariant(method[0]) + method[1..] + "Async")!;
        var input = row["args"]!.AsArray(); var args = reflected.GetParameters().Select((p, i) => p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : p.ParameterType == typeof(string) ? (object)input[i]!.GetValue<string>() : p.ParameterType == typeof(bool) ? (object)input[i]!.GetValue<bool>() : input[i]?.DeepClone()).ToArray();
        await (Task)reflected.Invoke(client, args)!;
        var request = Assert.Single(api.Requests); var output = row["output"]!;
        Assert.Equal(output["url"]!.GetValue<string>(), request.Url.AbsoluteUri);
        Assert.Equal(output["method"]!.GetValue<string>(), request.Method);
        StateParityTests.Equal(output["body"], request.Body is {} b ? JsonNode.Parse(b.GetRawText()) : null);
        Assert.Equal(RemoteAuthentication.Token, request.Authentication); Assert.Equal("[redacted]", request.Token!.ToString()); Assert.Equal("RemoteRequest", request.ToString());
    }
    [Fact]
    public async Task WrongPinSendsNoCredentialsAndMissingTokenNeverFallsBackToNegotiate()
    {
        var (client, api, tokens) = await Client(pin: new string('b', 64));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => client.WhoamiAsync()); Assert.Empty(api.Requests); Assert.DoesNotContain("fixture-credential", error.ToString());
        (client, api, tokens) = await Client(); await tokens.DeleteAsync("host_7462"); await Assert.ThrowsAsync<RemoteApiException>(() => client.WhoamiAsync()); Assert.Empty(api.Requests);
    }
    [Fact]
    public async Task NegotiateIsExplicitAndErrorDetailsRedactToken()
    {
        var (client, api, _) = await Client(RemoteAuthentication.Negotiate); api.Responses.Enqueue(new RemoteResponse(200)); await client.WhoamiAsync(); Assert.Equal(RemoteAuthentication.Negotiate, api.Requests.Single().Authentication); Assert.Null(api.Requests.Single().Token);
        (client, api, _) = await Client(); api.Responses.Enqueue(new RemoteResponse(409, JsonSerializer.SerializeToElement(new { title = "Conflict", detail = "fixture-credential", code = "busy" })));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => client.WhoamiAsync()); Assert.Equal(409, error.Status); Assert.Equal("busy", error.Code); Assert.DoesNotContain("fixture-credential", error.ToString());
    }
    [Fact]
    public async Task ErrorDetailsCannotEchoARequestPassword()
    {
        var (client, api, _) = await Client(); api.Responses.Enqueue(new RemoteResponse(400, JsonSerializer.SerializeToElement(new { detail = "rejected fixture-password" })));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => client.CreateUserAsync(new JsonObject { ["password"] = "fixture-password" }));
        Assert.DoesNotContain("fixture-password", error.ToString());
    }
    [Fact]
    public async Task TransportFailureRetainsSafeCauseAndRedactsUnsafeCause()
    {
        var (client, api, _) = await Client(); var cause = new IOException("DNS lookup failed"); api.Failure = cause;
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => client.WhoamiAsync());
        Assert.Equal(0, error.Status); Assert.Same(cause, error.InnerException); Assert.Contains("DNS lookup failed", error.ToString());
        api.Failure = new IOException("failed fixture-credential", new Exception("fixture-password"));
        error = await Assert.ThrowsAsync<RemoteApiException>(() => client.CreateUserAsync(new JsonObject { ["password"] = "fixture-password" }));
        Assert.NotNull(error.InnerException); Assert.DoesNotContain("fixture-credential", error.ToString()); Assert.DoesNotContain("fixture-password", error.ToString()); Assert.Contains("IOException", error.ToString());
    }
    [Fact]
    public void UnencryptedRemoteHostIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new RemoteHostClient(new FakeRemoteApi(), new FakeFileSystem(), new FakeTokenStore(), "http://host:7462"));
    }
}
