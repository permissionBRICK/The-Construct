using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Proxmox;

/// <summary>QMP input stays on stdin, including text translated to key events. No shell or input logging.</summary>
public sealed class ProxmoxQmp(IProcessRunner runner, ConstructdOptions options)
{
    // A short-lived local socket client avoids putting typed keys into pvesh's --command argv.
    // See https://www.qemu.org/docs/master/interop/qmp-spec.html for reply IDs and asynchronous events.
    public const string Script = """
        import json, socket, sys, time
        try:
            request = json.load(sys.stdin)
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
                sock.settimeout(15)
                sock.connect(sys.argv[1])
                stream = sock.makefile('rwb', buffering=0)
                def read():
                    line = stream.readline(1048577)
                    if not line or len(line) > 1048576:
                        raise ValueError()
                    return json.loads(line)
                if 'QMP' not in read():
                    raise ValueError()
                def execute(command, ident):
                    stream.write((json.dumps(dict(command, id=ident)) + '\n').encode())
                    for unused in range(256):
                        reply = read()
                        if reply.get('id') == ident:
                            if 'error' in reply or 'return' not in reply:
                                raise ValueError()
                            return reply['return']
                    raise ValueError()
                execute({'execute':'qmp_capabilities'}, 'capabilities')
                if request.get('running'):
                    if execute({'execute':'query-status'}, 'status').get('status') != 'running':
                        raise ValueError()
                results = []
                for index, command in enumerate(request['commands']):
                    if command.get('execute') not in ('input-send-event', 'screendump', 'query-mice', 'query-status'):
                        raise ValueError()
                    results.append(execute(command, index))
                    if request.get('paced'):
                        time.sleep(0.01)
                print(json.dumps(results))
        except Exception:
            print('[]')
            sys.exit(1)
        """;

    public async Task<JsonElement> RunAsync(int id, IReadOnlyList<object> commands, bool running, CancellationToken ct, bool paced = false)
    {
        try
        {
            var result = await runner.RunAsync(options.Proxmox.PythonPath,
                ["-c", Script, "/run/qemu-server/" + ProxmoxCommands.Number(id) + ".qmp"],
                JsonSerializer.Serialize(new { commands, running, paced }), TimeSpan.FromSeconds(20), null, ct);
            if (!result.Succeeded || result.StandardOutput.Length > 1 << 20) throw new ConsoleTransportException();
            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != commands.Count) throw new ConsoleTransportException();
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ConsoleTransportException(); }
    }
}
