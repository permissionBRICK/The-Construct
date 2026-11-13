using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;

namespace Constructd.Tests.Provisioning;

/// <summary>One call the job made: the command (an argument of ssh) and what went on stdin.</summary>
public sealed record SshCall(SshTarget Target, string Command, string? StandardInput);

/// <summary>
/// A scripted guest. It answers each kind of command the provision job sends, keeps a
/// <c>provisioned.env</c> the way <c>provision.sh</c> writes it (from the variables in the command),
/// and can hold a step until the test releases it.
/// </summary>
public partial class FakeSshRunner : ISshRunner
{
    public ConcurrentQueue<SshCall> Calls { get; } = new();
    public List<string> CopiedTarEntries { get; } = [];
    public Dictionary<string, string> Marker { get; } = new(StringComparer.Ordinal);

    /// <summary>Output and exit code of the main provision.sh run; the default is a clean run.</summary>
    public Func<SshCall, (int Exit, string[] Lines)> Provision { get; set; } = _ => (0, Clean);
    public Func<SshCall, (int Exit, string[] Lines)> ProjectPhase { get; set; } = _ => (0, Clean);
    public int UnpackExit { get; set; }
    public bool RebootRequired { get; set; }
    public string? SudoPrompt { get; set; }

    /// <summary>A step name ("copy", "provision", "project-commands", "applied") whose first command waits here.</summary>
    public string? HoldAt { get; set; }
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static readonly string[] Clean = ["installing...", "===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT==="];

    public static string[] WithErrors(params (string Title, int Exit, string Log)[] errors) =>
        ["installing...", "===CONSTRUCT-PROVISION-RESULT===", $"errors={errors.Length}",
            .. errors.Select(e => $"error={e.Title}|{e.Exit}|{e.Log}"), "===END-CONSTRUCT-PROVISION-RESULT==="];

    public IEnumerable<string> Commands => Calls.Select(c => c.Command);

    public virtual async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        var call = new SshCall(target, command, standardInput);
        Calls.Enqueue(call);
        var step = command.Contains("PROVISION_PHASE", StringComparison.Ordinal) ? "project-commands"
            : command.Contains("bin/provision.sh", StringComparison.Ordinal) ? "provision"
            : command.Contains("construct-repo.tar.gz", StringComparison.Ordinal) ? "copy"
            : command.Contains("provisioned.env", StringComparison.Ordinal) ? "applied" : "other";
        if (HoldAt == step && !command.StartsWith("umask 077", StringComparison.Ordinal))
        {
            HoldAt = null;
            Held.TrySetResult();
            await Release.Task;
        }

        if (SudoPrompt is not null && command.StartsWith("sudo -n", StringComparison.Ordinal))
        {
            lines?.Report(SudoPrompt);
            return 1;
        }

        (int Exit, string[] Lines) answer = step switch
        {
            "project-commands" => ProjectPhase(call),
            "provision" => ProvisionAndMark(call),
            "copy" => (command.Contains("tar -xzf", StringComparison.Ordinal) ? UnpackExit : 0, []),
            "applied" => (0, Marker.Select(kv => $"{kv.Key}={kv.Value}").ToArray()),
            _ when command.Contains("reboot-required", StringComparison.Ordinal) => (0, [RebootRequired ? "yes" : "no"]),
            _ => (0, []),
        };
        foreach (var line in answer.Lines) lines?.Report(line);
        return answer.Exit;
    }

    private (int, string[]) ProvisionAndMark(SshCall call)
    {
        var answer = Provision(call);
        if (answer.Exit is 0 or 3)
        {
            foreach (var (name, key) in new[] { ("CONSTRUCT_VERSION", "CONSTRUCT_COMMIT"), ("CONSTRUCT_PAYLOAD_HASH", "CONSTRUCT_PAYLOAD_HASH"),
                ("CONSTRUCT_DESIRED_HASH", "CONSTRUCT_DESIRED_HASH"), ("CONSTRUCT_CHANNEL", "CONSTRUCT_CHANNEL") })
                if (Variable(call.Command, name) is { } value) Marker[key] = value;
            Marker["REPROVISIONED_AT"] = "2026-11-13T00:00:00Z";
        }

        return answer;
    }

    [GeneratedRegex(@"(?<![A-Z_])([A-Z0-9_]+)='((?:[^']|'\\'')*)'")]
    private static partial Regex Assignment();

    /// <summary>The value of NAME='...' in a command, as the guest shell would see it (escapes undone twice for bash -lc).</summary>
    public static string? Variable(string command, string name)
    {
        var inner = Unwrap(command);
        foreach (Match m in Assignment().Matches(inner))
            if (m.Groups[1].Value == name) return m.Groups[2].Value.Replace("'\\''", "'", StringComparison.Ordinal);
        return null;
    }

    /// <summary>The script inside <c>bash -lc '…'</c>.</summary>
    public static string Unwrap(string command)
    {
        var start = command.IndexOf("bash -lc '", StringComparison.Ordinal);
        if (start < 0) return command;
        var end = command.LastIndexOf("' 2>&1", StringComparison.Ordinal);
        return command[(start + 10)..end].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct)
    {
        Calls.Enqueue(new SshCall(target, "scp " + remotePath, null));
        using var file = File.OpenRead(localPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry) CopiedTarEntries.Add($"{entry.Name}|{Convert.ToString((int)entry.Mode, 8)}");
        return Task.CompletedTask;
    }
}
