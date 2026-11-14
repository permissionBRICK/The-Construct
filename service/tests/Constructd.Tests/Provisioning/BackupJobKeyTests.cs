using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Core.Logic;

namespace Constructd.Tests.Provisioning;

/// <summary>The job key, the known-hosts edit and the <c>construct backup</c> output parsers.</summary>
public sealed class BackupJobKeyTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string E = "AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";

    private static string Keys(string id, params (string Fingerprint, string Wrapped)[] recipients) => new JsonObject
    {
        ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = "2026-11-13T10:00:00Z",
        ["recipients"] = new JsonArray(recipients.Select(r => (JsonNode)new JsonObject { ["fingerprint"] = r.Fingerprint, ["label"] = "x", ["wrapped"] = r.Wrapped }).ToArray()),
    }.ToJsonString();

    [Fact]
    public void The_job_key_unwraps_what_the_guest_wrapped_for_it_and_nothing_else()
    {
        var job = BackupJobKey.Create();
        var other = BackupJobKey.Create();
        Assert.Equal(job.Fingerprint, BackupJobKey.PublicFingerprint(job.PublicPem));
        Assert.Equal(job.Fingerprint, BackupJobKey.FingerprintOf(job.PrivatePem));
        Assert.Contains("BEGIN PUBLIC KEY", job.PublicPem, StringComparison.Ordinal);
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(job.PublicPem);
            Assert.Equal(3072, rsa.KeySize);
        }

        var keys = Keys(Id, (other.Fingerprint, BackupJobKey.Wrap(E, other.PublicPem)), (job.Fingerprint, BackupJobKey.Wrap(E, job.PublicPem)));
        Assert.Equal(E, BackupJobKey.Unwrap(keys, Id, job.PrivatePem));
        Assert.Equal(E, BackupJobKey.Unwrap(keys, Id, other.PrivatePem));
        // Another backup's keys file, a file without this key, a key that is not a backup key, junk.
        Assert.Null(BackupJobKey.Unwrap(keys, new string('f', 32), job.PrivatePem));
        Assert.Null(BackupJobKey.Unwrap(Keys(Id, (other.Fingerprint, BackupJobKey.Wrap(E, other.PublicPem))), Id, job.PrivatePem));
        Assert.Null(BackupJobKey.Unwrap(Keys(Id, (job.Fingerprint, BackupJobKey.Wrap("not a key", job.PublicPem))), Id, job.PrivatePem));
        Assert.Null(BackupJobKey.Unwrap(Keys(Id, (job.Fingerprint, "!!")), Id, job.PrivatePem));
        Assert.Null(BackupJobKey.Unwrap("{", Id, job.PrivatePem));
        Assert.Null(BackupJobKey.Unwrap(null, Id, job.PrivatePem));
    }

    /// <summary>The guest wraps with openssl (<c>build_keys_json</c>) and names keys by <c>key_fingerprint</c>: the engine must agree with both.</summary>
    [Fact]
    public void The_job_key_reads_what_openssl_wraps_as_the_guest_does()
    {
        var openssl = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "openssl")).FirstOrDefault(File.Exists);
        if (openssl is null || !OperatingSystem.IsLinux()) return;
        var job = BackupJobKey.Create();
        var dir = Directory.CreateTempSubdirectory("construct-jobkey-");
        try
        {
            var pub = Path.Combine(dir.FullName, "job.pem");
            File.WriteAllText(pub, job.PublicPem);
            var wrapped = Run(openssl, ["pkeyutl", "-encrypt", "-pubin", "-inkey", pub, "-pkeyopt", "rsa_padding_mode:oaep",
                "-pkeyopt", "rsa_oaep_md:sha256", "-pkeyopt", "rsa_mgf1_md:sha256"], Encoding.ASCII.GetBytes(E));
            Assert.Equal(E, BackupJobKey.Unwrap(Keys(Id, (job.Fingerprint, Convert.ToBase64String(wrapped))), Id, job.PrivatePem));
            var der = Run(openssl, ["pkey", "-pubin", "-in", pub, "-outform", "DER"], []);
            Assert.Equal(job.Fingerprint, Convert.ToHexStringLower(SHA256.HashData(der)));
        }
        finally { dir.Delete(true); }

        static byte[] Run(string file, string[] args, byte[] input)
        {
            var start = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return output.ToArray();
        }
    }

    [Fact]
    public void Known_hosts_loses_only_the_address_of_the_reinstalled_VM()
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        string Hashed(string name)
        {
            using var hmac = new HMACSHA1(salt);
            return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(name)))}";
        }

        var text = string.Join('\n',
            "# comment",
            "vm.test ssh-ed25519 AAAA1",
            "[vm.test]:2201 ssh-ed25519 AAAA2",
            "other.test,vm.test ssh-rsa AAAA3",
            Hashed("vm.test") + " ecdsa-sha2-nistp256 AAAA4",
            Hashed("[vm.test]:2201") + " ssh-ed25519 AAAA5",
            Hashed("keep.test") + " ssh-ed25519 AAAA6",
            "@cert-authority vm.test ssh-ed25519 AAAA7",
            "") ;

        var port22 = KnownHosts.Remove(text, "vm.test", 22);
        Assert.Equal(string.Join('\n',
            "# comment",
            "[vm.test]:2201 ssh-ed25519 AAAA2",
            "other.test ssh-rsa AAAA3",
            Hashed("[vm.test]:2201") + " ssh-ed25519 AAAA5",
            Hashed("keep.test") + " ssh-ed25519 AAAA6",
            ""), port22);

        var port2201 = KnownHosts.Remove(text, "vm.test", 2201);
        Assert.DoesNotContain("AAAA2", port2201, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA5", port2201, StringComparison.Ordinal);
        Assert.Contains("AAAA1", port2201, StringComparison.Ordinal);
        Assert.Contains("AAAA4", port2201, StringComparison.Ordinal);
        Assert.Equal("", KnownHosts.Remove("", "vm.test", 22));
    }

    [Fact]
    public void The_backup_cli_output_is_read_as_the_PowerShell_reads_it()
    {
        Assert.Equal(Id, BackupScript.CreatedId(["construct backup: staging", $"{{\"id\":\"{Id}\",\"sizeBytes\":1}}", "{not json"]));
        Assert.Null(BackupScript.CreatedId(["{\"id\":\"short\"}"]));
        var fp = new string('a', 64);
        Assert.Equal([fp], BackupScript.JobRecipients([$"[{{\"fingerprint\":\"{new string('b', 64)}\",\"label\":\"PC\"}},{{\"fingerprint\":\"{fp}\",\"label\":\"{BackupJobKey.Label}\"}}]"]));
        Assert.Empty(BackupScript.JobRecipients(["[]"])!);
        Assert.Null(BackupScript.JobRecipients(["no list"]));
        Assert.Throws<ArgumentException>(() => BackupScript.Restore("../x"));
        Assert.True(BackupScript.RestoreLeftVmUntouched(8));
        Assert.False(BackupScript.RestoreLeftVmUntouched(10));
        Assert.False(BackupScript.RestoreLeftVmUntouched(255));
    }
}
