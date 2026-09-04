using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

/// <summary>
/// The per-VM public host name rule (plan §4.12). The pattern is validated at startup, so what is
/// pinned here is exactly what the service will and will not accept from an admin's configuration.
/// </summary>
public class PublicHostPatternRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_pattern_is_valid_and_means_the_service_public_host(string? pattern)
    {
        Assert.Null(PublicHostPatternRules.Validate(pattern));
        Assert.Equal("buildbox.test", PublicHostPatternRules.Resolve(pattern, "buildbox.test", "work-vm"));
    }

    [Theory]
    [InlineData("{name}.vpn.example")]
    [InlineData("{name}.a.b.c.example.org")]
    [InlineData("{name}.vm.vpn.example")]
    public void A_usable_pattern_is_accepted(string pattern) =>
        Assert.Null(PublicHostPatternRules.Validate(pattern));

    [Fact]
    public void A_pattern_without_the_placeholder_is_refused()
    {
        var problem = PublicHostPatternRules.Validate("vpn.example");
        Assert.NotNull(problem);
        Assert.Contains("{name}", problem);
    }

    [Fact]
    public void The_placeholder_is_matched_ordinally_so_a_cased_one_is_not_it()
    {
        // {NAME} is not the placeholder, so this pattern names no VM — and every VM would end up
        // on the same host name, which is the whole thing the setting exists to prevent.
        Assert.NotNull(PublicHostPatternRules.Validate("{NAME}.vpn.example"));
    }

    [Fact]
    public void A_pattern_with_two_placeholders_is_refused()
    {
        var problem = PublicHostPatternRules.Validate("{name}.{name}.vpn.example");
        Assert.NotNull(problem);
        Assert.Contains("exactly once", problem);
    }

    [Theory]
    // Renders to a label that ends in a hyphen for EVERY name.
    [InlineData("{name}-.vpn.example")]
    // ...and one that starts with a dot.
    [InlineData(".{name}.vpn.example")]
    // A fixed suffix inside the label: fine for a short name, over the 63-character DNS label
    // limit for the longest name the instance-name rule allows. That is why validation renders
    // the extremes instead of reasoning about the pattern.
    [InlineData("{name}-verylongsuffix-that-pushes-the-label-past-the-limit-aaaaaaaa.vpn.example")]
    // An affix in the SAME label as the name is refused for the same reason: the longest valid
    // instance name is already a full 63-character label, so "vm-{name}" cannot render one.
    // Put the fixed part in its own label ("{name}.vm.vpn.example") instead.
    [InlineData("vm-{name}.vpn.example")]
    [InlineData("{name} .vpn.example")]
    [InlineData("http://{name}.vpn.example")]
    public void A_pattern_that_cannot_render_a_host_name_is_refused(string pattern) =>
        Assert.NotNull(PublicHostPatternRules.Validate(pattern));

    [Fact]
    public void Resolve_renders_the_vm_name()
    {
        Assert.Equal(
            "work-vm.vpn.example",
            PublicHostPatternRules.Resolve("{name}.vpn.example", "buildbox.test", "work-vm"));
    }

    [Fact]
    public void Resolve_falls_back_when_there_is_no_vm_name()
    {
        Assert.Equal(
            "buildbox.test",
            PublicHostPatternRules.Resolve("{name}.vpn.example", "buildbox.test", null));
    }

    [Fact]
    public void Options_render_through_the_same_rule()
    {
        var options = new ConstructdOptions { PublicHost = "buildbox.test" };
        Assert.Equal("buildbox.test", options.PublicHostFor("work-vm"));

        options.PublicHostPattern = "{name}.vpn.example";
        Assert.Equal("work-vm.vpn.example", options.PublicHostFor("work-vm"));
        Assert.Equal("other-vm.vpn.example", options.PublicHostFor("other-vm"));
    }

    [Fact]
    public void The_host_name_rule_matches_the_clients()
    {
        // The same accept/reject matrix the two registry readers apply to `publicHost`
        // (extension/src/instances.js HOSTNAME_RE, lib/AgentVm.Instances.ps1
        // $script:ConstructHostNameRe): a pattern the service accepts and they refuse would
        // produce VMs that cannot be recorded on the client.
        Assert.True(PublicHostPatternRules.IsHostName("work-vm.vpn.example"));
        Assert.True(PublicHostPatternRules.IsHostName("buildbox"));
        Assert.False(PublicHostPatternRules.IsHostName(""));
        Assert.False(PublicHostPatternRules.IsHostName("-x; calc"));
        Assert.False(PublicHostPatternRules.IsHostName("work-.vpn.example"));
        Assert.False(PublicHostPatternRules.IsHostName("work vm.vpn.example"));
        // .NET's `$` also matches before a trailing newline; \A/\z is why this is false here
        // and in JavaScript.
        Assert.False(PublicHostPatternRules.IsHostName("work-vm.vpn.example\n"));
    }
}
