using Constructd.Api.Infrastructure;
using Constructd.Core.Configuration;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Constructd.Tests.Hosting;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "constructd-filelog-" + Guid.NewGuid().ToString("n"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Writes_one_line_per_entry_with_time_level_category_message_and_exception()
    {
        var now = new DateTimeOffset(2026, 9, 26, 0, 45, 47, 123, TimeSpan.Zero);
        using (var provider = new FileLoggerProvider(_dir, 14, () => now))
        {
            var logger = provider.CreateLogger("Constructd.Windows.HyperV.HyperVDriver");
            logger.LogWarning("operation {Operation} failed: {Reason}", "create-vm", "powershell.exe exited with 1");
            logger.LogError(new InvalidOperationException("boom"), "job {Id} failed", "j1");
        }
        var text = File.ReadAllText(Path.Combine(_dir, "constructd-20260926.log"));
        Assert.Contains("2026-09-26T00:45:47.123Z WARN  Constructd.Windows.HyperV.HyperVDriver  operation create-vm failed: powershell.exe exited with 1", text);
        Assert.Contains("ERROR Constructd.Windows.HyperV.HyperVDriver  job j1 failed", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void Rolls_at_midnight_and_prunes_files_older_than_the_retention()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "constructd-20260901.log"), "old\n");
        File.WriteAllText(Path.Combine(_dir, "constructd-20260920.log"), "recent\n");
        File.WriteAllText(Path.Combine(_dir, "notes.log"), "not ours\n");
        var now = new DateTimeOffset(2026, 9, 26, 23, 59, 59, TimeSpan.Zero);
        using (var provider = new FileLoggerProvider(_dir, 14, () => now))
        {
            var logger = provider.CreateLogger("t");
            logger.LogInformation("before midnight");
            now = now.AddSeconds(2);
            logger.LogInformation("after midnight");
        }
        Assert.Contains("before midnight", File.ReadAllText(Path.Combine(_dir, "constructd-20260926.log")));
        Assert.Contains("after midnight", File.ReadAllText(Path.Combine(_dir, "constructd-20260927.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "constructd-20260901.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "constructd-20260920.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.log")));
    }

    [Fact]
    public void An_unwritable_location_never_throws()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "logs");
        File.WriteAllText(blocker, "a file where the directory should be");
        using var provider = new FileLoggerProvider(blocker, 14, () => DateTimeOffset.UtcNow);
        var exception = Record.Exception(() => provider.CreateLogger("t").LogInformation("dropped"));
        Assert.Null(exception);
    }

    [Fact]
    public void Resolves_next_to_the_database_on_a_real_host_and_off_in_fake_mode()
    {
        var real = new ConstructdOptions { DatabasePath = Path.Combine(_dir, "data", "constructd.db") };
        Assert.Equal(Path.Combine(_dir, "data", "logs"), FileLoggerProvider.Resolve(real));
        Assert.Null(FileLoggerProvider.Resolve(new ConstructdOptions { Fake = true }));
        Assert.Equal(Path.GetFullPath(_dir), FileLoggerProvider.Resolve(new ConstructdOptions { Fake = true, FileLog = new() { Directory = _dir } }));
        Assert.Null(FileLoggerProvider.Resolve(new ConstructdOptions { FileLog = new() { Enabled = false } }));
    }

    [Fact]
    public async Task The_host_writes_its_log_file_when_a_directory_is_configured()
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:FileLog:Directory"] = _dir });
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Constructd.Tests").LogWarning("hello from the test host");
        var file = Directory.EnumerateFiles(_dir, "constructd-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("hello from the test host", text);
        Assert.Contains("Service log:", text);
    }
}
