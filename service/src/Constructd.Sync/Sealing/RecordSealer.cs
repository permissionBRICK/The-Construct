using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Store.Records;

namespace Constructd.Sync.Sealing;

/// <summary>
/// Seals a payload with <c>D</c> using AES-256-GCM (9.2). The clear envelope is the associated
/// data, as canonical JSON (keys in ordinal order, no whitespace), so a ciphertext opens only under
/// the id, kind, epoch, actor, base revision, write id and deletion flag it was written with.
/// Layout: 12-byte nonce, ciphertext, 16-byte tag.
/// </summary>
public static class RecordSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonWriterOptions Canonical = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static byte[] Seal(byte[] key, RecordEnvelope envelope, JsonObject payload)
    {
        var plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length), AssociatedData(envelope));
        return sealedBytes;
    }

    /// <summary>Opens a stored record's payload; throws <see cref="CryptographicException"/> when it was not sealed for this envelope.</summary>
    public static JsonObject Open(byte[] key, RecordEnvelope envelope, byte[] sealedBytes)
    {
        if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("The sealed payload is too short.");
        var length = sealedBytes.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize, length), sealedBytes.AsSpan(NonceSize + length), plain, AssociatedData(envelope));
        return JsonNode.Parse(plain) as JsonObject ?? throw new CryptographicException("The payload is not a JSON object.");
    }

    /// <summary>The canonical JSON of the envelope.</summary>
    public static byte[] AssociatedData(RecordEnvelope envelope)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Canonical))
        {
            writer.WriteStartObject();
            writer.WriteString("actor", envelope.Actor);
            writer.WriteNumber("baseRevision", envelope.BaseRevision);
            writer.WriteBoolean("deleted", envelope.Deleted);
            writer.WriteNumber("epoch", envelope.Epoch);
            writer.WriteString("id", envelope.Id);
            writer.WriteString("kind", envelope.Kind);
            writer.WriteString("writeId", envelope.WriteId);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
