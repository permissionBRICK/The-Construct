using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Services;
using Constructd.Windows.HyperV;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Windows;

/// <summary>
/// The Hyper-V platform composes with no Construct checkout configured: the driver's PowerShell ships
/// in the service package (<c>hyperv\</c> next to the executable, which is this test's output directory
/// too), so <c>Constructd:ScriptsDir</c> is no longer required. Composed through
/// <see cref="ServiceComposition.AddHyperVPlatform"/>, the registrations behind the Windows check.
/// </summary>
public sealed class WindowsCompositionTests
{
    [Fact]
    public void The_hyperv_platform_composes_without_a_scripts_directory()
    {
        var options = PlatformOptions.Create(o => o.ScriptsDir = "");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();

        services.AddHyperVPlatform(options);

        using var provider = services.BuildServiceProvider();
        var driver = Assert.IsType<HyperVDriver>(provider.GetRequiredService<IHypervisorDriver>());
        Assert.Same(driver, provider.GetRequiredService<IVmFactsDriver>());
        Assert.Same(driver, provider.GetRequiredService<IVmCpuDriver>());
        Assert.NotNull(provider.GetRequiredService<IIsoBuilder>());
        Assert.NotNull(provider.GetRequiredService<IIsoMediaBuilder>());
    }

    [Fact]
    public void The_driver_scripts_are_copied_next_to_the_service()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "hyperv"), HyperVScripts.Directory);
        foreach (var file in HyperVScripts.All)
            Assert.True(File.Exists(HyperVScripts.PathOf(file)), $"{file} is not in the build output.");
    }
}
