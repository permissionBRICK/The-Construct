using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>
/// What a job that succeeded did not finish (design 7.1, F41): the optional guest steps that failed in its provisioning
/// (<c>failedSteps</c> of a provision or restore result, or of the <c>provision</c> part of a create or reinstall result).
/// The job is done; the client shows it as done with warnings, not as failed.
/// </summary>
public static class JobWarnings
{
    /// <summary>The titles of the failed optional steps of a job that succeeded; null when there are none.</summary>
    public static IReadOnlyList<string>? Of(Job job)
    {
        if (job.State != JobState.Succeeded || job.Result is null) return null;
        var result = job.Result is JsonElement element ? element : JsonSerializer.SerializeToElement(job.Result, job.Result.GetType(), ApiJson.Options);
        if (result.ValueKind != JsonValueKind.Object) return null;
        var titles = Titles(result);
        if (titles.Count == 0 && result.TryGetProperty("provision", out var provision) && provision.ValueKind == JsonValueKind.Object) titles = Titles(provision);
        return titles.Count == 0 ? null : titles;
    }

    private static List<string> Titles(JsonElement result)
    {
        var titles = new List<string>();
        if (!result.TryGetProperty("failedSteps", out var steps) || steps.ValueKind != JsonValueKind.Array) return titles;
        foreach (var step in steps.EnumerateArray())
            if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                titles.Add(title.GetString()!);
        return titles;
    }
}
