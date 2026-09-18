using System.Security.Cryptography;
using System.Text;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Media;
namespace Constructd.Api.Jobs;

public sealed class WindowsMediaJobs(IMediaStore store, IMediaFiles files, IMediaTransfer transfer, IMediaGate gate,
    IClock clock, Constructd.Windows.Media.WindowsMediaResolver resolver, ICapacityLedger capacity, MediaJobs mediaJobs, ConstructdOptions options)
{
    private static string Id(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    public async Task<MediaItem> PrepareAsync(MediaItem source, IProgress<string>? progress, CancellationToken ct)
    {
        if (source.Windows?.Prepared == true) return source;
        var id = Id("windows-prepared:" + source.Id + ":" + source.Sha256);
        await using var locked = await gate.AcquireAsync(id, "windows-prepare", ct);
        if (await store.GetAsync(id, ct) is { State: MediaState.Ready } cached) return cached;
        if (source.State != MediaState.Ready || source.Role != MediaRole.Install) throw new MediaException("media-not-ready");
        progress?.Report("Inspecting Windows images and preparing the no-prompt boot catalog.");
        WindowsMedia metadata;
        await using (var input = await files.OpenReadAsync(source.Path, ct)) metadata = WindowsIso.Inspect(input);
        if (source.Windows is null) await store.TryTransitionAsync(source.Id, MediaState.Ready, source with { Windows = metadata }, ct);
        var item = source with { Id = id, Path = files.PathFor(id), Name = source.Name + " (prepared)", State = MediaState.Transferring,
            Windows = metadata with { Prepared = true, OriginalId = source.Id }, Sha256 = null, ExpectedSha256 = null, JobId = null, ReservedBytes = source.SizeBytes ?? 0 };
        await ResetAsync(item, ct);
        try
        {
            await files.CreateAsync(files.PathFor(id, true), 0, ct);
            await using (var input = await files.OpenReadAsync(source.Path, ct))
            await using (var output = new FileStream(files.PathFor(id, true), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await WindowsIso.PrepareAsync(input, output, ct);
            await files.PublishAsync(files.PathFor(id, true), item.Path, ct);
            return await ReadyAsync(item, ct);
        }
        catch { await FailedAsync(item); throw new MediaException("windows-prepare-failed"); }
    }
    public async Task<MediaItem> AcquireAsync(string product, string edition, string language, IProgress<string>? progress, CancellationToken ct)
    {
        var selection = WindowsUnattendRenderer.Parse(product + "-" + edition);
        var id = Id("official-windows:" + selection.Product + ":" + language);
        await using var locked = await gate.AcquireAsync(id, "windows-acquire", ct);
        var original = await store.GetAsync(id, ct);
        if (original is not { State: MediaState.Ready })
        {
            var url = await resolver.ResolveAsync(product, language, ct);
            original = new(id, "host", product + " " + language, MediaRole.Install, MediaSource.Url, null, files.PathFor(id), MediaState.Transferring,
                null, 12L << 30, null, null, null, null, null, clock.UtcNow, null, null, true);
            await ResetAsync(original, ct);
            try
            {
                var result = await transfer.AcquireAsync(original, url, 12L << 30, TimeSpan.FromHours(3), progress, ct);
                await using var input = await files.OpenReadAsync(original.Path, ct);
                var metadata = WindowsIso.Inspect(input);
                if (metadata.Product != product || !metadata.Images.Any(i => i.Edition == edition)) throw new MediaException("windows-image-missing");
                original = original with { Windows = metadata };
                original = await ReadyAsync(original, ct);
            }
            catch { await FailedAsync(original); throw new MediaException("windows-acquire-failed"); }
        }
        if (!original.Windows!.Images.Any(i => i.Edition == edition)) throw new MediaException("windows-image-missing");
        return await PrepareAsync(original, progress, ct);
    }
    public async Task<MediaItem> AuxiliaryAsync(Vm vm, WindowsImage image, WindowsUnattend request, CancellationToken ct)
    {
        var id = Id("windows-unattend:" + vm.CurrentJobId);
        await using var locked = await gate.AcquireAsync(id, "windows-unattend", ct);
        if (await store.GetAsync(id, ct) is { State: MediaState.Ready } existing) return existing;
        var content = new Dictionary<string, string>(WindowsUnattendRenderer.Render(WindowsUnattendRenderer.Parse(vm.Hardware!.Windows!), image, request))
        { ["construct-report.ps1"] = WindowsUnattendRenderer.GuestReportScript(options.IsProxmox) };
        var item = new MediaItem(id, vm.Owner, "Windows answer file", MediaRole.Auxiliary, MediaSource.Upload, null, files.PathFor(id), MediaState.Transferring,
            null, 4L << 20, null, null, null, vm.CurrentJobId, vm.Name, clock.UtcNow, null, null);
        await ResetAsync(item, ct);
        try
        {
            await files.CreateAsync(files.PathFor(id, true), 0, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(files.PathFor(id, true), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await using (var output = new FileStream(files.PathFor(id, true), FileMode.Open, FileAccess.Write, FileShare.None))
                await WindowsIso.BuildAuxiliaryAsync(content, output, ct);
            await files.PublishAsync(files.PathFor(id, true), item.Path, ct);
            return await ReadyAsync(item, ct);
        }
        catch { await FailedAsync(item); throw new MediaException("windows-unattend-failed"); }
    }
    public async Task<MediaItem> GuestAgentAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (options.Fake) throw new MediaException("windows-guest-agent-unavailable");
        var id = Id("virtio-win-stable");
        await using var locked = await gate.AcquireAsync(id, "guest-agent-media", ct);
        if (await store.GetAsync(id, ct) is { State: MediaState.Ready } cached) return cached;
        var item = new MediaItem(id, "host", "virtio-win guest agent", MediaRole.Auxiliary, MediaSource.Url, null,
            files.PathFor(id), MediaState.Transferring, null, 1L << 30, null, null, null, null, null, clock.UtcNow, null, null, true);
        await ResetAsync(item, ct);
        try
        {
            await transfer.AcquireAsync(item, new("https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso"), 1L << 30, TimeSpan.FromHours(1), progress, ct);
            return await ReadyAsync(item, ct);
        }
        catch { await FailedAsync(item); throw new MediaException("windows-guest-agent-media-failed"); }
    }
    private async Task ResetAsync(MediaItem item, CancellationToken ct)
    {
        if (await store.GetAsync(item.Id, ct) is { } old)
        {
            if ((await store.ListReferencesAsync(item.Id, ct)).Count != 0) throw new MediaException("media-in-use");
            if (!await mediaJobs.DeleteLockedAsync(old, ct)) throw new MediaException("cleanup-pending");
            await store.RemoveAsync(old.Id, ct);
        }
        var decision = await capacity.TryReserveAsync(new(item.Owner, null, "windows-media:" + item.Id,
            [new(ReservationResource.Storage, item.ReservedBytes, item.Path, Path.GetPathRoot(files.Root))], TimeSpan.FromHours(4)), ct);
        if (!decision.Allowed) throw new MediaException("capacity-exhausted");
        await store.AddAsync(item, ct);
        await capacity.ConfirmAsync(decision.ReservationIds, VmState.Off, ct);
    }
    private async Task<MediaItem> ReadyAsync(MediaItem item, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(item.Path, ct);
        var ready = item with { State = MediaState.Ready, SizeBytes = stream.Length, ReservedBytes = stream.Length,
            Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), ReadyAt = clock.UtcNow };
        if (!await store.TryTransitionAsync(item.Id, MediaState.Transferring, ready, ct)) throw new MediaException("media-not-ready");
        foreach (var id in await mediaJobs.ReservationIdsAsync(item, ct)) await capacity.TrimAsync(id, stream.Length, ct);
        return ready;
    }
    private async Task FailedAsync(MediaItem item)
    {
        await mediaJobs.FailLockedAsync(item, "windows-media-failed");
    }
}
