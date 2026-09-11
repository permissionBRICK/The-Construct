using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Core.Desktop;

// Tray menu ids become the same webview messages the panel would have posted.
public static class DesktopCommand
{
    public static JsonElement Message(string id, bool microphoneEnabled = false)
    {
        var message = new JsonObject { ["type"] = "command", ["id"] = id == "startVm" ? "startConnect" : id };
        if (id == "mic") message = new JsonObject { ["type"] = "setAudio", ["enabled"] = !microphoneEnabled };
        if (id.Split(':', 2) is [("openForward" or "closeForward") and var command, var forward])
        {
            if (!ForwardProtocol.IsSafeId(forward)) throw new ArgumentException("Invalid forward id.");
            message["id"] = command; message["forward"] = forward;
        }
        return JsonSerializer.SerializeToElement(message, IpcJson.Options);
    }
}
