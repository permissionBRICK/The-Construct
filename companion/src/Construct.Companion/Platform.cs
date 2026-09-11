using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Remote;
using Construct.Companion.Host.Runtime;
using Construct.Companion.Windows;
namespace Construct.Companion;

// The native seams of this process, created once and shared by the tray, the windows and the Host.
internal sealed class Platform(HostFileSystem files, string localAppData, string version)
{
    public HostFileSystem Files { get; } = files;
    public string Version { get; } = version;
    public string StateDirectory { get; } = Path.Combine(localAppData, "The-Construct", "companion");
    public string StateRoot { get; } = Path.Combine(localAppData, "The-Construct");
    public string MediaDirectory { get; } = Path.Combine(files.GetRoot(Core.Abstractions.FileSystemRoot.InstallDirectory)!, "media");
    public SystemClock Clock { get; } = new();
    public DesktopLauncher Launcher { get; } = new(new DesktopProcess(), files);
    public HypervisorQuery Hypervisor { get; } = new(new CimVmQuery());
    public WasapiAudioCapture Capture { get; } = new();
    public DesktopRegistration Registration { get; } = new(new CurrentUserRegistry(), Application.ExecutablePath);
    public ProtectedTokenStore Tokens { get; } = new(files, new DpapiProtection(), Path.Combine(localAppData, "The-Construct", "remote"));
    public RollingLog Log { get; } = new(files, new SystemClock(), Path.Combine(localAppData, "The-Construct", "companion", "logs"));
    public WinRtToastRaiser Toast => new(Registration);
    // Loopback only: proxies and redirects must never see the bearer token.
    public static HttpClient LoopbackClient(TimeSpan timeout) => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = timeout };
}
