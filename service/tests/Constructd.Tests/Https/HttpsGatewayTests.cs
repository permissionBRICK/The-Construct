using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Constructd.Api.Https;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Proxmox;
using Constructd.Windows.Forwards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Https;

/// <summary>
/// The HTTPS front over real loopback sockets: TLS is terminated with the host's certificate, the byte stream
/// (Host header included) reaches the guest unchanged, the guest port is spoken to the way the forward says, the shared port
/// routes by server name to a VM's main forward and to the vault relay, a guest that does not answer gets a
/// page that reloads itself, and a renewed certificate is served without a restart.
/// </summary>
public sealed class HttpsGatewayTests : IAsyncDisposable
{
    private const string Pattern = "{name}.vms.test";
    private readonly string _directory = TestCertificates.TempDirectory();
    private readonly MutableClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };
    private readonly InMemoryVmRepository _vms = new();
    private readonly InMemoryForwardStore _store = new();
    private readonly InMemoryHostConfigStore _config;
    private readonly ConstructdOptions _options;
    private readonly HttpsCertificateStore _certificates;
    private readonly StaticDriver _driver = new();
    private readonly HttpsGateway _gateway;
    private readonly TcpRelayPortForwardManager _manager;
    private readonly TestCertificates.Issued _certificate = TestCertificates.SelfSigned(["*.vms.test", "vault.test"]);

    public HttpsGatewayTests()
    {
        _config = new InMemoryHostConfigStore(_clock);
        _options = new ConstructdOptions
        {
            ListenAddress = "127.0.0.1",
            PublicHost = "buildbox.test",
            PublicHostPattern = Pattern,
            SshForwardPorts = new PortRangeOptions(42501, 42509),
            AppForwardPorts = new PortRangeOptions(42511, 42559), // wide: ports a test just closed sit in TIME-WAIT and do not bind
            Https = { CertificateDirectory = _directory },
        };
        _certificates = new HttpsCertificateStore(_options, _clock, NullLogger<HttpsCertificateStore>.Instance);
        _certificates.Import(_certificate.ChainPem, _certificate.KeyPem);
        _gateway = new HttpsGateway(_store, _vms, _driver, _certificates, _config, _options, _clock,
            new ServiceCollection().BuildServiceProvider(), NullLogger<HttpsGateway>.Instance, new DnsHostAddressResolver());
        _manager = new TcpRelayPortForwardManager(_clock, _vms, _store, _driver, new DnsHostAddressResolver(), _options,
            NullLogger<TcpRelayPortForwardManager>.Instance, _gateway);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        await _gateway.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task An_https_forward_terminates_tls_and_relays_the_request_unchanged()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);

        var (served, response) = await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", $"work-vm.vms.test:{forward.PublicPort}");

        Assert.Equal(_certificate.Sha256, served);
        Assert.Contains("200 OK", response);
        Assert.Contains($"host=work-vm.vms.test:{forward.PublicPort}", response);
        Assert.Contains("tls=False", response);
        Assert.Equal(1, (await _store.ListAsync("work-vm", CancellationToken.None)).Count(f => f.Https));
    }

    [Fact]
    public async Task A_forward_that_says_its_port_speaks_tls_connects_with_tls()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest(TestCertificates.SelfSigned(["guest.internal"]));
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port, upstreamTls: true);

        var (_, response) = await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", "work-vm.vms.test");

        Assert.Contains("200 OK", response);
        Assert.Contains("tls=True", response);
        Assert.Equal(1, guest.Accepted);
    }

    [Fact]
    public async Task A_plain_forward_never_tries_tls_first()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);

        for (var i = 0; i < 2; i++)
            Assert.Contains("200 OK", (await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", "work-vm.vms.test")).Response);

        Assert.Equal(2, guest.Accepted);
        Assert.Equal(0, guest.TlsHellos);
    }

    [Fact]
    public async Task A_tls_forward_whose_port_serves_plain_http_says_so()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port, upstreamTls: true);

        var (_, response) = await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", "work-vm.vms.test");

        Assert.Contains("503 Service Unavailable", response);
        Assert.Contains("but not with TLS", response);
        Assert.Contains("--upstream-plain", response);
    }

    [Fact]
    public async Task The_connection_counts_towards_the_vm_while_it_is_open()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forward.PublicPort!.Value);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("work-vm.vms.test");
        await WaitForAsync(async () => await _manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None) == 1);
        client.Close();
        await WaitForAsync(async () => await _manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None) == 0);
    }

    [Fact]
    public async Task A_guest_that_does_not_answer_gets_a_page_that_reloads_itself()
    {
        await AddVmAsync("work-vm");
        var forward = await AddHttpsForwardAsync("work-vm", ClosedPort());

        var (_, response) = await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", "work-vm.vms.test");

        Assert.Contains("503 Service Unavailable", response);
        Assert.Contains("http-equiv=\"refresh\"", response);
        Assert.Contains("work-vm is running, but nothing answers on port", response);
    }

    [Fact]
    public async Task The_shared_port_routes_by_server_name_to_the_main_forward_and_the_vault()
    {
        await AddVmAsync("work-vm");
        await AddVmAsync("other-vm");
        using var main = new HttpGuest("main");
        using var side = new HttpGuest("side");
        using var vault = new HttpGuest("vault");
        await AddHttpsForwardAsync("work-vm", side.Port);
        await AddHttpsForwardAsync("work-vm", main.Port, main: true);
        await AddHttpsForwardAsync("other-vm", side.Port);
        var shared = FreePort();
        await _config.SetAsync("https", new HttpsConfig(shared), "test", CancellationToken.None);
        await _config.SetAsync("vault", new VaultConfig("https://vault.test"), "test", CancellationToken.None);
        _gateway.VaultRelayPort = () => vault.Port;
        await _gateway.ReconcileSharedAsync(CancellationToken.None);

        Assert.Contains("guest=main", (await RequestAsync(shared, "work-vm.vms.test", "work-vm.vms.test")).Response);
        Assert.Contains("guest=vault", (await RequestAsync(shared, "vault.test", "vault.test")).Response);
        // other-vm has no main forward, and an unknown name gets nothing.
        Assert.Contains("404 Not Found", (await RequestAsync(shared, "other-vm.vms.test", "other-vm.vms.test")).Response);
        Assert.Contains("404 Not Found", (await RequestAsync(shared, "nobody.vms.test", "nobody.vms.test")).Response);
        Assert.Contains(_gateway.Listeners(), l => l.Kind == "shared" && l.Port == shared && l.Listening);

        await _config.SetAsync("https", new HttpsConfig(null), "test", CancellationToken.None);
        await _gateway.ReconcileSharedAsync(CancellationToken.None);
        await AssertRefusedAsync(shared);
    }

    [Fact]
    public async Task The_shared_port_reaches_every_https_forward_under_its_own_name()
    {
        await AddVmAsync("work-vm");
        using var main = new HttpGuest("main");
        using var side = new HttpGuest("side");
        await AddHttpsForwardAsync("work-vm", main.Port, main: true);
        await AddHttpsForwardAsync("work-vm", side.Port, name: "side");
        var shared = FreePort();
        await _config.SetAsync("https", new HttpsConfig(shared), "test", CancellationToken.None);
        await _gateway.ReconcileSharedAsync(CancellationToken.None);

        // The Host header reaches the guest as the browser sent it, under any of the names.
        var named = await RequestAsync(shared, "work-vm-side.vms.test", "work-vm-side.vms.test");
        Assert.Contains("guest=side", named.Response);
        Assert.Contains("host=work-vm-side.vms.test", named.Response);
        Assert.Contains("guest=main", (await RequestAsync(shared, $"work-vm-{main.Port}.vms.test", $"work-vm-{main.Port}.vms.test")).Response);
        Assert.Contains("guest=main", (await RequestAsync(shared, "work-vm.vms.test", "work-vm.vms.test")).Response);
        Assert.Contains("404 Not Found", (await RequestAsync(shared, "work-vm-other.vms.test", "work-vm-other.vms.test")).Response);
    }

    [Fact]
    public async Task A_renewed_certificate_is_served_without_a_restart()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);

        // The renewal script runs the admin CLI: another process, another store over the same files.
        var renewed = TestCertificates.SelfSigned(["*.vms.test"]);
        Thread.Sleep(20);
        new HttpsCertificateStore(_options, _clock, NullLogger<HttpsCertificateStore>.Instance).Import(renewed.ChainPem, renewed.KeyPem);
        _clock.Advance(TimeSpan.FromSeconds(6));

        var (served, response) = await RequestAsync(forward.PublicPort!.Value, "work-vm.vms.test", "work-vm.vms.test");
        Assert.Equal(renewed.Sha256, served);
        Assert.Contains("200 OK", response);
    }

    [Fact]
    public async Task Removing_the_forward_closes_its_port()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);

        Assert.True(await _manager.RemoveForwardAsync("work-vm", forward.Id, CancellationToken.None));

        await AssertRefusedAsync(forward.PublicPort!.Value);
        Assert.DoesNotContain(_gateway.Listeners(), l => l.ForwardId == forward.Id);
    }

    [Fact]
    public async Task Reconciliation_reopens_https_forwards_after_a_restart()
    {
        await AddVmAsync("work-vm");
        using var guest = new HttpGuest();
        var forward = await AddHttpsForwardAsync("work-vm", guest.Port);
        await _gateway.StopAsync(forward.Id);
        await AssertRefusedAsync(forward.PublicPort!.Value);

        await _manager.ReconcileAsync(CancellationToken.None);

        Assert.Contains("200 OK", (await RequestAsync(forward.PublicPort.Value, "work-vm.vms.test", "work-vm.vms.test")).Response);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task AddVmAsync(string name)
    {
        await _vms.AddAsync(new Vm(name, "alice", 2, 4, 40, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            TokenKind: VmTokenKind.Primary), 10, CancellationToken.None);
    }

    private async Task<PortForward> AddHttpsForwardAsync(string vm, int vmPort, bool main = false, string? name = null, bool upstreamTls = false)
    {
        _driver.Port = vmPort;
        var added = await _manager.TryAddHttpsForwardAsync(vm, vmPort, "web", new HttpsForwardOptions(main, upstreamTls, name), 0, CancellationToken.None);
        Assert.Equal(AddForwardStatus.Added, added.Status);
        return added.Forward!;
    }

    private static async Task<(string Served, string Response)> RequestAsync(int port, string serverName, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        X509Certificate? served = null;
        await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            served = certificate;
            return true;
        });
        await tls.AuthenticateAsClientAsync(serverName);
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var reader = new StreamReader(tls);
        var response = await reader.ReadToEndAsync(timeout.Token);
        return (Convert.ToHexString(SHA256.HashData(served!.GetRawCertData())), response);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int ClosedPort() => FreePort();

    private static async Task AssertRefusedAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(25);
        }
    }

    /// <summary>Every VM sits on loopback; the port is the guest port each forward names.</summary>
    private sealed class StaticDriver : IHypervisorDriver
    {
        public int Port { get; set; } = 22;
        public DriverCapabilities Capabilities { get; } = new(false, true, DriverConsole.None);
        public Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken) => Task.FromResult(VmState.Running);
        public Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Endpoint?>(new Endpoint("127.0.0.1", Port));
        public Task<bool> WaitReachableAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A one-request HTTP server that echoes the Host header it received, optionally over TLS.</summary>
    private sealed class HttpGuest : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;
        private int _tlsHellos;

        /// <summary>Connections accepted, and TLS hellos a plain guest was sent.</summary>
        public int Accepted => Volatile.Read(ref _accepted);
        public int TlsHellos => Volatile.Read(ref _tlsHellos);

        public HttpGuest(TestCertificates.Issued? tls = null) : this("guest", tls)
        {
        }

        public HttpGuest(string name, TestCertificates.Issued? tls = null)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var certificate = tls is null ? null : X509Certificate2.CreateFromPem(tls.Leaf.ExportCertificatePem(), tls.KeyPem);
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { break; }
                    Interlocked.Increment(ref _accepted);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                Stream stream = client.GetStream();
                                if (certificate is not null)
                                {
                                    var server = new SslStream(stream);
                                    await server.AuthenticateAsServerAsync(certificate);
                                    stream = server;
                                }

                                var head = new StringBuilder();
                                var buffer = new byte[1024];
                                while (!head.ToString().Contains("\r\n\r\n"))
                                {
                                    var read = await stream.ReadAsync(buffer, _stop.Token);
                                    if (read == 0) return;
                                    if (certificate is null && head.Length == 0 && buffer[0] == 0x16)
                                    {
                                        Interlocked.Increment(ref _tlsHellos);
                                        // What node and nginx do with a TLS hello on a plain port.
                                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"), _stop.Token);
                                        return;
                                    }

                                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                                }

                                var host = head.ToString().Split("\r\n").First(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                                var body = $"guest={name} host={host} tls={certificate is not null}";
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), _stop.Token);
                                await stream.FlushAsync(_stop.Token);
                            }
                            catch (Exception)
                            {
                                // The test is over or the probe gave up.
                            }
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
