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
        // + $serviceEnv, then T3CODE_BUILD_MODE, DEFER_PROJECT_COMMANDS, REBOOT_AFTER_PROVISION. SWAP_SIZE_GB came
        // after the PowerShell provisioner.
        string[] expected =
        [
            "AI_TOOLS", "PROJECTS", "SSH_USER", "AGENT_NAME", "CLAUDE_USER", "GIT_USER_NAME_B64", "GIT_USER_EMAIL_B64",
            "GIT_CREDENTIAL_STORE", "GIT_CLONE_CREDENTIALS_B64", "GIT_CLONE_SKIP_HOSTS_B64", "CHECKOUT_PROJECTS", "SETUP_ROOT_SSH_KEY",
            "VSCODE_SERVER", "VSCODE_SERVE_WEB", "VSCODE_TUNNEL", "VSCODE_SERVE_WEB_TOKEN_B64", "VSCODE_CLIENT_COMMIT",
            "CONSTRUCT_VERSION", "SMB_SHARE", "CLAUDE_PARTIAL_STREAMING", "MIC_PASSTHROUGH", "OPENCODE_BACKGROUND_WATCHER",
            "SWAP_SIZE_GB", "T3CODE", "T3CODE_CHANNEL", "T3CODE_BUILD_SOURCE", "T3CODE_PATCHED", "T3CODE_AUTO_APPROVE", "T3CODE_HTTPS",
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

    [Fact]
    public void The_coding_agents_chosen_are_AI_TOOLS_and_without_a_choice_the_default_three()
    {
        string AiTools(VmDesiredState desired) =>
            ProvisionScript.MainEnvironment(Local() with { Desired = desired }).Single(v => v.Key == "AI_TOOLS").Value;
        var settings = new ProvisionSettings();
        Assert.Equal("opencode,claude-code,codex", AiTools(new VmDesiredState(["default"], settings)));
        Assert.Equal("claude-code", AiTools(new VmDesiredState(["default"], settings with { AiTools = "claude-code" })));
        Assert.Equal("claude-code,antigravity", AiTools(new VmDesiredState(["default"], settings with { AiTools = "codex" }, ["claude-code", "antigravity"])));
        // Every agent off: an empty AI_TOOLS would mean the guest's default.
        Assert.Equal("none", AiTools(new VmDesiredState(["default"], settings, [])));
    }

    [Fact]
    public void The_hash_of_a_state_without_coding_agents_is_what_it_was_and_their_order_does_not_count()
    {
        // Stored states from before the field keep their hash, so no VM shows a change that waits after the update.
        var before = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("""{"projects":["default"],"settings":{}}"""u8));
        Assert.Equal(before, ProvisionScript.DesiredHash(new VmDesiredState(["default"], new ProvisionSettings())));
        var one = ProvisionScript.DesiredHash(new VmDesiredState(["default"], new ProvisionSettings(), ["codex", "claude-code"]));
        Assert.Equal(one, ProvisionScript.DesiredHash(new VmDesiredState(["default"], new ProvisionSettings(), ["claude-code", "codex"])));
        Assert.NotEqual(before, one);
        Assert.NotEqual(one, ProvisionScript.DesiredHash(new VmDesiredState(["default"], new ProvisionSettings(), ["claude-code"])));
    }

    [Theory]
    [InlineData("'opencode,claude-code,codex'", "opencode,claude-code,codex")]
    [InlineData("claude-code, antigravity,pi", "claude-code,antigravity")]
    [InlineData("none", "")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_vms_saved_AI_TOOLS_reads_as_its_coding_agents(string? saved, string? agents) =>
        Assert.Equal(agents, CodingAgents.FromAiTools(saved) is { } list ? string.Join(',', list) : null);

    [Fact]
    public void The_swap_size_is_sent_as_stored_and_empty_keeps_the_vms_saved_choice()
    {
        static string Swap(ProvisionEnvInput input) => ProvisionScript.MainEnvironment(input).Single(v => v.Key == "SWAP_SIZE_GB").Value;
        Assert.Equal(string.Empty, Swap(Local()));
        Assert.Equal("4", Swap(Local() with { Desired = new VmDesiredState(["default"], new ProvisionSettings { SwapSizeGb = "4" }) }));
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
