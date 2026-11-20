using Construct.Companion.Core.Notifications;

namespace Construct.Agent.Tests;

/// <summary>The notification watch's one long-lived SSH connection must not keep its VM busy.</summary>
public sealed class NotificationWatchTests
{
    [Fact]
    public void The_watch_records_its_ssh_connection_where_the_guest_idle_report_looks()
    {
        var script = NotificationProtocol.WatchScript();
        Assert.Equal("/run/construct/watch-sessions", NotificationProtocol.WatchSessionsDirectory);
        Assert.Contains("ws='/run/construct/watch-sessions'/notify-$$", script, StringComparison.Ordinal);
        Assert.Contains("printf '%s\\n' \"$SSH_CONNECTION\" >\"$ws\"", script, StringComparison.Ordinal);
        Assert.Contains("rm -f \"$ws\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watch_directory_is_shell_quoted()
    {
        var script = NotificationProtocol.WatchScript(watchDir: "/tmp/it's here");
        Assert.Contains("mkdir -p '/tmp/it'\\''s here'", script, StringComparison.Ordinal);
    }
}
