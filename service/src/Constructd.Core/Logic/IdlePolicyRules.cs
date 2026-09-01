using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// The admin-vs-user split of plan §4.7: the user sets their own VM's policy, the admin sets the
/// service-wide default and an optional cap. Clamping is applied when a policy is stored AND again
/// when it is evaluated, so lowering the cap takes effect on VMs that were configured earlier.
/// </summary>
public static class IdlePolicyRules
{
    public static IdlePolicy Default(IdleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Clamp(new IdlePolicy(options.DefaultTimeoutMinutes, options.DefaultAction), options);
    }

    /// <summary>
    /// Applies the admin cap to a user-supplied policy:
    /// <list type="bullet">
    /// <item>a timeout above <see cref="IdleOptions.MaxTimeoutMinutes"/> is lowered to the cap;</item>
    /// <item>with <see cref="IdleOptions.ForceEnabled"/>, "off" (timeout 0 or action off) is replaced
    /// by the capped default, so a user cannot opt out of idling entirely;</item>
    /// <item>negative timeouts are normalized to 0 ("off").</item>
    /// </list>
    /// </summary>
    public static IdlePolicy Clamp(IdlePolicy policy, IdleOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);

        var timeout = Math.Max(0, policy.TimeoutMinutes);
        var action = policy.Action;
        var cap = options.MaxTimeoutMinutes;

        if (options.ForceEnabled)
        {
            if (action == IdleAction.Off)
            {
                action = options.DefaultAction == IdleAction.Off ? IdleAction.Save : options.DefaultAction;
            }

            if (timeout <= 0)
            {
                timeout = cap > 0 ? cap : Math.Max(1, options.DefaultTimeoutMinutes);
            }
        }

        if (cap > 0 && timeout > cap)
        {
            timeout = cap;
        }

        return timeout == policy.TimeoutMinutes && action == policy.Action
            ? policy
            : new IdlePolicy(timeout, action);
    }

    /// <summary>True when clamping would change the requested policy (surfaced on the API response).</summary>
    public static bool WouldClamp(IdlePolicy policy, IdleOptions options) =>
        !Equals(Clamp(policy, options), policy);
}
