using System.Net;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// With <c>Constructd:Negotiate:Enabled = true</c> the host registers the Negotiate scheme off
/// Windows too — what a PC's <c>Invoke-WebRequest -UseDefaultCredentials</c> answers with a Kerberos
/// ticket — while bearer tokens keep working alongside. Without the flag a Linux host has no such
/// scheme and challenges for a token only. The scheme table is what can be asserted in-process: the
/// Negotiate handler needs Kestrel's connection features, which the test server does not provide,
/// so the real challenge is checked against the installed host (docs/proxmox-host.md §5b).
/// </summary>
public sealed class NegotiateOnLinuxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-nego-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Enabled_negotiate_registers_the_scheme_off_windows()
    {
        // No request is made: the Negotiate handler inspects every request and refuses the test
        // server outright ("requires a server that supports IConnectionItemsFeature like Kestrel").
        using var app = new TestApp(Settings(negotiate: true));

        var schemes = app.Service<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(NegotiateDefaults.AuthenticationScheme));
        Assert.NotNull(await schemes.GetSchemeAsync(Constructd.Api.Auth.ConstructdSchemes.Bearer));
    }

    [Fact]
    public async Task Without_the_flag_a_linux_host_registers_no_negotiate_scheme()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var app = new TestApp(Settings(negotiate: false));

        var anonymous = await app.CreateAnonymousClient().GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.DoesNotContain(anonymous.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");

        var schemes = app.Service<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync(NegotiateDefaults.AuthenticationScheme));
    }

    private Dictionary<string, string?> Settings(bool negotiate)
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
            ["Constructd:Persistence"] = "Memory",
            ["Constructd:ListenAddress"] = "127.0.0.1",
            ["Constructd:SshForwardPorts:Start"] = "42501",
            ["Constructd:SshForwardPorts:End"] = "42509",
            ["Constructd:AppForwardPorts:Start"] = "42601",
            ["Constructd:AppForwardPorts:End"] = "42609",
            ["Constructd:ScriptsDir"] = _dir,
            ["Constructd:Iso:BootstrapPublicKeyPath"] = Path.Combine(_dir, "keys", "bootstrap_ed25519.pub"),
            ["Constructd:Iso:CacheDir"] = Path.Combine(_dir, "iso"),
            ["Constructd:HostAdmin:Media:RootDir"] = Path.Combine(_dir, "media"),
            ["Constructd:HostAdmin:Source:RootDir"] = Path.Combine(_dir, "source"),
            ["Constructd:DatabasePath"] = Path.Combine(_dir, "constructd.db"),
            ["Constructd:Proxmox:Node"] = "pve1",
            ["Constructd:Proxmox:SnippetDir"] = Path.Combine(_dir, "snippets"),
            ["Constructd:Negotiate:Enabled"] = negotiate ? "true" : "false",
            ["Constructd:Negotiate:DomainName"] = "HOME",
            ["Constructd:Idle:SchedulerEnabled"] = "false",
            ["Constructd:ForwardReconcileSeconds"] = "0",
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
