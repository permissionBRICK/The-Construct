using Construct.Companion.Core.Vault;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

// Expires key vault leases and runs the VM scrubs they leave behind; brokers attach per VM.
internal sealed class VaultRuntimeService(VaultService vault) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => vault.RunAsync(stoppingToken);
}
