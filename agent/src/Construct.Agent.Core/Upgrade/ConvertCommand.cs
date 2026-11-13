using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;

namespace Construct.Agent.Upgrade;

/// <summary>
/// <c>construct-agent convert --from-legacy [--from FOLDER]</c>: the converter (13.1) from a console. With an
/// agent running it asks that agent (its routes) and follows the run; otherwise it runs in this process on the
/// agent's directory. The recovery code must be set first, as in the dialog.
/// </summary>
public static class ConvertCommand
{
    public static async Task<int> RunAsync(AgentOptions options, Func<string?, Protection.IDataProtection>? protection, string? from, TextWriter output, CancellationToken ct)
    {
        if (await Repair.RunningAsync(options, ct).ConfigureAwait(false) is { } running)
        {
            using var http = new HttpClient { BaseAddress = new Uri(running.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", running.Token);
            using (var start = await http.PostAsJsonAsync("api/upgrade/convert", new ConvertRequest(from), ct).ConfigureAwait(false))
            {
                if (!start.IsSuccessStatusCode)
                {
                    output.WriteLine("The agent refused: " + (JsonNode.Parse(await start.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["title"] ?? start.StatusCode.ToString()));
                    return 1;
                }
            }

            JsonNode? view;
            do
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                view = await http.GetFromJsonAsync<JsonNode>("api/upgrade", ct).ConfigureAwait(false);
            }
            while (view?["running"]?.GetValue<bool>() == true);

            foreach (var item in view?["items"]?.AsArray() ?? []) output.WriteLine($"{item?["state"],-8} {item?["label"]}  {item?["message"]}");
            if (view?["error"] is JsonValue error) output.WriteLine(error.GetValue<string>());
            return view?["stage"]?.GetValue<string>() == BridgeState.Converted ? 0 : 1;
        }

        using var agent = new DeviceAgent(options, protection);
        using var upgrade = new UpgradeService(agent, options, options.Upgrade ?? new DefaultUpgradePlatform(Path.Combine(options.DataDirectory, "legacy_known_hosts")),
            new DeviceSettings(Path.Combine(options.DataDirectory, "device.json")), new AgentEngines(agent));
        if (from is { Length: > 0 }) upgrade.Bridge.Update(b => b with { From = Path.GetFullPath(from) });
        try
        {
            var done = await upgrade.ConvertAsync(ct, item => output.WriteLine($"{item.State,-8} {item.Label}  {item.Message}")).ConfigureAwait(false);
            foreach (var note in upgrade.Bridge.Read().Notes) output.WriteLine("note: " + note);
            output.WriteLine(done ? "Converted." : "Not finished: fix what failed and run it again.");
            return done ? 0 : 1;
        }
        catch (AgentFailure failure)
        {
            output.WriteLine(failure.Message);
            return 1;
        }
    }
}
