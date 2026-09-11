using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Windows.Updates;

public sealed record CheckedRelease(ReleaseDescriptor Release, ReleaseManifest Manifest, string[] Reasons, string Source = "self-contained");
public sealed class PackageStager(IReleaseSource source, IHostConfigStore config, ConstructdOptions options, IReleaseInfo installed, IProcessRunner? runner = null) : IUpdateStager
{
    public string UpdatesDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "updates");
    public async Task<UpdatesConfig> SettingsAsync(CancellationToken ct) => HostUpdateTrust.Apply(await config.GetAsync<UpdatesConfig>("updates", ct) ?? HostAdminDefaults.Updates, options);
    public async Task<CheckedRelease?> CheckAsync(string? releaseTag, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var releases = await source.ListHostReleasesAsync(settings.Repository, ct, releaseTag);
        var release = releases.OrderByDescending(r => r.PublishedAt).FirstOrDefault(r => releaseTag is null || r.Tag == releaseTag);
        if (release is null) return null;
        var temporary = Path.Combine(UpdatesDir, "check-" + Guid.NewGuid().ToString("n"));
        try { Directory.CreateDirectory(temporary); return await CheckReleaseAsync(release, temporary, ct); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private async Task<CheckedRelease> CheckReleaseAsync(ReleaseDescriptor release, string dir, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        await source.DownloadAsync(Asset(release, "manifest.json", 1024 * 1024), Path.Combine(dir, "manifest.json"), null, ct);
        var manifest = await ReadManifestAsync(dir, release, settings, ct);
        var reasons = UpdateCompatibility.Reasons(manifest, installed.SchemaVersion, installed.SchemaMinReadableBy, 1, HasSetting).ToList();
        var variant = await SelectSourceAsync(manifest, ct);
        var payload = Asset(release, variant == "framework-dependent" ? manifest.FrameworkDependentAsset! : manifest.PayloadAsset, 1024L * 1024 * 1024);
        var needed = checked(2 * payload.SizeBytes + 1024L * 1024 * 1024);
        foreach (var root in new[] { UpdatesDir, AppContext.BaseDirectory })
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < needed) reasons.Add("insufficient-space");
        return new(release, manifest, reasons.Distinct().ToArray(), variant);
    }
    private async Task<string> SelectSourceAsync(ReleaseManifest manifest, CancellationToken ct)
    {
        if (manifest.FrameworkDependentAsset is null || runner is null) return "self-contained";
        try
        {
            var result = await runner.RunAsync("dotnet", ["--list-runtimes"], null, TimeSpan.FromSeconds(15), null, ct);
            if (result.Succeeded && manifest.Runtimes!.All(r => result.StandardOutput.Split('\n').Any(line =>
                Regex.IsMatch(line.TrimEnd('\r'), "^" + Regex.Escape(r.Name) + " " + r.MajorVersion + @"\.\d+\.\d+ \[.+\]$"))))
                return "framework-dependent";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        return "self-contained";
    }
    private bool HasSetting(string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json")));
            var value = doc.RootElement;
            foreach (var segment in key.Split(':'))
            {
                var property = value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (property.Name is null) return false; value = property.Value;
            }
            return value.ValueKind != JsonValueKind.Null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return false; }
    }
    private async Task<ReleaseManifest> ReadManifestAsync(string dir, ReleaseDescriptor release, UpdatesConfig settings, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, "manifest.json"), ct);
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, UpdateFiles.Json) ?? throw new UpdateException("incompatible"); }
        catch (JsonException) { throw new UpdateException("incompatible"); }
        ManifestRules.Validate(manifest, settings.Repository, release); return manifest;
    }
    private static ReleaseAsset Asset(ReleaseDescriptor release, string name, long max)
    {
        var assets = release.Assets.Where(a => a.Name == name).ToArray();
        if (assets.Length != 1 || assets[0].SizeBytes <= 0 || assets[0].SizeBytes > max) throw new UpdateException("release-source-unreachable");
        return assets[0];
    }
    public async Task<StagedUpdate> StageAsync(string updateId, ReleaseDescriptor release, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = DirectoryFor(updateId); Directory.CreateDirectory(dir); UpdateFiles.NoLinks(dir);
        try
        {
            progress?.Report("check"); var check = await CheckReleaseAsync(release, dir, ct);
            if (check.Reasons.Length > 0) throw new UpdateException(check.Reasons[0]);
            progress?.Report("download");
            await source.DownloadAsync(Asset(release, check.Source == "framework-dependent" ? check.Manifest.FrameworkDependentAsset! : check.Manifest.PayloadAsset, 1024L * 1024 * 1024), Path.Combine(dir, "package.zip"), null, ct);
            progress?.Report("verify"); var files = ExtractAndVerify(dir, check.Manifest, check.Source);
            await UpdateFiles.WriteAsync(Path.Combine(dir, "verified.json"), new VerifiedFiles(updateId, check.Manifest.Commit, DateTimeOffset.UtcNow, files, check.Source), ct);
            return new(updateId, check.Manifest, dir, files.Select(f => f.Path).ToArray(), check.Source);
        }
        catch (Exception ex)
        {
            await UpdateFiles.WriteAsync(Path.Combine(dir, "verification-failed.json"), new { code = ex is UpdateException u ? u.Code : "verification-failed" }, CancellationToken.None);
            throw;
        }
    }
    public static IReadOnlyList<UpdateFile> ExtractAndVerify(string dir, ReleaseManifest manifest, string source = "self-contained")
    {
        try {return ExtractAndVerifyCore(dir,manifest,source);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or OverflowException) {throw new UpdateException("extraction-refused");}
    }
    private static IReadOnlyList<UpdateFile> ExtractAndVerifyCore(string dir, ReleaseManifest manifest, string source)
    {
        var zip = Path.Combine(dir, "package.zip");
        var fdd = source == "framework-dependent";
        var size = fdd ? manifest.FrameworkDependentSizeBytes : manifest.PayloadSizeBytes;
        var declaredTotal = fdd ? manifest.FrameworkDependentUncompressedSizeBytes : manifest.PayloadUncompressedSizeBytes;
        if (source is not ("framework-dependent" or "self-contained")) throw new UpdateException("incompatible");
        if (size is not null && new FileInfo(zip).Length != size) throw new UpdateException("payload-size-mismatch");
        if (UpdateFiles.Sha256(zip) != (fdd ? manifest.FrameworkDependentSha256 : manifest.PayloadSha256)) throw new UpdateException("payload-hash-mismatch");
        var extracted = Path.Combine(dir, "extracted"); UpdateFiles.NoLinks(extracted);
        using (var archive = ZipFile.OpenRead(zip))
        {
            CheckArchiveLengths(archive, declaredTotal);
            // Verify inflated lengths and hashes before creating destination files. Some ZIP
            // implementations truncate entry streams to the declared length, so count alone is insufficient.
            var hashes = archive.Entries.ToDictionary(e => e.FullName, e => CopyEntry(e, Stream.Null), StringComparer.OrdinalIgnoreCase);
            if (!hashes.TryGetValue("SHA256SUMS", out var sumsHash) || sumsHash != (fdd ? manifest.FrameworkDependentSumsSha256 : manifest.SumsSha256))
                throw new UpdateException("coverage-failed");
            using (var reader = new StreamReader(archive.GetEntry("SHA256SUMS")!.Open()))
            {
                var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length < 67 || line.Substring(64, 2) != "  " || !ManifestRules.Hash(line[..64]) || !ZipEntryRules.IsPayloadFile(line[66..]) ||
                        !covered.Add(line[66..]) || !hashes.TryGetValue(line[66..], out var hash) || hash != line[..64]) throw new UpdateException("coverage-failed");
                }
                if (covered.Count != hashes.Count - 1) throw new UpdateException("coverage-failed");
            }
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            Directory.CreateDirectory(extracted);
            foreach (var entry in archive.Entries)
            {
                var target = Path.Combine(extracted, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target); CopyEntry(entry, output);
            }
        }
        return VerifyFiles(extracted, manifest, source);
    }
    private static void CheckArchiveLengths(ZipArchive archive, long? declaredTotal)
    {
        if (archive.Entries.Count > 20000) throw new UpdateException("extraction-refused");
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!ZipEntryRules.IsSafe(name) || (name != "SHA256SUMS" && !ZipEntryRules.IsPayloadFile(name)) ||
                !names.Add(name) || (entry.ExternalAttributes & 0x400) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000) ||
                entry.Length > 268435456 || (total = checked(total + entry.Length)) > 1073741824 ||
                declaredTotal is not null && total > declaredTotal) throw new UpdateException("extraction-refused");
        }
        if (declaredTotal is not null && (declaredTotal <= 0 || total != declaredTotal)) throw new UpdateException("extraction-refused");
    }
    private static string CopyEntry(ZipArchiveEntry entry, Stream output)
    {
        using var input = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long written = 0; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            written = checked(written + read);
            if (written > entry.Length || written > 268435456) throw new UpdateException("extraction-refused");
            hash.AppendData(buffer, 0, read); output.Write(buffer, 0, read);
        }
        if (written != entry.Length) throw new UpdateException("extraction-refused");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static IReadOnlyList<UpdateFile> VerifyFiles(string extracted, ReleaseManifest manifest, string source = "self-contained")
    {
        var sums = Path.Combine(extracted, "SHA256SUMS");
        if (!File.Exists(sums) || UpdateFiles.Sha256(sums) != (source == "framework-dependent" ? manifest.FrameworkDependentSumsSha256 : manifest.SumsSha256)) throw new UpdateException("coverage-failed");
        var files = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(sums))
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !ManifestRules.Hash(line[..64]) ||
                !ZipEntryRules.IsPayloadFile(line[66..]) || !files.TryAdd(line[66..], new(line[66..], line[..64]))) throw new UpdateException("coverage-failed");
        }
        foreach (var file in Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories))
        {
            UpdateFiles.NoLinks(file); var relative = Path.GetRelativePath(extracted, file).Replace('\\', '/');
            if (relative == "SHA256SUMS") continue;
            if (!files.TryGetValue(relative, out var record) || UpdateFiles.Sha256(file) != record.Sha256) throw new UpdateException("coverage-failed");
        }
        if (files.Values.Any(f => !File.Exists(Path.Combine(extracted, f.Path))) ||
            !files.TryGetValue(manifest.UpdaterPath, out var updater) || updater.Sha256 != manifest.UpdaterSha256) throw new UpdateException("coverage-failed");
        return files.Values.ToArray();
    }
    public async Task<bool> VerifyStagedAsync(StagedUpdate staged, CancellationToken ct)
    {
        try
        {
            var dir = DirectoryFor(staged.UpdateId); if (dir != staged.StagedPath) return false;
            var m = await ReadManifestAsync(dir, new(staged.Manifest.ReleaseTag, staged.Manifest.Commit, default, []), await SettingsAsync(ct), ct);
            var verified = await UpdateFiles.ReadAsync<VerifiedFiles>(Path.Combine(dir, "verified.json"), ct);
            if (verified is null || verified.Source != staged.Source || staged.Source is not ("framework-dependent" or "self-contained")) return false;
            var fdd = staged.Source == "framework-dependent";
            if (fdd && await SelectSourceAsync(m, ct) != "framework-dependent") return false;
            var zip = Path.Combine(dir, "package.zip");
            var size = fdd ? m.FrameworkDependentSizeBytes : m.PayloadSizeBytes;
            if (m.Commit != staged.Manifest.Commit || UpdateFiles.Sha256(zip) != (fdd ? m.FrameworkDependentSha256 : m.PayloadSha256) ||
                size is not null && new FileInfo(zip).Length != size) return false;
            using (var archive = ZipFile.OpenRead(zip))
                CheckArchiveLengths(archive, fdd ? m.FrameworkDependentUncompressedSizeBytes : m.PayloadUncompressedSizeBytes);
            var files = VerifyFiles(Path.Combine(dir, "extracted"), m, staged.Source);
            return verified.UpdateId == staged.UpdateId && verified.Commit == m.Commit && files.SequenceEqual(verified.Files);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or JsonException or InvalidDataException or OverflowException) { return false; }
    }
    public string DirectoryFor(string id)
    { if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new UpdateException("update-not-staged"); return Path.Combine(UpdatesDir, id); }
    public Task RemoveStagedAsync(string updateId, CancellationToken ct)
    { var dir = DirectoryFor(updateId); UpdateFiles.NoLinks(dir); if (Directory.Exists(dir)) Directory.Delete(dir, true); return Task.CompletedTask; }
}
