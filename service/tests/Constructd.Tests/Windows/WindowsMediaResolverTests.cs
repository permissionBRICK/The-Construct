using System.ComponentModel;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Constructd.Tests.Windows;

public sealed class WindowsMediaResolverTests
{
    [Theory]
    [InlineData("en", "^English$")]
    [InlineData("en-US", "^English$")]
    [InlineData("en-GB", "^English International$")]
    [InlineData("de", "^German$")]
    [InlineData("de-AT", "^German$")]
    [InlineData("pt-BR", "^Brazilian Portuguese$")]
    [InlineData("pt", "^Portuguese$")]
    [InlineData("zh-TW", "^Chinese (Traditional)$")]
    [InlineData("xx", null)]
    public void Language_codes_map_to_anchored_Microsoft_language_names(string code, string? expected) =>
        Assert.Equal(expected, WindowsMediaResolver.FidoLanguage(code));

    [Fact]
    public void Microsofts_sentinel_rejection_is_reported_as_its_own_failure()
    {
        var reason = WindowsMediaResolver.FidoFailure(new ProcessResult(3,
            "Querying languages\r\nError: Sentinel marked this request as rejected.\r\n", "", false));
        Assert.Equal("Error: Sentinel marked this request as rejected.", reason);
        Assert.True(WindowsMediaResolver.IsMicrosoftRejection(reason));
        Assert.True(WindowsMediaResolver.IsMicrosoftRejection("Error: Your IP address has been banned ... message code 715-123130 and session ID x."));
    }

    [Fact]
    public async Task A_host_without_pwsh_is_named_as_the_reason()
    {
        if (OperatingSystem.IsWindows()) return;
        // What Process.Start throws on Linux when the executable is not on PATH.
        var runner = new RecordingProcessRunner { Failure = new Win32Exception(2, "No such file or directory") };
        var resolver = new WindowsMediaResolver(runner, new ConstructdOptions(), NullLogger<WindowsMediaResolver>.Instance);
        var error = await Assert.ThrowsAsync<MediaException>(() => resolver.RunFidoAsync("/tmp/fido.ps1", "^English$", default));
        Assert.Equal("windows-resolver-pwsh-missing", error.Code);
        Assert.Equal("pwsh", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public void Other_failures_keep_their_last_output_line()
    {
        var reason = WindowsMediaResolver.FidoFailure(new ProcessResult(1, "", "running scripts is disabled on this system\n", false));
        Assert.Equal("running scripts is disabled on this system", reason);
        Assert.False(WindowsMediaResolver.IsMicrosoftRejection(reason));
        Assert.Equal("Fido did not finish within the time limit.", WindowsMediaResolver.FidoFailure(new ProcessResult(-1, "", "", true)));
    }
}
