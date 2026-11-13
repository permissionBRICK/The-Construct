using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>Everything <see cref="ProvisionScript.MainEnvironment"/> needs besides the desired state.</summary>
/// <param name="ConnectedAsRoot">
/// The PowerShell's root-key fast path: no root key regeneration (<c>SETUP_ROOT_SSH_KEY=false</c>) and
/// no reboot announced. Otherwise the bootstrap path: a new root key and a reboot after the run.
/// </param>
/// <param name="SecretNames">The secret variables the run exports from its staged file.</param>
public sealed record ProvisionEnvInput(
    string VmName,
    VmDesiredState Desired,
    string DefaultSeedUser,
    string ConstructVersion,
    bool ConnectedAsRoot,
    bool DeferProjectCommands,
    IReadOnlySet<string> SecretNames,
    string? PayloadHash,
    string? DesiredHash,
    string? Channel);

/// <summary>The parsed <c>===CONSTRUCT-PROVISION-RESULT===</c> block.</summary>
public sealed record ProvisionResultBlock(bool Found, bool IsValid, int ErrorCount, IReadOnlyList<ProvisionStepError> Errors);

/// <summary>
/// The guest side of provisioning as text: the environment <c>bin/provision.sh</c> gets, the remote
/// commands, and the parsers for what it prints and writes. A port of <c>Provision-AgentVM.ps1</c>
/// (the <c>$envPrefix</c> at the <c>provision.sh</c> call, <c>Invoke-DeferredProjectCommands</c>,
/// <c>Invoke-SshStream</c>) and of <c>lib/AgentVm.Common.ps1</c>
/// (<c>ConvertFrom-ConstructProvisionResult</c>, <c>Get-ConstructAgentFixPrompt</c>). Pure.
/// </summary>
public static partial class ProvisionScript
{
    public const string RepoDir = "/opt/construct/repo";
    public const string RemoteArchive = "/tmp/construct-repo.tar.gz";
    public const string ResultStart = "===CONSTRUCT-PROVISION-RESULT===";
    public const string ResultEnd = "===END-CONSTRUCT-PROVISION-RESULT===";
    public const string ProjectCommandsTitle = "Running project provisioning commands";

    /// <summary>Secret variables. They reach the guest from a 0600 file over stdin, never in an argument.</summary>
    public const string VmTokenVariable = "CONSTRUCT_VM_TOKEN_B64";
    public const string CloneCredentialsVariable = "GIT_CLONE_CREDENTIALS_B64";
    public const string ServeWebTokenVariable = "VSCODE_SERVE_WEB_TOKEN_B64";

    /// <summary>POSIX single quoting (<c>ConvertTo-PosixSingleQuoted</c>).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string B64(string? value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// The variables of <c>provision.sh</c>'s <c>env</c> prefix, in the PowerShell's order, for the
    /// same settings. A secret variable whose value is staged is left out here (the remote shell
    /// exports it from the staged file); one that is not staged is sent empty, as the PowerShell does.
    /// The three marker keys of design 4.1 follow at the end, when known.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> MainEnvironment(ProvisionEnvInput input)
    {
        var s = input.Desired.Settings;
        var vars = new List<KeyValuePair<string, string>>();
        void Add(string name, string? value) => vars.Add(new(name, value ?? string.Empty));
        void AddSecretSlot(string name) { if (!input.SecretNames.Contains(name)) Add(name, string.Empty); }

        Add("AI_TOOLS", s.AiTools ?? "opencode,claude-code,codex");
        Add("PROJECTS", string.Join(',', input.Desired.Projects));
        Add("SSH_USER", s.SeedUser ?? input.DefaultSeedUser);
        Add("AGENT_NAME", string.IsNullOrEmpty(s.AgentName) ? input.VmName + "-agent" : s.AgentName);
        Add("CLAUDE_USER", s.ClaudeUser ?? "root");
        Add("GIT_USER_NAME_B64", B64(s.GitUserName));
        Add("GIT_USER_EMAIL_B64", B64(s.GitEmail));
        Add("GIT_CREDENTIAL_STORE", Bool(s.GitCredentialStore == true));
        AddSecretSlot(CloneCredentialsVariable);
        Add("GIT_CLONE_SKIP_HOSTS_B64", s.GitCloneSkipHostsB64);
        Add("CHECKOUT_PROJECTS", Bool(s.CheckoutProjects ?? true));
        Add("SETUP_ROOT_SSH_KEY", Bool(!input.ConnectedAsRoot));
        Add("VSCODE_SERVER", s.VsCodeServer ?? "true");
        Add("VSCODE_SERVE_WEB", s.VsCodeServeWeb ?? "true");
        Add("VSCODE_TUNNEL", s.VsCodeTunnel ?? "false");
        AddSecretSlot(ServeWebTokenVariable);
        Add("VSCODE_CLIENT_COMMIT", s.VsCodeClientCommit);
        Add("CONSTRUCT_VERSION", input.ConstructVersion);
        Add("SMB_SHARE", s.SmbShare ?? "true");
        Add("CLAUDE_PARTIAL_STREAMING", s.ClaudePartialStreaming ?? "true");
        Add("MIC_PASSTHROUGH", s.MicPassthrough ?? "false");
        Add("OPENCODE_BACKGROUND_WATCHER", s.OpenCodeBackgroundWatcher);
        Add("T3CODE", s.T3Code);
        Add("T3CODE_CHANNEL", s.T3CodeChannel);
        Add("T3CODE_BUILD_SOURCE", s.T3CodeBuildSource);
        Add("T3CODE_PATCHED", s.T3CodePatched);
        Add("T3CODE_AUTO_APPROVE", s.T3CodeAutoApprove);
        Add("T3CODE_HTTPS", s.T3CodeHttps);
        Add("DOCKER_REGISTRY_MIRRORS", s.DockerRegistryMirrors);
        // Get-ExternalEnvSuffix: the client-reachable identity, sent only when stated.
        if (!string.IsNullOrEmpty(s.ExternalHost))
        {
            Add("CONSTRUCT_EXTERNAL_HOST", s.ExternalHost);
            Add("CONSTRUCT_EXTERNAL_SSH_PORT", (s.ExternalSshPort ?? 22).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Get-ServiceEnvSuffix: only for a service-managed VM (the token is never in here).
        if (!string.IsNullOrWhiteSpace(s.ServiceUrl))
        {
            Add("CONSTRUCT_SERVICE_URL", s.ServiceUrl);
            if (!string.IsNullOrWhiteSpace(s.ServiceCaB64)) Add("CONSTRUCT_SERVICE_CA_B64", s.ServiceCaB64);
            var instance = string.IsNullOrWhiteSpace(s.InstanceName) ? input.VmName : s.InstanceName;
            Add("CONSTRUCT_INSTANCE_NAME", instance);
        }
        Add("T3CODE_BUILD_MODE", "server");
        if (input.DeferProjectCommands) Add("DEFER_PROJECT_COMMANDS", "true");
        Add("REBOOT_AFTER_PROVISION", Bool(!input.ConnectedAsRoot));
        if (!string.IsNullOrEmpty(input.PayloadHash)) Add("CONSTRUCT_PAYLOAD_HASH", input.PayloadHash);
        if (!string.IsNullOrEmpty(input.DesiredHash)) Add("CONSTRUCT_DESIRED_HASH", input.DesiredHash);
        if (!string.IsNullOrEmpty(input.Channel)) Add("CONSTRUCT_CHANNEL", input.Channel);
        return vars;
    }

    /// <summary>The variables of the project-commands phase (<c>Invoke-DeferredProjectCommands</c>).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ProjectPhaseEnvironment(ProvisionEnvInput input)
    {
        var s = input.Desired.Settings;
        var vars = new List<KeyValuePair<string, string>>
        {
            new("PROVISION_PHASE", "project-commands"),
            new("REBOOT_AFTER_PROVISION", Bool(!input.ConnectedAsRoot)),
            new("CHECKOUT_PROJECTS", Bool(s.CheckoutProjects ?? true)),
        };
        if (!input.SecretNames.Contains(CloneCredentialsVariable)) vars.Add(new(CloneCredentialsVariable, string.Empty));
        vars.Add(new("GIT_CLONE_SKIP_HOSTS_B64", s.GitCloneSkipHostsB64 ?? string.Empty));
        return vars;
    }

    /// <summary>Rejects values that cannot travel in a shell word safely even when quoted.</summary>
    public static string? Invalid(IEnumerable<KeyValuePair<string, string>> vars) =>
        vars.FirstOrDefault(v => v.Value.Any(c => c is '\0' or '\n' or '\r')).Key;

    public static string EnvPrefix(IEnumerable<KeyValuePair<string, string>> vars) =>
        "env " + string.Join(' ', vars.Select(v => v.Key + "=" + Quote(v.Value)));

    /// <summary>
    /// The command that runs inside the guest's root shell: loads the staged secrets (when there are
    /// any) and removes their file whatever happens, then runs <paramref name="body"/>.
    /// </summary>
    public static string WithSecrets(string? secretFile, string body) => secretFile is null
        ? body
        : $"trap 'rm -f {secretFile}' EXIT; set -a; . {secretFile}; set +a; {body}";

    /// <summary>The staged secrets file's content: one <c>NAME='value'</c> line per secret.</summary>
    public static string SecretFile(IEnumerable<KeyValuePair<string, string>> secrets) =>
        string.Concat(secrets.Select(s => s.Key + "=" + Quote(s.Value) + "\n"));

    /// <summary>
    /// Wraps a guest command as <c>Invoke-SshStream -Sudo</c> does: a colour-capable but
    /// non-interactive environment and a login shell, as root. Off root it goes through
    /// <c>sudo -S</c> (the password then comes on stdin) or, without one, <c>sudo -n</c>. Stderr is
    /// merged into the streamed output.
    /// </summary>
    public static string AsRoot(string command, bool connectedAsRoot, bool sudoPasswordOnStdin)
    {
        const string streamEnv = "env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive";
        var inner = $"{streamEnv} bash -lc {Quote(command)}";
        if (!connectedAsRoot) inner = (sudoPasswordOnStdin ? "sudo -S -p '' " : "sudo -n ") + inner;
        return inner + " 2>&1";
    }

    /// <summary>Stages a file readable only by the connecting user (<c>Send-GuestSecret</c>).</summary>
    public static string StageFileCommand(string remotePath) => $"umask 077; cat > {remotePath}";

    /// <summary>The PowerShell's unpack command for the uploaded archive, verbatim.</summary>
    public static string UnpackCommand(string seedUser) =>
        $"mkdir -p /opt/construct && rm -rf {RepoDir} && mkdir -p {RepoDir} && tar -xzf {RemoteArchive} -C {RepoDir} && chown -R {seedUser}:{seedUser} /opt/construct";

    public static string ProvisionCommand(IEnumerable<KeyValuePair<string, string>> vars) =>
        $"{EnvPrefix(vars)} bash {RepoDir}/bin/provision.sh";

    public const string ReadMarkerCommand = "cat /etc/construct/provisioned.env 2>/dev/null || true";

    public const string RebootRequiredCommand = "test -e /var/run/reboot-required && echo yes || echo no";

    /// <summary>The PowerShell's final step: strip the bootstrap key (root path only) and reboot in the background.</summary>
    public static string RebootCommand(string? bootstrapPublicKey, string seedUser)
    {
        var strip = string.IsNullOrWhiteSpace(bootstrapPublicKey) ? ""
            : $"sed -i '/{bootstrapPublicKey.Trim().Replace("/", "\\/", StringComparison.Ordinal)}/d' /home/{seedUser}/.ssh/authorized_keys 2>/dev/null || true; ";
        return strip + "nohup sh -c 'sleep 3; reboot' </dev/null >/dev/null 2>&1 &";
    }

    /// <summary>Without a reboot the PowerShell still strips a leftover bootstrap key on the root path.</summary>
    public static string StripBootstrapCommand(string bootstrapPublicKey, string seedUser) =>
        $"sed -i '/{bootstrapPublicKey.Trim().Replace("/", "\\/", StringComparison.Ordinal)}/d' /home/{seedUser}/.ssh/authorized_keys 2>/dev/null || true";

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    /// <summary>A guest output line as it is stored: no colour or cursor sequences, no carriage return.</summary>
    public static string CleanLine(string line) => Ansi().Replace(line, string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"^errors=([0-9]+)$")]
    private static partial Regex ErrorsLine();

    [GeneratedRegex(@"^error=([^|]*)\|(-?[0-9]+)(?:\|(.*))?$")]
    private static partial Regex ErrorLine();

    /// <summary><c>ConvertFrom-ConstructProvisionResult</c>: the last complete result block.</summary>
    public static ProvisionResultBlock ParseResult(IEnumerable<string> lines)
    {
        var clean = lines.Select(CleanLine).ToList();
        int start = -1, end = -1;
        for (var i = 0; i < clean.Count; i++)
        {
            if (clean[i] == ResultStart) { start = i; end = -1; }
            else if (start >= 0 && clean[i] == ResultEnd) end = i;
        }

        var errors = new List<ProvisionStepError>();
        var declared = -1;
        var found = start >= 0 && end > start;
        if (found)
        {
            for (var i = start + 1; i < end; i++)
            {
                if (ErrorsLine().Match(clean[i]) is { Success: true } count) declared = int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                else if (ErrorLine().Match(clean[i]) is { Success: true } error)
                    errors.Add(new(error.Groups[1].Value, int.Parse(error.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), error.Groups[3].Value));
            }
        }

        return new(found, found && declared >= 0 && declared == errors.Count, Math.Max(declared, 0), errors);
    }

    /// <summary><c>Get-ConstructAgentFixPrompt</c>: the repair agent's prompt, or nothing.</summary>
    public static IReadOnlyList<string> FixPrompt(IReadOnlyList<ProvisionStepError> errors)
    {
        var steps = errors.Where(e => !string.IsNullOrEmpty(e.Title))
            .Select(e => $"- Step '{e.Title}' failed (exit {e.ExitCode})" + (string.IsNullOrEmpty(e.LogPath) ? "" : $"; log: {e.LogPath}"))
            .ToList();
        if (steps.Count == 0) return [];
        var logs = errors.Where(e => !string.IsNullOrEmpty(e.LogPath)).Select(e => e.LogPath).ToList();
        var logRef = logs.Count switch
        {
            1 => $"Read the provisioning log at {logs[0]}",
            > 1 => $"Read the provisioning logs at: {string.Join(", ", logs)}",
            _ => "Check the provisioning output above",
        };
        return ["On the last Construct provisioning run, the following step(s) failed:", .. steps, $"{logRef} and diagnose and fix the underlying problem."];
    }

    /// <summary>Reads <c>/etc/construct/provisioned.env</c> (config-set.sh may single-quote values).</summary>
    public static IReadOnlyDictionary<string, string> ParseMarker(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || eq < 1) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
            map[line[..eq]] = value;
        }

        return map;
    }

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>CONSTRUCT_DESIRED_HASH</c>: SHA-256 over the canonical JSON of the desired state (fields in
    /// declaration order, unset fields left out), so the same choices always hash the same.
    /// </summary>
    public static string DesiredHash(VmDesiredState desired) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(desired, CanonicalJson)));
}
