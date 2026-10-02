using System.Globalization;
using System.Text;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

public sealed record VaultCleanReport(IReadOnlyList<string> Names, IReadOnlyList<string> Unscannable, int AgentFiles, int Redacted, int Deleted, int Kept,
    IReadOnlyList<string> Failures, IReadOnlyList<string> Partial);

// One scrub of one VM: search every file for the finished secrets (values travel on stdin only),
// redact agent logs in place without asking, and let the user decide for every other file.
public static class VaultCleaner
{
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(20), CleanTimeout = TimeSpan.FromMinutes(10);
    private sealed record Target(string Path, string PathB64, HashSet<int> Indexes, string Type, long Size);

    // roots/spool default to the VM layout; tests point them at a temporary tree.
    public static async Task<VaultCleanReport> RunAsync(string instance, ISshTransport ssh, IReadOnlyList<VaultCleanup> batch,
        Func<FileDecisionPrompt, CancellationToken, Task<IReadOnlyDictionary<string, string>?>> decide, CancellationToken token,
        IReadOnlyList<string>? roots = null, string spool = VaultProtocol.SpoolDirectory)
    {
        var names = batch.Select(c => c.Name).Distinct(StringComparer.Ordinal).ToArray();
        var patterns = batch.Select(c => VaultProtocol.Patterns(c.Value.Reveal(), c.Username)).ToArray();
        var unscannable = batch.Where((_, i) => patterns[i].Count == 0).Select(c => c.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (patterns.All(p => p.Count == 0)) return new(names, unscannable, 0, 0, 0, 0, [], []);
        var input = new StringBuilder();
        for (var i = 0; i < patterns.Length; i++) foreach (var p in patterns[i]) input.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(B64(p)).Append('\n');
        var scan = await ssh.RunRemoteScriptAsync(VaultProtocol.ScanScript(roots, dir: spool), ScanTimeout, token, new Secret(input.ToString())).ConfigureAwait(false);
        var (hits, complete) = VaultProtocol.ParseScan(scan.Stdout);
        if (scan.Code != 0 || !complete) throw new IOException("The VM scan did not complete.");

        // SQLite side files fold into their database: the scrub rewrites rows, never the -wal bytes.
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var hit in hits.Where(h => h.Indexes.All(i => i < patterns.Length)))
        {
            var aux = hit.Type == "sqlite-aux";
            var path = aux ? VaultProtocol.DatabaseFor(hit.Path) : hit.Path;
            if (!targets.TryGetValue(path, out var target))
                targets[path] = target = new(path, aux ? VaultProtocol.EncodePath(path) : hit.PathB64, [], aux ? "sqlite" : hit.Type, aux ? 0 : hit.Size);
            target.Indexes.UnionWith(hit.Indexes);
        }
        var agent = targets.Values.Where(t => VaultProtocol.IsAgentLog(t.Path)).OrderBy(t => t.Path, StringComparer.Ordinal).ToArray();
        var other = targets.Values.Where(t => !VaultProtocol.IsAgentLog(t.Path)).OrderBy(t => t.Path, StringComparer.Ordinal).ToArray();
        var failures = new List<string>(); var partial = new List<string>();
        var agentCleaned = 0;
        if (agent.Length > 0)
        {
            var results = await ApplyAsync(ssh, agent.Select(t => (FileDecisionPrompt.Redact, t)), patterns, spool, token).ConfigureAwait(false);
            agentCleaned = Tally(agent, results, failures, partial);
        }
        int redacted = 0, deleted = 0, kept = 0;
        if (other.Length > 0)
        {
            var files = other.Select(t => new FileDecisionItem(t.PathB64, t.Path, Describe(t, batch))).ToArray();
            var prompt = new FileDecisionPrompt($"Key vault — secrets left on “{instance}”",
                $"These files on the VM “{instance}” contain {string.Join(", ", names)}. Agent logs were cleaned already. Choose what to do with each file; keep leaves it untouched.", files);
            var choices = await decide(prompt, token).ConfigureAwait(false) ?? new Dictionary<string, string>();
            var chosen = other.Select(t => (Action: choices.TryGetValue(t.PathB64, out var a) && FileDecisionPrompt.Actions.Contains(a) ? a : FileDecisionPrompt.Keep, Target: t)).ToArray();
            kept = chosen.Count(c => c.Action == FileDecisionPrompt.Keep);
            var act = chosen.Where(c => c.Action != FileDecisionPrompt.Keep).ToArray();
            if (act.Length > 0)
            {
                var results = await ApplyAsync(ssh, act.Select(c => (c.Action, c.Target)), patterns, spool, token).ConfigureAwait(false);
                foreach (var group in act.GroupBy(c => c.Action))
                {
                    var done = Tally(group.Select(c => c.Target).ToArray(), results, failures, partial);
                    if (group.Key == FileDecisionPrompt.Delete) deleted += done; else redacted += done;
                }
            }
        }
        return new(names, unscannable, agentCleaned, redacted, deleted, kept, failures, partial);
    }

    private static async Task<IReadOnlyList<VaultCleanResult>> ApplyAsync(ISshTransport ssh, IEnumerable<(string Action, Target Target)> work, IReadOnlyList<string>[] patterns, string spool, CancellationToken token)
    {
        var input = new StringBuilder();
        foreach (var (action, target) in work)
        {
            if (action == FileDecisionPrompt.Delete)
            {
                input.Append("delete\t").Append(target.PathB64).Append('\n');
                // A deleted database takes its side files with it; missing ones report "missing".
                if (target.Type == "sqlite") foreach (var side in new[] { "-wal", "-shm", "-journal" }) input.Append("delete\t").Append(VaultProtocol.EncodePath(target.Path + side)).Append('\n');
                continue;
            }
            input.Append("redact\t").Append(target.PathB64);
            foreach (var p in target.Indexes.Order().SelectMany(i => patterns[i]).Distinct(StringComparer.Ordinal)) input.Append('\t').Append(B64(p));
            input.Append('\n');
        }
        var result = await ssh.RunRemoteScriptAsync(VaultProtocol.CleanScript(spool), CleanTimeout, token, new Secret(input.ToString())).ConfigureAwait(false);
        var (results, complete) = VaultProtocol.ParseClean(result.Stdout);
        if (result.Code != 0 || !complete) throw new IOException("The VM clean-up did not complete.");
        return results;
    }

    // A target counts as done when its own result is ok/partial (or already gone); side files never fail it.
    private static int Tally(IReadOnlyList<Target> targets, IReadOnlyList<VaultCleanResult> results, List<string> failures, List<string> partial)
    {
        var done = 0;
        foreach (var target in targets)
        {
            var own = results.FirstOrDefault(r => r.PathB64 == target.PathB64);
            switch (own?.Status)
            {
                case "ok" or "missing": done++; break;
                case "partial": done++; partial.Add(target.Path); break;
                default: failures.Add(target.Path); break;
            }
        }
        return done;
    }

    private static string Describe(Target target, IReadOnlyList<VaultCleanup> batch)
    {
        var secrets = string.Join(", ", target.Indexes.Order().Select(i => batch[i].Name).Distinct(StringComparer.Ordinal));
        var kind = target.Type switch { "sqlite" => "SQLite database", "binary" => "binary file", _ => "text file" };
        return target.Size > 0 ? $"{secrets} · {kind} · {Size(target.Size)}" : $"{secrets} · {kind}";
    }
    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B", < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB", _ => $"{bytes / 1048576.0:0.#} MB"
    };
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
