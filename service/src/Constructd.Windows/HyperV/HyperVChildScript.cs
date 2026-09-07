using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Windows.Internal;
namespace Constructd.Windows.HyperV;

internal static class HyperVChildScript
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    internal static readonly string[] SafeCodes = ["validation", "name-taken", "vm-not-off", "media-not-ready",
        "unsupported-capability", "secure-boot-template-locked", "artifact-ownership-unverified",
        "vm-incarnation-conflict", "disk-chain-invalid", "vm-identity-ambiguous", "vm-incarnation-changed", "storage-placement-unavailable"];

    // Descriptor data travels on stdin, never on argv or in dependency diagnostics.
    internal static string Build(string scriptsDir, string function, string arguments)
    {
        var root = ArgumentGuard.WindowsPath(scriptsDir, "ScriptsDir").TrimEnd('\\', '/');
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
                . {{PowerShellLiteral.Quote(root + @"\drivers\Load-ConstructDriver.ps1")}} -Backend 'hyperv-local' -Include ChildVm
                $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
                $result = {{function}} {{arguments}}
                $envelope = @{ ok = $true; value = $result }
            } catch {
                $envelope = @{ ok = $false }
                $code = [string]$_.Exception.Message
                if (@({{string.Join(",", SafeCodes.Select(PowerShellLiteral.Quote))}}) -ccontains $code) { $envelope.code = $code }
            }
            ConvertTo-Json -InputObject $envelope -Compress -Depth 16
            """;
    }
}
