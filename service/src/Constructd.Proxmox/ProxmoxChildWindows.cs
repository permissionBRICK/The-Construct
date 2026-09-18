using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Proxmox;
public sealed partial class ProxmoxChildVmPlatform
{
    private async Task<int> WindowsIdAsync(string name, string incarnation, CancellationToken ct)
    {
        var id = await commands.RequireAsync(name, ct); var config = await commands.ConfigAsync(id, ct);
        var owner = ReadOwnership(name) ?? throw Conflict(); VerifyOwner(config, owner);
        if (owner.VmId != id || owner.Incarnation != incarnation) throw Conflict(); return id;
    }
    private async Task<JsonElement?> WindowsExecAsync(int id, string script, string? input, CancellationToken ct)
    {
        var args = new List<string> { "guest", "exec", ProxmoxCommands.Number(id), "--timeout", input is null ? "5" : "60" };
        if (input is not null) args.AddRange(["--pass-stdin", "1"]);
        args.AddRange(["--", "powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))]);
        var result = await commands.RunAsync(options.Proxmox.QmPath, args, ct, TimeSpan.FromSeconds(input is null ? 10 : 90), input);
        if (!result.Succeeded || result.StandardOutput.Length > 65536) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (!doc.RootElement.TryGetProperty("exitcode", out var code) || code.GetInt32() != 0) return null;
            var output = ProxmoxCommands.String(doc.RootElement, "out-data");
            if (string.IsNullOrWhiteSpace(output)) return null;
            using var report = JsonDocument.Parse(output.Trim('\uFEFF', '\r', '\n', ' ')); return report.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }
    public async Task<WindowsGuestObservation> ObserveWindowsAsync(string name, string incarnation, CancellationToken ct)
    {
        var id = await WindowsIdAsync(name, incarnation, ct);
        var status = await commands.QueryAsync(["get", commands.VmPath(id) + "/status/current"], ct);
        double? uptime = status.TryGetProperty("uptime", out var up) && up.TryGetDouble(out var number) ? number : null;
        var report = await WindowsExecAsync(id, "if (Test-Path 'C:\\provision\\windows-report.json') { Get-Content -Raw 'C:\\provision\\windows-report.json' }", null, ct);
        try { return new(uptime, report?.Deserialize<WindowsGuestReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
        catch (JsonException) { return new(uptime, null); }
    }
    public async Task DeliverWindowsKeyAsync(string name, string incarnation, string key, CancellationToken ct)
    {
        if (!Regex.IsMatch(key, @"\A[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\z")) throw new ChildValidationException("validation", "key");
        var id = await WindowsIdAsync(name, incarnation, ct);
        // qm forwards stdin to the guest. The secret is absent from host argv, output and files.
        var script = """
            $ErrorActionPreference='Stop'
            $key=[Console]::In.ReadToEnd().Trim()
            if ($key -notmatch '^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$') { exit 2 }
            & cscript.exe //Nologo "$env:SystemRoot\System32\slmgr.vbs" /ipk $key *> $null
            if ($LASTEXITCODE -eq 0) { & cscript.exe //Nologo "$env:SystemRoot\System32\slmgr.vbs" /ato *> $null }
            $key=$null
            $report=Get-Content -Raw 'C:\provision\windows-report.json' | ConvertFrom-Json
            $license=Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'" | Where-Object { $_.PartialProductKey } | Select-Object -First 1
            $report.activation=if ($license.LicenseStatus -eq 1) { 'activated' } else { 'failed' }
            $report.partialKey=[string]$license.PartialProductKey
            $report | ConvertTo-Json -Compress | Set-Content 'C:\provision\windows-report.json' -Encoding UTF8
            '{"ok":true}'
            """;
        if (await WindowsExecAsync(id, script, key, ct) is null) throw ProxmoxCommands.Failure();
    }
    public Task ClearWindowsKeyAsync(string name, string incarnation, CancellationToken ct) => Task.CompletedTask;
    public async Task EjectWindowsMediaAsync(string name, string incarnation, bool installOnly, CancellationToken ct)
    {
        var id = await WindowsIdAsync(name, incarnation, ct); var config = await commands.ConfigAsync(id, ct);
        foreach (var slot in installOnly ? new[] { "ide2" } : new[] { "ide2", "ide0", "ide1" })
        {
            if (!config.TryGetProperty(slot, out _)) continue;
            if (!(ProxmoxCommands.String(config, slot) ?? "").Split(',').Contains("media=cdrom")) throw new ChildValidationException("artifact-ownership-unverified", "media");
            await commands.QmAsync(["set", ProxmoxCommands.Number(id), "--" + slot, "none,media=cdrom"], ct);
        }
        var actual = await commands.ConfigAsync(id, ct);
        if (HasMedia(actual, "ide2") || !installOnly && (HasMedia(actual, "ide0") || HasMedia(actual, "ide1"))) throw ProxmoxCommands.Failure();
    }
}
