using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
namespace Constructd.Api.Infrastructure;

public sealed record MaintenanceExempt;
public sealed class MaintenanceFilter(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IMaintenanceGate gate, IHostConfigStore config)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) ||
            HttpMethods.IsOptions(http.Request.Method) || http.GetEndpoint()?.Metadata.GetMetadata<MaintenanceExempt>() is not null)
        { await next(http); return; }
        var path = http.Request.Path.Value ?? "";
        var kind = HttpMethods.IsPost(http.Request.Method) && path.Equals("/api/v1/vms", StringComparison.OrdinalIgnoreCase) ? "create-vm" :
            HttpMethods.IsDelete(http.Request.Method) && path.StartsWith("/api/v1/vms/", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') == 4 ? "remove-vm" : "mutation:http";
        var handle = gate.TryEnter(kind, "http:"+http.TraceIdentifier, null);
        if (handle is null) { await Problem(http, gate, config).ConfigureAwait(false); return; }
        using var admission = new MaintenanceAdmission(handle,kind);
        MaintenanceAdmission.Current.Value = admission;
        try { await next(http); }
        finally { MaintenanceAdmission.Current.Value = null; }
    }
    public static async Task Problem(HttpContext http, IMaintenanceGate gate, IHostConfigStore config)
    {
        var marker = await config.GetAsync<MaintenanceMarker>("maintenance", http.RequestAborted);
        http.Response.Headers.RetryAfter = "30";
        await Results.Problem(statusCode:503, title:"maintenance", type:"urn:construct:problem:maintenance",
            extensions:new Dictionary<string,object?> { ["code"]="maintenance", ["phase"]=gate.State,
                ["retryAfterSeconds"]=30, ["updateId"]=marker?.UpdateId }).ExecuteAsync(http);
    }
}
