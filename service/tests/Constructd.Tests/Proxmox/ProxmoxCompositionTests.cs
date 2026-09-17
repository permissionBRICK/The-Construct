using System.Net;
using System.Net.Http.Json;
using Constructd.Core.Abstractions;
using Constructd.Proxmox;
using Constructd.Tests.Support;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// <c>Constructd:Backend=proxmox</c> composes the real host on Linux: the Proxmox driver, the seed
/// builder and the relay are what the container hands out, the unsupported features are not
/// advertised, and a token-authenticated client is served — the same API a Windows host offers.
/// </summary>
public sealed class ProxmoxCompositionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-pve-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task The_host_composes_the_proxmox_platform_and_advertises_only_what_it_has()
    {
        using var app = new TestApp(Settings());

        Assert.IsType<ProxmoxDriver>(app.Service<IHypervisorDriver>());
        Assert.IsType<CloudInitSeedBuilder>(app.Service<IIsoBuilder>());
        Assert.IsType<TcpRelayPortForwardManager>(app.Service<IPortForwardManager>());
        Assert.IsType<ProxmoxChildVmPlatform>(app.Service<IChildVmDriver>());
        Assert.IsType<ProxmoxInventory>(app.Service<IHypervisorInventory>());

        var features = app.Service<IReleaseInfo>().ApiFeatures;
        Assert.Contains("host-admin", features);
        Assert.Contains("primary-cpu", features);
        Assert.Contains("network-mode", features);
        Assert.DoesNotContain("network-mode", new Constructd.Api.Composition.ReleaseInfo().ApiFeatures);
        Assert.DoesNotContain("children", features);
        Assert.DoesNotContain("console", features);
        Assert.DoesNotContain("updates", features);

        var client = await app.CreateUserClientAsync("alice");
        var whoami = await client.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);

        var health = await client.GetFromJsonAsync<HealthBody>("/api/v1/health");
        Assert.NotNull(health);
        Assert.DoesNotContain("children", health!.ApiFeatures);
    }

    [Fact]
    public void A_misconfigured_proxmox_host_refuses_to_start_with_a_reason()
    {
        var settings = Settings();
        settings["Constructd:Proxmox:ImageVolume"] = "local:iso/not-an-import.iso";
        using var app = new TestApp(settings);

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateAnonymousClient());
        Assert.Contains("ImageVolume", Flatten(ex), StringComparison.Ordinal);
    }

    private Dictionary<string, string?> Settings()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        Directory.CreateDirectory(Path.Combine(_dir, "snippets"));
        File.WriteAllText(Path.Combine(_dir, "bin", "provision.sh"), "#!/bin/bash\n");
        File.WriteAllText(Path.Combine(_dir, "keys", "bootstrap_ed25519.pub"),
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEQKj5fCzJY2Rvk8d7dSfKgrW08S2kkxAHKDqmCKnkvR bootstrap@construct\n");

        return new Dictionary<string, string?>
        {
            ["Constructd:Fake"] = "false",
            ["Constructd:Backend"] = "proxmox",
            // SQLite, as installed: the durable stores couple to the SQLite capacity ledger, which
            // the memory persistence would not exercise.
            ["Constructd:Persistence"] = "Sqlite",
            ["Constructd:ListenAddress"] = "127.0.0.1",
            ["Constructd:SshForwardPorts:Start"] = "42301",
            ["Constructd:SshForwardPorts:End"] = "42309",
            ["Constructd:AppForwardPorts:Start"] = "42401",
            ["Constructd:AppForwardPorts:End"] = "42409",
            ["Constructd:ScriptsDir"] = _dir,
            ["Constructd:Iso:BootstrapPublicKeyPath"] = Path.Combine(_dir, "keys", "bootstrap_ed25519.pub"),
            ["Constructd:Iso:CacheDir"] = Path.Combine(_dir, "iso"),
            ["Constructd:HostAdmin:Media:RootDir"] = Path.Combine(_dir, "media"),
            ["Constructd:HostAdmin:Source:RootDir"] = Path.Combine(_dir, "source"),
            ["Constructd:DatabasePath"] = Path.Combine(_dir, "constructd.db"),
            ["Constructd:Proxmox:Node"] = "pve1",
            ["Constructd:Proxmox:SnippetDir"] = Path.Combine(_dir, "snippets"),
            ["Constructd:Idle:SchedulerEnabled"] = "false",
            ["Constructd:ForwardReconcileSeconds"] = "0",
        };
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed record HealthBody(string Status, IReadOnlyList<string> ApiFeatures);
}
