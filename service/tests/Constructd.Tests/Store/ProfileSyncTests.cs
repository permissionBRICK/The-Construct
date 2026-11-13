using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Contracts;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Store;

/// <summary>
/// Project profiles through the engine end to end (plan 9.3, 9.5, 9.6, 8.4): VM writes with a base,
/// direct edits found by the heartbeat digest, write-back into the VMs, conflicts delivered to the
/// VM and resolved, device edits, two engines against one stand-alone store, and revocation.
/// </summary>
public sealed class ProfileSyncTests
{
    private const string Web = """{"name":"web","repos":[{"url":"https://example.com/web.git"}],"sdks":{"node":"20"},"provisionCommands":["npm ci"]}""";

    private static string Vm(string name, string profile = "web") => $"/api/v1/vms/{name}/profiles/{profile}";

    [Fact]
    public async Task Two_VMs_editing_different_fields_of_one_profile_end_merged_in_both()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var device = h.DeviceClient(h.Laptop!);
        var created = await device.PutProfileAsync("/api/v1/profiles/web", Web, null);
        Assert.Equal("written", created.Status);

        using var one = await h.VmAsync("one", "web");
        using var two = await h.VmAsync("two", "web");
        await h.SyncAsync();
        Assert.Equal(created.Revision, h.Guests.Rev("one", "web"));
        Assert.Equal(created.Revision, h.Guests.Rev("two", "web"));
        Assert.Equal("20", h.Guests.Profile("two", "web")!["sdks"]!["node"]!.GetValue<string>());

        // Both edit on the revision they hold: one the SDK, the other the setup commands.
        var first = await one.PutProfileAsync(Vm("one"), Web.Replace("\"20\"", "\"22\""), created.Revision);
        Assert.Equal("written", first.Status);
        h.Guests.Apply("one", "web", first);
        var second = await two.PutProfileAsync(Vm("two"), Web.Replace("[\"npm ci\"]", "[\"npm ci\",\"npm run build\"]"), created.Revision);
        Assert.Equal("written", second.Status);
        h.Guests.Apply("two", "web", second);
        Assert.True(second.Revision > first.Revision);

        // The engine writes the merge back into the VM that has not seen it.
        await h.SyncAsync();
        foreach (var vm in new[] { "one", "two" })
        {
            var profile = h.Guests.Profile(vm, "web")!;
            Assert.Equal("22", profile["sdks"]!["node"]!.GetValue<string>());
            Assert.Equal(["npm ci", "npm run build"], profile["provisionCommands"]!.AsArray().Select(c => c!.GetValue<string>()));
            Assert.Equal(second.Revision, h.Guests.Rev(vm, "web"));
        }

        // Writing back changed the folders' digests; reading them again sends nothing.
        await h.HeartbeatAsync(one, "one");
        await h.HeartbeatAsync(two, "two");
        await h.SyncAsync();
        var current = (await device.GetFromJsonAsync<ProfileResponse>("/api/v1/profiles/web"))!;
        Assert.Equal(second.Revision, current.Revision);
    }

    [Fact]
    public async Task The_same_field_edited_on_two_VMs_is_a_conflict_that_reaches_the_VM_and_is_resolved_once()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var one = await h.VmAsync("one", "web");
        using var two = await h.VmAsync("two", "web");
        var created = await one.PutProfileAsync(Vm("one"), Web, null);
        h.Guests.Apply("one", "web", created);
        await h.SyncAsync();
        Assert.Equal(created.Revision, h.Guests.Rev("two", "web"));

        var first = await one.PutProfileAsync(Vm("one"), Web.Replace("\"20\"", "\"22\""), created.Revision);
        h.Guests.Apply("one", "web", first);
        // VM two's agent edited on the revision before, as if its write and the write-back crossed.
        var second = await two.PutProfileAsync(Vm("two"), Web.Replace("\"20\"", "\"18\""), created.Revision);
        Assert.Equal("conflict", second.Status);
        Assert.Equal(["sdks.node"], second.Fields);

        // The VMs whose edits it holds get it in their heartbeat reply; their folders are left alone meanwhile.
        await h.SyncAsync();
        var held = h.Guests.File("two", "web");
        await h.SyncAsync();
        Assert.Equal(held, h.Guests.File("two", "web"));
        var notices = await h.HeartbeatAsync(two, "two");
        var notice = Assert.Single(notices)!;
        Assert.Equal(second.ConflictId, notice["id"]!.GetValue<string>());
        Assert.Equal("web", notice["profile"]!.GetValue<string>());

        var details = (await two.GetFromJsonAsync<List<ProfileConflictResponse>>("/api/v1/vms/two/profile-conflicts"))!;
        var conflict = Assert.Single(details);
        Assert.Equal("18", conflict.Ours!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("22", conflict.Theirs!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("20", conflict.Base!["sdks"]!["node"]!.GetValue<string>());

        // Resolved once; a second resolution of the same conflict writes nothing.
        var resolved = await two.ResolveAsync($"/api/v1/vms/two/profile-conflicts/{conflict.Id}/resolve", Web.Replace("\"20\"", "\"24\""));
        Assert.Equal("written", resolved.Status);
        h.Guests.Apply("two", "web", resolved);
        var again = await one.ResolveAsync($"/api/v1/vms/one/profile-conflicts/{conflict.Id}/resolve", Web.Replace("\"20\"", "\"16\""));
        Assert.Equal("closed", again.Status);

        await h.SyncAsync();
        Assert.Empty(await h.HeartbeatAsync(two, "two"));
        Assert.Equal("24", h.Guests.Profile("one", "web")!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal(resolved.Revision, h.Guests.Rev("one", "web"));
    }

    [Fact]
    public async Task An_edit_on_a_revision_from_before_a_deletion_is_a_conflict_after_the_profile_was_created_again()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var device = h.DeviceClient(h.Laptop!);
        using var one = await h.VmAsync("one", "web");
        var created = await device.PutProfileAsync("/api/v1/profiles/web", Web, null);
        await h.SyncAsync();
        Assert.Equal(created.Revision, h.Guests.Rev("one", "web"));

        h.Guests.Down.Add("one");
        var deleted = await device.DeleteProfileAsync("/api/v1/profiles/web", created.Revision);
        Assert.Equal("written", deleted.Status);
        Assert.Null(deleted.File);
        var again = await device.PutProfileAsync("/api/v1/profiles/web", Web.Replace("\"20\"", "\"22\""), deleted.Revision);
        Assert.Equal("written", again.Status);
        await h.SyncAsync();

        // The VM still holds the revision from before the deletion.
        Assert.Equal(created.Revision, h.Guests.Rev("one", "web"));
        h.Guests.Down.Remove("one");
        var edit = await one.PutProfileAsync(Vm("one"), Web.Replace("\"20\"", "\"18\""), created.Revision);
        Assert.Equal("conflict", edit.Status);
        Assert.Contains("deleted", edit.Fields!);
        Assert.Equal("22", (await device.GetFromJsonAsync<ProfileResponse>("/api/v1/profiles/web"))!.Profile["sdks"]!["node"]!.GetValue<string>());

        // A deletion is only ever explicit: the VM deleting on the old revision is a conflict too.
        var deletion = await one.DeleteProfileAsync(Vm("one"), created.Revision);
        Assert.Equal("conflict", deletion.Status);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/profiles/web")).StatusCode);
    }

    [Fact]
    public async Task A_direct_edit_is_picked_up_from_the_digest_and_written_into_the_other_VMs()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var one = await h.VmAsync("one", "web");
        using var two = await h.VmAsync("two", "web");
        var created = await one.PutProfileAsync(Vm("one"), Web, null);
        h.Guests.Apply("one", "web", created);
        await h.SyncAsync();

        // The agent edits the file itself; the heartbeat carries the new digest.
        h.Guests.Edit("one", "web", Web.Replace("[\"npm ci\"]", "[\"npm ci\",\"make\"]"));
        await h.HeartbeatAsync(one, "one");
        await h.SyncAsync();
        await h.SyncAsync();

        foreach (var vm in new[] { "one", "two" })
            Assert.Equal(["npm ci", "make"], h.Guests.Profile(vm, "web")!["provisionCommands"]!.AsArray().Select(c => c!.GetValue<string>()));
        var revision = h.Guests.Rev("two", "web");
        Assert.True(revision > created.Revision);
        Assert.Equal(revision, h.Guests.Rev("one", "web"));

        // An invalid file is skipped, and nothing loops: further heartbeats change nothing.
        h.Guests.Edit("two", "web", "{ not json");
        await h.HeartbeatAsync(two, "two");
        await h.HeartbeatAsync(one, "one");
        await h.SyncAsync();
        await h.SyncAsync();
        Assert.Equal(revision, h.Guests.Rev("one", "web"));
        Assert.Equal("{ not json", h.Guests.File("two", "web"));
    }

    [Fact]
    public async Task A_missing_file_is_restored_and_only_construct_project_delete_deletes()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var one = await h.VmAsync("one", "web");
        var created = await one.PutProfileAsync(Vm("one"), Web, null);
        h.Guests.Apply("one", "web", created);
        var other = await one.PutProfileAsync(Vm("one", "api"), Web.Replace("\"web\"", "\"api\""), null);
        h.Guests.Apply("one", "api", other);
        await h.SyncAsync();

        // Every file gone (the mass-deletion case): all of them come back, nothing is deleted.
        File.Delete(Path.Combine(h.Guests.Folder("one"), "web.json"));
        File.Delete(Path.Combine(h.Guests.Folder("one"), "api.json"));
        await h.HeartbeatAsync(one, "one");
        await h.SyncAsync();
        Assert.Equal("20", h.Guests.Profile("one", "web")!["sdks"]!["node"]!.GetValue<string>());
        Assert.NotNull(h.Guests.Profile("one", "api"));
        Assert.Equal(created.Revision, h.Guests.Rev("one", "web"));

        // The deletion marker (the fallback of `construct project delete`) does delete.
        File.Delete(Path.Combine(h.Guests.Folder("one"), "api.json"));
        Directory.CreateDirectory(Path.Combine(h.Guests.Folder("one"), ".deleted"));
        File.WriteAllText(Path.Combine(h.Guests.Folder("one"), ".deleted", "api"), "");
        await h.HeartbeatAsync(one, "one");
        await h.SyncAsync();
        await h.SyncAsync();
        Assert.Null(h.Guests.File("one", "api"));
        Assert.False(File.Exists(Path.Combine(h.Guests.Folder("one"), ".deleted", "api")));
        Assert.True(h.Guests.Rev("one", "api") > other.Revision);
        using var device = h.DeviceClient(h.Laptop!);
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync("/api/v1/profiles/api")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/profiles/web")).StatusCode);
    }

    [Fact]
    public async Task A_VM_writes_only_its_own_profiles_and_a_user_without_a_device_reads_none()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var one = await h.VmAsync("one", "web");
        using var two = await h.VmAsync("two", "web");
        var response = await one.PutAsJsonAsync(Vm("two"), new ProfileWriteRequest(JsonNode.Parse(Web)!.AsObject(), null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Alice.PutAsJsonAsync(Vm("one"), new ProfileWriteRequest(JsonNode.Parse(Web)!.AsObject(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Alice.GetAsync("/api/v1/profiles")).StatusCode);

        // A name that is not the profile's, and an invalid profile, are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await one.PutAsJsonAsync(Vm("one", "api"), new ProfileWriteRequest(JsonNode.Parse(Web)!.AsObject(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await one.PutAsJsonAsync(Vm("one"), new ProfileWriteRequest(JsonNode.Parse("""{"name":"web","bogus":1}""")!.AsObject(), null))).StatusCode);

        // Bob's VM writes into Bob's account, which this engine does not take part in.
        using var bob = await h.App.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("bobvm");
        using var bobVm = h.App.CreateVmTokenClient(job.VmToken());
        var refused = await bobVm.PutAsJsonAsync(Vm("bobvm"), new ProfileWriteRequest(JsonNode.Parse(Web)!.AsObject(), null));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var device = h.DeviceClient(h.Laptop!);
        Assert.Empty((await device.GetFromJsonAsync<List<ProfileSummary>>("/api/v1/profiles"))!);
    }

    [Fact]
    public async Task A_write_without_a_base_never_overwrites()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var one = await h.VmAsync("one", "web");
        using var two = await h.VmAsync("two");
        var created = await one.PutProfileAsync(Vm("one"), Web, null);

        // The same content without a base is no conflict: nothing differs.
        var unchanged = await two.PutProfileAsync(Vm("two"), Web, null);
        Assert.Equal("written", unchanged.Status);

        var noBase = await two.PutProfileAsync(Vm("two"), Web.Replace("\"20\"", "\"18\""), null);
        Assert.Equal("conflict", noBase.Status);
        var unknownBase = await two.PutProfileAsync(Vm("two"), Web.Replace("\"20\"", "\"16\""), 999_999);
        Assert.Equal("conflict", unknownBase.Status);
        using var device = h.DeviceClient(h.Laptop!);
        Assert.Equal("20", (await device.GetFromJsonAsync<ProfileResponse>("/api/v1/profiles/web"))!.Profile["sdks"]!["node"]!.GetValue<string>());
        Assert.Null((await device.GetFromJsonAsync<List<ProfileConflictResponse>>("/api/v1/profile-conflicts"))!.First(c => c.Id == noBase.ConflictId).Base);
    }

    [Fact]
    public async Task Two_engines_sync_one_profile_through_a_standalone_store()
    {
        using var directory = new TempDirectory();
        await using var a = await ProfileSyncHarness.StartRemoteAsync("token-a");
        await using var b = await ProfileSyncHarness.StartRemoteAsync("token-b");
        await using var store = new StandaloneStoreFixture(directory.Path,
            ("token-a", ParticipantKind.Engine, a.EngineId), ("token-b", ParticipantKind.Engine, b.EngineId));
        a.RemoteStores.Server = store.Handler;
        b.RemoteStores.Server = store.Handler;
        var keys = AccountKeys.Create();
        a.Grant(keys);
        b.Grant(keys);

        using var vmA = await a.VmAsync("vma", "web");
        using var vmB = await b.VmAsync("vmb", "web");
        var created = await vmA.PutProfileAsync(Vm("vma"), Web, null);
        Assert.Equal("written", created.Status);
        a.Guests.Apply("vma", "web", created);
        Assert.NotNull(await store.Store.GetAsync(ProfileSyncHarness.User, "profile:web", default));

        await b.SyncAsync();
        Assert.Equal(created.Revision, b.Guests.Rev("vmb", "web"));

        // An edit on engine B, made while the store was unreachable, waits in B's outbox.
        b.RemoteStores.Down = true;
        var queued = await vmB.PutProfileAsync(Vm("vmb"), Web.Replace("\"20\"", "\"22\""), created.Revision);
        Assert.Equal("queued", queued.Status);
        Assert.Equal(created.Revision, queued.Revision);
        b.Guests.Apply("vmb", "web", queued);
        await b.SyncAsync();
        Assert.Equal("22", b.Guests.Profile("vmb", "web")!["sdks"]!["node"]!.GetValue<string>());

        b.RemoteStores.Down = false;
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("22", a.Guests.Profile("vma", "web")!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal(a.Guests.Rev("vma", "web"), b.Guests.Rev("vmb", "web"));
        Assert.True(a.Guests.Rev("vma", "web") > created.Revision);
    }

    [Fact]
    public async Task A_device_revoked_at_the_store_is_refused_by_the_engine_after_its_next_pass()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        var laptop = h.Laptop!;
        using var desktop = new TestDevice("desktop", () => h.App.Server.CreateHandler(), ProfileSyncHarness.StoreBase);
        var (pairingId, _) = await desktop.StartPairingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.ApproveAsync(pairingId)).Response.StatusCode);
        await desktop.CompletePairingAsync(pairingId);
        desktop.EngineTokens[h.EngineId] = await h.EnrollAtEngineAsync(desktop);
        using var client = h.DeviceClient(desktop);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/profiles")).StatusCode);

        // The laptop revokes the desktop at the store only; this engine was not reached.
        using (var storeClient = laptop.Store())
            Assert.Equal(HttpStatusCode.NoContent, (await storeClient.DeleteAsync("devices/" + desktop.Id)).StatusCode);
        await laptop.WriteDeviceRecordAsync(desktop, DeviceRecordValue.Revoked);

        await h.SyncAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/profiles")).StatusCode);
        using (var storeClient = laptop.Store())
        {
            var entry = (await storeClient.GetFromJsonAsync<List<DeviceEntry>>("devices", StoreJson.Options))!.Single(d => d.Id == desktop.Id);
            Assert.Equal([h.EngineId], entry.AcknowledgedBy);
        }
    }
}
