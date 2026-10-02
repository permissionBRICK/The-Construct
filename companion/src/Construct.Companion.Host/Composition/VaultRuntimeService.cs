using Construct.Companion.Core.Vault;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

// Expires key vault leases and runs the VM scrubs they leave behind (brokers attach per local VM), and
// syncs with the hosts and polls their approvals for the hosted VMs.
internal sealed class VaultRuntimeService(VaultService vault, VaultHosts hosts) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(vault.RunAsync(stoppingToken), hosts.RunAsync(stoppingToken));
}
