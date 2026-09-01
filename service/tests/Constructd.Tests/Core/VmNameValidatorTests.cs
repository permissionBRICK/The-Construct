using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class VmNameValidatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("agent-vm")]
    [InlineData("work-vm-2")]
    [InlineData("0abc")]
    [InlineData("0123456789012345678901234567890123456789")] // 40 chars, the maximum
    public void Accepts_dns_label_style_names(string name) => Assert.True(VmNameValidator.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-leading-dash")]
    [InlineData("Agent-VM")]           // uppercase: names become ssh aliases and file names
    [InlineData("with space")]
    [InlineData("with_underscore")]
    [InlineData("dots.are.out")]
    [InlineData("01234567890123456789012345678901234567890")] // 41 chars
    [InlineData("vm\nname")]
    public void Rejects_everything_else(string? name) => Assert.False(VmNameValidator.IsValid(name));
}
