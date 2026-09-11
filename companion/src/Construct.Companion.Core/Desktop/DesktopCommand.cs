using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Core.Desktop;

public static class DesktopCommand
{
    public static JsonElement Message(string id,bool microphoneEnabled=false)
    {
        var message=new JsonObject { ["type"]="command",["id"]=id=="startVm" ? "startConnect" : id };
        if (id=="mic") message=new JsonObject { ["type"]="setAudio",["enabled"]=!microphoneEnabled };
        foreach (var command in new[] { "openForward","closeForward" })
            if (id.StartsWith(command+":",StringComparison.Ordinal))
            {
                var forward=id[(command.Length+1)..];
                if (!ForwardProtocol.IsSafeId(forward)) throw new ArgumentException("Invalid forward id.");
                message["id"]=command; message["forward"]=forward;
            }
        return JsonSerializer.SerializeToElement(message,IpcJson.Options);
    }
}
