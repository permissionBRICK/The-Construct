using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// The guest sees the environment the old PowerShell provisioner gave it for the same settings (design 4,
/// "Guest equivalence"), pinned by the variable list below. The provisioner itself is gone; the engine is
/// the only implementation.
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
        // As the old Provision-AgentVM.ps1 sent them: the $envPrefix at the provision.sh call, + $externalEnv,
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
