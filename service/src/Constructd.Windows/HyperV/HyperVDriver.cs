using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.HyperV;

/// <summary>
/// A driver operation did not succeed. The message is composed here and names only the operation and
/// the VM — never the script, the stderr or a path — because it is what reaches the job error, the
/// audit trail and the API response (<see cref="SafeError"/>).
/// </summary>
public sealed class HypervisorOperationException(string operation, string vmName, string? detail = null)
    : Exception(
        vmName.Length == 0
            ? $"The Hyper-V driver failed during '{operation}'."
            : $"The Hyper-V driver failed during '{operation}' for VM '{vmName}'."), IConstructdError
{
    public string Operation { get; } = operation;

    public string VmName { get; } = vmName;

    /// <summary>
    /// How it failed, in this service's own words (an exit code, a timeout, "not the expected
    /// envelope"). Never the child's output: see <see cref="HyperVDriver"/>'s failure handling.
    /// </summary>
    public string? Detail { get; } = detail;
}

/// <summary>
/// <see cref="IHypervisorDriver"/> on top of the repo's PowerShell driver contract
/// (<c>docs/drivers.md</c>): every operation runs
/// <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand …</c>, and the
/// script dot-sources <c>drivers\Load-ConstructDriver.ps1</c> and calls the contract function.
///
/// The service therefore shares one implementation with the local install instead of reimplementing
/// Hyper-V against raw cmdlets — which is the whole point of the driver extraction (plan §4.2, B4).
/// </summary>
public sealed class HyperVDriver : IHypervisorDriver
{
    /// <summary>Enough for a create with a fresh VHD, well short of leaving a hung process forever.</summary>
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Removal merges checkpoint disks, which the lib waits out.</summary>
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A power change or a query is a handful of cmdlets.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _processes;
    private readonly ConstructdOptions _options;
    private readonly ILogger<HyperVDriver> _logger;
    private readonly Lock _capabilityGate = new();
    private DriverCapabilities? _capabilities;

    public HyperVDriver(IProcessRunner processes, ConstructdOptions options, ILogger<HyperVDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _processes = processes;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Capabilities come from <c>Get-ConstructDriverCapabilities</c> and are cached after the first
    /// successful read — they describe the backend, which does not change while the service runs. A
    /// failed probe is <em>not</em> cached and reports nothing supported, so a transient Hyper-V outage
    /// makes the service refuse the capability-gated operations (suspend) rather than promise them.
    /// </summary>
    public DriverCapabilities Capabilities
    {
        get
        {
            lock (_capabilityGate)
            {
                if (_capabilities is { } cached)
                {
                    return cached;
                }

                try
                {
                    // The interface exposes capabilities as a property, so this one call is synchronous.
                    // Task.Run keeps it off whatever context the caller is on, and it happens at most
                    // once per service lifetime.
                    var value = Task.Run(() => RunAsync(
                        "capabilities",
                        vmName: string.Empty,
                        HyperVScript.GetCapabilities(_options.ScriptsDir),
                        ShortTimeout,
                        progress: null,
                        CancellationToken.None)).GetAwaiter().GetResult();

                    // 'vmconnect' for hyperv-local; a URL is what a Proxmox-style backend would report.
                    var console = value.ValueKind == JsonValueKind.Object &&
                                  value.TryGetProperty("Console", out var consoleValue) &&
                                  consoleValue.ValueKind == JsonValueKind.String
                        ? consoleValue.GetString()
                        : null;

                    _capabilities = new DriverCapabilities(
                        Checkpoints: ReadBool(value, "Checkpoints"),
                        Suspend: ReadBool(value, "Suspend"),
                        Console: DriverConsole.Parse(console));

                    return _capabilities;
                }
                catch (Exception ex)
                {
                    // Only the safe description: this runs on a request path.
                    _logger.LogError(
                        "Could not read the hypervisor driver capabilities ({Error}); reporting none until it answers.",
                        SafeError.Describe(ex));

                    return new DriverCapabilities(Checkpoints: false, Suspend: false, Console: DriverConsole.None);
                }
            }
        }
    }

    public async Task CreateVmAsync(
        VmDescriptor descriptor,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var script = HyperVScript.CreateVm(
            _options.ScriptsDir,
            descriptor,
            _options.SwitchName,
            VhdPathFor(descriptor.Name));

        await RunAsync("create-vm", descriptor.Name, script, CreateTimeout, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken) =>
        await RunAsync(
            "remove-vm",
            name,
            HyperVScript.RemoveVm(_options.ScriptsDir, name),
            RemoveTimeout,
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(string name, CancellationToken cancellationToken) =>
        await RunAsync(
            "start-vm",
            name,
            HyperVScript.StartVm(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(string name, CancellationToken cancellationToken) =>
        await RunAsync(
            "stop-vm",
            name,
            HyperVScript.StopVm(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(string name, CancellationToken cancellationToken) =>
        await RunAsync(
            "save-vm",
            name,
            HyperVScript.SaveVm(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken)
    {
        var value = await RunAsync(
            "get-state",
            name,
            HyperVScript.GetState(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        return MapState(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    /// <summary>
    /// The contract's state enum (<c>docs/drivers.md</c> §3.2) mapped onto <see cref="VmState"/>.
    /// Anything unrecognized is <see cref="VmState.Unknown"/> — "can't tell", never "not installed".
    /// </summary>
    public static VmState MapState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "running" => VmState.Running,
        "off" => VmState.Off,
        "paused" => VmState.Paused,
        "saved" => VmState.Saved,
        "absent" => VmState.Absent,
        _ => VmState.Unknown,
    };

    public async Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        var value = await RunAsync(
            "get-endpoint",
            name,
            HyperVScript.GetEndpoint(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("SshHost", out var host) ||
            host.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(host.GetString()))
        {
            return null;
        }

        var port = value.TryGetProperty("SshPort", out var portValue) && portValue.TryGetInt32(out var parsed)
            ? parsed
            : 22;

        return new Endpoint(host.GetString()!, port);
    }

    public async Task<bool> WaitReachableAsync(
        string name,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Min(timeout.TotalSeconds, 86400));

        var value = await RunAsync(
            "wait-reachable",
            name,
            HyperVScript.WaitReachable(_options.ScriptsDir, name, seconds),
            // The script is meant to sit there for the whole wait; the process timeout is the wait plus
            // the driver's settle window and a margin, so only a genuinely stuck child is killed.
            timeout + TimeSpan.FromMinutes(5),
            progress,
            cancellationToken).ConfigureAwait(false);

        return value.ValueKind == JsonValueKind.True;
    }

    public async Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken) =>
        await RunAsync(
            "detach-media",
            name,
            HyperVScript.DetachInstallMedia(_options.ScriptsDir, name),
            ShortTimeout,
            progress: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>The VHDX path for a VM, or null when the admin left the storage root unset.</summary>
    private string? VhdPathFor(string vmName)
    {
        if (string.IsNullOrWhiteSpace(_options.VmStorageRoot))
        {
            return null;
        }

        var root = ArgumentGuard.WindowsPath(_options.VmStorageRoot, "Constructd:VmStorageRoot")
            .TrimEnd('\\', '/');

        return $@"{root}\{ArgumentGuard.VmName(vmName)}.vhdx";
    }

    /// <summary>
    /// Runs one driver script and returns its <c>value</c>. Progress lines are forwarded as they
    /// arrive, minus the final JSON line, which is the result rather than progress.
    /// </summary>
    private async Task<JsonElement> RunAsync(
        string operation,
        string vmName,
        string script,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-EncodedCommand",
            PowerShellEncoding.Encode(script),
        };

        var lines = progress is null ? null : new WithheldLastLineProgress(progress);

        var result = await _processes.RunAsync(
            _options.PowerShellPath,
            arguments,
            standardInput: null,
            timeout,
            lines,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw Fail(operation, vmName, $"timed out after {timeout.TotalMinutes:0} minutes");
        }

        if (result.ExitCode != 0)
        {
            throw Fail(operation, vmName, $"powershell.exe exited with {result.ExitCode}");
        }

        var lastLine = LastNonEmptyLine(result.StandardOutput);
        if (lastLine is null)
        {
            throw Fail(operation, vmName, "the script produced no output");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(lastLine);
        }
        catch (JsonException)
        {
            throw Fail(operation, vmName, "the last output line was not the expected JSON envelope");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok))
            {
                throw Fail(operation, vmName, "the result envelope was not the expected shape");
            }

            if (ok.ValueKind != JsonValueKind.True)
            {
                throw Fail(operation, vmName, "the driver reported the operation as failed");
            }

            // Cloned: the document is disposed on the way out.
            return root.TryGetProperty("value", out var value) ? value.Clone() : default;
        }
    }

    /// <summary>
    /// Builds the exception and logs the failure.
    ///
    /// <paramref name="reason"/> is <em>composed here</em> and is deliberately never built from the
    /// child's stdout or stderr. PowerShell error text routinely carries a script path, a whole command
    /// line, or the values a cmdlet was called with — and this service's rule is that dependency text is
    /// not repeated verbatim anywhere, the log included (see <see cref="SafeError"/>). What an operator
    /// gets instead is the operation, the VM, and how it failed; what they get for detail is the
    /// driver's own progress output, which is streamed to the job as it happens.
    /// </summary>
    private HypervisorOperationException Fail(string operation, string vmName, string reason)
    {
        _logger.LogError(
            "Hyper-V driver operation {Operation} for {Vm} failed: {Reason}",
            operation,
            vmName.Length == 0 ? "-" : vmName,
            reason);

        return new HypervisorOperationException(operation, vmName, reason);
    }

    private static bool ReadBool(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var flag) &&
        flag.ValueKind == JsonValueKind.True;

    private static string? LastNonEmptyLine(string output)
    {
        var lines = output.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

}

/// <summary>
/// Forwards every line except the most recent one, so the caller never sees the result envelope: the
/// script prints its JSON as the last stdout line, and until another line arrives the pending line
/// might be exactly that. Progress lags by one line and stops one line short, which is what "the
/// driver's own <c>Write-Step</c>/<c>Write-Ok</c> output, and nothing else" costs.
/// </summary>
internal sealed class WithheldLastLineProgress(IProgress<string> inner) : IProgress<string>
{
    private readonly Lock _gate = new();
    private string? _pending;

    public void Report(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string? release;
        lock (_gate)
        {
            release = _pending;
            _pending = value.TrimEnd();
        }

        if (release is not null)
        {
            inner.Report(release);
        }
    }
}
