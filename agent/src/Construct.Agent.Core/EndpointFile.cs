using System.Text.Json;

namespace Construct.Agent;

/// <summary>
/// The token file (6.2, as the Companion's <c>endpoint.json</c>): the running agent's loopback port and
/// the token every API call carries. Written 0600 at start with a new token, deleted at stop.
/// </summary>
public sealed record EndpointFile(int Version, int Port, string Token, int Pid, DateTimeOffset StartedAt, string AgentVersion)
{
    public const string FileName = "endpoint.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static EndpointFile? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<EndpointFile>(File.ReadAllBytes(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string path) => PrivateFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));
}
