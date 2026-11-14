using System.Text.Json.Nodes;
namespace Construct.Companion.Core.Abstractions;
public interface IInstanceConnections
{
    ISshTransport Ssh(JsonObject instance);
    Task<IForwardTransport> ForwardsAsync(JsonObject instance, ISshTransport ssh, CancellationToken token);
}
