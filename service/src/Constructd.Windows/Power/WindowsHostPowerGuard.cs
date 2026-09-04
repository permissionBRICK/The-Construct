using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Constructd.Core.Services;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Power;

/// <summary>
/// The Windows power availability request (plan §4.13): while it is held the host does not enter
/// sleep on its idle timer, which is exactly what happened to the field host overnight while VMs
/// were expected to keep serving.
///
/// One request object for the service's lifetime — created lazily on the first acquire, set and
/// cleared as VMs come and go, closed when the service stops. <c>PowerRequestSystemRequired</c> only:
/// the display may sleep, and away mode is for media playback, not for a machine nobody is sitting
/// at.
///
/// An administrator can see it on the host with <c>powercfg /requests</c>, where it shows up under
/// SYSTEM against the service executable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHostPowerGuard(ILogger<WindowsHostPowerGuard> logger) : HostPowerGuardBase
{
    /// <summary>
    /// What <c>powercfg /requests</c> prints next to the executable. Fixed for the lifetime of the
    /// request object: the reason string is copied into the kernel at creation, so the per-tick
    /// reason ("3 VM(s) running") goes to the log instead.
    /// </summary>
    private const string RequestReason = "Construct agent VMs are running";

    private const uint ReasonContextVersion = 0;             // POWER_REQUEST_CONTEXT_VERSION
    private const uint SimpleStringFlag = 0x00000001;        // POWER_REQUEST_CONTEXT_SIMPLE_STRING
    private const int SystemRequired = 1;                    // POWER_REQUEST_TYPE.PowerRequestSystemRequired

    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _request = IntPtr.Zero;
    private IntPtr _reason = IntPtr.Zero;

    protected override void Acquire(string reason)
    {
        var request = EnsureRequest();

        if (!PowerSetRequest(request, SystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerSetRequest(PowerRequestSystemRequired) failed.");
        }

        logger.LogInformation("Holding a power availability request so this host stays awake: {Reason}.", reason);
    }

    protected override void Release(string reason)
    {
        if (_request == IntPtr.Zero)
        {
            return;
        }

        if (!PowerClearRequest(_request, SystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerClearRequest(PowerRequestSystemRequired) failed.");
        }

        logger.LogInformation("Released the host's power availability request: {Reason}.", reason);
    }

    protected override void DisposeCore()
    {
        if (_request != IntPtr.Zero)
        {
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }

        if (_reason != IntPtr.Zero)
        {
            // Freed only now: the kernel keeps the REASON_CONTEXT for as long as the request lives.
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
        }
    }

    /// <summary>The one request object, created on first use. Called under the base class's lock.</summary>
    private IntPtr EnsureRequest()
    {
        if (_request != IntPtr.Zero)
        {
            return _request;
        }

        var reason = Marshal.StringToHGlobalUni(RequestReason);
        var context = new ReasonContext
        {
            Version = ReasonContextVersion,
            Flags = SimpleStringFlag,
            SimpleReasonString = reason,
        };

        var request = PowerCreateRequest(ref context);
        if (request == IntPtr.Zero || request == InvalidHandle)
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(reason);
            throw new Win32Exception(error, "PowerCreateRequest failed.");
        }

        _reason = reason;
        _request = request;
        return _request;
    }

    /// <summary>
    /// REASON_CONTEXT with the SIMPLE_STRING member of its union. The string is an explicitly
    /// allocated pointer rather than a marshalled <c>string</c> field so the struct stays blittable —
    /// which is what <c>[LibraryImport]</c> needs — and so its lifetime is ours to control.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
