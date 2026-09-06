using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Iso;

/// <summary>Invokes the independently released, self-contained .NET ISO tool using its stdin protocol.</summary>
public sealed class NativeIsoBuilder(IProcessRunner processes, IIsoFileSystem files, IIsoDownloader downloader,
    ConstructdOptions options, ILogger<NativeIsoBuilder> logger)
    : IsoBuilderBase(processes, files, downloader, options, logger)
{
    protected override string BuilderName => "native";
    protected override string GetBuilderPath(string scriptsDir) => ArgumentGuard.WindowsPath(
        string.IsNullOrWhiteSpace(_options.Iso.NativeBuilderPath)
            ? $@"{scriptsDir}\.construct-tools\iso\Construct.Iso.exe"
            : _options.Iso.NativeBuilderPath, "Constructd:Iso:NativeBuilderPath");

    protected override Task<ProcessResult> RunToolAsync(string sourceIso, string outputIso, string builderPath,
        string user, string password, IReadOnlyList<string> identity, string pubKey, string sourceId,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string Identity(string key, string fallback) => identity.FirstOrDefault(v => v.StartsWith(key + "=", StringComparison.Ordinal))
            is { } item ? item[(key.Length + 1)..] : fallback;
        var request = JsonSerializer.Serialize(new
        {
            SourceIso = sourceIso, OutputIso = outputIso, BootstrapPublicKeyPath = pubKey,
            User = user, Password = password, Hostname = Identity("VM_HOST", "agent-vm"),
            HostnameSource = Identity("VM_HOSTNAME_SOURCE", "static"), SourceId = sourceId,
            // The catalog reserves a new output with an empty file before invoking us.
            Overwrite = true
        });
        return _processes.RunAsync(builderPath, ["--request-stdin"], request, BuildTimeout, progress, cancellationToken);
    }
}
