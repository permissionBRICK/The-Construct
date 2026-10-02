using System.Text;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Vault;

// Runs the real guest side on Linux: bin/construct-secret.sh, the vault watch/respond scripts and
// the scan/clean scripts, with "SSH" replaced by a local bash that gets the same script and stdin.
public sealed class VaultGuestScriptTests
{
    private const string Value = "tok_E2E_value-123";
    private sealed class LocalTransport : ISshTransport
    {
        private readonly RuntimeProcessRunner runner = new();
        public Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Secret? standardInput = null)
            => runner.RunAsync(new("bash", ["-c", script], StandardInput: standardInput ?? new Secret(""), Timeout: timeout ?? TimeSpan.FromSeconds(60)), cancellationToken);
        public IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default) => runner.Start(new("bash", ["-c", script]), cancellationToken);
        public Task<bool> ProbeListeningPortAsync(int port, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private static bool Has(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));
    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "bin", "construct-secret.sh"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
    private static async Task<ProcessResult> Cli(string spool, string? stdin, params string[] args)
    {
        var invocation = new ProcessInvocation("bash", [Path.Combine(Repository(), "bin", "construct-secret.sh"), .. args],
            StandardInput: new Secret(stdin ?? ""), Timeout: TimeSpan.FromSeconds(60))
        {
            // The spool mode: a VM on a host service would otherwise send these requests to it.
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["CONSTRUCT_VAULT_SPOOL"] = spool, ["CONSTRUCT_VAULT_PICKUP_SEC"] = "15",
                // The "waiting for approval" notes stay in the test's tree, never this VM's /run.
                ["CONSTRUCT_VAULT_PENDING_DIR"] = Path.Combine(Path.GetDirectoryName(spool)!, "pending"),
                ["CONSTRUCT_SERVICE_URL"] = null, ["CONFIG_FILE"] = Path.Combine(spool, "no-config.env")
            }
        };
        return await new RuntimeProcessRunner().RunAsync(invocation);
    }
    private static VaultService Vault(FakePrompts prompts, IClock clock)
    {
        var vault = new VaultService(new VaultStore(new FakeFileSystem(), new FakeDataProtection(), "/vault.dat"), prompts, new FakeToastRaiser(), clock);
        vault.Save(new("api-token", "Token for the test API", "svc", new Secret(Value)));
        _ = new PromptApprover(vault, prompts); // answers each approval from prompts.Approvals
        return vault;
    }

    [Fact]
    public async Task RealCliTalksToTheBrokerThroughTheGuestSpool()
    {
        if (!OperatingSystem.IsLinux() || !Has("jq") || !Has("base64")) return;
        var temp = Directory.CreateTempSubdirectory("cc-vault-cli-"); var spool = Path.Combine(temp.FullName, "spool");
        var prompts = new FakePrompts(); var clock = new SystemClock(); var vault = Vault(prompts, clock);
        var broker = new VaultBroker("dev", new LocalTransport(), new SshProcessSupervisor(clock), vault, spool);
        try
        {
            broker.Start();
            var list = await Cli(spool, null, "list");
            Assert.Equal(0, list.Code); Assert.Contains("api-token", list.Stdout); Assert.Contains("Token for the test API", list.Stdout); Assert.DoesNotContain(Value, list.Stdout);

            prompts.Approvals.Enqueue(true);
            var get = await Cli(spool, null, "get", "api-token", "--reason", "e2e");
            Assert.Equal(0, get.Code); Assert.Equal(Value, get.Stdout);
            prompts.Approvals.Enqueue(true); // the single use is spent: the username needs a new approval
            var username = await Cli(spool, null, "get", "api-token", "--username");
            Assert.Equal(0, username.Code); Assert.Equal("svc", username.Stdout.TrimEnd('\n'));

            var add = await Cli(spool, "generated-secret-xyz\n", "add", "made-here", "--description", "Created by the test");
            Assert.True(add.Code == 0, add.Stderr);
            Assert.Equal("generated-secret-xyz", vault.Reveal("made-here")!.Reveal());
            Assert.Equal("agent:dev", vault.Secrets().Single(s => s.Name == "made-here").Origin);

            Assert.Equal(9, (await Cli(spool, null, "get", "missing-one")).Code);
            var released = await Cli(spool, null, "release", "--all");
            Assert.Equal(0, released.Code); Assert.Contains("made-here", released.Stdout);
            Assert.Empty(vault.Leases());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(spool, "responses")));
        }
        finally { await broker.DisposeAsync(); await vault.DisposeAsync(); temp.Delete(true); }
    }

    [Fact]
    public async Task RealScanAndCleanScrubATreeAndHonourEachDecision()
    {
        if (!OperatingSystem.IsLinux() || !Has("python3")) return;
        var temp = Directory.CreateTempSubdirectory("cc-vault-scrub-"); var root = Path.Combine(temp.FullName, "root"); var spool = Path.Combine(temp.FullName, "spool");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "repo")); Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
            var env = Path.Combine(root, "repo", ".env"); var envText = $"API_TOKEN={Value}\nOTHER=1\n";
            await File.WriteAllTextAsync(env, envText);
            var blob = Path.Combine(root, "blob.bin"); await File.WriteAllBytesAsync(blob, [0, 1, 2, .. Encoding.UTF8.GetBytes(Value), 0]);
            var keep = Path.Combine(root, "keep me.txt"); await File.WriteAllTextAsync(keep, "basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:" + Value)));
            var dependency = Path.Combine(root, "node_modules", "pkg", "index.js"); await File.WriteAllTextAsync(dependency, $"const t = '{Value}';");
            var database = Path.Combine(root, "app.db");
            var python = await new RuntimeProcessRunner().RunAsync(new("python3", ["-c",
                "import sqlite3,sys\nc=sqlite3.connect(sys.argv[1])\nc.execute('create table t(id integer primary key, body text)')\nc.execute('insert into t(body) values (?)', ('before " + Value + " after',))\nc.execute('insert into t(body) values (?)', ('clean row',))\nc.commit()",
                database]));
            Assert.Equal(0, python.Code);

            FileDecisionPrompt? asked = null;
            var report = await VaultCleaner.RunAsync("dev", new LocalTransport(), [new VaultCleanup("dev", "api-token", new Secret(Value), "svc", DateTimeOffset.UtcNow)],
                (prompt, _) =>
                {
                    asked = prompt;
                    var choice = prompt.Files.ToDictionary(f => f.Id, f => f.Path switch
                    {
                        var p when p == env || p == database => FileDecisionPrompt.Redact, var p when p == blob => FileDecisionPrompt.Delete, _ => FileDecisionPrompt.Keep
                    });
                    return Task.FromResult<IReadOnlyDictionary<string, string>?>(choice);
                }, CancellationToken.None, [root], spool);

            Assert.NotNull(asked);
            Assert.Equal(new[] { database, blob, keep, env }.Order(StringComparer.Ordinal), asked.Files.Select(f => f.Path));
            Assert.Contains("SQLite database", asked.Files.Single(f => f.Path == database).Detail);
            Assert.Equal((0, 2, 1, 1), (report.AgentFiles, report.Redacted, report.Deleted, report.Kept));
            Assert.Empty(report.Failures);
            Assert.Equal(envText.Replace(Value, new string('*', Value.Length), StringComparison.Ordinal), await File.ReadAllTextAsync(env));
            Assert.False(File.Exists(blob));
            Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:" + Value)), await File.ReadAllTextAsync(keep));
            Assert.Contains(Value, await File.ReadAllTextAsync(dependency));
            var rows = await new RuntimeProcessRunner().RunAsync(new("python3", ["-c", "import sqlite3,sys\nprint('|'.join(r[0] for r in sqlite3.connect(sys.argv[1]).execute('select body from t order by id')))", database]));
            Assert.Equal($"before {new string('*', Value.Length)} after|clean row", rows.Stdout.Trim());
            Assert.DoesNotContain(Value, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(database)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(spool));
        }
        finally { temp.Delete(true); }
    }
}
