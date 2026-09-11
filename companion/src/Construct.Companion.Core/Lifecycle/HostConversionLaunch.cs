using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Lifecycle;

public static class HostConversionLaunch
{
    private const string EncodingPrefix = "$u=New-Object Text.UTF8Encoding($false); [Console]::OutputEncoding=$u; [Console]::InputEncoding=$u; ";
    public static bool ValidHost(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?\z", RegexOptions.IgnoreCase) && !value.Contains("..", StringComparison.Ordinal);
    public static ProcessInvocation MachineIdentity() => new("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", PowerShellLaunch.Encode(EncodingPrefix + "$ErrorActionPreference='Stop'; $fqdn=$env:COMPUTERNAME; try { $fqdn=[Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName } catch {}; $lan=Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1; @{adminUser=[Security.Principal.WindowsIdentity]::GetCurrent().Name;hostName=$fqdn;ip=($lan.IPv4Address.IPAddress | Select-Object -First 1)} | ConvertTo-Json -Compress")], Timeout: TimeSpan.FromSeconds(30));
    public static string Script(JsonObject plan)
    {
        var path = Path.Combine(StateJson.String(plan["scriptsDir"]), "service", "host", "ConvertTo-ConstructHost.ps1");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        var inner = PowerShellLaunch.Encode("& " + PowerShellLaunch.SingleQuote(path) + " -PlanB64 '" + b64 + "'; exit $LASTEXITCODE");
        return "$ErrorActionPreference='Stop'; try { $p=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + inner + "'; exit $p.ExitCode } catch { Write-Error 'Host setup could not start. The UAC request may have been cancelled.'; exit 1 }";
    }
    public static ProcessInvocation Build(JsonObject plan)
    {
        // Preserve the extension's visible RunAs child. A detached caller also needs
        // a result on UAC rejection; only write one when the installer wrote none.
        var result = PowerShellLaunch.SingleQuote(StateJson.String(plan["resultPath"]));
        var command = Script(plan).Replace("Write-Error 'Host setup could not start.",
            "if (-not (Test-Path -LiteralPath " + result + ")) { @{ok=$false;error='Host setup could not start. The UAC request may have been cancelled.'} | ConvertTo-Json -Compress | Set-Content -LiteralPath " + result + " -Encoding UTF8 }; Write-Error 'Host setup could not start.", StringComparison.Ordinal);
        command = command.Replace("exit $p.ExitCode", "if (-not (Test-Path -LiteralPath " + result + ")) { @{ok=$false;error='Host setup ended without a result. See the installer console for details.'} | ConvertTo-Json -Compress | Set-Content -LiteralPath " + result + " -Encoding UTF8 }; exit $p.ExitCode", StringComparison.Ordinal);
        return new("cmd.exe", ["/c", "start", "", "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", PowerShellLaunch.Encode(EncodingPrefix + command)], StateJson.String(plan["scriptsDir"]));
    }
}
