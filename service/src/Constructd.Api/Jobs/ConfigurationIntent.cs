using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed record ConfigurationIntent(ChildHardware Hardware, string? InstallId, string? AuxiliaryId, bool TemplateChanged, string Incarnation)
{
    public static bool Applies(OperationKeyRecord key, Vm vm)
    {
        if (key.Kind is not ("child-hardware" or "child-media") || !Ownership.SameName(key.Owner, vm.Owner) || !Ownership.SameName(key.Target, vm.Name)) return false;
        if (string.IsNullOrWhiteSpace(key.IntentJson)) return true;
        try
        {
            var intent = JsonSerializer.Deserialize<ConfigurationIntent>(key.IntentJson, ApiJson.Options);
            return string.IsNullOrEmpty(intent?.Incarnation) || intent.Incarnation == vm.Incarnation;
        }
        catch (JsonException) { return true; } // Corrupt intent is not permission to start.
    }
}
