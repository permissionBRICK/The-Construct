using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests.Parity;

public sealed class ParityTests
{
    public static IEnumerable<object[]> Rows(string area)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "Fixtures", area + ".json")));
        return document.RootElement.EnumerateArray().Select(row => new object[] { row.Clone() }).ToArray();
    }
    public static IEnumerable<object[]> Quotes => Rows("shell-quoting");
    public static IEnumerable<object[]> Scripts => Rows("guest-scripts");
    public static IEnumerable<object[]> Hosts => Rows("host-label");
    public static IEnumerable<object[]> Ssh => Rows("ssh-args");

    [Theory, MemberData(nameof(Quotes))]
    public void ShellQuoting(JsonElement row) => Assert.Equal(row.GetProperty("output").GetString(), ShellQuote.Single(row.GetProperty("input").GetString()));

    [Theory, MemberData(nameof(Scripts))]
    public void GuestScript(JsonElement row)
    {
        var values = row.GetProperty("values").Deserialize<Dictionary<string, string>>()!;
        Assert.Equal(row.GetProperty("output").GetString(), GuestScripts.Render(row.GetProperty("name").GetString()!, values));
    }

    [Theory, MemberData(nameof(Hosts))]
    public void HostLabel(JsonElement row)
    {
        var input = row.GetProperty("input").GetString();
        Assert.Equal(row.GetProperty("normalized").GetString(), ForwardHost.Normalize(input));
        Assert.Equal(row.GetProperty("urlHost").GetString(), ForwardHost.ForUrl(input));
        Assert.Equal(row.GetProperty("bindHost").GetString(), ForwardHost.BindHostFor(input));
    }

    [Theory, MemberData(nameof(Ssh))]
    public async Task SshArgvIsRecordedWithoutShell(JsonElement row)
    {
        var kind = row.GetProperty("kind").GetString();
        if (kind is "address" or "bind" or "port")
        {
            if (kind == "port") Assert.Equal(row.GetProperty("output").GetInt32(), SshArgs.NormalizeSshPort(row.GetProperty("input").GetInt32()));
            else Assert.Equal(row.GetProperty("output").GetString(), kind == "address"
                ? SshArgs.NormalizeConnectAddress(row.GetProperty("input").GetString()) : SshArgs.NormalizeBindHost(row.GetProperty("input").GetString()));
            return;
        }
        var config = row.GetProperty("cfg").Deserialize<SshConfiguration>(IpcJson.Options)!;
        var keyPath = row.GetProperty("keyPath").GetString();
        string[] args;
        if (row.GetProperty("kind").GetString() == "run")
            args = SshArgs.Build(config, row.GetProperty("command").GetString()!, keyPath);
        else
        {
            var options = row.GetProperty("opts");
            args = SshArgs.BuildLocalForward(config, row.GetProperty("localPort").GetInt32(), row.GetProperty("vmPort").GetInt32(), keyPath,
                options.TryGetProperty("bindHost", out var bind) ? bind.GetString() : null,
                options.TryGetProperty("connectAddress", out var address) ? address.GetString() : null,
                options.TryGetProperty("connectPort", out var port) ? port.GetInt32() : null);
        }
        var runner = new FakeProcessRunner();
        await runner.RunAsync(new ProcessInvocation("ssh", args));
        Assert.Equal("ssh", runner.Invocations.Single().FileName);
        Assert.Equal(row.GetProperty("output").EnumerateArray().Select(arg => arg.GetString()), runner.Invocations.Single().Arguments);
    }

    [Fact]
    public void EveryEmbeddedScriptHasAGoldenFixture()
    {
        var fixtureNames = Scripts.Select(row => ((JsonElement)row[0]).GetProperty("name").GetString()!)
            .Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(fixtureNames, GuestScripts.Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MissingTemplateValuesFailAndReplacementIsSinglePass()
    {
        Assert.Throws<ArgumentException>(() => GuestScripts.Render("not-present"));
        Assert.Throws<ArgumentException>(() => GuestScripts.Render("forwards-capability"));
        Assert.Contains("{{dir}}", GuestScripts.Render("forwards-capability", new Dictionary<string, string> { ["dir"] = "'{{dir}}'" }));
    }

    [Fact]
    public void ScriptWrappingMatchesNodeArgvFixture()
    {
        var command = SshArgs.WrapScriptCommand("echo 'ü'\n");
        Assert.Contains(Ssh.Select(row => (JsonElement)row[0]).Where(row => row.TryGetProperty("command", out _)).Select(row => row.GetProperty("command").GetString()), value => value == command);
    }
}
