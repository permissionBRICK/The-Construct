using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Ipc;

namespace Construct.Companion.Core.Desktop;

public sealed record ActivationPlan(IReadOnlyList<UiActivation> Views, string? ForwardInstance = null, string? ForwardId = null);
public static class Activation
{
    public static ActivationPlan Resolve(CommandLine command, IReadOnlyCollection<string> instances,
        IReadOnlyCollection<string> hosts)
    {
        void Instance(string? value) { if (value is not null && !instances.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Instance is not registered."); }
        void Host(string? value) { if (value is not null && !hosts.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Host is not registered."); }
        Instance(command.Instance); Host(command.Host);
        var views = new List<UiActivation>();
        if (command.Panel) views.Add(new("panel", command.Instance));
        if (command.Settings) views.Add(new("settings", command.Instance));
        if (command.HostAdmin) views.Add(new("hostadmin", Host: command.Host));
        if (command.Popup) views.Add(new("popup", command.Instance));
        string? forwardInstance = null, forwardId = null;
        if (command.Uri is not null)
        {
            if (!Uri.TryCreate(command.Uri, UriKind.Absolute, out var uri)) throw new ArgumentException("Invalid Construct activation URI.");
            if (uri.Scheme != "construct" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0)
                throw new ArgumentException("Invalid Construct activation URI.");
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = field.Split('=', 2);
                var key = Uri.UnescapeDataString(pair[0]);
                if (!query.TryAdd(key, Uri.UnescapeDataString(pair.Length == 2 ? pair[1].Replace('+', ' ') : "")))
                    throw new ArgumentException("Duplicate activation parameter.");
            }
            var instance = query.GetValueOrDefault("instance"); var host = query.GetValueOrDefault("host");
            Instance(instance); Host(host);
            var route = uri.AbsolutePath is "" or "/" ? uri.Host : "";
            switch (route)
            {
                case "open": views.Add(new("panel", instance)); break;
                case "settings": views.Add(new("settings", instance)); break;
                case "hostadmin": views.Add(new("hostadmin", Host: host)); break;
                case "forward":
                    if (instance is null || !query.TryGetValue("id", out var id) || !ForwardProtocol.IsSafeId(id))
                        throw new ArgumentException("Invalid forward activation.");
                    forwardInstance = instance; forwardId = id; break;
                default: views.Add(new("popup")); break;
            }
        }
        if (views.Count == 0 && forwardId is null && !command.Background && !command.Quit && !command.SelfTest && !command.Version)
            views.Add(new("popup", command.Instance));
        return new(views, forwardInstance, forwardId);
    }
}
