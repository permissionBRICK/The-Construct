using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Sqlite.Migrations;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Source;

public sealed class SourceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "source-store-" + Guid.NewGuid().ToString("n"));
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private SqliteDatabase Database() { var db = new SqliteDatabase(Path.Combine(root, "test.db")); db.EnsureCreated(); return db; }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoresAgreeOnRowsTransitionsChargesAndPins(bool sqlite)
    {
        var db = Database(); IVmRepository vms = sqlite ? new SqliteVmRepository(db) : new InMemoryVmRepository();
        ISourceStore store = sqlite ? new SqliteSourceStore(db) : new InMemorySourceStore(vms);
        var commit = new string('a', 40);
        await vms.AddAsync(new("vm", "alice", 1, 1, 8, now, VmState.Off, null, null, IdlePolicy.Disabled, [],
            Guest: GuestReport.Unknown with { ConstructCommit = "abcdef0" }, SourceCommit: commit), 5, default);
        Assert.Equal(new[] { commit, "abcdef0" }, (await store.ListPinnedCommitsAsync(default)).Order());
        var item = new SourceItem(commit, SourceState.Downloading, 123, new string('b', 64), "host-" + commit, null, "job", now, null, now);
        await store.UpsertAsync(item, default);
        Assert.Equal(item, await store.GetAsync(commit, default));
        Assert.Equal(123, await store.CommittedBytesAsync(default));
        Assert.False(await store.TryTransitionAsync(commit, SourceState.Ready, item, default));
        foreach (var state in new[] { SourceState.Ready, SourceState.Deleting, SourceState.Failed })
        {
            var next = item with { State = state, ReadyAt = now, Error = "admin" };
            Assert.True(await store.TryTransitionAsync(commit, item.State, next, default)); item = next;
            Assert.Equal(state == SourceState.Failed ? 0 : 123, await store.CommittedBytesAsync(default));
        }
        await store.TouchAsync(commit, now.AddDays(1), default);
        Assert.Equal(now.AddDays(1), Assert.Single(await store.ListAsync(default)).LastUsedAt);
        await store.UpsertAsync(item with { State = SourceState.Downloading }, default);
        Assert.Equal(123, await store.CommittedBytesAsync(default));
        await store.DeleteAsync(commit, default); Assert.Empty(await store.ListAsync(default));
    }
    [Fact]
    public void LiteralDdlAppliesToPreFeatureAndM700Database()
    {
        var db = Database(); using var c = db.Open();
        // Reconstruct the pre-feature schema while retaining every M700 table and row.
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DROP TABLE source_cache; ALTER TABLE vms DROP COLUMN source_commit; DELETE FROM schema_migrations WHERE id=800";
        cmd.ExecuteNonQuery();
        using (var tx = c.BeginTransaction()) { new M800_SourceCache().Apply(c, tx); tx.Commit(); }
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('source_cache')"; Assert.Equal(10L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('vms') WHERE name='source_commit'"; Assert.Equal(1L, cmd.ExecuteScalar());
        Assert.Equal(800, SqliteMigrations.SchemaVersion); Assert.Equal(0, SqliteMigrations.MinReadableBy);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
