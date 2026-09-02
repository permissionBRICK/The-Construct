namespace Constructd.Windows.Iso;

/// <summary>
/// Fetches the source Ubuntu ISO the autoinstall image is remastered from. One admin-configured URL,
/// downloaded once into the cache — the service never goes looking for "the current LTS" on its own,
/// so a host's guests cannot change release because a mirror did.
/// </summary>
public interface IIsoDownloader
{
    Task DownloadAsync(Uri source, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>HTTPS download straight to disk, streamed (the file is gigabytes).</summary>
public sealed class HttpIsoDownloader(HttpClient client) : IIsoDownloader
{
    private readonly HttpClient _client = client;

    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Downloaded next to the destination and renamed, so an interrupted download can never be
        // mistaken for a complete cached ISO on the next start.
        var partial = destinationPath + ".part";

        progress?.Report($"downloading the source ISO from {source.Host}");

        using var response = await _client
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var target = File.Create(partial))
        {
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(partial, destinationPath, overwrite: true);
        progress?.Report("source ISO downloaded");
    }
}
