using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Network;

public sealed class ChildForwardTests
{
    private static async Task<Vm> Child(TestApp app, string parent = "parent", string name = "child", SharingScope sharing = SharingScope.Private)
    {
        var primary = (await app.Vms.GetAsync(parent, default))!;
        var child = primary with { Name = name, Kind = VmKind.Child, Parent = parent, Sharing = sharing, VmTokenHash = null,
            SshForwardPort = null, Incarnation = Guid.NewGuid().ToString(), TokenKind = VmTokenKind.Legacy };
        await app.Vms.AddAsync(child, 50, default);
        var addresses = app.Service<FakeGuestAddressProvider>();
        addresses.Adapters[name] = [new(child.Incarnation, "nic", "001122334455", false, "switch")];
        addresses.Adapters[parent] = [new(primary.Incarnation ?? "primary-id", "nic", "001122334456", false, "switch")];
        addresses.Reported[name] = [new("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, app.Clock.UtcNow, false)];
        addresses.HostAddresses = [IPAddress.Parse("10.2.3.1")];
        addresses.Subnets = [new("10.2.3.1/24", "switch", "vEthernet (switch)")];
        return child;
    }
    [Fact]
    public async Task Parent_requests_child_and_owner_lists_acks_removes_without_changing_primary_shape()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        var job = await owner.CreateVmAsync("parent"); using var parent = app.CreateVmTokenClient(job.VmToken());
        await Child(app);
        var forward = await (await parent.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 3000, connectPort = 80 })).ReadAsync<ForwardResponse>();
        Assert.Equal("child", forward.VmName); Assert.Equal("10.2.3.4", forward.Destination!.ConnectAddress);
        Assert.Equal("parent", forward.Destination.Via); Assert.Equal("vm:parent", forward.Destination.RequestedBy);
        Assert.Equal(80, forward.Destination.ConnectPort); Assert.False(forward.Destination.Verified);
        Assert.Equal(ForwardRelationship.Parent, forward.Destination.Relationship);
        var list = await (await owner.GetAsync("/api/v1/vms/parent/forwards?via=parent")).ReadAsync<List<ForwardResponse>>();
        Assert.Single(list); Assert.Equal(forward.Id, list[0].Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await parent.PostJsonAsync($"/api/v1/vms/child/forwards/{forward.Id}/ack", new { status = "open", localPort = 3000 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync($"/api/v1/vms/child/forwards/{forward.Id}/ack", new { status = "open", localPort = 3000 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync($"/api/v1/vms/child/forwards/{forward.Id}")).StatusCode);
        var legacyShape = await owner.PostJsonAsync("/api/v1/vms/parent/forwards", new { vmPort = 3000 });
        Assert.DoesNotContain("destination", await legacyShape.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostJsonAsync("/api/v1/vms/parent/forwards", new { vmPort = 3000, via = "parent" })).StatusCode);
    }
    [Theory]
    [InlineData(false, true, "host-forwards-disabled", HttpStatusCode.Forbidden)]
    [InlineData(true, false, "host-forwards-disabled", HttpStatusCode.Forbidden)]
    [InlineData(true, true, "address-unverifiable", HttpStatusCode.Conflict)]
    public async Task Host_and_owner_policy_cannot_be_bypassed_through_parent_or_shared_consumer(bool host, bool ownerAllowed, string code, HttpStatusCode status)
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice", allowHostForwards: ownerAllowed);
        var job = await owner.CreateVmAsync("parent"); using var parent = app.CreateVmTokenClient(job.VmToken());
        await Child(app, sharing: SharingScope.Host);
        using var other = await app.CreateUserClientAsync("bob", allowHostForwards: true);
        await app.Service<IHostConfigStore>().SetAsync("network", new NetworkConfig(host, true), "test", default);
        foreach (var caller in new[] { owner, parent, other })
        {
            using var response = await caller.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80, target = "host" });
            Assert.Equal(status, response.StatusCode); Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Empty(await app.Forwards.ListAsync("child", default));
    }
    [Fact]
    public async Task Shared_consumers_use_their_own_primary_and_rows_and_cannot_remove_or_ack_others()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent");
        var child = await Child(app, sharing: SharingScope.Host);
        using var bob = await app.CreateUserClientAsync("bob"); var job = await bob.CreateVmAsync("bob-primary"); using var guest = app.CreateVmTokenClient(job.VmToken());
        app.Service<FakeGuestAddressProvider>().Adapters["bob-primary"] = [new("id", "nic", "aa", false, "switch")];
        var own = await (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
        var shared = await (await guest.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
        Assert.NotEqual(own.Id, shared.Id); Assert.Equal("bob-primary", shared.Destination!.Via);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80, via = "parent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.DeleteAsync($"/api/v1/vms/child/forwards/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostJsonAsync($"/api/v1/vms/child/forwards/{own.Id}/ack", new { status = "open", localPort = 80 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostJsonAsync($"/api/v1/vms/child/forwards/{shared.Id}/ack", new { status = "open", localPort = 80 })).StatusCode);
        Assert.Single(await (await guest.GetAsync("/api/v1/vms/child/forwards")).ReadAsync<List<ForwardResponse>>());
        await app.Service<IAdmissionStore>().MutateAsync(null, scope => scope.UpdateSharingAsync(child.Name, SharingScope.Private), default);
        await app.Service<INetworkPolicyReconciler>().OnSharingChangedAsync(child with { Sharing = SharingScope.Private }, SharingScope.Host, default);
        Assert.Single(await app.Forwards.ListAsync("child", default));
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/vms/child/forwards")).StatusCode);
    }
    [Fact]
    public async Task Legacy_child_and_wrong_parent_credentials_cannot_request_child_forwards()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent"); var child = await Child(app);
        using var other = await app.CreateUserClientAsync("bob"); var job = await other.CreateVmAsync("wrong-parent"); using var wrong = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Forbidden, (await wrong.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        var rotation = await owner.PostJsonAsync("/api/v1/vms/parent/token", new { kind = "legacy" });
        var token = (await rotation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("vmToken").GetString()!;
        using var legacy = app.CreateVmTokenClient(token);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/vms/parent/forwards?via=parent")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/vms/parent/forwards?includeChildren=TRUE")).StatusCode);
        // Even a corrupt child row carrying a copied hash is rejected by token authentication.
        await app.Vms.UpdateAsync(child with { VmTokenHash = (await app.Vms.GetAsync("parent", default))!.VmTokenHash }, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/vms/child/forwards")).StatusCode);
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
    }
    [Fact]
    public async Task Address_unknown_change_conflict_and_return_clear_stale_acks()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent"); await Child(app);
        var addresses = app.Service<FakeGuestAddressProvider>(); addresses.Reported["child"] = [];
        var f = await (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
        Assert.Equal("error", f.Status); Assert.Equal("guest address unknown yet", f.Message);
        addresses.Reported["child"] = [new("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, app.Clock.UtcNow, false)];
        await app.Service<AccessExposure>().ReconcileAsync(default);
        Assert.Null((await app.Service<IForwardStore>().GetAsync(f.Id, default))!.Ack);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync($"/api/v1/vms/child/forwards/{f.Id}/ack", new { status = "open", localPort = 80 })).StatusCode);
        addresses.Reported["parent"] = addresses.Reported["child"];
        await app.Service<AccessExposure>().ReconcileAsync(default);
        var changed = (await app.Service<IForwardStore>().GetAsync(f.Id, default))!;
        Assert.Null(changed.Destination!.ConnectAddress); Assert.Equal("guest address changed", changed.Ack!.Message);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync($"/api/v1/vms/child/forwards/{f.Id}/ack", new { status = "open", localPort = 80 })).StatusCode);
        addresses.Reported["parent"] = [];
        await app.Service<AccessExposure>().ReconcileAsync(default);
        Assert.Null((await app.Service<IForwardStore>().GetAsync(f.Id, default))!.Ack);
    }
    [Fact]
    public async Task Intended_rules_follow_create_sharing_delete_and_never_claim_isolation()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent"); var child = await Child(app);
        using var bob = await app.CreateUserClientAsync("bob"); await bob.CreateVmAsync("consumer");
        var policy = app.Service<INetworkPolicyReconciler>();
        await policy.OnVmCreatedAsync(child, default);
        var rules = await policy.ListRulesAsync("child", default); Assert.Equal("parent-child", Assert.Single(rules).Kind);
        await policy.OnSharingChangedAsync(child with { Sharing = SharingScope.Host }, SharingScope.Private, default);
        rules = await policy.ListRulesAsync("child", default); Assert.Equal(2, rules.Count); Assert.All(rules, r => Assert.Equal("intended", r.State));
        Assert.Equal("none", policy.IsolationLevel);
        await policy.OnVmDeletedAsync("child", default); Assert.Empty(await policy.ListRulesAsync("child", default));
        var direct = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/child/addresses");
        Assert.False(direct.GetProperty("addresses")[0].GetProperty("verified").GetBoolean()); Assert.Equal("none", direct.GetProperty("isolation").GetString());
    }
    [Fact]
    public async Task Multiple_primaries_and_admin_access_require_explicit_owned_via_and_wrong_target_ids_fail()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent"); await Child(app); await owner.CreateVmAsync("second");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        var f = await (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80, via = "parent" })).ReadAsync<ForwardResponse>();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin); await admin.CreateVmAsync("admin-primary");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80, via = "parent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80, via = "admin-primary" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/vms/parent/forwards/{f.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostJsonAsync($"/api/v1/vms/parent/forwards/{f.Id}/ack", new { status = "open", localPort = 80 })).StatusCode);
        Assert.NotNull(await app.Service<IForwardStore>().GetAsync(f.Id, default));
    }
    [Fact]
    public async Task Address_evidence_and_stale_ack_validation_fail_closed()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent"); await Child(app);
        var f = await (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
        var store = app.Service<IForwardStore>(); var before = (await store.GetAsync(f.Id, default))!;
        var addresses = app.Service<FakeGuestAddressProvider>(); addresses.HostAddresses = [];
        Assert.False(await app.Service<AccessExposure>().TryAckAsync(before, new(AckStatus.Open, 80, null, "", app.Clock.UtcNow), default));
        Assert.Equal(AckStatus.Error, (await store.GetAsync(f.Id, default))!.Ack!.Status);
        addresses.HostAddresses = [IPAddress.Parse("10.2.3.1")];
        addresses.Reported["child"] = [new("10.2.3.5", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, app.Clock.UtcNow, false)];
        await app.Service<AccessExposure>().ReconcileAsync(default);
        Assert.False(await app.Service<AccessExposure>().TryAckAsync(before, new(AckStatus.Open, 80, null, "", app.Clock.UtcNow), default));
        Assert.Null((await store.GetAsync(f.Id, default))!.Ack);
        await app.Service<IHostConfigStore>().SetAsync("network", new NetworkConfig(true, false), "test", default);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/vms/child/addresses")).StatusCode);
    }
    [Fact]
    public async Task Revoking_primary_token_removes_its_child_tunnels_on_reconciliation()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); var job = await owner.CreateVmAsync("parent"); await Child(app);
        using var parent = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Created, (await parent.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/vms/parent/token")).StatusCode);
        await app.Service<AccessExposure>().ReconcileAsync(default);
        Assert.Empty(await app.Forwards.ListAsync("child", default));
    }

}
