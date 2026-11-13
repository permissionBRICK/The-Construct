using System.Diagnostics;
using System.Text;

namespace Construct.Agent.Git;

/// <summary>
/// The clone credentials of one job, as <c>GIT_CLONE_CREDENTIALS_B64</c> takes them, and the HTTP(S) hosts that had
/// no working one. <paramref name="CredentialsB64"/> is null when no host had one.
/// </summary>
public sealed record GitCloneCredentialSet(string? CredentialsB64, IReadOnlyList<string> HostsWithoutCredential)
{
    public static readonly GitCloneCredentialSet None = new(null, []);
}

/// <summary>
/// The old client's clone credentials (<c>Resolve-GitCloneCredential</c>, <c>Test-ConstructGitHostCredential</c>):
/// for every HTTP(S) host of the repo URLs, one credential from this device's own git credential helper
/// (<c>git credential fill</c>), checked once with <c>git ls-remote</c> against one of that host's repos, and sent
/// as <c>scheme://user:token@host</c> lines. Nothing ever prompts: terminal prompts, askpass and the Git Credential
/// Manager's dialogs are off, and every git call has a time limit. A host whose helper has nothing, or whose
/// credential the check refuses, is left out and named; git missing on the device gives no credentials. No credential
/// is written to a file, an argument list, a log or an error.
/// </summary>
/// <param name="searchPath">Where <c>git</c> is looked for (PATH syntax); null is this process's <c>PATH</c>.</param>
public sealed class GitCloneCredentials(string? searchPath = null, TimeSpan? timeout = null)
{
    private const string UserVariable = "CONSTRUCT_GIT_USERNAME";
    private const string TokenVariable = "CONSTRUCT_GIT_TOKEN";

    // A helper that answers only "get" with the credential from the child's environment: no secret in argv or a file.
    private const string EnvironmentHelper =
        "!f() { test \"$1\" = get || return 0; printf 'username=%s\\npassword=%s\\n' \"$" + UserVariable + "\" \"$" + TokenVariable + "\"; }; f";

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    /// <summary>Gathers the credentials for <paramref name="repoUrls"/>; never throws for a host or for git missing.</summary>
    public async Task<GitCloneCredentialSet> GatherAsync(IEnumerable<string> repoUrls, CancellationToken ct)
    {
        var hosts = Hosts(repoUrls);
        if (hosts.Count == 0) return GitCloneCredentialSet.None;
        if (FindGit() is not { } git) return new(null, hosts.Keys.ToArray());

        var lines = new List<string>();
        var missing = new List<string>();
        foreach (var (origin, repo) in hosts)
        {
            var credential = await FillAsync(git, repo, ct).ConfigureAwait(false);
            if (credential is { } c && await WorksAsync(git, repo, c, ct).ConfigureAwait(false))
                lines.Add($"{repo.Scheme}://{Uri.EscapeDataString(c.User)}:{Uri.EscapeDataString(c.Token)}@{repo.Authority}");
            else
                missing.Add(origin);
        }

        return new(lines.Count == 0 ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', lines))), missing);
    }

    /// <summary>
    /// The HTTP(S) origins (<c>scheme://host[:port]</c>) of the URLs, each with its first repo, without user info.
    /// A URL that carries a password already brings its own credential and is left alone; SSH and other URLs need none from here.
    /// </summary>
    public static IReadOnlyDictionary<string, Uri> Hosts(IEnumerable<string> repoUrls)
    {
        var hosts = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in repoUrls)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) continue;
            if (uri.UserInfo.Contains(':')) continue;
            var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
            var origin = clean.GetLeftPart(UriPartial.Authority);
            hosts.TryAdd(origin, clean);
        }

        return hosts;
    }

    private string? FindGit()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        return null;
    }

    /// <summary><c>git credential fill</c> for the repo, as a clone of it would ask; null when the helper has nothing.</summary>
    private async Task<(string User, string Token)?> FillAsync(string git, Uri repo, CancellationToken ct)
    {
        var request = $"protocol={repo.Scheme}\nhost={repo.Authority}\npath={repo.AbsolutePath.TrimStart('/')}\n\n";
        var result = await RunAsync(git, ["credential", "fill"], request, null, ct).ConfigureAwait(false);
        if (result is not { Exit: 0 } ok) return null;
        string? user = null, token = null;
        foreach (var line in ok.Output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("username=", StringComparison.Ordinal)) user = text["username=".Length..];
            else if (text.StartsWith("password=", StringComparison.Ordinal)) token = text["password=".Length..];
        }

        return user is { Length: > 0 } && token is { Length: > 0 } ? (user, token) : null;
    }

    /// <summary>One <c>git ls-remote</c> of the repo with only this credential (the device's helpers switched off for it).</summary>
    private async Task<bool> WorksAsync(string git, Uri repo, (string User, string Token) credential, CancellationToken ct)
    {
        var result = await RunAsync(git,
            ["-c", "credential.helper=", "-c", "credential.helper=" + EnvironmentHelper, "-c", "core.hooksPath=", "ls-remote", "--heads", "--", repo.AbsoluteUri],
            null, new() { [UserVariable] = credential.User, [TokenVariable] = credential.Token }, ct).ConfigureAwait(false);
        return result is { Exit: 0 };
    }

    /// <summary>Runs git with prompts off; null when it could not start or ran out of time (it is killed then).</summary>
    private async Task<(int Exit, string Output)?> RunAsync(string git, string[] arguments, string? input, Dictionary<string, string>? secrets, CancellationToken ct)
    {
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Nothing from this process may inject config, trace output or a prompt into the child.
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (key.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CONSTRUCT_GIT_", StringComparison.OrdinalIgnoreCase)
                || key is "GIT_ASKPASS" or "SSH_ASKPASS" or "SSH_ASKPASS_REQUIRE" or "GIT_CONFIG_PARAMETERS")
                start.Environment.Remove(key);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ASKPASS"] = "";
        foreach (var (key, value) in secrets ?? []) start.Environment[key] = value;

        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                var error = process.StandardError.ReadToEndAsync(limit.Token);
                try
                {
                    if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException) { /* git ended without reading its input */ }

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                _ = await error.ConfigureAwait(false);
                return (process.ExitCode, text);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
