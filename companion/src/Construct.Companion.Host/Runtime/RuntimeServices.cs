using System.Security.Cryptography;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Construct.Companion.Host.Runtime;

// The spool claim id of this process (cc-<8 hex>), shared by every local forwarder.
public sealed record RuntimeClaimId(string Value)
{
    public static RuntimeClaimId Create() => new("cc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
}
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.Delay(delay, cancellationToken);
}
public static class RuntimeServices
{
    // TryAdd: the Windows app (or the fakes) registers its own seams first and wins.
    public static IServiceCollection AddRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<HostFileSystem>();
        services.TryAddSingleton<IStateFileSystem>(p => p.GetRequiredService<HostFileSystem>());
        services.TryAddSingleton<IFileSystem>(p => p.GetRequiredService<HostFileSystem>());
        services.TryAddSingleton<IProcessLiveness, HostProcesses>();
        services.TryAddSingleton<IProcessRunner, RuntimeProcessRunner>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IRuntimeProcesses, SshProcessSupervisor>();
        services.TryAddSingleton<IPortReservations, PortReservations>();
        services.TryAddSingleton<IPortProbe, SocketPortProbe>();
        services.TryAddSingleton<IAudioServerFactory, LoopbackAudioServerFactory>();
        services.TryAddSingleton<RuntimeMessageBus>();
        services.TryAddSingleton<SharedAudioCapture>();
        services.TryAddSingleton(_ => RuntimeClaimId.Create());
        return services;
    }
}
