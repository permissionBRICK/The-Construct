using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Constructd.Core.Abstractions;

namespace Constructd.Windows.Forwards;

/// <summary>
/// The host's IPv4 TCP table via <c>iphlpapi!GetExtendedTcpTable</c>
/// (<c>TCP_TABLE_OWNER_PID_ALL</c>).
///
/// <see cref="IPGlobalProperties.GetActiveTcpConnections"/> would almost do, but it drops the owning
/// process and — more importantly — is the same call without the ability to ask for the owner table,
/// which the plan's idle work wants available. The P/Invoke is confined to this class; everything that
/// decides anything works on <see cref="TcpConnectionInfo"/> and is tested with a fake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class IpHlpApiTcpTableReader : ITcpTableReader
{
    private const int AfInet = 2;                       // AF_INET
    private const int TcpTableOwnerPidAll = 5;          // TCP_TABLE_OWNER_PID_ALL
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    public IReadOnlyList<TcpConnectionInfo> Read()
    {
        var size = 0;

        // First call sizes the buffer; the table can grow between the two calls, so the loop retries.
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, AfInet, TcpTableOwnerPidAll, reserved: 0);

        for (var attempt = 0; attempt < 5 && status == ErrorInsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, order: false, AfInet, TcpTableOwnerPidAll, reserved: 0);
                if (status == NoError)
                {
                    return ReadRows(buffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        if (status != NoError)
        {
            throw new NetworkInformationException((int)status);
        }

        return [];
    }

    private static List<TcpConnectionInfo> ReadRows(IntPtr buffer)
    {
        var count = Marshal.ReadInt32(buffer);
        var rows = new List<TcpConnectionInfo>(count);
        var rowSize = Marshal.SizeOf<TcpRow>();
        var cursor = buffer + sizeof(int);

        for (var i = 0; i < count; i++)
        {
            var row = Marshal.PtrToStructure<TcpRow>(cursor);
            cursor += rowSize;

            rows.Add(new TcpConnectionInfo(
                new IPAddress(row.LocalAddress),
                Port(row.LocalPort),
                new IPAddress(row.RemoteAddress),
                Port(row.RemotePort),
                MapState(row.State),
                row.OwningProcessId));
        }

        return rows;
    }

    /// <summary>Ports come back in network byte order, in the low two bytes of a DWORD.</summary>
    private static int Port(uint value) => ((int)(value & 0xFF) << 8) | (int)((value >> 8) & 0xFF);

    /// <summary>MIB_TCP_STATE → the BCL's enum, so nothing downstream needs a Windows constant.</summary>
    private static TcpState MapState(uint state) => state switch
    {
        1 => TcpState.Closed,
        2 => TcpState.Listen,
        3 => TcpState.SynSent,
        4 => TcpState.SynReceived,
        5 => TcpState.Established,
        6 => TcpState.FinWait1,
        7 => TcpState.FinWait2,
        8 => TcpState.CloseWait,
        9 => TcpState.Closing,
        10 => TcpState.LastAck,
        11 => TcpState.TimeWait,
        12 => TcpState.DeleteTcb,
        _ => TcpState.Unknown,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int OwningProcessId;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U4)]
    private static partial uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        int reserved);
}
