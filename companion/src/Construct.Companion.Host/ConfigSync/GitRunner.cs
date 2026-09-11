using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;
public sealed class GitRunner(IProcessRunner processes)
{
    public static readonly string[] Identity = ["-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath="];
    public async Task<ProcessResult> RunAsync(string cwd, IEnumerable<string> args, bool identity = false, string? index = null, bool network = false, CancellationToken cancellationToken = default)
    {
        var argv = (identity ? Identity.Concat(args) : args).ToArray();
        if (argv.Any(ConfigSyncRules.UrlHasCredentials)) return new(-1, Stderr: "Remove the credentials from the URL -- let your git credential helper supply them.");
        try
        {
            var result = await processes.RunAsync(new("git", argv, cwd, Timeout: TimeSpan.FromSeconds(network ? 60 : 30)) { EnvironmentOverrides = index == null ? null : new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = index } }, cancellationToken);
            // stdout includes git show/merge-file data: consumers must retain those bytes.
            // It is deliberately never included in diagnostics except via RedactGitOutput.
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(-1, Stderr: "git process failed"); } // a process exception can echo argv; never let it reach a caller
    }
    public async Task<string> RequireAsync(string cwd, IEnumerable<string> args, bool identity = false, string? index = null, bool network = false, CancellationToken cancellationToken = default)
    {
        var r = await RunAsync(cwd, args, identity, index, network, cancellationToken);
        if (r.Code != 0) throw new ConfigSyncException(ConfigSyncRules.RedactGitOutput("git operation failed: " + r.Stderr));
        return r.Stdout.TrimEnd('\r', '\n');
    }
    public async Task<GitPresence> DetectAsync(string cwd, CancellationToken ct = default)
    {
        var r = await RunAsync(cwd, ["--version"], cancellationToken: ct);
        var match = System.Text.RegularExpressions.Regex.Match(r.Stdout, @"\d+\.\d+[\d.]*"); return new(r.Code == 0, r.Code == 0 && match.Success ? match.Value : null);
    }
}
public sealed record GitPresence(bool Present, string? Version);
