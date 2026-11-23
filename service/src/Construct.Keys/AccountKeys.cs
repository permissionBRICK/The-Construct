using System.Security.Cryptography;

namespace Construct.Keys;

/// <summary>
/// One user's keys (8.2): the root <c>U</c> and the independent keys under it, <c>K</c> (vault),
/// <c>D</c> (sync records) and <c>B</c> (backup recipient, RSA-3072). Every wrap carries the epoch
/// in clear and binds it as associated data. Only full devices hold this in clear.
/// </summary>
public sealed class AccountKeys : IDisposable
{
    public const int RsaBits = 3072;

    private AccountKeys(long epoch, byte[] root, byte[] vault, byte[] records, RSA backup)
    {
        Epoch = epoch;
        Root = root;
        Vault = vault;
        Records = records;
        Backup = backup;
    }

    public long Epoch { get; }

    /// <summary><c>U</c>, 256 bits.</summary>
    public byte[] Root { get; }

    /// <summary><c>K</c>, 256 bits; today's vault key is carried over as this key (13.1).</summary>
    public byte[] Vault { get; }

    /// <summary><c>D</c>, 256 bits.</summary>
    public byte[] Records { get; }

    /// <summary><c>B</c>, the backup recipient key pair.</summary>
    public RSA Backup { get; }

    /// <param name="vaultKey">An existing vault key to keep as <c>K</c>; a new one when null.</param>
    public static AccountKeys Create(long epoch = 1, byte[]? vaultKey = null)
    {
        if (vaultKey is { Length: not 32 }) throw new ArgumentException("The vault key is 256 bits.", nameof(vaultKey));
        return new AccountKeys(epoch, RandomNumberGenerator.GetBytes(32), vaultKey?.ToArray() ?? RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32), RSA.Create(RsaBits));
    }

    /// <summary><c>wrap(U, K)</c>, <c>wrap(U, D)</c>, <c>wrap(U, B.priv)</c> and <c>B.pub</c> in clear.</summary>
    public WrappedKeys Wrap() => new(
        Epoch,
        KeyWrap.Wrap(Root, Vault, Context("K", Epoch)),
        KeyWrap.Wrap(Root, Records, Context("D", Epoch)),
        KeyWrap.Wrap(Root, Backup.ExportPkcs8PrivateKey(), Context("B", Epoch)),
        Backup.ExportSubjectPublicKeyInfo());

    /// <summary>Unwraps everything under <paramref name="root"/>; throws when <c>U</c> is not the one they were wrapped under.</summary>
    public static AccountKeys Open(byte[] root, WrappedKeys wrapped)
    {
        var backup = RSA.Create();
        backup.ImportPkcs8PrivateKey(KeyWrap.Unwrap(root, wrapped.Backup, Context("B", wrapped.Epoch)), out _);
        return new AccountKeys(wrapped.Epoch, root.ToArray(),
            KeyWrap.Unwrap(root, wrapped.Vault, Context("K", wrapped.Epoch)),
            KeyWrap.Unwrap(root, wrapped.Records, Context("D", wrapped.Epoch)),
            backup);
    }

    /// <summary><c>U</c> sealed to a device key, for that device to keep (8.2).</summary>
    public RootSeal SealRootTo(ParticipantPublicKey device) => new(Epoch, SealedBox.Seal(device, Root, RootContext(Epoch)));

    public static byte[] OpenRoot(ParticipantKey device, RootSeal seal) => device.Open(seal.Sealed, RootContext(seal.Epoch));

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Root);
        CryptographicOperations.ZeroMemory(Vault);
        CryptographicOperations.ZeroMemory(Records);
        Backup.Dispose();
    }

    private static string Context(string name, long epoch) => $"account-key/{name}/{epoch}";

    private static string RootContext(long epoch) => $"root/device/{epoch}";
}

/// <summary>The account's keys as the store keeps them: wrapped under <c>U</c>, with the epoch in clear.</summary>
public sealed record WrappedKeys(long Epoch, byte[] Vault, byte[] Records, byte[] Backup, byte[] BackupPublic);

/// <summary><c>U</c> sealed to one public key, with its epoch in clear.</summary>
public sealed record RootSeal(long Epoch, byte[] Sealed);
