using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class IdlePolicyRulesTests
{
    private static IdleOptions Options(int cap = 0, bool force = false, int defaultTimeout = 120) => new()
    {
        MaxTimeoutMinutes = cap,
        ForceEnabled = force,
        DefaultTimeoutMinutes = defaultTimeout,
        DefaultAction = IdleAction.Save,
    };

    [Fact]
    public void Without_a_cap_the_user_policy_is_kept()
    {
        var policy = new IdlePolicy(600, IdleAction.Shutdown);

        Assert.Equal(policy, IdlePolicyRules.Clamp(policy, Options()));
    }

    [Fact]
    public void A_timeout_above_the_cap_is_lowered()
    {
        var clamped = IdlePolicyRules.Clamp(new IdlePolicy(600, IdleAction.Save), Options(cap: 60));

        Assert.Equal(60, clamped.TimeoutMinutes);
        Assert.Equal(IdleAction.Save, clamped.Action);
    }

    [Fact]
    public void A_timeout_below_the_cap_is_untouched()
    {
        var clamped = IdlePolicyRules.Clamp(new IdlePolicy(30, IdleAction.Save), Options(cap: 60));

        Assert.Equal(30, clamped.TimeoutMinutes);
    }

    [Fact]
    public void Off_survives_a_cap_unless_idling_is_forced()
    {
        var off = new IdlePolicy(0, IdleAction.Off);

        Assert.Equal(off, IdlePolicyRules.Clamp(off, Options(cap: 60)));

        var forced = IdlePolicyRules.Clamp(off, Options(cap: 60, force: true));
        Assert.Equal(60, forced.TimeoutMinutes);
        Assert.Equal(IdleAction.Save, forced.Action);
    }

    [Fact]
    public void Forcing_without_a_cap_falls_back_to_the_default_timeout()
    {
        var forced = IdlePolicyRules.Clamp(new IdlePolicy(0, IdleAction.Off), Options(force: true, defaultTimeout: 45));

        Assert.Equal(45, forced.TimeoutMinutes);
        Assert.Equal(IdleAction.Save, forced.Action);
    }

    [Fact]
    public void Negative_timeouts_normalize_to_off()
    {
        var clamped = IdlePolicyRules.Clamp(new IdlePolicy(-5, IdleAction.Save), Options());

        Assert.Equal(0, clamped.TimeoutMinutes);
        Assert.True(clamped.IsDisabled);
    }

    [Fact]
    public void The_service_default_is_clamped_too()
    {
        var policy = IdlePolicyRules.Default(Options(cap: 30, defaultTimeout: 120));

        Assert.Equal(30, policy.TimeoutMinutes);
    }

    [Fact]
    public void Would_clamp_reports_whether_the_request_survives_unchanged()
    {
        Assert.True(IdlePolicyRules.WouldClamp(new IdlePolicy(600, IdleAction.Save), Options(cap: 60)));
        Assert.False(IdlePolicyRules.WouldClamp(new IdlePolicy(30, IdleAction.Save), Options(cap: 60)));
    }
}
