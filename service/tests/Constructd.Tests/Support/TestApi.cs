using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;

namespace Constructd.Tests.Support;

/// <summary>Request helpers shared by the integration tests.</summary>
public static class TestApi
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, ApiJson.Options);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body, ApiJson.Options);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>Starts a VM creation job and returns the accepted job id.</summary>
    public static async Task<string> StartCreateVmAsync(
        this HttpClient client,
        string name,
        int cpu = 4,
        int ramGb = 8,
        int diskGb = 64,
        object? opts = null)
    {
        var response = await client.PostJsonAsync("/api/v1/vms", new { name, cpu, ramGb, diskGb, opts });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<JobAcceptedResponse>();
        return accepted.JobId;
    }

    /// <summary>Creates a VM and waits for the job to succeed.</summary>
    public static async Task<JobResponse> CreateVmAsync(this HttpClient client, string name)
    {
        var jobId = await client.StartCreateVmAsync(name);
        var job = await client.WaitForJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, job.State);
        return job;
    }

    /// <summary>Polls a job until it reaches a terminal state.</summary>
    public static async Task<JobResponse> WaitForJobAsync(this HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/v1/jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var job = await response.ReadAsync<JobResponse>();

            if (job.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
            {
                return job;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Job {jobId} did not finish within {JobTimeout}.");
    }

    /// <summary>The VM-scoped token a creation job hands out exactly once, in <c>result.vmToken</c>.</summary>
    public static string VmToken(this JobResponse job)
    {
        var token = job.VmTokenOrNull();
        Assert.False(string.IsNullOrWhiteSpace(token), "the job result carried no vmToken");
        return token!;
    }

    /// <summary>The <c>result.vmToken</c> of a creation job, or null when it was already consumed.</summary>
    public static string? VmTokenOrNull(this JobResponse job)
    {
        Assert.NotNull(job.Result);
        var element = (JsonElement)job.Result!;
        Assert.True(element.TryGetProperty("vmToken", out var value), "the job result has no 'vmToken' property");
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static JsonElement ResultElement(this JobResponse job, string property)
    {
        var element = (JsonElement)job.Result!;
        Assert.True(element.TryGetProperty(property, out var value), $"job result has no '{property}'");
        return value;
    }
}
