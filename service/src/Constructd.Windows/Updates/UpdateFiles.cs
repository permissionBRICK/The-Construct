using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Constructd.Windows.Updates;

public sealed record UpdateFile(string Path, string Sha256);
public sealed record VerifiedFiles(string UpdateId, string Commit, DateTimeOffset VerifiedAt, IReadOnlyList<UpdateFile> Files);
public sealed record InstallRecord(string Commit, string PackageVersion, DateTimeOffset InstalledAt, string? PreviousCommit, string? UpdateId, IReadOnlyList<UpdateFile> Files);
public static class UpdateFiles
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, ct), Json) : default;
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Sha256(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new Core.Logic.UpdateException("extraction-refused");
    }
}
