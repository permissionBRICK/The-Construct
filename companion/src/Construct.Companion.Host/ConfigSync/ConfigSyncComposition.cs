using Construct.Companion.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Construct.Companion.Host.ConfigSync;

public static class ConfigSyncComposition
{
    public static IServiceCollection AddConfigSync(this IServiceCollection services)
    {
        services.TryAddSingleton<ConfigSyncFactory>();
        return services;
    }
}
// One area per instance; areas of instances that share a config directory share its queue,
// so a long dialog-driven action never interleaves with another instance's tick.
public sealed class ConfigSyncFactory(IStateFileSystem files, IProcessLiveness processes, IProcessRunner runner, IClock clock, IPrompts prompts, IClipboard clipboard)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> repositoryQueues = new(StringComparer.OrdinalIgnoreCase);
    public ConfigSyncArea Create(string configDir, string stagingRoot, ISshTransport ssh, string vmBranch)
    {
        var repo = new ConfigRepository(new GitRunner(runner), files, configDir);
        var syncLock = new SyncLock(files, processes, clock, repo.Directory);
        var remotes = new ConfigRemotes(repo, stagingRoot);
        var engine = new ConfigSyncEngine(repo, syncLock, ssh, vmBranch);
        var queue = repositoryQueues.GetOrAdd(repo.Directory, _ => new SemaphoreSlim(1, 1));
        return new(repo, new ConfigSyncRuntime(engine, repo, remotes, clock, queue), new ConfigSyncActions(repo, remotes, syncLock, prompts, clipboard, clock, queue));
    }
}
public sealed record ConfigSyncArea(ConfigRepository Repository, ConfigSyncRuntime Runtime, ConfigSyncActions Actions) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}
