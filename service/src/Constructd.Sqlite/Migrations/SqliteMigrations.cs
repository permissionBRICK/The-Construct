namespace Constructd.Sqlite.Migrations;

public static class SqliteMigrations
{
    // One registration per feature; reserved ranges are documented in the contract.
    public static IReadOnlyList<ISqliteMigration> All { get; } = [new M100_VmKindsAndDelegation(), new M300_CapacityLedger()];
    public static int SchemaVersion => All.Max(m => m.Id);
    public static int MinReadableBy => All.Where(m => m.Breaking).Select(m => m.Id).DefaultIfEmpty(0).Max();
}
