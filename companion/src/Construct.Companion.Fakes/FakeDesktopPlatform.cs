using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;

namespace Construct.Companion.Fakes;

public sealed class FakeDataProtection : IDataProtection
{
    public bool Denied { get; set; }
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Select(b => (byte)(b ^ 0x55)).ToArray();
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Denied ? throw new System.Security.Cryptography.CryptographicException("Unavailable") : Protect(ciphertext);
}
public sealed class FakeCimVmQuery : ICimVmQuery
{
    public CimVmState? State { get; set; }
    public bool Denied { get; set; }
    public Task<CimVmState?> QueryAsync(string vmName, CancellationToken cancellationToken = default) => Denied ? throw new UnauthorizedAccessException() : Task.FromResult(State);
}
public sealed class FakeDesktopProcess : IDesktopProcess
{
    public List<ProcessInvocation> Invocations { get; } = [];
    public List<string> Opened { get; } = [];
    public Dictionary<string, string> Executables { get; } = [];
    public Dictionary<string, string> Environment { get; } = [];
    public Task OpenAsync(string target, CancellationToken cancellationToken = default) { Opened.Add(target); return Task.CompletedTask; }
    public Task StartAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default) { Invocations.Add(invocation); return Task.CompletedTask; }
    public string? FindOnPath(string executable) => Executables.GetValueOrDefault(executable);
    public string? EnvironmentValue(string name) => Environment.GetValueOrDefault(name);
}
public sealed class FakeMessageSink : IMessageSink
{
    public List<InstanceMessage> Posted { get; } = [];
    private readonly List<(string Scope, Channel<JsonElement> Channel)> subscribers = [];
    public Task PostAsync(string instance, JsonElement message, CancellationToken cancellationToken = default)
    { lock (subscribers) Posted.Add(new(instance, message.Clone())); return Task.CompletedTask; }
    public void Publish(string instance, JsonElement message)
    { lock (subscribers) foreach (var s in subscribers.Where(s => s.Scope == instance)) s.Channel.Writer.TryWrite(message.Clone()); }
    public async IAsyncEnumerable<JsonElement> Subscribe(string instance, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entry = (instance, Channel.CreateUnbounded<JsonElement>());
        lock (subscribers) subscribers.Add(entry);
        try { await foreach (var message in entry.Item2.Reader.ReadAllAsync(cancellationToken)) yield return message; }
        finally { lock (subscribers) subscribers.Remove(entry); }
    }
}
