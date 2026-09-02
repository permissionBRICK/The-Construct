using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

/// <summary>
/// The other half of the forward contract, from the guest's side.
///
/// <c>bin/construct-expose.sh</c> reads this API with a deliberately lenient parser, and — on a VM
/// where <c>jq</c> is missing — a purely textual one: it splits a JSON array on <c>{…}</c> and greps
/// flat <c>"key": value</c> pairs out of each piece. That makes two things load-bearing which no
/// C# type system notices: the forward objects must stay <b>flat</b>, and <c>url</c>/<c>status</c>/
/// <c>localPort</c>/<c>hostLabel</c>/<c>message</c> must mean exactly what the spool ack document
/// means by them.
///
/// So these tests re-implement that parser here and run it against the <b>real serialized bytes</b>
/// of the real routes. A change that nests the ack, renames a field or fills <c>url</c> in for a
/// failed forward fails here rather than in the field, where the symptom is an agent printing a
/// dead link.
/// </summary>
public class ExposeCliContractTests
{
    // ── The CLI's parser, transcribed ───────────────────────────────────────────────────────

    /// <summary>`json_objects` without jq: `tr -d '\n'` then `grep -o '{[^{}]*}'`.</summary>
    private static List<string> JsonObjects(string json) =>
        [.. Regex.Matches(json.Replace("\n", string.Empty), "{[^{}]*}").Select(m => m.Value)];

    /// <summary>`json_field`: the quoted form first, then the bare one; JSON null reads as empty.</summary>
    private static string JsonField(string document, string key)
    {
        var quoted = Regex.Match(document, "\"" + key + "\"[ \t]*:[ \t]*\"([^\"]*)\"");
        var value = quoted.Success
            ? quoted.Groups[1].Value
            : Regex.Match(document, "\"" + key + "\"[ \t]*:[ \t]*([A-Za-z0-9.+-]*)").Groups[1].Value;

        return value == "null" ? string.Empty : value;
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535;

    /// <summary>What the CLI does with one forward: the link, "keep waiting", or a final failure.</summary>
    private enum CliOutcome
    {
        Link,
        NotOpenYet,
        Failed,
    }

    private static (CliOutcome Outcome, string Text) LinkFromForward(string document)
    {
        var url = JsonField(document, "url");
        if (url.Length > 0)
        {
            return (CliOutcome.Link, url);
        }

        if (JsonField(document, "status") == "error")
        {
            return (CliOutcome.Failed, JsonField(document, "message"));
        }

        var localPort = JsonField(document, "localPort");
        if (!IsPort(localPort))
        {
            return (CliOutcome.NotOpenYet, string.Empty);
        }

        var hostLabel = JsonField(document, "hostLabel");
        return (CliOutcome.Link, $"http://{(hostLabel.Length > 0 ? hostLabel : "localhost")}:{localPort}/");
    }

    // ── The real bytes ──────────────────────────────────────────────────────────────────────

    private static async Task<(TestApp App, HttpClient Owner, HttpClient Guest)> SetupAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        return (app, owner, app.CreateVmTokenClient(job.VmToken()));
    }

    /// <summary>The list route's response body, exactly as the guest's curl receives it.</summary>
    private static async Task<string> ListAsync(HttpClient guest) =>
        await (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).Content.ReadAsStringAsync();

    private static async Task<string> AddClientAsync(HttpClient owner, int vmPort = 5173) =>
        (await (await owner.PostAsJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort, label = "vite dev", target = "client" }, ApiJson.Options))
            .ReadAsync<ForwardResponse>()).Id;

    [Fact]
    public async Task Every_forward_in_the_list_is_a_flat_object()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var id = await AddClientAsync(owner);
        await owner.PostAsJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" }, ApiJson.Options);
        await owner.PostAsJsonAsync($"/api/v1/vms/work-vm/forwards/{id}/ack",
            new { status = "open", localPort = 5173, hostLabel = "christoph-pc" }, ApiJson.Options);

        var body = await ListAsync(guest);

        // Two forwards in, two objects out: a nested object would split into more pieces (or
        // swallow the outer one), which is exactly how the jq-less parser breaks.
        Assert.Equal(2, JsonObjects(body).Count);
    }

    [Fact]
    public async Task A_queued_client_forward_reads_as_not_open_yet()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        await AddClientAsync(owner);

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));
        Assert.Equal(CliOutcome.NotOpenYet, LinkFromForward(forward).Outcome);
    }

    [Fact]
    public async Task An_open_ack_reads_as_the_loopback_link()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var id = await AddClientAsync(owner);
        await owner.PostAsJsonAsync($"/api/v1/vms/work-vm/forwards/{id}/ack",
            new { status = "open", localPort = 5173 }, ApiJson.Options);

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));
        var (outcome, text) = LinkFromForward(forward);

        Assert.Equal(CliOutcome.Link, outcome);
        Assert.Equal("http://localhost:5173/", text);
    }

    /// <remarks>
    /// The CLI reads <c>url</c> first, so this passes whichever of the two the service filled in —
    /// which is the point of the lenient shape: it is the same document either way.
    /// </remarks>
    [Fact]
    public async Task A_host_label_and_a_remapped_port_read_as_the_named_link()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var id = await AddClientAsync(owner);
        await owner.PostAsJsonAsync($"/api/v1/vms/work-vm/forwards/{id}/ack",
            new { status = "open", localPort = 18800, hostLabel = "christoph-pc" }, ApiJson.Options);

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));
        var (outcome, text) = LinkFromForward(forward);

        Assert.Equal(CliOutcome.Link, outcome);
        Assert.Equal("http://christoph-pc:18800/", text);
        // ...and the ack fields the CLI would fall back on say the same thing.
        Assert.Equal("18800", JsonField(forward, "localPort"));
        Assert.Equal("christoph-pc", JsonField(forward, "hostLabel"));
    }

    [Fact]
    public async Task An_error_ack_reads_as_a_final_failure_not_a_link()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var id = await AddClientAsync(owner);
        await owner.PostAsJsonAsync($"/api/v1/vms/work-vm/forwards/{id}/ack",
            new { status = "error", message = "no free local port" }, ApiJson.Options);

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));
        var (outcome, text) = LinkFromForward(forward);

        Assert.Equal(CliOutcome.Failed, outcome);
        Assert.Equal("no free local port", text);
    }

    [Fact]
    public async Task A_host_forward_still_reads_as_its_lan_url()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var created = await (await owner.PostAsJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" }, ApiJson.Options))
            .ReadAsync<ForwardResponse>();

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));
        var (outcome, text) = LinkFromForward(forward);

        Assert.Equal(CliOutcome.Link, outcome);
        Assert.Equal($"http://buildbox.test:{created.PublicPort}/", text);
    }

    /// <remarks>
    /// `construct expose --close 5173` finds the id by matching <c>vmPort</c>, and `--list` prints
    /// <c>id</c>/<c>vmPort</c>/<c>target</c>/<c>label</c>. Those four field names are as much of the
    /// contract as the link is.
    /// </remarks>
    [Fact]
    public async Task The_fields_list_and_close_match_on_are_readable()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var _guest = guest;

        var id = await AddClientAsync(owner);

        var forward = Assert.Single(JsonObjects(await ListAsync(guest)));

        Assert.Equal(id, JsonField(forward, "id"));
        Assert.Equal("5173", JsonField(forward, "vmPort"));
        Assert.Equal(ForwardTarget.Client.ToString().ToLowerInvariant(), JsonField(forward, "target"));
        Assert.Equal("vite dev", JsonField(forward, "label"));
    }
}
