using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Api.Endpoints;

/// <summary>
/// What a forward is for and where its service lives (docs/expose.md). Optional on the wire: forwards made without
/// one, by older clients included, keep working as before.
/// </summary>
internal sealed record ForwardNote(string? Purpose, string? Source, IResult? Problem = null)
{
    public const int MaxPurpose = 200;
    public const int MaxSource = 400;

    public static ForwardNote Parse(CreateForwardRequest? request)
    {
        var purpose = Clean(request?.Purpose);
        // A path is kept exactly as given: no whitespace squeezed, nothing trimmed.
        var source = string.IsNullOrWhiteSpace(request?.Source) ? null : request.Source;
        if (purpose?.Length > MaxPurpose)
            return new(null, null, CodedProblems.Validation("purpose", $"At most {MaxPurpose} characters."));
        if (source is not null && (source.Length > MaxSource || source.Any(char.IsControl)))
            return new(null, null, CodedProblems.Validation("source", $"A path on one line, at most {MaxSource} characters."));
        return new(purpose, source);
    }

    /// <summary>
    /// Stores the note on a forward that was just added. The forward exists already, so nothing here may fail the
    /// request (a client would retry and get a second forward): the write is not cancelled with the request, and a
    /// failed one leaves the forward without its note and is logged.
    /// </summary>
    public static async Task<PortForward> KeepAsync(HttpContext http, PortForward forward, ForwardNote note, CancellationToken ct)
    {
        _ = ct;
        if (note.Purpose is null && note.Source is null) return forward;
        try
        {
            if (await http.RequestServices.GetRequiredService<IForwardStore>().SetNoteAsync(forward.Id, note.Purpose, note.Source, CancellationToken.None))
                return forward with { Purpose = note.Purpose, Source = note.Source };
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ForwardNote))
                .LogWarning("The note of forward {Id} was not stored ({Error}); the forward works without it.", forward.Id, ex.GetType().Name);
        }
        return forward;
    }

    /// <summary>The refusal at the per-VM cap says how to make room.</summary>
    public static IResult LimitReached(string vmName, int max) => CodedProblems.Create(403, "forward-limit",
        $"VM '{vmName}' already has the maximum of {max} forwards. List them (construct expose --list), check each one's " +
        "purpose and source, and close the ones no longer needed (construct expose --close ID).");

    // One line: control characters out, whitespace squeezed, trimmed; nothing left is no note.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = string.Join(' ', new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? null : cleaned;
    }
}
