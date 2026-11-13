using System.Diagnostics;
using System.Text;
using Construct.Agent.Git;

namespace Construct.Agent.Tests;

/// <summary>
/// The clone credentials a device adds to a job (plan 7.4), gathered against a fake <c>git</c> on the search path:
/// the helper gives a credential, gives none, would prompt, or git is missing; each host is checked once, and no
/// credential reaches an argument.
/// </summary>
public sealed class GitCredentialTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "agent-test-git-" + Guid.NewGuid().ToString("n"));

    public GitCredentialTests()
    {
        Directory.CreateDirectory(_bin);
        FakeGit.Install(_bin);
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, true); }
        catch (IOException) { }
    }

    private static string Decode(string? b64) => b64 is null ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    [Fact]
    public async Task A_credential_from_the_helper_that_the_host_accepts_is_sent_in_the_old_line_format()
    {
        FakeGit.Helper(_bin, "git.example.test", "bob", "t/ok:en");
        FakeGit.Accept(_bin, "git.example.test", "bob", "t/ok:en");
        FakeGit.Helper(_bin, "code.example.test:8443", "carol", "second-token");
        FakeGit.Accept(_bin, "code.example.test:8443", "carol", "second-token");

        var found = await new GitCloneCredentials(_bin).GatherAsync(
            ["https://git.example.test/team/app.git", "https://code.example.test:8443/x/y", "git@ssh.example.test:a/b.git"], default);

        Assert.Equal("https://bob:t%2Fok%3Aen@git.example.test\nhttps://carol:second-token@code.example.test:8443", Decode(found.CredentialsB64));
        Assert.Empty(found.HostsWithoutCredential);
        // Prompts off for every call, and the credential never in an argument list.
        var calls = FakeGit.Calls(_bin);
        Assert.All(calls, c => Assert.Contains("prompt=0 gcm=never askpass=[]", c, StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("t/ok", StringComparison.Ordinal) || c.Contains("second-token", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("credential.helper= -c credential.helper=!", StringComparison.Ordinal) && c.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_is_checked_once_whatever_the_number_of_its_repos()
    {
        FakeGit.Helper(_bin, "git.example.test", "bob", "token-1");
        FakeGit.Accept(_bin, "git.example.test", "bob", "token-1");

        var found = await new GitCloneCredentials(_bin).GatherAsync(
            ["https://git.example.test/a.git", "https://GIT.example.test/b.git", "https://bob@git.example.test/c.git"], default);

        Assert.Equal("https://bob:token-1@git.example.test", Decode(found.CredentialsB64));
        Assert.Single(FakeGit.Checks(_bin));
        Assert.Single(FakeGit.Calls(_bin), c => c.Contains("credential fill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_whose_helper_has_nothing_or_whose_credential_is_refused_is_left_out_and_named()
    {
        FakeGit.Helper(_bin, "refused.example.test", "bob", "wrong");
        FakeGit.Accept(_bin, "refused.example.test", "bob", "right");
        FakeGit.Helper(_bin, "good.example.test", "bob", "good-token");
        FakeGit.Accept(_bin, "good.example.test", "bob", "good-token");

        var found = await new GitCloneCredentials(_bin).GatherAsync(
            ["https://none.example.test/a.git", "https://refused.example.test/b.git", "https://good.example.test/c.git"], default);

        Assert.Equal("https://bob:good-token@good.example.test", Decode(found.CredentialsB64));
        Assert.Equal(["https://none.example.test", "https://refused.example.test"], found.HostsWithoutCredential);
    }

    [Fact]
    public async Task A_helper_that_would_prompt_gives_no_credential_and_does_not_hang()
    {
        FakeGit.Prompts(_bin, "git.example.test");
        var clock = Stopwatch.StartNew();

        var found = await new GitCloneCredentials(_bin, TimeSpan.FromSeconds(10)).GatherAsync(["https://git.example.test/a.git"], default);

        Assert.Null(found.CredentialsB64);
        Assert.Equal(["https://git.example.test"], found.HostsWithoutCredential);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_git_that_hangs_is_stopped_at_the_time_limit()
    {
        FakeGit.Hangs(_bin, "git.example.test");
        var clock = Stopwatch.StartNew();

        var found = await new GitCloneCredentials(_bin, TimeSpan.FromSeconds(2)).GatherAsync(["https://git.example.test/a.git"], default);

        Assert.Null(found.CredentialsB64);
        Assert.Equal(["https://git.example.test"], found.HostsWithoutCredential);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Without_git_there_are_no_credentials_and_no_error()
    {
        var empty = Path.Combine(_bin, "empty");
        Directory.CreateDirectory(empty);

        var found = await new GitCloneCredentials(empty).GatherAsync(["https://git.example.test/a.git", "http://plain.example.test/b"], default);

        Assert.Null(found.CredentialsB64);
        Assert.Equal(["https://git.example.test", "http://plain.example.test"], found.HostsWithoutCredential);
    }

    [Fact]
    public async Task Repos_without_an_http_host_or_with_their_own_password_need_nothing()
    {
        var found = await new GitCloneCredentials(_bin).GatherAsync(
            ["git@github.example.test:a/b.git", "ssh://git@x.example.test/a", "https://u:own-secret@y.example.test/a", "not a url", ""], default);

        Assert.Same(GitCloneCredentialSet.None, found);
        Assert.Empty(FakeGit.Calls(_bin));
    }
}

/// <summary>
/// A shell script named <c>git</c>: <c>credential fill</c> answers from <c>fill-&lt;host&gt;</c> (or prompts, or hangs),
/// <c>ls-remote</c> runs the credential helper it was given, as git would, and accepts what <c>accept-&lt;host&gt;</c> holds.
/// It logs its arguments and prompt switches (never a credential: there is none in them) to <c>calls.log</c>.
/// </summary>
internal static class FakeGit
{
    private const string Script = """
        #!/bin/sh
        dir=$(dirname "$0")
        printf '%s | prompt=%s gcm=%s askpass=[%s]\n' "$*" "${GIT_TERMINAL_PROMPT-unset}" "${GCM_INTERACTIVE-unset}" "${GIT_ASKPASS-unset}" >> "$dir/calls.log"
        if [ "$1" = credential ]; then
          host=
          while IFS= read -r line; do
            [ -z "$line" ] && break
            case "$line" in host=*) host=${line#host=} ;; esac
          done
          if [ -f "$dir/hang-$host" ]; then sleep 60; exit 0; fi
          if [ -f "$dir/prompt-$host" ]; then
            if [ "$GIT_TERMINAL_PROMPT" = 0 ]; then echo "fatal: could not read Username: terminal prompts disabled" >&2; exit 128; fi
            sleep 60; exit 0
          fi
          if [ -f "$dir/fill-$host" ]; then printf 'protocol=https\nhost=%s\n' "$host"; cat "$dir/fill-$host"; exit 0; fi
          echo "fatal: could not read Username" >&2; exit 128
        fi
        helper=; prev=; url=
        for a in "$@"; do
          if [ "$prev" = "-c" ]; then case "$a" in credential.helper=!*) helper=${a#credential.helper=!} ;; esac; fi
          prev=$a; url=$a
        done
        host=${url#*://}; host=${host%%/*}
        echo "$host" >> "$dir/checks.log"
        out=$(sh -c "$helper get")
        user=$(printf '%s\n' "$out" | sed -n 's/^username=//p')
        pass=$(printf '%s\n' "$out" | sed -n 's/^password=//p')
        [ -f "$dir/accept-$host" ] && [ "$(cat "$dir/accept-$host")" = "$user:$pass" ] && exit 0
        echo "fatal: Authentication failed" >&2
        exit 128
        """;

    public static void Install(string bin)
    {
        var path = Path.Combine(bin, "git");
        File.WriteAllText(path, Script.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void Helper(string bin, string host, string user, string token) =>
        File.WriteAllText(Path.Combine(bin, "fill-" + host), $"username={user}\npassword={token}\n");

    public static void Accept(string bin, string host, string user, string token) => File.WriteAllText(Path.Combine(bin, "accept-" + host), $"{user}:{token}");

    public static void Prompts(string bin, string host) => File.WriteAllText(Path.Combine(bin, "prompt-" + host), "");

    public static void Hangs(string bin, string host) => File.WriteAllText(Path.Combine(bin, "hang-" + host), "");

    public static string[] Calls(string bin) => Lines(Path.Combine(bin, "calls.log"));

    public static string[] Checks(string bin) => Lines(Path.Combine(bin, "checks.log"));

    private static string[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];
}
