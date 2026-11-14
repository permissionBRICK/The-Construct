using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Constructd.Api.Contracts;
using Constructd.Api.Devices;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Store;

/// <summary>
/// A device's SSH key on its user's VMs (design 5.1, 7.2): registered with the device token, written
/// into root's <c>authorized_keys</c> as marked lines, taken off at a revocation, retried at the
/// heartbeat, and never on another user's VM; every other line of the file stays.
/// </summary>
public sealed class DeviceSshKeyTests
{
    private static readonly string UserLine = NewKey("someone@pc");

    public static string NewKey(string comment = "device")
    {
        var type = "ssh-ed25519"u8.ToArray();
        var blob = new byte[4 + type.Length + 4 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)type.Length);
        type.CopyTo(blob, 4);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4 + type.Length), 32);
        RandomNumberGenerator.Fill(blob.AsSpan(8 + type.Length));
        return $"ssh-ed25519 {Convert.ToBase64String(blob)} {comment}";
    }

    private static string Bare(string key) => string.Join(' ', key.Split(' ').Take(2));

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    private static async Task<TestDevice> PairAsync(ProfileSyncHarness h, string name)
    {
        var device = new TestDevice(name, () => h.App.Server.CreateHandler(), ProfileSyncHarness.StoreBase);
        var (pairingId, _) = await device.StartPairingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Laptop!.ApproveAsync(pairingId)).Response.StatusCode);
        await device.CompletePairingAsync(pairingId);
        device.EngineTokens[h.EngineId] = await h.EnrollAtEngineAsync(device);
        return device;
    }

    [Fact]
    public async Task Registered_keys_reach_the_users_running_VMs_as_marked_lines_and_other_lines_stay()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        Directory.CreateDirectory(Path.GetDirectoryName(h.Guests.AuthorizedKeysPath("vma"))!);
        File.WriteAllText(h.Guests.AuthorizedKeysPath("vma"), UserLine + "\n# a comment\n");

        var key = NewKey("laptop@pc");
        using var laptop = h.DeviceClient(h.Laptop!);
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(key))).StatusCode);

        var expected = $"{Bare(key)} {DeviceSshKeys.Marker}{h.Laptop!.Id}";
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(expected), "the laptop's key is on the VM");
        Assert.Equal([UserLine, "# a comment", expected], h.Guests.AuthorizedKeys("vma").TrimEnd('\n').Split('\n'));

        // A new key replaces the device's old one.
        var second = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(second))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(second)), "the new key is on the VM");
        Assert.DoesNotContain(Bare(key), h.Guests.AuthorizedKeys("vma"));
        Assert.StartsWith(UserLine + "\n# a comment\n", h.Guests.AuthorizedKeys("vma"));
    }

    [Fact]
    public async Task Only_a_device_registers_and_only_a_valid_key()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Alice.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(NewKey()))).StatusCode);
        using var laptop = h.DeviceClient(h.Laptop!);
        foreach (var bad in new[] { "", "ssh-ed25519 not-base64", NewKey() + "\nssh-ed25519 AAAA x", "ssh-rsa " + NewKey().Split(' ')[1], "command=\"x\" " + NewKey() })
            Assert.Equal(HttpStatusCode.BadRequest, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(bad))).StatusCode);
        Assert.Empty(h.App.Service<EngineKeyStore>().DeviceSshKeys(ProfileSyncHarness.User));
    }

    [Fact]
    public async Task A_key_never_reaches_another_users_VM()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var bob = await h.App.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("vmb");
        using var laptop = h.DeviceClient(h.Laptop!);
        var key = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(key))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(key)), "on alice's VM");
        await h.SyncAsync();
        Assert.Equal("", h.Guests.AuthorizedKeys("vmb"));
    }

    [Fact]
    public async Task A_revoked_device_loses_its_line_on_every_VM_the_engine_reaches()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var desktop = await PairAsync(h, "desktop");
        using var laptopClient = h.DeviceClient(h.Laptop!);
        using var desktopClient = h.DeviceClient(desktop);
        var laptopKey = NewKey();
        var desktopKey = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptopClient.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(laptopKey))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await desktopClient.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(desktopKey))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(laptopKey)) && h.Guests.AuthorizedKeys("vma").Contains(Bare(desktopKey)), "both keys");

        // Revoked here by the laptop: the line goes at once.
        Assert.Equal(HttpStatusCode.NoContent, (await laptopClient.DeleteAsync("/api/v1/devices/" + desktop.Id)).StatusCode);
        await Until(() => !h.Guests.AuthorizedKeys("vma").Contains(Bare(desktopKey)), "the desktop's key is gone");
        Assert.Contains(Bare(laptopKey), h.Guests.AuthorizedKeys("vma"));
    }

    [Fact]
    public async Task A_device_revoked_at_the_store_loses_its_line_at_the_next_pass()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var desktop = await PairAsync(h, "desktop");
        using var desktopClient = h.DeviceClient(desktop);
        var desktopKey = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await desktopClient.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(desktopKey))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(desktopKey)), "the desktop's key");

        using (var storeClient = h.Laptop!.Store())
            Assert.Equal(HttpStatusCode.NoContent, (await storeClient.DeleteAsync("devices/" + desktop.Id)).StatusCode);
        await h.Laptop.WriteDeviceRecordAsync(desktop, DeviceRecordValue.Revoked);

        await h.SyncAsync();
        Assert.DoesNotContain(Bare(desktopKey), h.Guests.AuthorizedKeys("vma"));
        Assert.Empty(h.App.Service<EngineKeyStore>().DeviceSshKeys(ProfileSyncHarness.User).Where(k => k.DeviceId == desktop.Id));
    }

    [Fact]
    public async Task A_VM_that_was_down_gets_the_keys_at_its_next_heartbeat()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        h.Guests.Down.Add("vma");
        using var laptop = h.DeviceClient(h.Laptop!);
        var key = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(key))).StatusCode);
        await Until(() => h.Guests.Scripts.Any(s => s.Contains(Bare(key))), "a write was tried");

        // The loop does not retry a VM that failed with these keys; its heartbeat does.
        h.Guests.Down.Remove("vma");
        await h.SyncAsync();
        Assert.Equal("", h.Guests.AuthorizedKeys("vma"));
        await h.HeartbeatAsync(vm, "vma");
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(key)), "written at the heartbeat");
    }

    [Fact]
    public async Task A_write_never_lands_on_a_VM_that_replaced_the_one_asked_about()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var laptop = h.DeviceClient(h.Laptop!);
        var key = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(key))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(key)), "written");
        File.WriteAllText(h.Guests.AuthorizedKeysPath("vma"), "");

        var keys = h.App.Service<DeviceSshKeys>();
        var current = (await h.App.Service<Constructd.Core.Abstractions.IVmRepository>().GetAsync("vma", default))!;
        Assert.False(await keys.WriteAsync(current with { Created = current.Created.AddMinutes(-5) }, default));
        Assert.False(await keys.WriteAsync(current with { Owner = "bob" }, default));
        Assert.Equal("", h.Guests.AuthorizedKeys("vma"));

        // Owner names compare without case, as ownership does.
        Assert.True(keys.Manages("ALICE"));
        Assert.Single(await keys.LinesAsync("Alice", default));
    }

    [Fact]
    public async Task A_new_provision_forgets_what_the_old_guest_had()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var laptop = h.DeviceClient(h.Laptop!);
        var key = NewKey();
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/ssh-key", new DeviceSshKeyRequest(key))).StatusCode);
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(key)), "written");

        // A reinstalled guest (same name and creation time) starts without the keys.
        File.WriteAllText(h.Guests.AuthorizedKeysPath("vma"), "");
        await h.HeartbeatAsync(vm, "vma");
        await Task.Delay(300);
        Assert.Equal("", h.Guests.AuthorizedKeys("vma"));
        h.App.Service<DeviceSshKeys>().Forget((await h.App.Service<Constructd.Core.Abstractions.IVmRepository>().GetAsync("vma", default))!);
        await h.HeartbeatAsync(vm, "vma");
        await Until(() => h.Guests.AuthorizedKeys("vma").Contains(Bare(key)), "written again");
    }

    [Fact]
    public void The_script_replaces_only_marked_lines()
    {
        var script = DeviceSshKeys.Script(["ssh-ed25519 AAAA construct-device:abc"]);
        Assert.Contains("grep -v -E ' construct-device:[0-9a-z]+$'", script);
        Assert.Contains("\nssh-ed25519 AAAA construct-device:abc\nCONSTRUCT_DEVICE_KEYS\n", script);
        Assert.Null(DeviceSshKeys.Normalize("ssh-ed25519 AAAA\nCONSTRUCT_DEVICE_KEYS"));
        Assert.Equal(Bare(UserLine), DeviceSshKeys.Normalize(UserLine));
    }
}
