using System.Security.Cryptography;
using Construct.Companion.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Construct.Companion.Host.Runtime;

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
    public static IServiceCollection AddRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessRunner, RuntimeProcessRunner>();
        services.TryAddSingleton<IClock, SystemClock>(); services.TryAddSingleton<IRuntimeProcesses, SshProcessSupervisor>();
        services.TryAddSingleton<IPortReservations, PortReservations>(); services.TryAddSingleton<IPortProbe, SocketPortProbe>();
        services.TryAddSingleton<IAudioServerFactory, LoopbackAudioServerFactory>(); services.TryAddSingleton<RuntimeMessageBus>();
        services.TryAddSingleton<Construct.Companion.Core.Audio.SharedAudioCapture>();
        services.TryAddSingleton(_ => RuntimeClaimId.Create());
        return services;
    }
}
