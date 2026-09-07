using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Endpoints;

public static class ConsoleEndpoints
{
    private const string VmKey = "console.vm";
    public static RouteGroupBuilder MapConsoleEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vms/{name}/console").RequireAuthorization(Policies.UserOrPrimaryToken);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext; var sp = http.RequestServices; var ct = http.RequestAborted;
            var vms = sp.GetRequiredService<IVmRepository>();
            var lookup = await ApiHelpers.ResolveVmAsync(http, vms, sp.GetRequiredService<IAuthorizationService>(),
                (string)http.Request.RouteValues["name"]!, Policies.ConsoleOperator, ct);
            if (!lookup.Ok) return lookup.Failure!;
            var vm = lookup.Vm!;
            if (vm.Deleting || (vm.Parent is not null && await vms.GetAsync(vm.Parent, ct) is not { Deleting: false, ChildCreationClosed: false }))
                return CodedProblems.Create(409, "vm-deleting", "The VM or its parent is being deleted.");
            http.Items[VmKey] = vm;
            if (!HttpMethods.IsGet(http.Request.Method)) Audit(http, "console");
            try { return await next(ctx); }
            catch (ConsoleTransportException e) { return e.TooLarge ? Problem(413, "screenshot-too-large", "Screenshot exceeds 4 MiB.") : Unavailable(); }
            catch (JsonException) { return CodedProblems.Validation("body", "Invalid console request."); }
            catch (BadHttpRequestException) { return CodedProblems.Validation("body", "Invalid console request."); }
        });
        group.MapGet("/capabilities", Capabilities);
        group.MapPost("/sessions", Create).Audited("console.session-create");
        group.MapPost("/sessions/{sid}/renew", Renew).Audited("console.session-renew");
        group.MapDelete("/sessions/{sid}", Remove).Audited("console.session-delete");
        group.MapGet("/sessions/{sid}/screenshot", Screenshot);
        group.MapPost("/sessions/{sid}/keyboard", Keyboard).Audited("console.keyboard");
        group.MapPost("/sessions/{sid}/mouse", Mouse).Audited("console.mouse");
        return api;
    }
    private static Vm Vm(HttpContext http) => (Vm)http.Items[VmKey]!;
    private static string Principal(HttpContext http) => http.User.IsPrimaryToken() ? "vm:" + http.User.VmTokenName() : http.User.NameOrEmpty();
    private static void Audit(HttpContext http, string action, string? extra = null)
    { var vm = Vm(http); CodedProblems.Audit(http, action, vm.Owner, vm.Parent, vm.Name, extra); }
    private static IResult Problem(int status, string code, string detail, string? device = null, uint? returnValue = null, string? fallback = null, int? retryAfterSeconds = null) =>
        Results.Problem(statusCode: status, title: code, type: "urn:construct:problem:" + code, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["device"] = device, ["returnValue"] = returnValue, ["fallback"] = fallback, ["retryAfterSeconds"] = retryAfterSeconds,
                ["maxBytes"] = status == 413 ? ConsoleSessionRules.MaxScreenshotBytes : null });
    private static IResult Unavailable(string? device = null, uint? rv = null) => Problem(409, "console-unavailable", "Console device is unavailable in the current VM state.", device, rv);
    private static IResult Expired() => Problem(410, "console-session-expired", "Console session has expired or is unavailable to this caller.");
    private static IResult Rate(HttpContext http, int retryAfterSeconds = 1)
    {
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(429, "rate-limited", "Console session limit exceeded.", retryAfterSeconds: retryAfterSeconds);
    }
    private static ConsoleSession? Session(HttpContext http, string sid, IConsoleSessionStore sessions, IClock clock)
    {
        var s = sessions.Get(sid, clock.UtcNow);
        return s is not null && Ownership.SameName(s.VmName, Vm(http).Name) && Ownership.SameName(s.Principal, Principal(http)) ? s : null;
    }
    private static async Task<T?> Body<T>(HttpContext http, CancellationToken ct)
    {
        // Also bounds chunked bodies; typed text is never included in validation messages.
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int count;
        while ((count = await http.Request.Body.ReadAsync(bytes, ct)) > 0)
        { if (buffer.Length + count > 16384) throw new JsonException(); buffer.Write(bytes, 0, count); }
        if (buffer.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), ApiJson.Options);
    }
    private static async Task<IResult> Capabilities(HttpContext http, IConsoleTransport transport, CancellationToken ct) =>
        Results.Ok(ConsoleCapabilitiesResponse.From(transport.Capabilities, await transport.GetScreenAsync(Vm(http).Name, ct)));
    private static bool Usable(ConsoleScreen s) => s.VideoHeadPresent && s.NativeWidth > 0 && s.NativeHeight > 0;
    private static async Task<IResult> Create(HttpContext http, IConsoleTransport transport, IConsoleSessionStore sessions, IClock clock, CancellationToken ct)
    {
        var request = await Body<ConsoleSessionRequest>(http, ct);
        if (request?.OperationKey is { Length: > 128 }) return CodedProblems.Validation("operationKey", "Operation key is too long.");
        var screen = await transport.GetScreenAsync(Vm(http).Name, ct);
        if (!Usable(screen) || transport.Capabilities.Screenshot == CapabilityLevel.Unsupported) return Unavailable("videoHead");
        var s = sessions.TryCreate(Vm(http).Name, Principal(http), screen.NativeWidth, screen.NativeHeight, ConsoleSessionRules.Ttl, clock.UtcNow);
        if (s is null) return Rate(http, 60);
        Audit(http, "console-session-create");
        return Results.Created($"/api/v1/vms/{s.VmName}/console/sessions/{s.Id}", new { sessionId = s.Id, expiresAt = s.ExpiresAt,
            screen = new { width = screen.NativeWidth, height = screen.NativeHeight }, capabilities = ConsoleCapabilitiesResponse.From(transport.Capabilities, screen) });
    }
    private static async Task<IResult> Renew(string sid, HttpContext http, IConsoleTransport transport, IConsoleSessionStore sessions, IClock clock, CancellationToken ct)
    {
        if (Session(http, sid, sessions, clock) is null) return Expired();
        var screen = await transport.GetScreenAsync(Vm(http).Name, ct);
        if (!Usable(screen) || transport.Capabilities.Screenshot == CapabilityLevel.Unsupported) return Unavailable("videoHead");
        var s = sessions.Renew(sid, ConsoleSessionRules.Ttl, clock.UtcNow, screen.NativeWidth, screen.NativeHeight);
        Audit(http, "console-session-renew");
        return s is null ? Expired() : Results.Ok(new { expiresAt = s.ExpiresAt });
    }
    private static IResult Remove(string sid, HttpContext http, IConsoleSessionStore sessions, IClock clock)
    {
        if (Session(http, sid, sessions, clock) is null) return Expired();
        sessions.Remove(sid); Audit(http, "console-session-delete"); return Results.NoContent();
    }
    private static async Task<IResult> Screenshot(string sid, HttpContext http, IConsoleTransport transport, IConsoleSessionStore sessions, IClock clock, CancellationToken ct)
    {
        if (Session(http, sid, sessions, clock) is null) return Expired();
        if (!sessions.TryTakeRate(sid, "screenshot", 4, clock.UtcNow)) return Rate(http);
        var screen = await transport.GetScreenAsync(Vm(http).Name, ct);
        if (!Usable(screen) || transport.Capabilities.Screenshot == CapabilityLevel.Unsupported) return Unavailable("videoHead");
        var width = screen.NativeWidth; var height = screen.NativeHeight;
        if ((http.Request.Query.TryGetValue("width", out var w) && !int.TryParse(w, out width)) ||
            (http.Request.Query.TryGetValue("height", out var h) && !int.TryParse(h, out height)) ||
            !ConsoleSessionRules.Dimensions(width, height, screen.NativeWidth, screen.NativeHeight))
            return CodedProblems.Validation("dimensions", "Dimensions must be positive, at most native, and at most 4 million pixels.");
        var result = await transport.ScreenshotAsync(Vm(http).Name, width, height, ct);
        if (!result.Ok) return Unavailable("videoHead", result.ReturnValue);
        if (result.Png.Length > ConsoleSessionRules.MaxScreenshotBytes) return Problem(413, "screenshot-too-large", "Screenshot exceeds 4 MiB.");
        http.Response.Headers["X-Construct-Screen-Width"] = screen.NativeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Construct-Screen-Height"] = screen.NativeHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(result.Png.ToArray(), "image/png");
    }
    private static async Task<IResult> Keyboard(string sid, HttpContext http, IConsoleTransport transport, IConsoleSessionStore sessions, IClock clock, CancellationToken ct)
    {
        if (Session(http, sid, sessions, clock) is null) return Expired();
        if (!sessions.TryTakeRate(sid, "input", 50, clock.UtcNow)) return Rate(http);
        var r = await Body<ConsoleKeyboardRequest>(http, ct);
        if (r is null || (r.Kind is not ("text" or "key" or "scancodes" or "ctrlAltDel") || !Enum.TryParse<KeyboardInputKind>(r.Kind, true, out var kind)) || r.Scancodes?.Any(x => x < 0 || x > 255) == true)
            return CodedProblems.Validation("keyboard", "Invalid keyboard input.");
        var input = new KeyboardInput(kind, r.Text, r.KeyCode, r.Press, r.Scancodes?.Select(x => (byte)x).ToArray());
        if (!ConsoleSessionRules.Valid(input)) return CodedProblems.Validation("keyboard", "Invalid keyboard input.");
        Audit(http, "console-keyboard", $"kind={kind.ToString().ToLowerInvariant()}, chars={r.Text?.Length ?? 0}");
        if (transport.Capabilities.Keyboard == CapabilityLevel.Unsupported) return Unavailable("keyboard");
        var result = await transport.KeyboardAsync(Vm(http).Name, input, ct);
        return result.Applied ? Results.Ok(new { accepted = true, returnValue = 0 }) : Unavailable("keyboard", result.ReturnValue);
    }
    private static async Task<IResult> Mouse(string sid, HttpContext http, IConsoleTransport transport, IConsoleSessionStore sessions, IClock clock, CancellationToken ct)
    {
        var s = Session(http, sid, sessions, clock); if (s is null) return Expired();
        if (!sessions.TryTakeRate(sid, "input", 50, clock.UtcNow)) return Rate(http);
        var r = await Body<ConsoleMouseRequest>(http, ct);
        if (r is null || (r.Kind is not ("moveAbsolute" or "moveRelative" or "click" or "press" or "release") || !Enum.TryParse<MouseInputKind>(r.Kind, true, out var kind))) return CodedProblems.Validation("mouse", "Invalid mouse input.");
        var input = new MouseInput(kind, r.X, r.Y, r.Dx, r.Dy, r.Button);
        if (!ConsoleSessionRules.Valid(input, s.NativeWidth, s.NativeHeight)) return CodedProblems.Validation("mouse", "Invalid mouse input or coordinates.");
        Audit(http, "console-mouse", "kind=" + kind.ToString().ToLowerInvariant());
        var screen = await transport.GetScreenAsync(Vm(http).Name, ct);
        if (!screen.SyntheticMousePresent && !screen.Ps2MousePresent) return Unavailable("mouse");
        if (kind == MouseInputKind.MoveAbsolute && !screen.SyntheticMousePresent)
            return Problem(409, "console-unavailable", "Absolute mouse is unavailable; relative movement is available.", "syntheticMouse", fallback: "moveRelative");
        if (!ConsoleSessionRules.Valid(input, screen.NativeWidth, screen.NativeHeight)) return CodedProblems.Validation("mouse", "Coordinates exceed the current screen.");
        var result = await transport.MouseAsync(Vm(http).Name, input, ct);
        return result.Applied ? Results.Ok(new { applied = true }) : Results.Ok(new { applied = false,
            unavailable = new { device = result.Device, returnValue = result.ReturnValue, fallback = result.Fallback } });
    }
}
