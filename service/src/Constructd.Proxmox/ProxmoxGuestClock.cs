namespace Constructd.Proxmox;

/// <summary>
/// Sets a guest's clock to the node's clock through the QEMU guest agent socket, after a resume
/// from a saved state (<c>qm suspend --todisk</c> + <c>qm start</c>). The restored guest keeps the
/// kernel clock it was saved with, and its RTC device state is restored with it, so neither
/// <c>hwclock</c> nor timesyncd's next poll (up to half an hour away) corrects it promptly.
/// Hyper-V's time-synchronization integration service did this implicitly; QEMU needs
/// <c>guest-set-time</c> with an explicit timestamp. Proxmox does not expose that command through
/// <c>qm guest cmd</c> or its agent API, hence the direct socket client, like <see cref="ProxmoxQmp"/>.
/// </summary>
public static class ProxmoxGuestClock
{
    /// <summary>
    /// Argument 1 is the VM's QGA socket (<c>/run/qemu-server/&lt;vmid&gt;.qga</c>). Retries until the
    /// agent answers — the socket exists as soon as QEMU runs, but the agent inside the guest takes
    /// a moment to service its channel again after the resume. The 0xFF byte and guest-sync reset
    /// the agent's parser per the QGA protocol; the timestamp is read fresh per attempt so a long
    /// wait cannot make the written time stale. Prints nothing secret; exit 0 only after the guest
    /// acknowledged the new time.
    /// </summary>
    public const string Script = """
        import json, os, socket, sys, time
        deadline = time.monotonic() + 90
        while True:
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
                    sock.settimeout(10)
                    sock.connect(sys.argv[1])
                    stream = sock.makefile('rwb', buffering=0)
                    def execute(command, sentinel=False):
                        stream.write((b'\xff' if sentinel else b'') + (json.dumps(command) + '\n').encode())
                        for unused in range(256):
                            line = stream.readline(1048577)
                            if not line or len(line) > 1048576:
                                raise ValueError()
                            reply = json.loads(line)
                            if 'error' in reply:
                                raise ValueError()
                            if 'return' in reply:
                                return reply['return']
                        raise ValueError()
                    ident = int.from_bytes(os.urandom(4), 'big') + 1
                    if execute({'execute': 'guest-sync', 'arguments': {'id': ident}}, sentinel=True) != ident:
                        raise ValueError()
                    execute({'execute': 'guest-set-time', 'arguments': {'time': time.time_ns()}})
                    print('synced')
                    sys.exit(0)
            except SystemExit:
                raise
            except Exception:
                if time.monotonic() >= deadline:
                    sys.exit(1)
                time.sleep(2)
        """;
}
