using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.State;
namespace Construct.Companion.Tests.Parity;
public sealed class HostAdminIpcParityTests
{
    public static IEnumerable<object[]> Rows => ParityTests.Rows("hostadmin-ipc");
    [Theory, MemberData(nameof(Rows))]
    public void MatchesJavaScript(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var input = row["input"]; var now = DateTimeOffset.FromUnixTimeMilliseconds(row["now"]!.GetValue<long>());
        var kind = row["kind"]!.GetValue<string>();
        JsonNode? actual = kind switch
        {
            "projectOpenPath" => JsonValue.Create(ProjectNavigation.OpenPath(input)),
            "cascade" => HostAdminProtocol.CascadeConfirmation(input!.AsObject()), "cascadeKind" => JsonValue.Create(HostAdminProtocol.CascadeKind(input!.AsObject())),
            "classify" => HostAdminProtocol.Classify(input!.AsObject()), "poll" => JsonValue.Create(HostAdminProtocol.PollInterval(input!.AsObject())),
            "lifetime" => HostAdminProtocol.ParseLifetime(input), "features" => HostAdminProtocol.Features(input), "tabs" => HostAdminProtocol.TabsFor(HostAdminProtocol.Features(input)),
            "allowance" or "overrides" or "newUser" => HostAdminProtocol.ParseForm(kind, input!.AsObject()), "userForm" => HostAdminProtocol.ParseForm("user", input!.AsObject()),
            "idleClamp" => HostAdminProtocol.ClampIdlePolicy(input!["policy"]!.AsObject(), input["max"]!.GetValue<double>()), "idle" => HostAdminProtocol.IdlePolicy(input),
            "vm" => HostAdminViews.Vm(input, now), "vms" => HostAdminViews.Vms(input, now), "children" => HostAdminViews.Children(input, now), "childDelete" => HostAdminProtocol.ChildDeleteConfirmation(input!.AsObject()),
            "overview" => HostAdminViews.Overview(input), "capacity" => HostAdminViews.Capacity(input), "media" => HostAdminViews.Media(input), "iso" => HostAdminViews.IsoCatalog(input),
            "job" => HostAdminViews.Job(input), "audit" => HostAdminViews.Audit(input), "config" => HostAdminViews.Config(input), "capabilities" => HostAdminViews.Capabilities(input),
            "updates" => HostAdminViews.Updates(input), "updateActions" => HostAdminViews.UpdateActions(input), "user" => HostAdminViews.User(input), "allowanceForm" => HostAdminViews.AllowanceForm(input), "allowanceText" => JsonValue.Create(HostAdminViews.AllowanceText(input)),
            _ => throw new InvalidOperationException(kind)
        };
        StateParityTests.Equal(row["output"], actual);
    }
}
