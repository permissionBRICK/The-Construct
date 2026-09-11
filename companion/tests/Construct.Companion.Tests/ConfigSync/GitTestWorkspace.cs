using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Fakes;
using Construct.Companion.Host.ConfigSync;

namespace Construct.Companion.Tests.ConfigSync;
internal sealed class GitTestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "companion-config-test-" + Guid.NewGuid().ToString("N"));
    public ConfigSyncFileSystem Files { get; } = new();
    public ConfigSyncProcessRunner Processes { get; } = new();
    public FakeClock Clock { get; } = new();
    public GitRunner Git { get; }
    public ConfigRepository Repo { get; }
    public string Store => Path.Combine(Root, "store");
    public string Cache => Path.Combine(Root, "cache");
    public SyncLock Lock { get; }
    public LocalStoreTransport Ssh { get; }
    public ConfigSyncEngine Engine { get; }
    public GitTestWorkspace()
    {
        Directory.CreateDirectory(Root); Git = new(Processes); Repo = new(Git, Files, Files, Path.Combine(Root, "config")); Repo.EnsureConfigTree();
        Lock = new(Files, Files, Clock, Repo.Directory); Ssh = new(Processes, Root); Engine = new(Repo, Lock, Ssh, storeRoot: Store);
    }
    public static string Profile(string name, string command = "echo base") => ProfileCodec.CanonicalizeProfileText(name, ConfigSyncRules.Serialize(new { name, provisionCommands = new[] {command} })).Content!;
    public void Host(string name, string? text = null) => Repo.WriteText(Repo.FilePath("projects",name), text ?? Profile(name));
    public void Vm(string name, string? text = null) { Directory.CreateDirectory(Store); File.WriteAllText(Path.Combine(Store,name+".json"),text ?? Profile(name)); }
    public Task<string> G(params string[] args) => Git.RequireAsync(Repo.Directory,args);
    public void Dispose() => Directory.Delete(Root,true);
}
internal sealed class LocalStoreTransport(ConfigSyncProcessRunner processes, string cwd) : ISshTransport
{
    public Func<string, Task>? BeforeRun { get; set; }
    public int Calls { get; private set; }
    public bool Offline { get; set; }
    public async Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Calls++; if (Offline) return new(-1);
        if (BeforeRun != null) await BeforeRun(script);
        return await processes.RunAsync(new("bash", [], cwd, new Secret(script), timeout), cancellationToken);
    }
    public IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
