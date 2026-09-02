using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class VmNameValidatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("agent-vm")]
    [InlineData("work-vm-2")]
    [InlineData("0abc")]
    [InlineData("work")]                // the instance "construct-work" used to alias
    [InlineData("construct")]           // "construct" without the hyphen is a fine name
    [InlineData("constructor")]         // the prototype-pollution fixture is a real name
    [InlineData("my-construct-work")]   // merely CONTAINING the prefix is fine
    public void Accepts_dns_label_style_names(string name) => Assert.True(VmNameValidator.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-leading-dash")]
    [InlineData("Agent-VM")]           // uppercase: names become ssh aliases and file names
    [InlineData("with space")]
    [InlineData("with_underscore")]
    [InlineData("dots.are.out")]
    [InlineData("vm\nname")]
    public void Rejects_everything_else(string? name) => Assert.False(VmNameValidator.IsValid(name));

    // THE TRAILING-HYPHEN REGRESSION. A name ending in a hyphen derives the endpoint
    // "<name>.mshome.net" — "work-.mshome.net" is not a host name at all, so the client
    // registry refused the identity of a name the validators had accepted. One rule now:
    // alphanumeric FIRST *and* LAST. Shared fixtures with extension/test/instances.test.js
    // and test/instances.test.ps1.
    [Theory]
    [InlineData("work-")]
    [InlineData("-")]
    [InlineData("a-")]
    [InlineData("work-vm-")]
    public void Rejects_trailing_hyphen(string name) => Assert.False(VmNameValidator.IsValid(name));

    // THE RESERVED PREFIX. "construct-<name>" was an abandoned alias convention whose
    // leftover prefix-strip in the config-branch derivation mapped the valid instance
    // "construct-work" onto "vm-work" — the config store of the *different*, equally valid
    // instance "work". The prefix is reserved everywhere instead.
    [Theory]
    [InlineData("construct-work")]
    [InlineData("construct-w")]
    [InlineData("construct-")]
    public void Rejects_the_reserved_prefix(string name) => Assert.False(VmNameValidator.IsValid(name));

    [Theory]
    [InlineData("construct-work")]
    [InlineData("Construct-Work")]      // a display-cased name asks the same question
    [InlineData("CONSTRUCT-work")]
    public void Reserved_prefix_is_case_insensitive(string name) => Assert.True(VmNameValidator.IsReserved(name));

    [Theory]
    [InlineData("work")]
    [InlineData("construct")]
    [InlineData("my-construct-work")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_else_is_reserved(string? name) => Assert.False(VmNameValidator.IsReserved(name));

    // .NET's '$' also matches just before a trailing newline, so "^…$" accepted "work\n"
    // while the JavaScript and PowerShell readers refused it — the service and the client
    // would then disagree about which names exist. \A…\z is the true end.
    [Theory]
    [InlineData("work\n")]
    [InlineData("work-vm\n")]
    public void Rejects_a_trailing_newline(string name) => Assert.False(VmNameValidator.IsValid(name));

    // The 63/64 BOUNDARY — a DNS label's own limit, because the name IS a label of
    // "<name>.mshome.net". Mirrored assertion-for-assertion in
    // extension/test/instances.test.js and test/instances.test.ps1.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(62)]
    [InlineData(63)]
    public void Accepts_up_to_the_dns_label_limit(int length) =>
        Assert.True(VmNameValidator.IsValid(new string('a', length)));

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(200)]
    public void Rejects_past_the_dns_label_limit(int length) =>
        Assert.False(VmNameValidator.IsValid(new string('a', length)));

    [Fact]
    public void A_maximum_length_name_may_carry_interior_hyphens()
    {
        var name = new string('a', 31) + "-" + new string('b', 31);
        Assert.Equal(63, name.Length);
        Assert.True(VmNameValidator.IsValid(name));
    }

    // The longest accepted name still has to survive its OWN derivations. Its ssh key file
    // is "construct_" + 63 + "_ed25519" = 81 characters, which is why the client's key-file
    // rule carries its own 128-character bound while the ssh-ALIAS token rule stays at 64
    // (the alias is the bare 63-character name). Pinned here so the service can never accept
    // a name the client registry would then refuse to record.
    [Fact]
    public void The_longest_accepted_name_still_yields_a_usable_key_file_name()
    {
        var longest = new string('a', 63);
        Assert.True(VmNameValidator.IsValid(longest));
        Assert.Equal(81, $"construct_{longest}_ed25519".Length);
        Assert.True($"construct_{longest}_ed25519".Length <= 128);   // KEY_FILE_NAME_RE
        Assert.True(longest.Length <= 64);                            // SAFE_TOKEN_RE (hostAlias)
        Assert.False(VmNameValidator.IsValid(new string('a', 64)));
    }

    // THE CROSS-LANGUAGE END-TO-END CONTRACT, as ONE fixture: the two names that used to
    // collide on a single config-sync branch. "work" is a VM the service will address;
    // "construct-work" is refused outright, so the pair can never exist at the same time.
    // The registry half of the same fixture lives in extension/test/instances.test.js
    // (RESERVED_PREFIX_REGISTRY) and test/instances.test.ps1 ($reservedJson), which drive
    // byte-identical registry bytes containing exactly these two names.
    [Fact]
    public void Work_is_accepted_and_construct_work_is_rejected()
    {
        Assert.True(VmNameValidator.IsValid("work"));
        Assert.False(VmNameValidator.IsValid("construct-work"));
        Assert.False(VmNameValidator.IsReserved("work"));
        Assert.True(VmNameValidator.IsReserved("construct-work"));
        // ...and the branch each one WOULD derive (vm-<name>) is different, which is the
        // whole point: nothing folds "construct-work" onto "work"'s store any more.
        Assert.NotEqual($"vm-{"work"}", $"vm-{"construct-work"}");
    }

    [Fact]
    public void The_rule_text_states_the_reserved_prefix()
    {
        Assert.Contains("construct-", VmNameValidator.Rule, StringComparison.Ordinal);
        Assert.Contains("1-63", VmNameValidator.Rule, StringComparison.Ordinal);
    }
}
