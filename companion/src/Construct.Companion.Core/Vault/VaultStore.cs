using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using static Construct.Companion.Core.Runtime.RuntimeJson;
namespace Construct.Companion.Core.Vault;

// The whole vault (secrets, leases, pending cleanups) is one JSON document, DPAPI CurrentUser
// protected and stored as base64 text like the remote/<slug>.token files. It lives outside the
// recursively watched The-Construct state root so a lease counting down does not wake every watcher.
public sealed class VaultStore(IFileSystem files, IDataProtection protection, string path)
{
    public string PathName => path;
    public static string DefaultPath(string localAppData) => Path.Combine(localAppData, "The-Construct-Vault", "vault.dat");

    // A file that exists but cannot be opened is never treated as empty: saving over it would
    // destroy every secret (a profile copied to another PC, a DPAPI master key change).
    public VaultDocument Load()
    {
        byte[]? stored;
        try { stored = files.ReadFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultUnavailableException("The key vault file cannot be read."); }
        if (stored is null) return new();
        byte[] raw;
        try { raw = protection.Unprotect(Convert.FromBase64String(Encoding.UTF8.GetString(stored).Trim().TrimStart('﻿'))); }
        catch (Exception e) when (e is FormatException or CryptographicException) { throw new VaultUnavailableException("The key vault cannot be decrypted by this Windows account."); }
        try { return Parse(JsonNode.Parse(raw) as JsonObject ?? throw new VaultUnavailableException("The key vault file is damaged.")); }
        catch (JsonException) { throw new VaultUnavailableException("The key vault file is damaged."); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    public void Save(VaultDocument document)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(Serialize(document));
        try { files.WriteFileAtomic(path, Encoding.UTF8.GetBytes(Convert.ToBase64String(protection.Protect(raw)))); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    // Moves an unreadable file aside so the user can start over without losing the evidence.
    public string QuarantineUnreadable(DateTimeOffset now)
    {
        var target = path + ".unreadable-" + now.ToUnixTimeSeconds();
        var bytes = files.ReadFile(path);
        if (bytes is not null) { files.WriteFileAtomic(target, bytes); files.DeleteFile(path); }
        return target;
    }

    private static JsonObject Serialize(VaultDocument document) => new()
    {
        ["v"] = 1,
        ["secrets"] = new JsonArray(document.Secrets.Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name, ["description"] = s.Description, ["username"] = s.Username, ["secret"] = s.Value.Reveal(),
            ["createdAt"] = s.CreatedAt.ToUnixTimeMilliseconds(), ["updatedAt"] = s.UpdatedAt.ToUnixTimeMilliseconds(), ["origin"] = s.Origin
        }).ToArray()),
        ["leases"] = new JsonArray(document.Leases.Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Id, ["instance"] = l.Instance, ["name"] = l.Name, ["usesLeft"] = l.UsesLeft, ["expiresAt"] = l.ExpiresAt.ToUnixTimeMilliseconds(),
            ["grantedAt"] = l.GrantedAt.ToUnixTimeMilliseconds(), ["reason"] = l.Reason, ["origin"] = l.Origin
        }).ToArray()),
        ["cleanups"] = new JsonArray(document.Cleanups.Select(c => (JsonNode)new JsonObject
        {
            ["instance"] = c.Instance, ["name"] = c.Name, ["secret"] = c.Value.Reveal(), ["username"] = c.Username, ["dueAt"] = c.DueAt.ToUnixTimeMilliseconds()
        }).ToArray())
    };

    private static VaultDocument Parse(JsonObject root)
    {
        var document = new VaultDocument();
        foreach (var s in root.Array("secrets").OfType<JsonObject>())
            if (VaultProtocol.IsValidName(s.Str("name")) && s.Str("secret").Length > 0)
                document.Secrets.Add(new(s.Str("name"), s.Str("description"), s.Str("username"), new(s.Str("secret")), Time(s["createdAt"]), Time(s["updatedAt"]), s.Str("origin")));
        foreach (var l in root.Array("leases").OfType<JsonObject>())
            if (VaultProtocol.IsValidName(l.Str("name")) && l.Str("instance").Length > 0)
                document.Leases.Add(new(l.Str("id"), l.Str("instance"), l.Str("name"), Int(l["usesLeft"]), Time(l["expiresAt"]), Time(l["grantedAt"]), l.Str("reason"), l.Str("origin")));
        foreach (var c in root.Array("cleanups").OfType<JsonObject>())
            if (c.Str("instance").Length > 0 && c.Str("secret").Length > 0)
                document.Cleanups.Add(new(c.Str("instance"), c.Str("name"), new(c.Str("secret")), c.Str("username"), Time(c["dueAt"])));
        return document;
    }
    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) && d == Math.Truncate(d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;
    private static DateTimeOffset Time(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(d, 0, 253402300799999)) : DateTimeOffset.UnixEpoch;
}
