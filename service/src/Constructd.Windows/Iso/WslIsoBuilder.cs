using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Iso;

/// <summary>Retained shell/WSL strategy for hosts that explicitly select PerVm.</summary>
public sealed class WslIsoBuilder(IProcessRunner processes, IIsoFileSystem files, IIsoDownloader downloader,
    ConstructdOptions options, ILogger<WslIsoBuilder> logger)
    : IsoBuilderBase(processes, files, downloader, options, logger)
{
    protected override string BuilderName => "WSL";
    protected override string GetBuilderPath(string scriptsDir) => $@"{scriptsDir}\bin\build-autoinstall-iso.sh";

    protected override async Task<ProcessResult> RunToolAsync(string sourceIso, string outputIso, string builderPath,
        string user, string password, IReadOnlyList<string> identity, string pubKey, string sourceId,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var lfScript = builderPath[..builderPath.LastIndexOf('\\')] + @"\.build-autoinstall.lf.sh";
        _files.WriteAllText(lfScript, _files.ReadAllText(builderPath).Replace("\r", string.Empty, StringComparison.Ordinal));
        try
        {
            var arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(_options.WslDistro))
                arguments.AddRange(["-d", ArgumentGuard.Text(_options.WslDistro, "Constructd:WslDistro")]);
            arguments.AddRange(["-u", "root", "--", "env", $"VM_USER={user}", $"VM_PASS={password}"]);
            arguments.AddRange(identity);
            arguments.AddRange([$"SOURCE_ID={sourceId}",
                $"BOOTSTRAP_PUBKEY_FILE={WslPath.FromWindows(pubKey, "Constructd:Iso:BootstrapPublicKeyPath")}",
                "bash", WslPath.FromWindows(lfScript, "iso build script"),
                WslPath.FromWindows(sourceIso, "Constructd:Iso:SourcePath"), WslPath.FromWindows(outputIso, "output iso")]);
            return await _processes.RunAsync(_options.WslPath, arguments, null, BuildTimeout, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { _files.DeleteFile(lfScript); }
            catch (IOException) { }
        }
    }
}
