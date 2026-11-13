using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Api.Endpoints;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

/// <summary><c>GET /events</c> (design 6.1): who hears of what, and resuming from a cursor.</summary>
public class ChangeStreamTests
{
    [Fact]
    public async Task A_user_hears_of_their_own_vm_and_job_and_an_admin_of_everything()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var carol = await app.CreateUserClientAsync("carol");
        using var admin = await app.CreateUserClientAsync("root", Role.Admin);

        await using var bobStream = await Stream.OpenAsync(bob);
        await using var carolStream = await Stream.OpenAsync(carol);
        await using var adminStream = await Stream.OpenAsync(admin);
        Assert.Equal("reset", (await bobStream.NextAsync()).Type);
        Assert.Equal("reset", (await carolStream.NextAsync()).Type);
        Assert.Equal("reset", (await adminStream.NextAsync()).Type);

        var bobJob = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("bob-vm"));
        var carolJob = await carol.WaitForJobAsync(await carol.StartCreateVmAsync("carol-vm"));
        Assert.Equal(JobState.Succeeded, carolJob.State);

        // Bob: his VM appears and his job starts and ends, exactly once; nothing of Carol's.
        var bobs = await bobStream.UntilAsync(e => e.Type == "job.ended" && e.Id == bobJob.Id);
        Assert.Contains(bobs, e => e is { Type: "vm.created", Id: "bob-vm", Vm: "bob-vm" });
        Assert.Contains(bobs, e => e.Type == "job.started" && e.Id == bobJob.Id);
        Assert.Single(bobs, e => e.Type == "job.ended");
        Assert.True(bobs.Zip(bobs.Skip(1)).All(p => p.First.Seq < p.Second.Seq));

        var carols = await carolStream.UntilAsync(e => e.Type == "job.ended" && e.Id == carolJob.Id);
        Assert.DoesNotContain(bobs, e => e.Vm == "carol-vm" || e.Id == carolJob.Id);
        Assert.DoesNotContain(carols, e => e.Vm == "bob-vm" || e.Id == bobJob.Id);

        var admins = await adminStream.UntilAsync(e => e.Type == "job.ended" && e.Id == carolJob.Id);
        Assert.Contains(admins, e => e.Type == "job.ended" && e.Id == bobJob.Id);

        // Deleting: the owner hears of it, the other user does not.
        Assert.Equal(HttpStatusCode.Accepted, (await bob.DeleteAsync("/api/v1/vms/bob-vm")).StatusCode);
        var deleted = await bobStream.UntilAsync(e => e is { Type: "vm.deleted", Id: "bob-vm" });
        Assert.Contains(deleted, e => e.Type == "job.started");
        await carolStream.SilentAsync(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task A_cursor_resumes_without_a_repeat_and_a_lost_one_gets_a_reset()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var feed = app.Service<ChangeFeed>();
        long Now() { using var at = feed.Subscribe(null); return at.Current; }
        var before = Now();
        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("bob-vm"));
        using var all = feed.Subscribe(before);
        var ended = all.Replay.Last(c => c.Type == "job.ended" && c.Id == job.Id);
        var started = all.Replay.First(c => c.Type == "job.started" && c.Id == job.Id);

        await using (var resumed = await Stream.OpenAsync(bob, started.Seq))
        {
            var replayed = await resumed.UntilAsync(e => e.Seq == ended.Seq);
            Assert.All(replayed, e => Assert.True(e.Seq > started.Seq));
            Assert.Single(replayed, e => e.Type == "job.ended");
        }

        await using (var behind = await Stream.OpenAsync(bob, started.Seq - 10 * ChangeFeed.Capacity))
        {
            var reset = await behind.NextAsync();
            Assert.Equal("reset", reset.Type);
            Assert.Equal(Now(), reset.Seq);
        }

        await using var current = await Stream.OpenAsync(bob, Now());
        await current.SilentAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task A_vm_token_cannot_follow_the_stream()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("bob-vm"));
        using var vm = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.GetAsync("/api/v1/events")).StatusCode);
    }

    [Fact]
    public void Visibility_follows_ownership_initiator_and_role()
    {
        static ClaimsPrincipal User(string name, bool admin = false)
        {
            var identity = new ClaimsIdentity("test", ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.Name, name));
            identity.AddClaim(new Claim(ConstructdClaims.KnownUser, "true"));
            if (admin) identity.AddClaim(new Claim(ClaimTypes.Role, nameof(Role.Admin)));
            return new ClaimsPrincipal(identity);
        }

        var backup = new Change(1, "backup.added", "b1", "vm1", "bob");
        Assert.True(EventEndpoints.Visible(User("Bob"), backup));
        Assert.False(EventEndpoints.Visible(User("carol"), backup));
        Assert.True(EventEndpoints.Visible(User("root", admin: true), backup));

        var initiated = new Change(2, "job.ended", "j1", "vm1", "root", Initiator: "carol");
        Assert.True(EventEndpoints.Visible(User("carol"), initiated));
        Assert.False(EventEndpoints.Visible(User("vm:vm1"), initiated with { Initiator = "vm:vm1" }));

        var offer = new Change(3, "update.offered", "abc", null, null);
        Assert.False(EventEndpoints.Visible(User("bob"), offer));
        Assert.True(EventEndpoints.Visible(User("bob"), offer with { Type = "update.finished", Everyone = true }));
    }

    [Fact]
    public async Task A_complete_backup_and_its_removal_are_announced_for_the_vms_owner()
    {
        using var h = await Constructd.Tests.Backups.BackupHarness.CreateAsync();
        using var changes = h.App.Service<ChangeFeed>().Subscribe(null);
        var id = await h.BackupAsync();
        await using (await h.Coordinator.LockAsync("alice", "dev", default))
            await h.Coordinator.DeleteLockedAsync((await h.Store.GetAsync(id, default))!, default);

        var seen = new List<Change>();
        while (changes.Live.TryRead(out var change)) seen.Add(change);
        var backups = seen.Where(c => c.Type.StartsWith("backup.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["backup.added", "backup.removed"], backups.Select(c => c.Type));
        Assert.All(backups, c => Assert.Equal((id, "dev", "alice"), (c.Id, c.Vm, c.Owner)));
    }

    private sealed record Event(long Seq, string Type, string? Id, string? Vm);

    private sealed class Stream(HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
    {
        public static async Task<Stream> OpenAsync(HttpClient client, long? since = null)
        {
            var response = await client.GetAsync("/api/v1/events" + (since is { } s ? "?since=" + s : ""), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            return new Stream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        private Task<Event?>? _pending;

        public async Task<Event> NextAsync(TimeSpan? timeout = null) =>
            await TryNextAsync(timeout ?? TimeSpan.FromSeconds(30)) ?? throw new TimeoutException("No event arrived.");

        public async Task<List<Event>> UntilAsync(Func<Event, bool> last)
        {
            var seen = new List<Event>();
            while (true)
            {
                var next = await NextAsync();
                seen.Add(next);
                if (last(next)) return seen;
            }
        }

        public async Task SilentAsync(TimeSpan wait)
        {
            var next = await TryNextAsync(wait);
            Assert.Null(next);
        }

        private async Task<Event?> TryNextAsync(TimeSpan timeout)
        {
            _pending ??= ReadAsync();
            var done = await Task.WhenAny(_pending, Task.Delay(timeout));
            if (done != _pending) return null;
            var result = await _pending;
            _pending = null;
            return result;
        }

        private async Task<Event?> ReadAsync()
        {
            string? type = null, data = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.Length == 0 && type is not null)
                {
                    var json = JsonNode.Parse(data!)!;
                    return new Event(json["seq"]!.GetValue<long>(), type, json["id"]?.GetValue<string>(), json["vm"]?.GetValue<string>());
                }
            }

            return null;
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
