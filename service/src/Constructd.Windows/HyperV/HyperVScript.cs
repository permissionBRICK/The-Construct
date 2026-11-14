using System.Text;
using Constructd.Core.Domain;
using Constructd.Windows.Internal;

namespace Constructd.Windows.HyperV;

/// <summary>
/// Composes the PowerShell the Hyper-V driver runs.
///
/// The service does not call Hyper-V cmdlets from C#: every script dot-sources the driver's own
/// <c>hyperv\HyperV.Driver.ps1</c> (<see cref="HyperVScripts"/>), which ships inside the service
/// package, and calls the contract functions of <c>docs/drivers.md</c> there. Those functions were
/// moved unchanged from the client's driver (<c>drivers/hyperv-local</c>), so the service needs no
/// Construct checkout.
///
/// Every script has the same shape: dot-source the driver, run the operation into <c>$result</c>, and
/// print exactly one compressed JSON envelope as the last stdout line. Everything before it is the
/// driver's own <c>Write-Step</c>/<c>Write-Ok</c> progress, which the caller forwards to the job
/// (plan §4.4).
/// </summary>
internal static class HyperVScript
{
    public static string CreateVm(
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

        return Build(body.ToString());
    }

    public static string RemoveVm(string vmName) => Call("Remove-ConstructVm", vmName);

    public static string StartVm(string vmName) => Call("Start-ConstructVm", vmName);

    public static string SetMemory(string vmName, int ramGb) => Build(
        $"Set-ConstructVmMemory -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))} -MemoryGB {ArgumentGuard.Positive(ramGb, "ramGb", 1024)}");

    public static string GetNested(string vmName) => Build(
        $"$result = [bool](Get-ConstructVmNested -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))})");

    public static string SetNested(string vmName, bool enabled) => Build(
        $"Set-ConstructVmNested -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))} -Enabled {PowerShellLiteral.Bool(enabled)}");

    public static string SetCpuCount(string vmName, int cpus) => Build(
        $"Set-ConstructVmCpuCount -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))} -ProcessorCount {ArgumentGuard.Positive(cpus, "cpus", 64)}");

    /// <summary>
    /// <c>-Force</c> so Hyper-V never asks: the service runs under the SCM with
    /// <c>-NonInteractive</c>, where a confirmation prompt is a hang, not a question.
    /// </summary>
    public static string StopVm(string vmName) =>
        Build($"Stop-ConstructVm -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))} -Force");

    public static string SaveVm(string vmName) => Call("Save-ConstructVm", vmName);

    public static string DetachInstallMedia(string vmName) => Call("Detach-ConstructInstallMedia", vmName);

    public static string GetState(string vmName) =>
        Build($"$result = [string](Get-ConstructVmState -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))})");

    public static string GetEndpoint(string vmName)
    {
        var name = PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName));

        // $$ raw string: {{…}} interpolates, so PowerShell's own braces need no escaping.
        return Build(
            $$"""
              $endpoint = Get-ConstructVmEndpoint -Name {{name}}
              $result = @{ SshHost = [string]$endpoint.SshHost; SshPort = [int]$endpoint.SshPort }
              """);
    }

    public static string WaitReachable(string vmName, int timeoutSeconds)
    {
        var name = PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName));
        var seconds = ArgumentGuard.Positive(timeoutSeconds, "reachability timeout", 86400);
        return Build($"$result = [bool](Wait-ConstructVmReachable -Name {name} -TimeoutSeconds {seconds})");
    }

    public static string GetCapabilities() =>
        Build(
            """
            $caps = Get-ConstructDriverCapabilities
            $result = @{ Checkpoints = [bool]$caps.Checkpoints; Suspend = [bool]$caps.Suspend; Console = [string]$caps.Console; Backend = [string]$caps.Backend }
            """);

    /// <summary>Read-only facts an adoption needs (<c>Get-ConstructVmFacts</c>); null value: no such VM.</summary>
    public static string GetFacts(string vmName) =>
        Build($"$result = Get-ConstructVmFacts -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))}");

    private static string Call(string function, string vmName) =>
        Build($"{function} -Name {PowerShellLiteral.Quote(ArgumentGuard.VmName(vmName))}");

    /// <summary>
    /// Wraps an operation in the envelope every driver call shares. The result JSON is the last stdout
    /// line; a failure inside the script is reported <em>in</em> that envelope rather than as a crash,
    /// so the caller always has one thing to parse.
    /// </summary>
    private static string Build(string body)
    {
        var driver = PowerShellLiteral.Quote(HyperVScripts.PathOf(HyperVScripts.Driver));

        return $$"""
                 $ErrorActionPreference = 'Stop'
                 $ProgressPreference = 'SilentlyContinue'
                 $result = $null
                 try {
                     $constructDriver = {{driver}}
                     if (-not (Test-Path -LiteralPath $constructDriver)) { throw "Construct Hyper-V driver not found: $constructDriver" }
                     . $constructDriver
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
