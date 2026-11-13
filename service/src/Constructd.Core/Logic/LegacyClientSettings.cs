using System.Text.Json.Nodes;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// The provisioning choices the old client kept for a VM, read into the engine's desired state when
/// that VM is adopted. The old client keeps two files (<c>extension/src/instancestate.js</c>,
/// <c>lib/AgentVm.InstanceState.ps1</c>): the install's <c>.construct-settings.json</c> holds the
/// install-wide git identity and, for the default instance, that VM's own keys at its top level; a
/// named instance keeps its own keys in <c>instances\&lt;name&gt;.json</c>. Keys the old client never
/// stored stay null, which means the engine's default. Nothing here is a secret.
/// </summary>
public static class LegacyClientSettings
{
    /// <summary>
    /// The desired state from the two files. <paramref name="instanceState"/> is the named instance's
    /// own file, or null for the default instance, whose keys are in <paramref name="settings"/>.
    /// </summary>
    public static VmDesiredState ToDesired(JsonObject? settings, JsonObject? instanceState)
    {
        settings ??= [];
        var vm = instanceState ?? settings;

        string? Toggle(string key) => vm[key] is JsonValue value && value.TryGetValue<bool>(out var on) ? (on ? "true" : "false") : null;
        string? Text(JsonObject from, string key) =>
            from[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var channel = Text(vm, "t3codeChannel");

        var projects = vm["projects"] is JsonArray list
            ? list.Select(p => p is JsonValue v && v.TryGetValue<string>(out var name) ? name.Trim() : null)
                .Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal).Select(name => name!).ToArray()
            : [];

        return new VmDesiredState(projects, new ProvisionSettings
        {
            GitUserName = Text(settings, "gitUserName"),
            GitEmail = Text(settings, "gitEmail"),
            GitCredentialStore = settings["gitCredentialStore"] is JsonValue store && store.TryGetValue<bool>(out var keep) ? keep : null,
            VsCodeServeWeb = Toggle("vsCodeServeWeb"),
            VsCodeTunnel = Toggle("vsCodeTunnel"),
            SmbShare = Toggle("smbShare"),
            MicPassthrough = Toggle("micPassthrough"),
            ClaudePartialStreaming = Toggle("claudePartialStreaming"),
            OpenCodeBackgroundWatcher = Toggle("opencodeBackgroundWatcher"),
            T3Code = Toggle("t3code"),
            T3CodeChannel = channel is "stable" or "nightly" ? channel : null,
            T3CodePatched = Toggle("t3codePatched"),
            T3CodeAutoApprove = Toggle("t3codeAutoApprove"),
        });
    }
}
