using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Microsoft.Data.Sqlite;

namespace Constructd.Tests.Backups;

/// <summary>The SQLite store (migration M870) and the in-memory fake behave alike.</summary>
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "constructd-backup-store-" + Guid.NewGuid().ToString("n"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private IBackupStore Store(bool sqlite)
    {
        if (!sqlite) return new InMemoryBackupStore();
        Directory.CreateDirectory(_directory);
        var database = new SqliteDatabase(Path.Combine(_directory, "test.db"));
        database.EnsureCreated();
        return new SqliteBackupStore(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sessions_complete_unpin_and_delete_by_their_state(bool sqlite)
    {
        var store = Store(sqlite);
        var first = new ConfigBackup(new string('1', 32), "Alice", "dev", BackupTriggers.PreReinstall, BackupStates.Open, T0);
        var second = new ConfigBackup(new string('2', 32), "alice", "DEV", BackupTriggers.Scheduled, BackupStates.Open, T0.AddHours(1));
        var other = new ConfigBackup(new string('3', 32), "bob", "dev", BackupTriggers.Manual, BackupStates.Open, T0.AddHours(2));
        foreach (var backup in new[] { first, second, other }) await store.InsertAsync(backup, default);

        // Owner and VM compare case-insensitively; newest first.
        Assert.Equal([second.Id, first.Id], (await store.ListAsync("ALICE", "dev", default)).Select(b => b.Id));
        Assert.Equal(3, (await store.ListAllAsync(default)).Count);

        Assert.True(await store.SetKeysAsync(first.Id, "{\"k\":1}", default));
        Assert.True(await store.CompleteAsync(first.Id, T0.AddMinutes(5), 1234, new string('f', 64), T0.AddDays(14), default));
        Assert.False(await store.SetKeysAsync(first.Id, "{}", default));
        Assert.False(await store.CompleteAsync(first.Id, T0, 1, "x", null, default));
        var stored = (await store.GetAsync(first.Id, default))!;
        Assert.Equal(first with
        {
            State = BackupStates.Complete, CompletedAt = T0.AddMinutes(5), SizeBytes = 1234, Sha256 = new string('f', 64),
            PinnedUntil = T0.AddDays(14), KeysJson = "{\"k\":1}",
        }, stored);

        Assert.True(await store.UnpinAsync(first.Id, default));
        Assert.Null((await store.GetAsync(first.Id, default))!.PinnedUntil);
        Assert.False(await store.UnpinAsync(second.Id, default));

        Assert.True(await store.DeleteAsync(second.Id, default));
        Assert.False(await store.DeleteAsync(second.Id, default));
        Assert.Null(await store.GetAsync(second.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vm_state_round_trips_and_is_replaced_whole(bool sqlite)
    {
        var store = Store(sqlite);
        Assert.Null(await store.GetVmAsync("alice", "dev", default));
        var state = new BackupVmState("alice", "dev", 12, 3, T0, T0.AddMinutes(1), new BackupFailure(T0, "manual", "export-failed", "tar exited 2"),
            T0, T0.AddMinutes(2), T0.AddMinutes(3));
        await store.SaveVmAsync(state, default);
        Assert.Equal(state, await store.GetVmAsync("ALICE", "Dev", default));

        await store.SaveVmAsync(new BackupVmState("Alice", "DEV", LastSuccessAt: T0.AddDays(1)), default);
        var replaced = (await store.GetVmAsync("alice", "dev", default))!;
        Assert.Equal(new BackupVmState("alice", "dev", LastSuccessAt: T0.AddDays(1)), replaced);
        Assert.Single(await store.ListVmsAsync(default));

        await store.DeleteVmAsync("ALICE", "dev", default);
        Assert.Empty(await store.ListVmsAsync(default));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
