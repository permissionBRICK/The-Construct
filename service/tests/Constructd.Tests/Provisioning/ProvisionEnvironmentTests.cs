using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// The guest must see the same environment from the engine as from <c>Provision-AgentVM.ps1</c> for
/// the same settings (design 4, "Guest equivalence"). Pinned twice: by the variable list below, and —
/// where <c>pwsh</c> is installed — against the PowerShell's own statements, evaluated from the script.
/// </summary>
public sealed class ProvisionEnvironmentTests
{
    private static ProvisionEnvInput Remote(IReadOnlySet<string>? secrets = null) => new(
        "devbox",
        new VmDesiredState(["default", "web"], new ProvisionSettings
        {
            AiTools = "claude-code", SeedUser = "agent", T3Code = "true", GitUserName = "Ann O'Neil", GitEmail = "ann@example.test",
            GitCredentialStore = true, ExternalHost = "host.test", ExternalSshPort = 2201, ServiceUrl = "https://host.test:8443",
            ServiceCaB64 = "Q0E=", VsCodeClientCommit = new string('d', 40), DockerRegistryMirrors = "https://mirror.example.test",
        }),
        "construct", "c0ffee0000000000000000000000000000000001", ConnectedAsRoot: false, DeferProjectCommands: true,
        secrets ?? new HashSet<string>(), null, null, null);

    private static ProvisionEnvInput Local() => new(
        "agent-vm", new VmDesiredState(["default"], new ProvisionSettings()), "agent", "unversioned",
        ConnectedAsRoot: true, DeferProjectCommands: false, new HashSet<string>(), null, null, null);

    [Fact]
    public void The_variable_set_is_the_PowerShell_one_in_its_order()
    {
        // From Provision-AgentVM.ps1: the $envPrefix at the provision.sh call, + $externalEnv,
        // + $serviceEnv, then T3CODE_BUILD_MODE, DEFER_PROJECT_COMMANDS, REBOOT_AFTER_PROVISION.
        string[] expected =
        [
            "AI_TOOLS", "PROJECTS", "SSH_USER", "AGENT_NAME", "CLAUDE_USER", "GIT_USER_NAME_B64", "GIT_USER_EMAIL_B64",
            "GIT_CREDENTIAL_STORE", "GIT_CLONE_CREDENTIALS_B64", "GIT_CLONE_SKIP_HOSTS_B64", "CHECKOUT_PROJECTS", "SETUP_ROOT_SSH_KEY",
            "VSCODE_SERVER", "VSCODE_SERVE_WEB", "VSCODE_TUNNEL", "VSCODE_SERVE_WEB_TOKEN_B64", "VSCODE_CLIENT_COMMIT",
            "CONSTRUCT_VERSION", "SMB_SHARE", "CLAUDE_PARTIAL_STREAMING", "MIC_PASSTHROUGH", "OPENCODE_BACKGROUND_WATCHER",
            "T3CODE", "T3CODE_CHANNEL", "T3CODE_BUILD_SOURCE", "T3CODE_PATCHED", "T3CODE_AUTO_APPROVE", "T3CODE_HTTPS",
            "DOCKER_REGISTRY_MIRRORS", "CONSTRUCT_EXTERNAL_HOST", "CONSTRUCT_EXTERNAL_SSH_PORT", "CONSTRUCT_SERVICE_URL",
            "CONSTRUCT_SERVICE_CA_B64", "CONSTRUCT_INSTANCE_NAME", "T3CODE_BUILD_MODE", "DEFER_PROJECT_COMMANDS", "REBOOT_AFTER_PROVISION",
        ];
        Assert.Equal(expected, ProvisionScript.MainEnvironment(Remote()).Select(v => v.Key));
        // A local VM on the root path: no external identity, no service, no deferral.
        Assert.Equal(expected.Except(["CONSTRUCT_EXTERNAL_HOST", "CONSTRUCT_EXTERNAL_SSH_PORT", "CONSTRUCT_SERVICE_URL",
            "CONSTRUCT_SERVICE_CA_B64", "CONSTRUCT_INSTANCE_NAME", "DEFER_PROJECT_COMMANDS"]), ProvisionScript.MainEnvironment(Local()).Select(v => v.Key));
        // Staged secrets leave the prefix (the shell exports them); the engine's markers come last.
        var withSecrets = Remote(new HashSet<string> { ProvisionScript.CloneCredentialsVariable }) with
        { PayloadHash = new string('a', 64), DesiredHash = new string('b', 64), Channel = "main" };
        var keys = ProvisionScript.MainEnvironment(withSecrets).Select(v => v.Key).ToList();
        Assert.DoesNotContain(ProvisionScript.CloneCredentialsVariable, keys);
        Assert.Equal(["CONSTRUCT_PAYLOAD_HASH", "CONSTRUCT_DESIRED_HASH", "CONSTRUCT_CHANNEL"], keys[^3..]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_environment_matches_the_PowerShell_statements_evaluated_from_the_script(bool remote)
    {
        if (FindPwsh() is not { } pwsh) return; // pinned by the test above alone
        var input = remote ? Remote() : Local();
        var s = input.Desired.Settings;
        var cloneCred = remote ? "aHR0cHM6Ly91OnRAZ2l0LnRlc3Q=" : "";
        var vars = new Dictionary<string, object?>
        {
            ["AiTools"] = s.AiTools ?? "opencode,claude-code,codex", ["Projects"] = string.Join(',', input.Desired.Projects),
            ["SeedUser"] = s.SeedUser ?? input.DefaultSeedUser, ["agentNameArg"] = input.VmName + "-agent", ["RemoteUser"] = "root",
            ["gitNameB64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(s.GitUserName ?? "")),
            ["gitEmailB64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(s.GitEmail ?? "")),
            ["gitCredStore"] = s.GitCredentialStore == true ? "true" : "false", ["cloneCredB64"] = cloneCred, ["cloneSkipHostsB64"] = "",
            ["checkoutArg"] = "true", ["setupRootKeyArg"] = input.ConnectedAsRoot ? "false" : "true",
            ["VsCodeServer"] = "true", ["VsCodeServeWeb"] = "true", ["VsCodeTunnel"] = "false", ["serveWebTokenB64"] = "",
            ["vsCodeCommit"] = s.VsCodeClientCommit ?? "", ["constructVersion"] = input.ConstructVersion, ["SmbShare"] = "true",
            ["ClaudePartialStreaming"] = "true", ["MicPassthrough"] = "false", ["OpenCodeBackgroundWatcher"] = "",
            ["T3Code"] = s.T3Code ?? "", ["T3CodeChannel"] = "", ["T3CodeBuildSource"] = "", ["T3CodePatched"] = "",
            ["T3CodeAutoApprove"] = "", ["T3CodeHttps"] = "", ["DockerRegistryMirrors"] = s.DockerRegistryMirrors ?? "",
            // The PowerShell's default endpoint is not sent; the engine sends it only when stated.
            ["VmHost"] = s.ExternalHost ?? "agent-vm.mshome.net", ["SshPort"] = s.ExternalSshPort ?? 22, ["PublicHost"] = "",
            ["ServiceUrl"] = s.ServiceUrl ?? "", ["InstanceName"] = input.VmName, ["serviceCaB64"] = s.ServiceCaB64 ?? "",
        };
        var scenario = JsonSerializer.Serialize(new { useRootKey = input.ConnectedAsRoot, defer = input.DeferProjectCommands, vars });
        // The PowerShell's 'explicitly bound' rule only matters for the default endpoint, which the engine never sends.
        var (main, phase) = RunDriver(pwsh, scenario, explicitlyBound: remote);

        // The engine stages the clone credential instead; the guest's environment is the same.
        var secrets = remote ? new HashSet<string> { ProvisionScript.CloneCredentialsVariable } : new HashSet<string>();
        var engineInput = input with { SecretNames = secrets };
        var engine = ProvisionScript.MainEnvironment(engineInput).ToDictionary(v => v.Key, v => v.Value);
        var enginePhase = ProvisionScript.ProjectPhaseEnvironment(engineInput).ToDictionary(v => v.Key, v => v.Value);
        if (remote) { engine[ProvisionScript.CloneCredentialsVariable] = cloneCred; enginePhase[ProvisionScript.CloneCredentialsVariable] = cloneCred; }

        Assert.Equal(ParseEnv(main).OrderBy(v => v.Key), engine.OrderBy(v => v.Key));
        Assert.Equal(ParseEnv(phase).OrderBy(v => v.Key), enginePhase.OrderBy(v => v.Key));
        Assert.EndsWith(" bash /opt/construct/repo/bin/provision.sh", phase, StringComparison.Ordinal);
    }

    /// <summary>Words of an <c>env A='x' B=y …</c> command as the shell sees them (single quotes only).</summary>
    internal static Dictionary<string, string> ParseEnv(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in command)
        {
            if (quoted) { if (c == '\'') quoted = false; else word.Append(c); continue; }
            if (c == '\'') { quoted = true; any = true; continue; }
            if (c == ' ') { if (any || word.Length > 0) words.Add(word.ToString()); word.Clear(); any = false; continue; }
            if (c == '\\') continue; // only in '\'' here
            word.Append(c);
        }

        if (any || word.Length > 0) words.Add(word.ToString());
        Assert.Equal("env", words[0]);
        return words.Skip(1).TakeWhile(w => w.Contains('=', StringComparison.Ordinal))
            .Select(w => w.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    }

    private static (string Main, string Phase) RunDriver(string pwsh, string scenario, bool explicitlyBound)
    {
        var script = FindRepoFile("Provision-AgentVM.ps1");
        var driver = FindRepoFile("service/tests/Constructd.Tests/Provisioning/provision-env.driver.ps1");
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", driver, script, scenario, explicitlyBound ? "1" : "0" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000));
        Assert.True(process.ExitCode == 0, error);
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        return (lines.Single(l => l.StartsWith("MAIN:", StringComparison.Ordinal))[5..], lines.Single(l => l.StartsWith("PHASE:", StringComparison.Ordinal))[6..]);
    }

    private static string? FindPwsh() =>
        new[] { "/usr/local/bin/pwsh", "/usr/bin/pwsh", "/opt/microsoft/powershell/7/pwsh" }.FirstOrDefault(File.Exists);

    internal static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relativePath);
    }
}
