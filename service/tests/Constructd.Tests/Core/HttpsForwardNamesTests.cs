using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

/// <summary>
/// The names HTTPS forwards are served under on the shared port (docs/https-forwards.md, "Forward names") and
/// the route table that decides which name reaches which forward.
/// </summary>
public class HttpsForwardNamesTests
{
    private const string Pattern = "{name}.vms.test";
    private const string PublicHost = "buildbox.test";
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static PortForward Https(string id, string vm, int vmPort, int minutes = 0, string? name = null, bool main = false) =>
        new(id, vm, vmPort, 2300 + vmPort % 100, ForwardTarget.Host, id, T0.AddMinutes(minutes), Https: true, Main: main, HttpsName: name);

    [Fact]
    public void The_default_name_is_the_vm_port()
    {
        Assert.Equal("5173", HttpsForwardNames.Effective(Https("a", "work-vm", 5173)));
        Assert.Equal("app", HttpsForwardNames.Effective(Https("a", "work-vm", 5173, name: "app")));
        Assert.Equal("work-vm-app", HttpsForwardNames.Label("work-vm", "app"));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("a")]
    [InlineData("my-app-2")]
    [InlineData("5173")]
    public void A_dns_label_is_a_name(string name) => Assert.Null(HttpsForwardNames.Problem("work-vm", name));

    [Theory]
    [InlineData("")]
    [InlineData("App")]
    [InlineData("-app")]
    [InlineData("app-")]
    [InlineData("my_app")]
    [InlineData("a.b")]
    [InlineData("ä")]
    public void Anything_else_is_refused(string name) => Assert.NotNull(HttpsForwardNames.Problem("work-vm", name));

    [Fact]
    public void Vm_and_name_together_must_fit_one_label()
    {
        var vm = new string('v', 50);
        Assert.Null(HttpsForwardNames.Problem(vm, new string('n', 12)));            // 50 + 1 + 12 = 63
        Assert.Contains("63", HttpsForwardNames.Problem(vm, new string('n', 13))); // 64
        Assert.Null(HttpsForwardNames.Host(Pattern, PublicHost, vm, new string('n', 13)));
    }

    [Fact]
    public void Without_a_pattern_there_is_no_named_host()
    {
        Assert.Null(HttpsForwardNames.Host(null, PublicHost, "work-vm", "app"));
        Assert.Equal("work-vm-app.vms.test", HttpsForwardNames.Host(Pattern, PublicHost, "work-vm", "app"));
    }

    [Fact]
    public void Each_https_forward_is_routed_under_its_own_name_and_the_main_one_also_under_the_vms()
    {
        var t3 = Https("t3", "work-vm", 5178, main: true);
        var app = Https("app", "work-vm", 5173, 1, name: "app");
        var plain = new PortForward("plain", "work-vm", 8080, 2380, ForwardTarget.Host, "plain", T0);
        var table = HttpsRouteTable.Build([t3, app, plain], ["work-vm"], Pattern, PublicHost, "vault.vms.test");

        Assert.Equal("t3", table.Hosts["work-vm.vms.test"].Id);
        Assert.Equal("t3", table.Hosts["work-vm-5178.vms.test"].Id);
        Assert.Equal("app", table.Hosts["WORK-VM-APP.vms.test"].Id);
        Assert.DoesNotContain(table.Hosts, h => h.Value.Id == "plain");

        Assert.Equal("work-vm.vms.test", table.HostOf(t3));
        Assert.Equal("work-vm-app.vms.test", table.HostOf(app));
        Assert.Equal("work-vm-app.vms.test", table.NamedHostOf(app));
    }

    [Fact]
    public void Of_several_main_forwards_the_newest_gets_the_vms_name_and_the_older_one_keeps_its_own()
    {
        var older = Https("older", "work-vm", 5178, 0, main: true);
        var newer = Https("newer", "work-vm", 5173, 5, main: true);
        var table = HttpsRouteTable.Build([newer, older], ["work-vm"], Pattern, PublicHost, null);

        Assert.Equal("work-vm.vms.test", table.HostOf(newer));
        Assert.Equal("work-vm-5178.vms.test", table.HostOf(older));
    }

    [Fact]
    public void A_vms_name_and_the_vault_are_never_a_forwards()
    {
        // work + "vm-db" renders work-vm-db, a VM's own name; my + "vault" renders the vault's host.
        var shadowed = Https("shadowed", "work", 5432, name: "vm-db");
        var vaultLike = Https("vaultlike", "my", 9000, name: "vault");
        var table = HttpsRouteTable.Build([shadowed, vaultLike], ["work", "work-vm-db", "my"], Pattern, PublicHost, "my-vault.vms.test");

        Assert.False(table.Hosts.ContainsKey("work-vm-db.vms.test"));
        Assert.False(table.Hosts.ContainsKey("my-vault.vms.test"));
        Assert.Null(table.HostOf(shadowed));
        Assert.Null(table.HostOf(vaultLike));
    }

    [Fact]
    public void Two_forwards_with_one_name_the_older_one_keeps_it()
    {
        // work-vm + "app" and work + "vm-app" both render work-vm-app.
        var first = Https("first", "work-vm", 5173, 0, name: "app");
        var second = Https("second", "work", 5174, 1, name: "vm-app");
        var table = HttpsRouteTable.Build([second, first], ["work-vm", "work"], Pattern, PublicHost, null);

        Assert.Equal("first", table.Hosts["work-vm-app.vms.test"].Id);
        Assert.Null(table.HostOf(second));
    }

    [Fact]
    public void Forwards_of_missing_vms_and_without_a_pattern_route_nowhere()
    {
        var orphan = Https("orphan", "gone-vm", 5173, main: true);
        Assert.Empty(HttpsRouteTable.Build([orphan], ["work-vm"], Pattern, PublicHost, null).Hosts);

        var table = HttpsRouteTable.Build([Https("a", "work-vm", 5173, main: true)], ["work-vm"], null, PublicHost, "vault.test");
        Assert.Empty(table.Hosts);
        Assert.Equal("vault.test", table.VaultHost);
    }
}
