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

        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 80, https = true, upstreamTls = false, target = "client" })).StatusCode);
        var unstated = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 80, https = true });
        Assert.Equal(HttpStatusCode.BadRequest, unstated.StatusCode);
        Assert.Contains("upstreamTls", await unstated.Content.ReadAsStringAsync());
        var plain = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 8080, https = true, upstreamTls = false })).ReadAsync<ForwardResponse>();
        Assert.Equal(false, plain.UpstreamTls);
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

        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true, upstreamTls = false, main = true })).ReadAsync<ForwardResponse>();

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

    private static async Task<(TestApp App, HttpClient Admin, HttpClient Vm, int Shared)> SharedSetupAsync(string[]? certificateNames = null)
    {
        var (app, admin, vm) = await SetupAsync(new Dictionary<string, string?> { ["Constructd:PublicHostPattern"] = "{name}.vms.test" });
        var issued = TestCertificates.SelfSigned(certificateNames ?? ["*.vms.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);
        var shared = FreePort();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/host/config", new { https = new { sharedPort = shared, startOnRequest = true } })).StatusCode);
        return (app, admin, vm, shared);
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";

    [Fact]
    public async Task Every_https_forward_is_advertised_under_its_own_name_on_the_shared_port()
    {
        var (app, _, vm, shared) = await SharedSetupAsync();
        using var _ = app;

        var named = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, name = "App" })).ReadAsync<ForwardResponse>();
        Assert.Equal("app", named.Name);
        Assert.Equal($"https://work-vm-app.vms.test:{shared}/", named.Url);
        Assert.Equal($"https://work-vm.vms.test:{named.PublicPort}/", named.PortUrl);

        var byPort = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 8080, https = true, upstreamTls = false })).ReadAsync<ForwardResponse>();
        Assert.Equal("8080", byPort.Name);
        Assert.Equal($"https://work-vm-8080.vms.test:{shared}/", byPort.Url);

        var listed = await (await vm.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>();
        Assert.Contains(listed, f => f.Id == named.Id && f.Url == named.Url);
        Assert.Contains(listed, f => f.Id == byPort.Id && f.Url == byPort.Url);

        var availability = await AvailabilityAsync(vm);
        Assert.Equal($"https://work-vm-{{name}}.vms.test:{shared}/", availability.NamedAddress);
    }

    [Fact]
    public async Task A_forward_name_is_a_dns_label_unique_on_the_host_and_never_a_vms()
    {
        var (app, admin, vm, _) = await SharedSetupAsync();
        using var _ = app;
        Assert.Equal(HttpStatusCode.Created, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, name = "app" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5174, https = true, upstreamTls = false, name = "my_app" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5174, https = true, upstreamTls = false, name = new string('n', 60) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5174, target = "host", name = "plain" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5174, https = true, upstreamTls = false, name = " " })).StatusCode);

        var again = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5174, https = true, upstreamTls = false, name = "app" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("https-name-taken", await CodeOfAsync(again));
        // A DEFAULT name is never refused: a second forward of the same port is created, and only the older one
        // gets the name; the newer one keeps its port address.
        var first = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false })).ReadAsync<ForwardResponse>();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondForward = await second.ReadAsync<ForwardResponse>();
        Assert.Contains("work-vm-5173.vms.test", first.Url);
        Assert.Equal($"https://work-vm.vms.test:{secondForward.PublicPort}/", secondForward.Url);

        // work + "vm-app" would be work-vm-app, and work-vm + "db" is the VM work-vm-db.
        var other = app.CreateVmTokenClient((await admin.CreateVmAsync("work")).VmToken());
        var clash = await other.PostJsonAsync("/api/v1/vms/work/forwards", new { vmPort = 5173, https = true, upstreamTls = false, name = "vm-app" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("https-name-taken", await CodeOfAsync(clash));
        Assert.Contains("work-vm", await clash.Content.ReadAsStringAsync());
        await admin.CreateVmAsync("work-vm-db");
        var vmName = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5432, https = true, upstreamTls = false, name = "db" });
        Assert.Equal(HttpStatusCode.Conflict, vmName.StatusCode);
        Assert.Equal("https-name-taken", await CodeOfAsync(vmName));
        Assert.Contains("the VM", await vmName.Content.ReadAsStringAsync());

        // ...and never the key vault's address.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/host/config", new { vault = new { webUrl = "https://work-vm-vault.vms.test" } })).StatusCode);
        var vault = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 9000, https = true, upstreamTls = false, name = "vault" });
        Assert.Equal(HttpStatusCode.Conflict, vault.StatusCode);
        Assert.Contains("key vault", await vault.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_default_name_that_does_not_fit_one_label_keeps_the_port_address()
    {
        var (app, admin, _, _) = await SharedSetupAsync();
        using var _ = app;
        var longName = "long-" + new string('v', 55); // 60 characters: <vm>-5178 would be 65
        var vm = app.CreateVmTokenClient((await admin.CreateVmAsync(longName)).VmToken());

        var created = await vm.PostJsonAsync($"/api/v1/vms/{longName}/forwards", new { vmPort = 5178, https = true, upstreamTls = false, main = true });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.StartsWith($"https://{longName}.vms.test:", (await created.ReadAsync<ForwardResponse>()).Url);
    }

    [Fact]
    public async Task A_shared_port_that_is_not_listening_is_never_advertised()
    {
        var (app, admin, vm) = await SetupAsync(new Dictionary<string, string?> { ["Constructd:PublicHostPattern"] = "{name}.vms.test" });
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["*.vms.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);
        // Held for the whole test, so the shared listener cannot bind it (FreePort's range rule applies here too).
        using var squatter = new TcpListener(IPAddress.Any, FreePort());
        squatter.Start();
        var busy = ((IPEndPoint)squatter.LocalEndpoint).Port;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/host/config", new { https = new { sharedPort = busy, startOnRequest = true } })).StatusCode);

        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, main = true })).ReadAsync<ForwardResponse>();

        Assert.Equal($"https://work-vm.vms.test:{forward.PublicPort}/", forward.Url);
        var availability = await AvailabilityAsync(vm);
        Assert.Null(availability.MainAddress);
        Assert.Null(availability.NamedAddress);
    }

    [Fact]
    public async Task Without_a_shared_port_forwards_of_one_port_are_not_refused_for_their_name()
    {
        var (app, _, vm) = await SetupAsync();
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["buildbox.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);

        Assert.Equal(HttpStatusCode.Created, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false })).StatusCode);
    }

    [Fact]
    public async Task A_second_main_forward_is_refused_unless_it_replaces_the_first()
    {
        var (app, _, vm, shared) = await SharedSetupAsync();
        using var _ = app;
        var t3 = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true, upstreamTls = false, main = true, label = "T3" })).ReadAsync<ForwardResponse>();
        Assert.Equal($"https://work-vm.vms.test:{shared}/", t3.Url);

        var taken = await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, main = true });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("main-taken", await CodeOfAsync(taken));
        Assert.Contains("'T3'", await taken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, replaceMain = true })).StatusCode);

        app.Clock.Advance(TimeSpan.FromSeconds(1)); // "newest" is by creation time; the test clock stands still otherwise
        var replaced = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, main = true, replaceMain = true })).ReadAsync<ForwardResponse>();
        Assert.Equal($"https://work-vm.vms.test:{shared}/", replaced.Url);
        // The older main forward keeps its own name, and gets the VM's back once the newer one is closed.
        var listed = await (await vm.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>();
        Assert.Equal($"https://work-vm-5178.vms.test:{shared}/", listed.Single(f => f.Id == t3.Id).Url);
        Assert.Equal(HttpStatusCode.NoContent, (await vm.DeleteAsync($"/api/v1/vms/work-vm/forwards/{replaced.Id}")).StatusCode);
        listed = await (await vm.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>();
        Assert.Equal($"https://work-vm.vms.test:{shared}/", listed.Single(f => f.Id == t3.Id).Url);
    }

    [Fact]
    public async Task A_name_the_certificate_does_not_cover_keeps_the_forwards_port_address()
    {
        var (app, _, vm, _) = await SharedSetupAsync(["work-vm.vms.test"]);
        using var _ = app;

        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5173, https = true, upstreamTls = false, name = "app" })).ReadAsync<ForwardResponse>();

        Assert.Equal($"https://work-vm.vms.test:{forward.PublicPort}/", forward.Url);
        Assert.Null(forward.PortUrl);
        Assert.Null((await AvailabilityAsync(vm)).NamedAddress);
    }

    [Fact]
    public void The_default_https_port_is_left_out_of_a_main_address()
    {
        var forward = new PortForward("id", "work-vm", 5178, 2301, ForwardTarget.Host, "T3", DateTimeOffset.UtcNow, Https: true, Main: true);

        Assert.Equal("https://work-vm.vms.test/", ForwardResponse.From(forward, "work-vm.vms.test", 443, "work-vm.vms.test").Url);
        Assert.Equal("https://work-vm.vms.test:8443/", ForwardResponse.From(forward, "work-vm.vms.test", 8443, "work-vm.vms.test").Url);
        Assert.Equal("https://work-vm.vms.test:2301/", ForwardResponse.From(forward, "work-vm.vms.test", null, "work-vm.vms.test").Url);
        Assert.Equal("https://work-vm.vms.test:2301/", ForwardResponse.From(forward, "work-vm.vms.test", 443, null).Url);
    }

    [Fact]
    public async Task Opening_a_saved_vms_address_starts_it_and_shows_a_waiting_page()
    {
        var (app, admin, vm) = await SetupAsync();
        using var _ = app;
        var issued = TestCertificates.SelfSigned(["buildbox.test"]);
        app.Service<HttpsCertificateStore>().Import(issued.ChainPem, issued.KeyPem);
        app.Driver.EndpointHostSuffix = ".invalid";
        var forward = await (await vm.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 5178, https = true, upstreamTls = false })).ReadAsync<ForwardResponse>();
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

    /// <summary>
    /// A free port the host config accepts as the shared port: the OS picks from the ephemeral range, which contains
    /// this class's app forward range (42601-42619), and the config refuses a port inside a forward range.
    /// </summary>
    private static int FreePort()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port is < 42601 or > 42619 && port != 7462) return port;
        }
    }
}
