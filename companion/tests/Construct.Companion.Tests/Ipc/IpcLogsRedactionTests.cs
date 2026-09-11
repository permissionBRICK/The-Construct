using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Tests.Ipc;

public sealed class IpcLogsRedactionTests
{
    [Theory]
    [InlineData("Authorization Bearer abcDEF123 failed", "Authorization Bearer [redacted] failed")]
    [InlineData("VmToken: secret-value", "VmToken [redacted]")]
    [InlineData("hash 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef seen", "hash [redacted] seen")]
    [InlineData("plain message", "plain message")]
    public void MessagesLoseCredentialShapedTokens(string input, string expected) => Assert.Equal(expected, IpcLogs.Redact(input));
    [Fact]
    public void LongMessagesAreCapped() => Assert.Equal(301, IpcLogs.Redact(new string('x', 500)).Length);
}
