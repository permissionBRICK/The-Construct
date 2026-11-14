using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Core.HostAdmin;

// The Configuration tab's HTTPS certificate card (port of hostadmin.js toHttpsView and
// httpsCertificateRequest). Key material only passes through CertificateRequest.
public static partial class HostAdminViews
{
    public static string Fingerprint(JsonNode? value)
    {
        var s = Text(value).ToUpperInvariant();
        return Regex.IsMatch(s, "^[0-9A-F]+$") && s.Length % 2 == 0 ? string.Join(":", Enumerable.Range(0, s.Length / 2).Select(i => s.Substring(i * 2, 2))) : s;
    }
    public static JsonObject Https(JsonNode? input, DateTimeOffset now)
    {
        var s = input as JsonObject ?? []; var config = s["config"] as JsonObject ?? [];
        var listeners = Array(s["listeners"]).OfType<JsonObject>().ToArray();
        JsonObject? certificate = null;
        if (s["certificate"] is JsonObject c)
        {
            double? daysLeft = DateTimeOffset.TryParse(Text(c["notAfter"]), out var end) ? Math.Floor((end - now).TotalMilliseconds / 86400000) : null;
            var names = string.Join(", ", Array(c["dnsNames"]).Select(Text).Where(n => n.Length > 0));
            certificate = new JsonObject
            {
                ["subject"] = Text(c["subject"]), ["issuer"] = Text(c["issuer"]), ["names"] = names.Length > 0 ? names : "—",
                ["validUntil"] = FormatWhen(c["notAfter"]), ["daysLeft"] = daysLeft,
                ["expiry"] = daysLeft is not { } d ? "" : d < 0 ? "expired" : $"{N(d)} day{(d == 1 ? "" : "s")} left",
                ["expiring"] = daysLeft is < 14, ["sha256"] = Fingerprint(c["sha256"]), ["chainLength"] = Number(c["chainLength"]) ?? 0,
                ["installedAt"] = FormatWhen(c["installedAt"]),
            };
        }
        string Listener(JsonObject l) => (Text(l["kind"]) == "shared" ? "Shared port" : Text(l["vmName"]) is { Length: > 0 } vm ? vm : Text(l["forwardId"]) is { Length: > 0 } id ? id : "Forward")
            + (Number(l["port"]) is > 0 and var port ? " :" + N(port) : "") + ": " + (Text(l["error"]) is { Length: > 0 } error ? error : "not listening");
        return new()
        {
            ["certificate"] = certificate,
            ["sharedPort"] = Number(config["sharedPort"]) is { } shared ? N(shared) : "off",
            ["warnings"] = new JsonArray(Array(s["warnings"]).Select(Text).Where(w => w.Length > 0).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["listening"] = listeners.Count(l => StateJson.Boolean(l["listening"]) == true),
            ["failedListeners"] = new JsonArray(listeners.Where(l => StateJson.Boolean(l["listening"]) != true || Text(l["error"]).Length > 0).Select(l => (JsonNode?)JsonValue.Create(Listener(l))).ToArray()),
            ["problem"] = "",
        };
    }
    /// <summary>The webview's choice as the PUT body: a PEM chain with its key, or a base64 PFX with an optional password.</summary>
    public static JsonObject CertificateRequest(JsonObject args)
    {
        static string Raw(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var pfx = Raw(args["pfx"]).Trim();
        if (pfx.Length > 0) return new() { ["ok"] = true, ["body"] = new JsonObject { ["pfx"] = pfx, ["password"] = Raw(args["password"]) is { Length: > 0 } password ? password : null } };
        var chain = Raw(args["certificatePem"]); var key = Raw(args["privateKeyPem"]);
        if (chain.Trim().Length == 0 || key.Trim().Length == 0) return new() { ["ok"] = false, ["error"] = "Choose a PEM certificate chain and its key, or a PFX file." };
        return new() { ["ok"] = true, ["body"] = new JsonObject { ["certificatePem"] = chain, ["privateKeyPem"] = key } };
    }
}
