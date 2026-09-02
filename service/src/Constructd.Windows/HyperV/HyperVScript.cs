using System.Text;
using Constructd.Core.Domain;
using Constructd.Windows.Internal;

namespace Constructd.Windows.HyperV;

/// <summary>
/// Composes the PowerShell the Hyper-V driver runs.
///
/// The service does <b>not</b> talk to Hyper-V: it dot-sources
/// <c>drivers\Load-ConstructDriver.ps1</c> and calls the contract functions of
/// <c>docs/drivers.md</c> — the very functions the local install uses. One implementation, two entry
/// points; a driver fix reaches the service without being ported, and a backend added to the loader is
/// reachable from here by changing the backend id, not the service.
///
/// Every script has the same shape: dot-source the shared lib and the loader, run the operation into
/// <c>$result</c>, and print exactly one compressed JSON envelope as the last stdout line. Everything
/// before it is the driver's own <c>Write-Step</c>/<c>Write-Ok</c> progress, which the caller forwards
/// to the job (plan §4.4).
/// </summary>
internal static class HyperVScript
{
    /// <summary>The backend id passed to the loader. Only <c>hyperv-local</c> exists today.</summary>
    public const string Backend = "hyperv-local";

    public static string CreateVm(
        string scriptsDir,
        VmDescriptor descriptor,
        string switchName,
        string? vhdPath)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var name = ArgumentGuard.VmName(descriptor.Name);
        var body = new StringBuilder();

        body.AppendLine("$descriptor = @{}");
        body.AppendLine($"$descriptor['Name'] = {PowerShellLiteral.Quote(name)}");
        body.AppendLine($"$descriptor['ProcessorCount'] = {ArgumentGuard.Positive(descriptor.Cpu, "cpu", 512)}");
        body.AppendLine($"$descriptor['MemoryGB'] = {ArgumentGuard.Positive(descriptor.RamGb, "ramGb", 4096)}");
        body.AppendLine($"$descriptor['DiskGB'] = {ArgumentGuard.Positive(descriptor.DiskGb, "diskGb", 65536)}");
        body.AppendLine(
            $"$descriptor['SwitchName'] = {PowerShellLiteral.Quote(ArgumentGuard.Text(switchName, "Constructd:SwitchName"))}");

        // Left out entirely when unset, so the driver applies Hyper-V's own default folder — exactly
        // what a local install gets.
        if (!string.IsNullOrWhiteSpace(vhdPath))
        {
            body.AppendLine(
                $"$descriptor['VhdPath'] = {PowerShellLiteral.Quote(ArgumentGuard.WindowsPath(vhdPath, "vhd path"))}");
        }

        if (!string.IsNullOrWhiteSpace(descriptor.IsoPath))
        {
            body.AppendLine(
                $"$descriptor['IsoPath'] = {PowerShellLiteral.Quote(ArgumentGuard.WindowsPath(descriptor.IsoPath, "iso path"))}");
        }

        body.AppendLine($"$descriptor['Nested'] = {PowerShellLiteral.Bool(descriptor.Nested)}");
        body.AppendLine(
            $"$descriptor['AutomaticCheckpoints'] = {PowerShellLiteral.Bool(descriptor.AutomaticCheckpoints)}");
        body.AppendLine("New-ConstructVm -Descriptor $descriptor");

        // docs/drivers.md §3.3: New-ConstructVm deliberately leaves the VM off, so the caller decides
        // when the unattended install starts. For the service that is right here.
        body.Append($"Start-ConstructVm -Name {PowerShellLiteral.Quote(name)}");

        return Build(scriptsDir, body.ToString());
    }

    public static string RemoveVm(string scriptsDir, string vmName) =>
        Call(scriptsDir, "Remove-ConstructVm", vmName);

    public static string StartVm(string scriptsDir, string vmName) =>
        Call(scriptsDir, "Start-ConstructVm", vmName);

    /// <summary>
    /// <c>-Force</c> so Hyper-V never asks: the service runs under the SCM with
    /// <c>-NonInteractive</c>, where a confirmation prompt is a hang, not a question.
    /// </summary>
    public static string StopVm(string scriptsDir, string vmName) =>
        Build(scriptsDir, $"Stop-ConstructVm -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))} -Force");

    public static string SaveVm(string scriptsDir, string vmName) =>
        Call(scriptsDir, "Save-ConstructVm", vmName);

    public static string DetachInstallMedia(string scriptsDir, string vmName) =>
        Call(scriptsDir, "Detach-ConstructInstallMedia", vmName);

    public static string GetState(string scriptsDir, string vmName) =>
        Build(scriptsDir, $"$result = [string](Get-ConstructVmState -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))})");

    public static string GetEndpoint(string scriptsDir, string vmName)
    {
        var name = PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName));

        // $$ raw string: {{…}} interpolates, so PowerShell's own braces need no escaping.
        return Build(
            scriptsDir,
            $$"""
              $endpoint = Get-ConstructVmEndpoint -Name {{name}}
              $result = @{ SshHost = [string]$endpoint.SshHost; SshPort = [int]$endpoint.SshPort }
              """);
    }

    public static string WaitReachable(string scriptsDir, string vmName, int timeoutSeconds)
    {
        var name = PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName));
        var seconds = ArgumentGuard.Positive(timeoutSeconds, "reachability timeout", 86400);
        return Build(scriptsDir, $"$result = [bool](Wait-ConstructVmReachable -Name {name} -TimeoutSeconds {seconds})");
    }

    public static string GetCapabilities(string scriptsDir) =>
        Build(
            scriptsDir,
            """
            $caps = Get-ConstructDriverCapabilities
            $result = @{ Checkpoints = [bool]$caps.Checkpoints; Suspend = [bool]$caps.Suspend; Console = [string]$caps.Console; Backend = [string]$caps.Backend }
            """);

    private static string Call(string scriptsDir, string function, string vmName) =>
        Build(scriptsDir, $"{function} -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))}");

    /// <summary>
    /// Wraps an operation in the envelope every driver call shares. The result JSON is the last stdout
    /// line; a failure inside the script is reported <em>in</em> that envelope rather than as a crash,
    /// so the caller always has one thing to parse.
    /// </summary>
    private static string Build(string scriptsDir, string body)
    {
        var root = ArgumentGuard.WindowsPath(scriptsDir, "Constructd:ScriptsDir").TrimEnd('\\', '/');
        var lib = PowerShellLiteral.Quote($@"{root}\lib\AgentVm.Common.ps1");
        var loader = PowerShellLiteral.Quote($@"{root}\drivers\Load-ConstructDriver.ps1");

        return $$"""
                 $ErrorActionPreference = 'Stop'
                 $ProgressPreference = 'SilentlyContinue'
                 $result = $null
                 try {
                     $constructLib = {{lib}}
                     if (-not (Test-Path -LiteralPath $constructLib)) { throw "Construct lib not found: $constructLib" }
                     . $constructLib
                     $constructLoader = {{loader}}
                     if (-not (Test-Path -LiteralPath $constructLoader)) { throw "Construct driver loader not found: $constructLoader" }
                     . $constructLoader -Backend '{{Backend}}'
                 {{Indent(body)}}
                     $envelope = [ordered]@{ ok = $true; value = $result }
                 } catch {
                     $envelope = [ordered]@{ ok = $false; error = [string]$_.Exception.Message }
                 }
                 ConvertTo-Json $envelope -Compress -Depth 6
                 """;
    }

    private static string Indent(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n');

        return string.Join(Environment.NewLine, lines.Select(line => line.Length == 0 ? line : "    " + line));
    }
}
