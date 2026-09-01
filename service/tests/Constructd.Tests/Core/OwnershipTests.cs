using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class OwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static Vm VmOwnedBy(string owner) => new(
        "work-vm", owner, 4, 8, 64, Now, VmState.Running, null, null, IdlePolicy.Disabled, Vm.NoForwards);

    [Fact]
    public void Owner_may_access_their_own_vm() =>
        Assert.True(Ownership.CanAccessVm("DOMAIN\\alice", Role.User, VmOwnedBy("DOMAIN\\alice")));

    [Fact]
    public void Owner_match_is_case_insensitive_like_windows_identities() =>
        Assert.True(Ownership.CanAccessVm("domain\\Alice", Role.User, VmOwnedBy("DOMAIN\\alice")));

    [Fact]
    public void Another_user_may_not_access_it() =>
        Assert.False(Ownership.CanAccessVm("DOMAIN\\mallory", Role.User, VmOwnedBy("DOMAIN\\alice")));

    [Fact]
    public void Admins_may_access_every_vm() =>
        Assert.True(Ownership.CanAccessVm("DOMAIN\\admin", Role.Admin, VmOwnedBy("DOMAIN\\alice")));

    [Fact]
    public void Anonymous_may_not_access_anything() =>
        Assert.False(Ownership.CanAccessVm(null, Role.User, VmOwnedBy("DOMAIN\\alice")));

    [Fact]
    public void Vm_tokens_reach_only_their_own_vm()
    {
        Assert.True(Ownership.VmTokenCanAccessVm("work-vm", "work-vm"));
        Assert.False(Ownership.VmTokenCanAccessVm("work-vm", "other-vm"));
        Assert.False(Ownership.VmTokenCanAccessVm(null, "work-vm"));
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, false)]
    [InlineData(0, 0, false)]
    public void Quota_is_enforced_on_creation(int maxVms, int owned, bool allowed)
    {
        var user = new User("DOMAIN\\alice", Role.User, maxVms, Now);
        Assert.Equal(allowed, Ownership.CanCreateVm(user, owned));
    }
}
