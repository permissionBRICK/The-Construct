using System.Text.Json;
using System.Text.Json.Serialization;
using Construct.Keys;

namespace Construct.Agent.State;

/// <summary>
/// What the agent keeps between runs, in <c>state.json</c> (0600). Every secret in it (the device's
/// private key, the store token, engine tokens) is held only protected (<see cref="Protection.IDataProtection"/>);
/// <c>U</c> is kept sealed to the device key (8.2). The engine list is the device's read cache of
/// its <c>engine</c> records (9.2) plus what it needs to talk to each.
/// </summary>
public sealed class AgentState
{
    public int Version { get; set; } = 1;

    /// <summary>The protection chosen at first run (<c>libsecret</c> or <c>file</c>).</summary>
    public string? Protection { get; set; }

    /// <summary>The device key pair, exported and protected (base64).</summary>
    public string? DeviceKey { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>The account this device belongs to; null until it is registered.</summary>
    public string? Account { get; set; }

    /// <summary>The engine whose built-in store holds the account (local engine id).</summary>
    public string? StoreEngine { get; set; }

    /// <summary>The device's store token, protected (base64).</summary>
    public string? StoreToken { get; set; }

    /// <summary><c>U</c> sealed to this device's key.</summary>
    public RootSeal? RootSeal { get; set; }

    public List<EngineEntry> Engines { get; set; } = [];

    /// <summary>A first-device setup that registered this device but has not finished; setup again resumes it.</summary>
    public bool SetupPending { get; set; }

    [JsonIgnore]
    public bool Registered => Account is not null && StoreToken is not null && RootSeal is not null && !SetupPending;
}

/// <summary>One engine the user works with.</summary>
public sealed class EngineEntry
{
    /// <summary>The agent's id for it: derived from the address, so every device names it alike.</summary>
    public required string Id { get; set; }

    /// <summary>The engine's base address (<c>https://host:port</c>).</summary>
    public required string Url { get; set; }

    /// <summary>SHA-256 of the engine's TLS certificate, upper-case hex, taken when it was added.</summary>
    public required string Pin { get; set; }

    public required string Name { get; set; }

    /// <summary>The engine's own id (<c>GET /engine</c>), once known.</summary>
    public string? EngineId { get; set; }

    public string? HostPublicKey { get; set; }

    /// <summary>This device's token at the engine, protected.</summary>
    public string? DeviceToken { get; set; }

    /// <summary>The user's sign-in at the engine (an API token), protected; kept until a device token replaces it.</summary>
    public string? UserToken { get; set; }

    /// <summary>The engine's id from an address (<see cref="Id"/>).</summary>
    public static string IdFor(string url) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.ToLowerInvariant())))[..12].ToLowerInvariant();
}

/// <summary>Reads and writes <c>state.json</c>.</summary>
public static class StateFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static AgentState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllBytes(path), Json) ?? new AgentState() : new AgentState();

    public static void Save(string path, AgentState state) => PrivateFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
}
