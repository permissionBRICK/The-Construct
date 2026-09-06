using Constructd.Api.Hosting;
using Constructd.Core.Configuration;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Constructd.Tests.Api;

public sealed class ForwardReconciliationServiceTests
{
    [Fact]
    public void Forward_reconciliation_is_enabled_by_default_independently_of_idle_and_power()
    {
        Assert.Equal(30, new ConstructdOptions().ForwardReconcileSeconds);
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:ForwardReconcileSeconds"] = "30",
            ["Constructd:Idle:SchedulerEnabled"] = "false",
            ["Constructd:Power:KeepHostAwake"] = "false",
        });
        var services = app.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(services, service => service is ForwardReconciliationService);
        Assert.DoesNotContain(services, service => service is IdleSchedulerService);
    }

    [Fact]
    public void Periodic_passes_can_be_disabled_explicitly()
    {
        using var app = new TestApp();
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(),
            service => service is ForwardReconciliationService);
    }
}
