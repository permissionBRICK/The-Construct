namespace Constructd.Sqlite;

public sealed partial class SqliteVmRepository
{
    public async Task<bool> RecordPressureSaveAsync(string name, long expectedGeneration, DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vms SET state='Saved', power_generation=power_generation+1,
                pressure_saved_at=@at, pressure_saved_generation=power_generation+1
            WHERE name=@name AND power_generation=@expected AND deleting=0;
            """;
        command.With("@name", name).With("@expected", expectedGeneration).With("@at", SqliteDatabase.Text(at));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
