using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Https;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Https;

/// <summary>
/// The HTTPS forward API (docs/https-forwards.md): an admin sets the host certificate, a VM asks for an HTTPS
/// forward with its own token, the advertised address follows the shared port and the per-VM names, the
/// capabilities tell a guest whether to ask, and opening a stopped VM's address starts it.
/// </summary>
public sealed class HttpsApiTests
{
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Constructd:ListenAddress"] = "127.0.0.1",
        ["Constructd:AppForwardPorts:Start"] = "42601",
        ["Constructd:AppForwardPorts:End"] = "42619",
    };

    private static async Task<(TestApp App, HttpClient Admin, HttpClient Vm)> SetupAsync(IDictionary<string, string?>? extra = null, bool allowHttps = true)
    {
        var settings = new Dictionary<string, string?>(Settings);
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>()) settings[key] = value;
        var app = new TestApp(settings);
        var admin = await app.CreateUserClientAsync("root-admin", role: Role.Admin);
        if (allowHttps)
        {
            var granted = await (await admin.PutJsonAsync("/api/v1/users/root-admin", new { allowHttpsForwards = true })).ReadAsync<UserDetailResponse>();
            Assert.True(granted.AllowHttpsForwards);
        }

        var job = await admin.CreateVmAsync("work-vm");
        return (app, admin, app.CreateVmTokenClient(job.VmToken()));
    }

    private static async Task<HttpsAvailabilityResult> AvailabilityAsync(HttpClient vm) =>
        await (await vm.GetAsync("/api/v1/vms/work-vm/https")).ReadAsync<HttpsAvailabilityResult>();

    [Fact]
    public async Task A_vm_is_offered_https_only_when_every_condition_holds()
    {
        var (app, admin, vm) = await SetupAsync(new Dictionary<string, string?> { ["Constructd:PublicHostPattern"] = "{name}.vms.test" }, allowHttps: false);
        using var _ = app;
        var store = app.Service<HttpsCertificateStore>();

        Assert.Equal("no-certificate", (await AvailabilityAsync(vm)).Reason);

        var other = TestCertificates.SelfSigned(["*.elsewhere.test"]);
        store.Import(other.ChainPem, other.KeyPem);
        Assert.Equal("certificate-name", (await AvailabilityAsync(vm)).Reason);

        var issued = TestCertificates.SelfSigned(["*.vms.test"]);
        store.Import(issued.ChainPem, issued.KeyPem);
        var notAllowed = await AvailabilityAsync(vm);
        Assert.Equal("not-allowed", notAllowed.Reason);
        var refused = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("https-unavailable", await refused.Content.ReadAsStringAsync());

        await admin.PutJsonAsync("/api/v1/users/root-admin", new { allowHttpsForwards = true });
        var available = await AvailabilityAsync(vm);
        Assert.True(available.Available);
        Assert.Null(available.MainAddress);

        var shared = FreePort();
        await admin.PutJsonAsync("/api/v1/host/config", new { https = new { sharedPort = shared, startOnRequest = true } });
        Assert.Equal($"https://work-vm.vms.test:{shared}/", (await AvailabilityAsync(vm)).MainAddress);

        await admin.PutJsonAsync("/api/v1/host/config", new { network = new { hostForwardsEnabled = false, directAddressReporting = true, defaultMode = "relayed" } });
        Assert.Equal("host-forwards-disabled", (await AvailabilityAsync(vm)).Reason);
    }

    private static object Pem(TestCertificates.Issued issued) => new { certificatePem = issued.ChainPem, privateKeyPem = issued.KeyPem };

    [Fact]
    public async Task Without_a_certificate_an_https_forward_is_refused_and_capabilities_say_so()
    {
        var (app, admin, vm) = await SetupAsync();
        using var _ = app;

        var refused = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("https-unavailable", await refused.Content.ReadAsStringAsync());

        var capabilities = JsonDocument.Parse(await (await vm.GetAsync("/api/v1/host/capabilities")).Content.ReadAsStringAsync());
        Assert.False(capabilities.RootElement.GetProperty("policy").GetProperty("https").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task An_admin_sets_and_removes_the_certificate_and_a_user_cannot()
    {
        var (app, admin, _) = await SetupAsync();
        using var _app = app;
        var issued = TestCertificates.SelfSigned(["*.vms.test", "buildbox.test"]);
        using var user = await app.CreateUserClientAsync("carol");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutJsonAsync("/api/v1/host/https/certificate", Pem(issued))).StatusCode);
        var bad = await admin.PutJsonAsync("/api/v1/host/https/certificate", new { certificatePem = issued.ChainPem, privateKeyPem = TestCertificates.SelfSigned(["x.test"]).KeyPem });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("invalid-certificate", await bad.Content.ReadAsStringAsync());

        var set = await admin.PutJsonAsync("/api/v1/host/https/certificate", Pem(issued));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var status = JsonDocument.Parse(await set.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(issued.Sha256, status.GetProperty("certificate").GetProperty("sha256").GetString());
        Assert.True(status.GetProperty("names").GetProperty("publicHostCovered").GetBoolean());
        Assert.DoesNotContain("PRIVATE KEY", await set.Content.ReadAsStringAsync());

        var removed = JsonDocument.Parse(await (await admin.DeleteAsync("/api/v1/host/https/certificate")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("certificate").ValueKind);
        Assert.Null(app.Service<HttpsCertificateStore>().Current);
    }

    [Fact]
    public async Task Https_forwards_are_host_forwards_with_an_https_address()
    {
        var (app, admin, vm) = await SetupAsync();
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["buildbox.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);

        var created = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true, upstreamTls = true, label = "T3" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var forward = await created.ReadAsync<ForwardResponse>();

        Assert.True(forward.Https);
        Assert.Equal(ForwardTarget.Host, forward.Target);
        Assert.Equal(true, forward.UpstreamTls);
        Assert.Equal($"https://buildbox.test:{forward.PublicPort}/", forward.Url);
        Assert.StartsWith("https:", app.Forwards.Materialized[forward.Id]);

        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 80, https = true, target = "client" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 80, main = true, target = "host" })).StatusCode);

        var listed = await (await vm.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>();
        Assert.Contains(listed, f => f.Id == forward.Id && f.Https && f.Url == forward.Url);
    }

    [Fact]
    public async Task A_main_forward_is_advertised_on_the_shared_port_under_the_vms_own_name()
    {
        var (app, admin, vm) = await SetupAsync(new Dictionary<string, string?> { ["Constructd:PublicHostPattern"] = "{name}.vms.test" });
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["*.vms.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);
        var shared = FreePort();

        var conflict = await admin.PutJsonAsync("/api/v1/host/config", new { https = new { sharedPort = 2250, startOnRequest = true } });
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/host/config", new { https = new { sharedPort = shared, startOnRequest = true } })).StatusCode);

        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true, main = true })).ReadAsync<ForwardResponse>();

        Assert.True(forward.Main);
        Assert.Equal($"https://work-vm.vms.test:{shared}/", forward.Url);
        Assert.Equal($"https://work-vm.vms.test:{forward.PublicPort}/", forward.PortUrl);

        var capabilities = JsonDocument.Parse(await (await vm.GetAsync("/api/v1/host/capabilities")).Content.ReadAsStringAsync()).RootElement
            .GetProperty("policy").GetProperty("https");
        Assert.True(capabilities.GetProperty("available").GetBoolean());
        Assert.Equal(shared, capabilities.GetProperty("sharedPort").GetInt32());
        Assert.True(capabilities.GetProperty("mainAddress").GetBoolean());

        var status = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/host/https")).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(status.GetProperty("listeners").EnumerateArray(), l => l.GetProperty("kind").GetString() == "shared" && l.GetProperty("listening").GetBoolean());
    }

    [Fact]
    public void The_default_https_port_is_left_out_of_a_main_address()
    {
        var forward = new PortForward("id", "work-vm", 5178, 2301, ForwardTarget.Host, "T3", DateTimeOffset.UtcNow, Https: true, Main: true);

        Assert.Equal("https://work-vm.vms.test/", ForwardResponse.From(forward, "work-vm.vms.test", 443).Url);
        Assert.Equal("https://work-vm.vms.test:2301/", ForwardResponse.From(forward, "work-vm.vms.test", null).Url);
        Assert.Equal("https://work-vm.vms.test:2301/", ForwardResponse.From(forward with { Main = false }, "work-vm.vms.test", 443).Url);
    }

    [Fact]
    public async Task Opening_a_saved_vms_address_starts_it_and_shows_a_waiting_page()
    {
        var (app, admin, vm) = await SetupAsync();
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["buildbox.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);
        app.Driver.EndpointHostSuffix = ".invalid";
        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true })).ReadAsync<ForwardResponse>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "save" })).StatusCode);
        Assert.Equal(VmState.Saved, app.Driver.StateOf("work-vm"));

        // The in-memory forward manager only records forwards; open this one on the real front.
        var stored = (await app.Service<IForwardStore>().GetAsync(forward.Id, CancellationToken.None))!;
        var gateway = app.Service<HttpsGateway>();
        gateway.Ensure(stored);
        try
        {
            var response = await GetAsync(forward.PublicPort!.Value);
            Assert.Contains("503 Service Unavailable", response);
            Assert.Contains("Starting work-vm", response);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (app.Driver.StateOf("work-vm") != VmState.Running)
            {
                Assert.True(DateTime.UtcNow < deadline, "the VM was not started");
                await Task.Delay(50);
            }

            var audit = app.Service<IAuditLog>();
            var deadlineAudit = DateTime.UtcNow.AddSeconds(5);
            while (!(await audit.QueryAsync(50, CancellationToken.None)).Any(e => e.Action == "vm.power" && e.Detail!.Contains("https-start-on-request")))
            {
                Assert.True(DateTime.UtcNow < deadlineAudit, "no audit entry");
                await Task.Delay(50);
            }
        }
        finally
        {
            await gateway.StopAsync(forward.Id);
        }
    }

    private static async Task<string> GetAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("buildbox.test");
        await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: buildbox.test\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(tls);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
