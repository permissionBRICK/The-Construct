using System.Text.Json;
using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// The <c>construct backup</c> calls the engine makes on a guest (<c>bin/construct-backup.sh</c>), run
/// from the repository copy as <c>Provision-AgentVM.ps1</c> does for enrolment, so they work before
/// and after provisioning installed the <c>construct</c> command. Keys travel on stdin only: a public
/// key for <c>recipients add</c>, the backup key <c>E</c> as the first line for <c>verify</c> and
/// <c>restore</c>. Pure.
/// </summary>
public static partial class BackupScript
{
    public const string Cli = "bash " + ProvisionScript.RepoDir + "/bin/construct-backup.sh";

    public static string RecipientsAdd(string label) => $"{Cli} recipients add --label {ProvisionScript.Quote(label)}";

    public static string RecipientsRemove(string fingerprint) =>
        BackupJobKey.IsFingerprint(fingerprint) ? $"{Cli} recipients remove {fingerprint}" : throw new ArgumentException("Not a fingerprint.", nameof(fingerprint));

    public const string CreatePreReinstall = Cli + " create --trigger pre-reinstall";

    public const string RecipientsList = Cli + " recipients list";

    /// <summary>
    /// The fingerprints labelled <see cref="BackupJobKey.Label"/> in the JSON array <c>recipients list</c>
    /// prints, or null when no such array is in the output.
    /// </summary>
    public static IReadOnlyList<string>? JobRecipients(IEnumerable<string> lines) => RecipientsListed(lines, BackupJobKey.Label);

    /// <summary>
    /// The fingerprints in the JSON array <c>recipients list</c> prints, only those with <paramref name="label"/>
    /// when given; null when no such array is in the output.
    /// </summary>
    public static IReadOnlyList<string>? RecipientsListed(IEnumerable<string> lines, string? label = null)
    {
        foreach (var raw in lines.Reverse())
        {
            var line = raw.Trim();
            if (!line.StartsWith('[')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                return doc.RootElement.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.Object
                        && (label is null || r.TryGetProperty("label", out var named) && named.ValueKind == JsonValueKind.String && named.GetString() == label)
                        && r.TryGetProperty("fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String && BackupJobKey.IsFingerprint(fp.GetString()))
                    .Select(r => r.GetProperty("fingerprint").GetString()!)
                    .ToList();
            }
            catch (JsonException) { }
        }

        return null;
    }

    public static string Verify(string backupId) => $"{Cli} verify --from-host {Id(backupId)}";

    public static string Restore(string backupId) => $"{Cli} restore --from-host {Id(backupId)}";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    public static bool IsBackupId(string? id) => id is not null && IdPattern().IsMatch(id);

    private static string Id(string id) => IsBackupId(id) ? id : throw new ArgumentException("Not a backup id.", nameof(id));

    /// <summary>The exit codes of <c>construct backup</c> after which a restore has not touched the VM: usage, keys, host, transfer, decryption, lock.</summary>
    public static bool RestoreLeftVmUntouched(int exitCode) => exitCode is >= 2 and <= 9;

    /// <summary>
    /// What <c>restore-config.sh</c> did with the backup's SSH host keys, by the line it printed:
    /// <c>Replaced</c> (the VM presents the backup's keys now, other ones than before), <c>Kept</c> (the
    /// VM had them already), or neither (the backup carried none, or they could not be put in place;
    /// the VM keeps its own).
    /// </summary>
    public static (bool Replaced, bool Kept) HostKeys(IEnumerable<string> lines)
    {
        bool replaced = false, kept = false;
        foreach (var line in lines)
        {
            switch (line.Trim())
            {
                case "restored SSH host keys": replaced = true; break;
                case "SSH host keys already match the backup": kept = true; break;
            }
        }

        return (replaced, kept);
    }

    /// <summary>What a <c>construct backup</c> exit code means (<c>Get-ConstructBackupExitMessage</c>).</summary>
    public static string ExitMessage(int exitCode) => exitCode switch
    {
        2 => "usage error",
        3 => "no backup recipients are enrolled",
        4 => "the VM has no host service or the host lacks config backups",
        5 => "not enough space on the VM",
        6 => "the host refused",
        7 => "the transfer failed",
        8 => "decryption or verification failed",
        9 => "another backup or restore is running",
        10 => "an export or restore step failed",
        255 => "the VM could not be reached over SSH",
        _ => $"exit {exitCode}",
    };

    /// <summary>The backup id in the JSON line <c>create</c> prints (<c>ConvertFrom-ConstructBackupCreateOutput</c>), the last one wins.</summary>
    public static string? CreatedId(IEnumerable<string> lines)
    {
        string? found = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String && IsBackupId(id.GetString()))
                    found = id.GetString();
            }
            catch (JsonException) { }
        }

        return found;
    }
}
