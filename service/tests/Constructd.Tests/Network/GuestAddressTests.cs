using System.Net;
using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Windows.Network;
using Constructd.Tests.Support;
using Microsoft.Extensions.Logging;
namespace Constructd.Tests.Network;

public sealed class GuestAddressTests
{
    [Theory]
    [InlineData("10.2.3.4", true)] [InlineData("10.2.3.255", false)] [InlineData("10.2.3.0", false)]
    [InlineData("127.0.0.1", false)] [InlineData("0.0.0.0", false)] [InlineData("255.255.255.255", false)]
    [InlineData("169.254.1.1", false)] [InlineData("224.0.0.1", false)] [InlineData("10.2.3.1", false)]
    [InlineData("10.3.3.4", false)] [InlineData("::1", false)] [InlineData("fe80::2", false)]
    [InlineData("ff02::1", false)] [InlineData("::", false)] [InlineData("fd00::2", true)]
    [InlineData("::ffff:10.2.3.4", false)]
    public void Sanity_rules_filter_addresses_without_claiming_ownership(string value, bool expected)
    {
        var adapter = new GuestAdapter("id", "nic", "aa", false, "switch");
        Assert.Equal(expected, GuestAddressRules.Usable(IPAddress.Parse(value), [IPAddress.Parse("10.2.3.1")],
            [adapter], [adapter], [new("10.2.3.1/24", "switch", "vEthernet"), new("fd00::1/64", "switch", "vEthernet")]));
    }
    [Fact]
    public void Overlapping_subnets_on_different_switches_do_not_match()
    {
        var child = new GuestAdapter("id", "nic", "aa", false, "one");
        var via = child with { SwitchName = "two" };
        Assert.False(GuestAddressRules.Usable(IPAddress.Parse("10.2.3.4"), [], [child], [via], [new("10.2.3.0/24", "one", "eth")]));
    }
    [Fact]
    public async Task Provider_pins_argv_passes_identity_in_stdin_and_marks_output_unverified()
    {
        var runner = new RecordingProcessRunner().RespondStdout("""
            {"ok":true,"value":{"vms":[{"name":"child","addresses":[{"address":"10.2.3.4","family":"ipv4","source":"kvp","observedAt":"2026-01-01T00:00:00Z","verified":true,"adapterId":"nic"}],"adapters":[]}],"neighbors":[],"subnets":[],"hostAddresses":[]}}
            """);
        var vms = new InMemoryVmRepository();
        await vms.AddAsync(new("child", "alice", 1, 1, 10, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child, Incarnation: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 10, default);
        var clock = new MutableClock();
        var provider = new HyperVGuestAddressProvider(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, vms, clock);
        var address = Assert.Single(await provider.GetReportedAddressesAsync("child", default));
        Assert.False(address.Verified); Assert.Equal(clock.UtcNow, address.ObservedAt);
        var call = Assert.Single(runner.Calls); Assert.Equal("powershell.exe", call.FileName);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" }, call.Arguments.Take(5));
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[5]));
        Assert.Contains("Get-ConstructVmAddresses -VmRequests @($data.vms)", script);
        Assert.DoesNotContain("aaaaaaaa", script);
        using var input = JsonDocument.Parse(call.StandardInput!);
        Assert.Equal("child", input.RootElement.GetProperty("vms")[0].GetProperty("name").GetString()); Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", input.RootElement.GetProperty("vms")[0].GetProperty("vmId").GetString());
    }
    [Theory]
    [InlineData("not-json")] [InlineData("{\"ok\":false}")] [InlineData("{\"ok\":true,\"value\":{}}")]
    public async Task Provider_failures_are_empty_without_leaking_process_output(string output)
    {
        var runner = new RecordingProcessRunner().RespondStdout(output);
        using var logs = new CapturedLogs(); using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var provider = new HyperVGuestAddressProvider(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, new InMemoryVmRepository(), new MutableClock(), factory.CreateLogger<HyperVGuestAddressProvider>());
        Assert.Empty(await provider.GetHostAddressesAsync(default));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning);
        runner.Failure = new IOException("sensitive guest output");
        Assert.Empty(await provider.GetGuestSubnetsAsync(default));
        runner.Failure = null; runner.Default = new(42, "sensitive guest output", "sensitive guest output", false);
        Assert.Empty(await provider.GetGuestSubnetsAsync(default));
        Assert.Equal(3, logs.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.DoesNotContain("sensitive guest output", logs.AllText());
        Assert.DoesNotContain(output, logs.AllText());
        Assert.Contains("42", logs.AllText());
    }
    [Fact]
    public async Task Multiple_adapters_require_report_to_match_the_adapter_on_the_shared_switch_and_history_survives_forward_removal()
    {
        var vms = new InMemoryVmRepository(); var source = new FakeGuestAddressProvider(); var history = new InMemoryNetworkRuleStore();
        var child = new Vm("child", "alice", 1, 1, 10, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child, Incarnation: "id");
        await vms.AddAsync(child, 10, default); await vms.AddAsync(child with { Name = "parent", Kind = VmKind.Primary }, 10, default);
        source.Adapters["child"] = [new("id", "one", "aa", false, "one"), new("id", "two", "bb", false, "two")];
        source.Adapters["parent"] = [new("id", "nic", "cc", false, "one")];
        source.HostAddresses = [IPAddress.Parse("10.2.3.1")];
        source.Subnets = [new("10.2.3.0/24", "one", "eth"), new("10.2.3.0/24", "two", "eth2")];
        var address = new GuestAddress("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, DateTimeOffset.UtcNow, false, "two");
        source.Reported["child"] = [address]; var resolver = new GuestAddressResolver(source, vms, history);
        Assert.Null((await resolver.SelectAsync(child, "parent", default)).Address);
        source.Reported["child"] = [address with { AdapterId = "one" }];
        Assert.Equal("10.2.3.4", (await resolver.SelectAsync(child, "parent", default)).Address);
        await history.RememberAddressAsync("parent", "10.2.3.4", default);
        Assert.True((await resolver.SelectAsync(child, "parent", default)).Conflict);
    }
    [Theory]
    [InlineData("vm-not-found")]
    [InlineData("adapter-query-failed")]
    [InlineData("sensitive dependency output")]
    public async Task Per_vm_failure_logs_only_category_and_name_and_preserves_sibling_reports(string error)
    {
        var vms = new InMemoryVmRepository(); var clock = new MutableClock();
        var healthy = new Vm("healthy", "alice", 1, 1, 10, clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            Kind: VmKind.Child, Incarnation: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await vms.AddAsync(healthy, 10, default);
        await vms.AddAsync(healthy with { Name = "missing", Kind = VmKind.Primary, State = VmState.Unknown, Incarnation = null }, 10, default);
        await vms.AddAsync(healthy with { Name = "absent", Kind = VmKind.Primary, State = VmState.Absent }, 10, default);
        var report = new GuestAddress("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, clock.UtcNow, false, "nic");
        var facts = new HyperVGuestAddressProvider.Snapshot(
            [new("healthy", [report], []), new("missing", [report], [], error)], [], [], ["10.2.3.1"]);
        var runner = new RecordingProcessRunner().RespondStdout(JsonSerializer.Serialize(new { ok = true, value = facts }, Constructd.Api.Infrastructure.ApiJson.Options));
        using var logs = new CapturedLogs(); using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var provider = new HyperVGuestAddressProvider(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, vms, clock, factory.CreateLogger<HyperVGuestAddressProvider>());
        var snapshot = await provider.CaptureAsync(default);
        Assert.Equal("10.2.3.4", Assert.Single(await snapshot.GetReportedAddressesAsync("healthy", default)).Address);
        Assert.Empty(await snapshot.GetReportedAddressesAsync("missing", default));
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("missing", logs.AllText());
        Assert.Contains(error == "sensitive dependency output" ? "vm-query-failed" : error, logs.AllText());
        Assert.DoesNotContain("sensitive dependency output", logs.AllText());
        using var input = JsonDocument.Parse(Assert.Single(runner.Calls).StandardInput!);
        Assert.Equal(2, input.RootElement.GetProperty("vms").GetArrayLength());
        Assert.DoesNotContain("absent", input.RootElement.GetProperty("vms").GetRawText());
    }

}
