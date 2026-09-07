using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Windows.Internal;
namespace Constructd.Windows.HyperV;

internal static class HyperVChildScript
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

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
            }
            ConvertTo-Json -InputObject $envelope -Compress -Depth 16
            """;
    }
}
