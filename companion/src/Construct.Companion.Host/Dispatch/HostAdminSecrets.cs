using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class HostAdministration
{
    private async Task SecretAction(Model m, RemoteHostClient client, string action, JsonObject args, CancellationToken ct)
    {
        var name=Text(args["name"]); string title, note, notice; JsonNode? response; string plaintext;
        if(action=="issueToken")
        {
            var label=Text(args["label"]);
            response=await client.IssueUserTokenAsync(name,new JsonObject { ["label"]=label.Length==0?"issued from VS Code":label },ct);
            plaintext=Text(response?["token"]); if(plaintext.Length==0) plaintext=Text(response?["secret"]); if(plaintext.Length==0) plaintext=Text(response?["plaintext"]);
            title=$"API token for {name}"; note=$"Label: {(label.Length==0?"(none)":label)}. Hand it to {name} over a channel you trust.";
            notice=$"A token was issued for {name}; it is shown once.";
        }
        else
        {
            var kind=Text(args["kind"]).ToLowerInvariant()=="legacy"?"legacy":"primary";
            if(!await prompts.ConfirmAsync(new ConfirmationPrompt($"Rotate the VM token of \"{name}\" ({kind} kind)?",$"The previous token stops working immediately. To restore the guest credential, run Provision-AgentVM.ps1 -InstanceName {name} -RotateVmToken. That command issues and delivers a fresh token.","Rotate token"),ct)) return;
            response=await client.RotateVmTokenAsync(name,new JsonObject { ["kind"]=kind },ct); plaintext=Text(response?["vmToken"]);
            title=$"VM token of {name} ({kind})"; note=$"To issue and deliver a fresh guest credential, run Provision-AgentVM.ps1 -InstanceName {name} -RotateVmToken.";
            notice=$"The VM token of {name} was rotated ({kind}); issue and deliver a fresh guest credential with Provision-AgentVM.ps1 -InstanceName {name} -RotateVmToken.";
        }
        // No response object, plaintext, or clipboard data enters the model or IPC.
        response=null;
        m.State["notice"]=new JsonObject { ["level"]="info",["text"]=notice };
        Publish(m);
        if(plaintext.Length==0) Notice(m,$"{title}: the service returned no plaintext. Nothing was changed on this PC.");
        else await prompts.ShowSecretOnceAsync(title,new Secret(plaintext),note,ct);
        plaintext="";
        if(action=="issueToken") await PushTokens(m,client,name,ct);
        await Load(m,client,ct);
    }
    private async Task PushTokens(Model m, RemoteHostClient client, string name, CancellationToken ct)
    {
        var result=await client.UserTokensAsync(name,ct);
        var rows=new JsonArray((result as JsonArray ?? []).OfType<JsonObject>().Select(t=>(JsonNode)new JsonObject { ["id"]=Text(t["id"]),["label"]=Text(t["label"]),["created"]=FormatWhen(t["created"]),["lastUsed"]=t["lastUsed"] is null?"never":FormatWhen(t["lastUsed"]) }).ToArray());
        events.HostAdmin(m.Host.Slug,new { type="hostadmin.tokens",name,tokens=rows });
    }
}
