using Construct.Companion.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Construct.Companion.Host.ConfigSync;
public static class ConfigSyncComposition
{
    // Existing host registrations win. IClock, IPrompts and IClipboard are supplied
    // by the host/app (or Fakes); no Windows implementation is registered on Linux.
    public static IServiceCollection AddConfigSync(this IServiceCollection services)
    {
        services.TryAddSingleton<ConfigSyncFileSystem>();
        services.TryAddSingleton<IFileSystem>(p=>p.GetRequiredService<ConfigSyncFileSystem>());
        services.TryAddSingleton<IConfigSyncStorage>(p=>p.GetRequiredService<ConfigSyncFileSystem>());
        services.TryAddSingleton<IProcessRunner,ConfigSyncProcessRunner>();
        services.TryAddSingleton<ConfigSyncFactory>(); return services;
    }
}
public sealed class ConfigSyncFactory(IFileSystem files,IConfigSyncStorage storage,IProcessRunner processes,IClock clock,IPrompts prompts,IClipboard clipboard)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> repositoryQueues=new(StringComparer.OrdinalIgnoreCase);
    public ConfigSyncArea Create(string configDir,string stagingRoot,ISshTransport ssh,string vmBranch)
    {
        var repo=new ConfigRepository(new GitRunner(processes),files,storage,configDir); var syncLock=new SyncLock(files,storage,clock,repo.Directory);
        var remotes=new ConfigRemotes(repo,stagingRoot); var engine=new ConfigSyncEngine(repo,syncLock,ssh,vmBranch);
        var queue=repositoryQueues.GetOrAdd(repo.Directory,_=>new SemaphoreSlim(1,1));
        var runtime=new ConfigSyncRuntime(engine,repo,remotes,clock,queue); return new(repo,remotes,runtime,new(repo,remotes,syncLock,prompts,clipboard,clock,queue));
    }
}
public sealed record ConfigSyncArea(ConfigRepository Repository,ConfigRemotes Remotes,ConfigSyncRuntime Runtime,ConfigSyncActions Actions) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}
