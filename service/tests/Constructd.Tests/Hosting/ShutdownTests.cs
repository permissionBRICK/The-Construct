using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Store.Records;
using Constructd.Tests.Store;
using Constructd.Tests.Support;
using Constructd.Tests.Vault;

namespace Constructd.Tests.Hosting;

/// <summary>
/// The engine stops promptly while clients hold responses open. Kestrel waits for open requests when the host stops
/// and cuts them only at the host's shutdown timeout (30 s), which the Windows service lifetime then reports as an
/// unhandled exception. A device agent keeps the change stream open all the time, so this was every stop.
/// </summary>
public sealed class ShutdownTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_engine_stops_promptly_with_event_streams_and_long_polls_open()
    {
        var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:ListenUrl"] = "http://127.0.0.1:0",
            // The background services run as on a real host.
            ["Constructd:Idle:SchedulerEnabled"] = "true",
            ["Constructd:Lease:SchedulerEnabled"] = "true",
            ["Constructd:Vault:SchedulerEnabled"] = "true",
            ["Constructd:Power:KeepHostAwake"] = "true",
            ["Constructd:ForwardReconcileSeconds"] = "5",
        });
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app.StartServer();
        var release = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await app.AddUserAsync("alice", Role.Admin);
            var vault = await VaultHarness.CreateAsync(app: app);
            await vault.PutEntriesAsync(vault.Entry("db", "pw-123456", "admin"));
            var pending = await vault.PendingAsync(vault.Request("get", ["db"]));

            // Clients of the test's own: the factory closes the ones it made before it stops the host.
            using var alice = Client(app, ConstructdHeaders.TestIdentity, "alice");
            using var guest = Client(app, "Authorization", ConstructdSchemes.VmToken + " " + vault.GuestToken);

            // The change stream, as the device agent follows it.
            using var changes = await alice.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, changes.StatusCode);
            var changeStream = new StreamReader(await changes.Content.ReadAsStreamAsync());
            Assert.Equal(": connected", await changeStream.ReadLineAsync());

            // A running job's progress stream.
            var job = await app.Service<IJobEngine>().SubmitAsync("test", null, "alice", (_, _) => release.Task, CancellationToken.None);
            using var progress = await alice.GetAsync($"/api/v1/jobs/{job.Id}/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
            var progressRead = progress.Content.ReadAsStringAsync();

            // A guest waiting for a vault approval, and a device waiting for a store change.
            var approval = guest.GetAsync($"/api/v1/vms/dev/vault/requests/{pending}?wait=25");
            var storeBase = new Uri(app.ClientOptions.BaseAddress, "/api/v1/store/");
            using var device = new TestDevice("carol-laptop", () => new SocketsHttpHandler(), storeBase);
            using (var signedIn = await app.CreateUserClientAsync("carol"))
            {
                signedIn.BaseAddress = storeBase;
                Assert.Equal(HttpStatusCode.OK, (await device.EnrollAsync(signedIn)).StatusCode);
            }

            using var store = device.Store();
            store.Timeout = Timeout.InfiniteTimeSpan;
            var cursor = (await store.GetFromJsonAsync<FeedPage>("records?since=0", StoreJson.Options))!.Cursor;
            var feed = store.GetAsync($"records?since={cursor}&wait=30");

            // Every one of them is open when the stop begins.
            await Task.Delay(500);
            Assert.False(progressRead.IsCompleted);
            Assert.False(approval.IsCompleted);
            Assert.False(feed.IsCompleted);

            var stopping = Stopwatch.StartNew();
            await app.DisposeAsync();
            stopping.Stop();
            Assert.True(stopping.Elapsed < Prompt, $"The stop took {stopping.Elapsed.TotalSeconds:0.0} s.");

            // Each ended cleanly: the streams at their end, the long-polls with "nothing yet".
            using var bounded = new CancellationTokenSource(Prompt);
            await changeStream.ReadToEndAsync(bounded.Token);
            await progressRead.WaitAsync(bounded.Token);
            using (var answered = await approval.WaitAsync(bounded.Token)) Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
            using (var answered = await feed.WaitAsync(bounded.Token))
            {
                Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
                Assert.Empty((await answered.Content.ReadFromJsonAsync<FeedPage>(StoreJson.Options))!.Records);
            }
        }
        finally
        {
            release.TrySetResult(new JobOutcome(null));
            await app.DisposeAsync();
        }
    }

    private static HttpClient Client(TestApp app, string header, string value)
    {
        var client = new HttpClient { BaseAddress = app.ClientOptions.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        return client;
    }
}
