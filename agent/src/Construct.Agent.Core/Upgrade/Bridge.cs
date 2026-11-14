using System.Text.Json;

namespace Construct.Agent.Upgrade;

/// <summary>
/// <c>bridge.json</c> in the agent's directory: where the one-time upgrade stands (11.6, 13.2). <c>stage</c> is
/// <c>converted</c> only once everything was copied; until then the steps say what is done. <c>done</c> lists
/// the items the converter already copied, so a second run leaves them alone even if they were changed since.
/// No secret is ever written here.
/// </summary>
public sealed record BridgeState
{
    public const string Converted = "converted";

    /// <summary><c>new</c>, <c>converting</c> or <c>converted</c>.</summary>
    public string Stage { get; init; } = "new";

    /// <summary>The previous install folder the converter read.</summary>
    public string? From { get; init; }

    /// <summary>The engine step: <c>done</c> (the solo engine or the shared hosts were added) or <c>skipped</c> (nothing local).</summary>
    public string? Engine { get; init; }

    /// <summary>This PC's own (solo) engine, which adopts the local VMs; null when the PC has none.</summary>
    public string? LocalEngine { get; init; }

    /// <summary>The engine that holds the account's store (the first-device setup's engine).</summary>
    public string? StoreEngine { get; init; }

    public IReadOnlyList<string> Done { get; init; } = [];

    /// <summary>The last run's items and their outcome, for the dialog.</summary>
    public IReadOnlyList<UpgradeItem> Items { get; init; } = [];

    /// <summary>What the last run says the user should know (leases end, upstream links kept as data, ...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The account the last run wrote <see cref="Notes"/> for; they are shown to that account only.</summary>
    public string? NotesAccount { get; init; }

    /// <summary>The user went on from the notes after the conversion; they are not shown again.</summary>
    public bool NotesRead { get; init; }

    /// <summary>Shared hosts that still run the old stack (no <c>GET /engine</c>): this device stays signed in the old way there.</summary>
    public IReadOnlyList<string> OldHosts { get; init; } = [];

    public DateTimeOffset? ConvertedAt { get; init; }
}

/// <summary>One item of a conversion run: <c>done</c>, <c>skipped</c> (already there), <c>pending</c> (retried later) or <c>failed</c>.</summary>
public sealed record UpgradeItem(string Id, string Label, string State, string? Message = null);

public sealed class BridgeFile(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public string Path => path;

    public BridgeState Read()
    {
        lock (_gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<BridgeState>(File.ReadAllBytes(path), Json) ?? new() : new(); }
            catch (JsonException) { return new(); }
        }
    }

    public BridgeState Update(Func<BridgeState, BridgeState> change)
    {
        lock (_gate)
        {
            var next = change(Read());
            PrivateFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            return next;
        }
    }
}
