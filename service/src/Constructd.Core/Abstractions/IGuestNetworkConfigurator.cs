namespace Constructd.Core.Abstractions;

/// <summary>Updates cloud-init networking on a fully stopped guest.</summary>
public interface IGuestNetworkConfigurator
{
    Task ConfigureNetworkAsync(string name, string? address, string? gateway, IReadOnlyList<string>? dns, CancellationToken ct);
}
