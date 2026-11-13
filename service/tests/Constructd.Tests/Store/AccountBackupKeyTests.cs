using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Constructd.Api.Contracts;
using Constructd.Api.Devices;
using Constructd.Core.Logic;
using Constructd.Sync.Participation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Store;

/// <summary>
/// The account's backup key on its VMs (design 8.2, 13.1): a device registers <c>B.pub</c> once, enrols it on its
/// own VMs through the engine and removes other recipients only while the account's key stays enrolled; never on
/// another user's VM, and the key travels on stdin only.
/// </summary>
public sealed class AccountBackupKeyTests
{
    private static string NewPem(int bits = 3072)
    {
        using var rsa = RSA.Create(bits);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    [Fact]
    public async Task Only_a_device_registers_and_only_an_RSA_key_of_3072_bits_or_more()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Alice.PutAsJsonAsync("/api/v1/device/backup-key", new AccountBackupKeyRequest(NewPem()))).StatusCode);
        using var laptop = h.DeviceClient(h.Laptop!);
        foreach (var bad in new[] { "", "not a key", NewPem(2048) })
            Assert.Equal(HttpStatusCode.BadRequest, (await laptop.PutAsJsonAsync("/api/v1/device/backup-key", new AccountBackupKeyRequest(bad))).StatusCode);
        Assert.Null(h.App.Service<EngineKeyStore>().BackupKey(ProfileSyncHarness.User));
    }

    [Fact]
    public async Task A_device_enrols_the_key_on_its_own_VM_and_removes_an_old_one_only_while_the_account_key_is_there()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        var old = BackupJobKey.PublicFingerprint(NewPem())!;
        h.Guests.BackupRecipients["vma"] = [old];
        using var laptop = h.DeviceClient(h.Laptop!);

        // Before a device registered the key there is nothing to enrol.
        Assert.Equal(HttpStatusCode.Conflict, (await laptop.PostAsync("/api/v1/vms/vma/backup-recipients", null)).StatusCode);
        var pem = NewPem();
        var fingerprint = BackupJobKey.PublicFingerprint(pem)!;
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/backup-key", new AccountBackupKeyRequest(pem))).StatusCode);

        // Not yet enrolled on the VM: the old key stays, so the VM keeps a recipient a device holds.
        var early = await laptop.DeleteAsync($"/api/v1/vms/vma/backup-recipients/{old}");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("account-key-not-enrolled", await early.Content.ReadAsStringAsync());
        Assert.Equal([old], h.Guests.BackupRecipients["vma"]);

        var added = await laptop.PostAsync("/api/v1/vms/vma/backup-recipients", null);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(fingerprint, (await added.Content.ReadFromJsonAsync<BackupRecipientResponse>())!.Fingerprint);
        Assert.Contains(fingerprint, h.Guests.BackupRecipients["vma"]);
        Assert.All(h.Guests.BackupCalls, c => Assert.DoesNotContain("PUBLIC KEY", c.Command, StringComparison.Ordinal));
        Assert.Contains(h.Guests.BackupCalls, c => c.Command.Contains(AccountBackupKeys.Label, StringComparison.Ordinal) && c.Input == pem);

        // The account's own key never comes off; an old one does.
        Assert.Equal(HttpStatusCode.Conflict, (await laptop.DeleteAsync($"/api/v1/vms/vma/backup-recipients/{fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await laptop.DeleteAsync("/api/v1/vms/vma/backup-recipients/not-a-fingerprint")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.DeleteAsync($"/api/v1/vms/vma/backup-recipients/{old}")).StatusCode);
        Assert.Equal([fingerprint], h.Guests.BackupRecipients["vma"]);

        // A VM that does not answer.
        h.Guests.Down.Add("vma");
        Assert.Equal(HttpStatusCode.BadGateway, (await laptop.PostAsync("/api/v1/vms/vma/backup-recipients", null)).StatusCode);
    }

    [Fact]
    public async Task Another_users_VM_and_a_caller_without_a_device_token_are_refused()
    {
        await using var h = await ProfileSyncHarness.StartAsync();
        using var vm = await h.VmAsync("vma");
        using var bob = await h.App.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("vmb");
        var old = BackupJobKey.PublicFingerprint(NewPem())!;
        h.Guests.BackupRecipients["vmb"] = [old];
        using var laptop = h.DeviceClient(h.Laptop!);
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/v1/device/backup-key", new AccountBackupKeyRequest(NewPem()))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await laptop.PostAsync("/api/v1/vms/vmb/backup-recipients", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await laptop.DeleteAsync($"/api/v1/vms/vmb/backup-recipients/{old}")).StatusCode);
        // The user's own sign-in without a device token, even on the user's own VM.
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Alice.PostAsync("/api/v1/vms/vma/backup-recipients", null)).StatusCode);
        Assert.DoesNotContain(h.Guests.BackupCalls, c => c.Vm == "vmb");
        Assert.Equal([old], h.Guests.BackupRecipients["vmb"]);
    }
}
