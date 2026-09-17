using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Sqlite;
namespace Constructd.Tests.Persistence;

public sealed class TokenUsageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly string path = Path.Combine(Path.GetTempPath(), $"usage-{Guid.NewGuid():n}.db");
    private SqliteDatabase Open() { var db = new SqliteDatabase(path); db.EnsureCreated(); return db; }
    private static TokenUsageDay Day(string date, long tokens = 100, string tool = "claude") => new(date, tool, 40, 60, 0, 0, tokens, 1234567);
    private static Vm Vm(string name = "vm", string owner = "alice") => new(name, owner, 2, 4, 20, Now, VmState.Running, null, null, new(60, IdleAction.Save), []);

    [Fact]
    public async Task Reports_overwrite_case_insensitively_and_survive_restart_and_deletion()
    {
        var db = Open(); var vms = new SqliteVmRepository(db); var store = new SqliteTokenUsageStore(db); var vm = Vm();
        await vms.AddAsync(vm, 10, default);
        Assert.True(await store.UpsertAsync(vm, [Day("2026-09-16"), Day("2026-09-17")], Now, default));
        Assert.True(await store.UpsertAsync(vm with { Name = "VM" }, [Day("2026-09-16"), Day("2026-09-17", 200)], Now, default));
        var reopened = new SqliteTokenUsageStore(Open());
        var rows = await reopened.ListAsync("ALICE", "vm", default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(300m, TokenUsageMath.Aggregate(rows, "all", Now).Totals.Tokens);
        Assert.Equal(2.469134m, TokenUsageMath.Aggregate(rows, "all", Now).Totals.CostUsd);
        await vms.RemoveAsync(vm.Name, default);
        rows = await reopened.ListAsync(null, null, default);
        Assert.All(rows, r => Assert.NotNull(r.VmDeletedAt));
        Assert.True(Assert.Single(TokenUsageMath.Aggregate(rows, "month", Now).ByVm).Deleted);
        Assert.False(await store.UpsertAsync(vm, [Day("2026-09-17")], Now, default));
        Assert.Empty(await store.ListAsync("bob", null, default));
    }

    [Fact]
    public async Task Deleting_and_replaced_vms_cannot_report()
    {
        var db = Open(); var vms = new SqliteVmRepository(db); var store = new SqliteTokenUsageStore(db); var vm = Vm();
        await vms.AddAsync(vm, 10, default);
        Assert.False(await store.UpsertAsync(vm with { Incarnation = "different" }, [Day("2026-09-17")], Now, default));
        await vms.UpdateAsync(vm with { Deleting = true }, default);
        Assert.False(await store.UpsertAsync(vm, [Day("2026-09-17")], Now, default));
    }

    [Fact]
    public void Windows_precedence_tools_and_former_owners()
    {
        TokenUsageRow Row(string date, string vm = "vm", string owner = "alice", string tool = "claude", bool deleted = false) =>
            new(vm, owner, Day(date, 100, tool), Now, deleted ? Now : null);
        var rows = new[] { Row("2026-08"), Row("2026-09"), Row("2026-09-16"), Row("2026-09-17", "VM"),
            Row("2026-09", tool: "codex"), Row("2026-09-17", "gone", deleted: true), Row("2026-09-17", "other", "bob") };
        Assert.Equal(300m, TokenUsageMath.Aggregate(rows, "today", Now).Totals.Tokens);
        var month = TokenUsageMath.Aggregate(rows, "month", Now);
        Assert.Equal(500m, month.Totals.Tokens);
        Assert.Equal(400m, month.ByUser.Single(u => u.User == "alice").Tokens);
        Assert.Equal(2, month.ByUser.Single(u => u.User == "alice").Vms);
        Assert.Equal(2, month.ByVm.Single(v => v.Vm.Equals("vm", StringComparison.OrdinalIgnoreCase)).Tools.Count);
        Assert.Equal(600m, TokenUsageMath.Aggregate(rows, "all", Now).Totals.Tokens);
        Assert.Equal(0m, TokenUsageMath.Aggregate(rows, "today", Now.AddDays(1)).Totals.Tokens);
        Assert.Throws<ArgumentException>(() => TokenUsageMath.Aggregate(rows, "bad", Now));
    }

    [Fact]
    public async Task Retention_uses_usage_dates_and_keeps_boundary_month_until_its_end_expires()
    {
        var db = Open(); var vms = new SqliteVmRepository(db); var store = new SqliteTokenUsageStore(db); var vm = Vm();
        await vms.AddAsync(vm, 10, default);
        await store.UpsertAsync(vm, [Day("2025-07"), Day("2025-08"), Day("2025-08-12"), Day("2025-08-13")], Now, default);
        Assert.Equal(2, await store.PruneAsync(new(2025, 8, 13), default));
        Assert.Equal(2, (await store.ListAsync(null, null, default)).Count);
        Assert.Equal(1, await store.PruneAsync(new(2025, 8, 31), default));
        Assert.Equal(1, await store.PruneAsync(new(2025, 9, 1), default));
    }

    [Theory]
    [InlineData("2026-02-29", false)] [InlineData("2024-02-29", true)]
    [InlineData("2026-13", false)] [InlineData("2026-09", true)] [InlineData("2026-9", false)]
    public void Strict_calendar_dates(string day, bool valid) => Assert.Equal(valid, TokenUsageMath.ValidDay(day));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
    }
}
