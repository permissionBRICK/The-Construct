using System.Text.Json;
using Constructd.Api.Endpoints;
using Constructd.Api.Jobs;
namespace Constructd.Api.Admin;
public static partial class AdminCli
{
    private static async Task<int> WindowsKeysAsync(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer, CancellationToken ct)
    {
        var store = services.GetRequiredService<WindowsLicenseStore>();
        switch (args.ElementAtOrDefault(1))
        {
            case "list":
                var s = await store.SnapshotAsync(ct); return writer.Result(new { keys = s.Keys, guests = s.Guests }, "Windows license pool.");
            case "add":
                var line = await Console.In.ReadLineAsync(ct);
                if (line is null || line.Length > 8192) return writer.Usage("Supply a key record as one JSON line on stdin.");
                var input = JsonSerializer.Deserialize<WindowsLicenseEndpoints.AddKeyRequest>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (input is null) return writer.Usage("Invalid key record.");
                var key = await store.AddAsync(input.Product, input.Edition, input.Kind, input.Key, input.Budget, input.Notes, "host-admin", ct);
                return writer.Result(key, "Added Windows key ending " + key.PartialKey + ".");
            case "delete" when args.Count == 3:
                await store.DeleteAsync(args[2], "host-admin", ct); return writer.Result(new { id = args[2] }, "Removed Windows key.");
            default: return writer.Usage("Use windows-keys list, add, or delete <id>.");
        }
    }
}
