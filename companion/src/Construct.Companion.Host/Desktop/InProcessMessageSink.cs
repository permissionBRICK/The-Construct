using System.Runtime.CompilerServices;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Desktop;

// S3 binds post to the dispatcher, using the same bus for runtime and UI output.
public sealed class InProcessMessageSink(RuntimeMessageBus bus, Func<string,JsonElement,CancellationToken,Task> post) : IMessageSink
{
    public Task PostAsync(string instance, JsonElement message, CancellationToken cancellationToken = default) => post(instance,message.Clone(),cancellationToken);
    public async IAsyncEnumerable<JsonElement> Subscribe(string instance,[EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var subscription=bus.Subscribe(instance);
        await foreach (var envelope in subscription.ReadAsync(cancellationToken)) yield return envelope.Message.Clone();
    }
}
