using System.Security.Cryptography;

namespace Constructd.Core.Logic;

/// <summary>
/// The one thing this service needs to know about an SSH public key: its fingerprint.
///
/// It goes into the ISO catalog's sidecar, because "which bootstrap key is baked into this media?"
/// is the question behind the only failure that looks like a working install and is not: a client
/// whose key the guest does not authorize provisions right up to the SSH handshake and then stops.
/// It is the same value <c>ssh-keygen -lf</c> prints, so an admin can compare the two by eye.
/// </summary>
public static class SshPublicKey
{
    /// <summary>
    /// <c>SHA256:…</c> for an <c>authorized_keys</c>-style line, or false when the text is not one.
    /// Never throws: a key file the admin pointed at can be anything at all.
    /// </summary>
    public static bool TryFingerprint(string? publicKeyText, out string fingerprint)
    {
        fingerprint = string.Empty;

        if (string.IsNullOrWhiteSpace(publicKeyText))
        {
            return false;
        }

        // "<type> <base64 blob> [comment]" — the blob is the only field that is hashed, which is why
        // renaming the comment does not change the fingerprint.
        var fields = publicKeyText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (blob.Length == 0)
        {
            return false;
        }

        // ssh-keygen prints base64 with the padding stripped.
        fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return true;
    }

    /// <summary>The fingerprint, or a marker that says so — for a sidecar field that must exist.</summary>
    public static string FingerprintOrUnknown(string? publicKeyText) =>
        TryFingerprint(publicKeyText, out var fingerprint) ? fingerprint : "unknown";
}
