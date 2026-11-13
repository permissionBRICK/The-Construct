using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>
/// The rules of <c>POST /jobs/{id}/retry</c> (design 7.1), shared with the repair agent's retry: a failed
/// <c>provision</c>, <c>reinstall</c> or <c>restore</c>, on the VM it ran on, and the VM's latest such job.
/// </summary>
public static class JobRetryRules
{
    public static bool IsLifecycleKind(string kind) => kind is JobKinds.Provision or JobKinds.Reinstall or JobKinds.Restore;

    /// <summary>The request id of the one retry a failed job gets; a repeat finds the retry it started.</summary>
    public static string RequestId(Job failed) => "retry:" + failed.Id;

    /// <summary>Why <paramref name="failed"/> cannot be retried on <paramref name="vm"/>, as (status, code, message), or null.</summary>
    public static (int Status, string Code, string Message)? Refusal(Job failed, Vm vm, IEnumerable<Job> all)
    {
        if (!IsLifecycleKind(failed.Kind) || failed.VmName is null)
            return (409, "not-retryable", $"A {failed.Kind} job cannot be retried.");
        if (failed.State != JobState.Failed) return (409, "job-not-failed", "Only a failed job can be retried.");
        // The same VM: one created again under the name, by anybody, is not the one the job ran on.
        if (!Ownership.SameName(vm.Name, failed.VmName) || !Ownership.SameName(vm.Owner, failed.Owner) || vm.Created > failed.Created)
            return (409, "vm-changed", "The VM this job ran on no longer exists.");
        var requestId = RequestId(failed);
        if (all.Any(j => IsLifecycleKind(j.Kind) && j.Created > failed.Created && j.OperationKey != requestId
                && Ownership.SameName(j.VmName, vm.Name) && Ownership.SameName(j.Owner, vm.Owner)))
            return (409, "job-superseded", "A later job ran on this VM; only the latest one can be retried.");
        return null;
    }
}
