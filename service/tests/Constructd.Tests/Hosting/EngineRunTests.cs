using Constructd.Api.Hosting;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Hosting;

/// <summary>
/// The safety net in Program.cs: a stop that runs past the shutdown timeout. The lifetime below throws as the
/// Windows service lifetime does, which made every such stop an unhandled exception (".NET Runtime 1026").
/// </summary>
public sealed class EngineRunTests
{
    [Fact]
    public async Task A_plain_run_throws_when_the_stop_runs_past_the_timeout()
    {
        var (host, lifetime, _) = Build();
        var run = host.RunAsync();
        await lifetime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, lifetime.Stopped);
    }

    [Fact]
    public async Task The_engine_run_logs_it_and_lets_the_lifetime_finish_its_stop()
    {
        var (host, lifetime, logs) = Build();
        var run = EngineRun.RunAsync(host, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("engine"));
        await lifetime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, lifetime.Stopped);
        Assert.Contains("ran past its shutdown timeout", logs.AllText());
    }

    [Fact]
    public async Task A_failure_that_is_not_a_late_stop_still_ends_the_run()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<FailsToStart>();
        var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EngineRun.RunAsync(host, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("engine")));
    }

    [Fact]
    public async Task A_start_cancelled_by_a_failing_background_service_still_fails_the_run()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<FailsInTheBackground>();
        builder.Services.AddHostedService<StartsUntilCancelled>();
        var host = builder.Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EngineRun.RunAsync(host, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("engine")).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static (IHost Host, ServiceLikeLifetime Lifetime, CapturedLogs Logs) Build()
    {
        var lifetime = new ServiceLikeLifetime();
        var logs = new CapturedLogs();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));
        builder.Services.AddSingleton<IHostLifetime>(lifetime);
        builder.Services.AddHostedService<SlowToStop>();
        return (builder.Build(), lifetime, logs);
    }

    /// <summary>As WindowsServiceLifetime.StopAsync: an expired token throws before the stop is reported.</summary>
    private sealed class ServiceLikeLifetime : IHostLifetime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Stopped;

        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Stopped);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the stop until the shutdown timeout, as an open request did.</summary>
    private sealed class SlowToStop : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Fails after its start returned, so the host stops itself while the next service still starts.</summary>
    private sealed class FailsInTheBackground : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(50, stoppingToken);
            throw new InvalidOperationException("background failure");
        }
    }

    /// <summary>A start that ends only when the host cancels it.</summary>
    private sealed class StartsUntilCancelled : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("start failed");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
