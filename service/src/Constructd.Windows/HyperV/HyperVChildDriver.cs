using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
namespace Constructd.Windows.HyperV;

public sealed class HyperVChildDriver(IProcessRunner processes, ConstructdOptions options, IHypervisorDriver legacy) : IChildVmDriver, IChildVmStorage, IChildVmCreationOwnership
{
    private BackendCapabilities? _capabilities;
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    public async Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        await _capabilityGate.WaitAsync(ct);
        try
        {
            if (_capabilities is not null) return _capabilities;
            var value = await RunAsync("capabilities", "Get-ConstructDriverExtendedCapabilities", "", new { }, ct);
            // The legacy PowerShell console shape predates BackendCapabilities; keep the existing adapter authoritative.
            var node = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!;
            node["legacy"] = null;
            var result = node.Deserialize<BackendCapabilities>(HyperVChildScript.Json) ?? throw Fail("capabilities");
            if (result.Generations is null || result.Notes is null) throw Fail("capabilities");
            return _capabilities = result with { Legacy = legacy.Capabilities };
        }
        catch (JsonException) { throw Fail("capabilities"); }
        finally { _capabilityGate.Release(); }
    }

    public async Task<string?> GetCreationOperationAsync(string name, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        var value = await RunAsync("creation-owner", "Get-ConstructChildCreationOperation", "-Name $inputData.name -VhdPath $inputData.vhdPath", new { name, vhdPath = DiskPath(name) }, ct);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
    public async Task<ChildStoragePlacement> ResolveStorageAsync(string name, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        return Read<ChildStoragePlacement>(await RunAsync("storage", "Get-ConstructChildStorage", "-Name $inputData.name -VhdPath $inputData.vhdPath", new { name, vhdPath = DiskPath(name) }, ct));
    }
    public async Task<ChildStoragePlacement> ResolvePrimaryStorageAsync(string name, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        // The existing primary driver has this historical default, independently of Get-VMHost.
        var path = DiskPath(name) ?? @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks\" + name + ".vhdx";
        return Read<ChildStoragePlacement>(await RunAsync("storage", "Get-ConstructChildStorage", "-Name $inputData.name -VhdPath $inputData.vhdPath", new { name, vhdPath = path }, ct));
    }
    public Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct) =>
        CreateOwnedAsync(descriptor, Guid.NewGuid().ToString("n"), progress, ct);
    public async Task CreateOwnedAsync(ChildVmDescriptor descriptor, string operationId, IProgress<string>? progress, CancellationToken ct)
    {
        ValidateDescriptor(descriptor);
        HardwarePresets.ValidateCapabilities(descriptor.Hardware, await GetCapabilitiesAsync(ct), descriptor.AuxiliaryMediaPath is not null);
        var canonicalDisk = DiskPath(descriptor.Name) ?? (await ResolveStorageAsync(descriptor.Name, ct)).DiskPath;
        progress?.Report("Creating general-purpose VM hardware and disk.");
        await RunAsync("create", "New-ConstructChildVm", "-Descriptor $inputData", new { descriptor.Name, descriptor.Hardware, VhdPath = descriptor.VhdPath ?? canonicalDisk, OwnershipPath = canonicalDisk + ".childvm.json", descriptor.InstallMediaPath, descriptor.AuxiliaryMediaPath, descriptor.SwitchName, OperationId = operationId }, ct, TimeSpan.FromMinutes(30));
    }
    public async Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        progress?.Report("Removing child VM and owned disk artifacts.");
        await RunAsync("remove", "Remove-ConstructChildVm", "-Name $inputData.name -VhdPath $inputData.vhdPath", new { name, vhdPath = DiskPath(name) }, ct, TimeSpan.FromMinutes(30));
    }
    public async Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        HardwarePresets.ValidateCapabilities(hardware, await GetCapabilitiesAsync(ct), false);
        await RunAsync("hardware", "Set-ConstructChildHardware", "-Name $inputData.name -Hardware $inputData.hardware -ResendTemplate $inputData.resendTemplate", new { name, hardware, resendTemplate }, ct);
    }
    public async Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct)
    {
        ArgumentGuard.VmName(name); ValidatePath(installMediaPath); ValidatePath(auxiliaryMediaPath);
        if (bootOrder.Any(x => !Enum.IsDefined(x)) || bootOrder.Distinct().Count() != bootOrder.Count) throw new ChildValidationException("validation", "bootOrder");
        var caps = await GetCapabilitiesAsync(ct);
        if (auxiliaryMediaPath is not null && (caps.MaxOpticalDrives < 2 || caps.AuxiliaryMedia == CapabilityLevel.Unsupported)) throw new ChildValidationException("unsupported-capability", "auxiliaryMedia");
        await RunAsync("media", "Set-ConstructChildMedia", "-Name $inputData.name -InstallMediaPath $inputData.installMediaPath -AuxiliaryMediaPath $inputData.auxiliaryMediaPath -BootOrder $inputData.bootOrder", new { name, installMediaPath, auxiliaryMediaPath, bootOrder }, ct);
    }
    public async Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct) =>
        Read<AttachedMedia>(await NamedAsync("attached-media", "Get-ConstructChildAttachedMedia", name, ct));
    public async Task<string?> GetVmIdAsync(string name, CancellationToken ct)
    {
        var value = await NamedAsync("id", "Get-ConstructChildVmId", name, ct);
        return value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw Fail("id");
    }
    public async Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct) =>
        Read<VmCapabilitiesSnapshot>(await NamedAsync("vm-capabilities", "Get-ConstructChildVmCapabilities", name, ct));
    public async Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        if (timeout.TotalSeconds < 1 || timeout.TotalSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(timeout));
        progress?.Report("Requesting guest shutdown through integration services.");
        return Read<GracefulShutdownOutcome>(await RunAsync("shutdown", "Stop-ConstructChildVmGracefully", "-Name $inputData.name -TimeoutSeconds $inputData.timeoutSeconds", new { name, timeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds) }, ct, timeout + TimeSpan.FromMinutes(1)));
    }
    private Task<JsonElement> NamedAsync(string operation, string function, string name, CancellationToken ct)
    { ArgumentGuard.VmName(name); return RunAsync(operation, function, "-Name $inputData.name", new { name }, ct); }
    private static T Read<T>(JsonElement value)
    {
        try { return value.Deserialize<T>(HyperVChildScript.Json) ?? throw Fail("read"); }
        catch (JsonException) { throw Fail("read"); }
    }
    private string? DiskPath(string name) => string.IsNullOrWhiteSpace(options.VmStorageRoot) ? null : ArgumentGuard.WindowsPath(options.VmStorageRoot, "VmStorageRoot").TrimEnd('\\', '/') + "\\" + name + ".vhdx";
    private static void ValidatePath(string? path) { if (path is not null) ArgumentGuard.WindowsPath(path, "media path"); }
    private static void ValidateDescriptor(ChildVmDescriptor d)
    {
        ArgumentGuard.VmName(d.Name); ArgumentGuard.Text(d.SwitchName, "switchName");
        ValidatePath(d.VhdPath); ValidatePath(d.InstallMediaPath); ValidatePath(d.AuxiliaryMediaPath);
        HardwarePresets.ValidateShape(d.Hardware);
    }
    private static HypervisorOperationException Fail(string operation) => new("child-" + operation, "");
    private async Task<JsonElement> RunAsync(string operation, string function, string arguments, object input, CancellationToken ct, TimeSpan? timeout = null)
    {
        var script = HyperVChildScript.Build(options.ScriptsDir, function, arguments);
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(options.PowerShellPath,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", PowerShellEncoding.Encode(script)],
                JsonSerializer.Serialize(input, HyperVChildScript.Json), timeout ?? TimeSpan.FromMinutes(5), null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw Fail(operation); }
        if (result.TimedOut || result.ExitCode != 0) throw Fail(operation);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.Trim());
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw Fail(operation);
            return root.GetProperty("value").Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { throw Fail(operation); }
    }
}
