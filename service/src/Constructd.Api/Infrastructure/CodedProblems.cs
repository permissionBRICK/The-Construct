namespace Constructd.Api.Infrastructure;

public static class CodedProblems
{
    public static IResult Create(int status, string code, string detail, string? field = null) => Results.Problem(
        statusCode: status, title: code, detail: detail, type: "urn:construct:problem:" + code,
        extensions: field is null ? new Dictionary<string, object?> { ["code"] = code } :
            new Dictionary<string, object?> { ["code"] = code, ["field"] = field, ["reason"] = detail });
    public static IResult Validation(string field, string reason) => Create(400, "validation", reason, field);
    public static void Audit(HttpContext http, string operation, string owner, string? parent = null, string? target = null, string? extra = null)
    {
        static string Clean(string s) => new(s.Where(c => !char.IsControl(c) && c != ',' && c != '=').Take(200).ToArray());
        var detail = $"op={Clean(operation)}, owner={Clean(owner)}, initiator={Clean(http.User.Identity?.Name ?? "anonymous")}";
        if (parent is not null) detail += ", parent=" + Clean(parent);
        if (target is not null) detail += ", target=" + Clean(target);
        if (extra is not null) detail += ", " + extra;
        http.SetAuditDetail(detail);
    }
}
