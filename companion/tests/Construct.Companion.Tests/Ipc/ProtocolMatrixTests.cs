using System.Text.RegularExpressions;
using Construct.Companion.Host.Dispatch;
namespace Construct.Companion.Tests.Ipc;
public sealed class ProtocolMatrixTests
{
    [Fact]
    public void DocumentedSetEqualsDispatcherAndCoversExtensionSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "extension", "extension.js"))) root = root.Parent;
        Assert.NotNull(root);
        var readme = File.ReadAllText(Path.Combine(root.FullName, "companion", "README.md"));
        string[] Documented(string kind) => Regex.Matches(readme, @"\| " + kind + @" \| `([^`]+)` \|").Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(MessageDispatcher.KnownMessages.Order(), Documented("message"));
        Assert.Equal(MessageDispatcher.KnownCommands.Order(), Documented("command"));
        var source = File.ReadAllText(Path.Combine(root.FullName, "extension", "extension.js"));
        var start = source.IndexOf("function handleMessage(", StringComparison.Ordinal);
        source = source[start..source.IndexOf("\nfunction ", start + 10, StringComparison.Ordinal)];
        Assert.Equal(MessageDispatcher.KnownMessages.Order(), Regex.Matches(source, "case \"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().Order());
        foreach (Match match in Regex.Matches(source, "id === \"([^\"]+)\"")) Assert.Contains(match.Groups[1].Value, MessageDispatcher.KnownCommands);
        var dispatcher = File.ReadAllText(Path.Combine(root.FullName, "companion", "src", "Construct.Companion.Host", "Dispatch", "MessageDispatcher.cs"));
        foreach (var command in MessageDispatcher.KnownCommands) Assert.Contains("case \"" + command + "\":", dispatcher);
    }
}
