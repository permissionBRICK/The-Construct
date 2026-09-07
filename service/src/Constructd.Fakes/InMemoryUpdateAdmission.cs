using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class InMemoryUpdateAdmission(IHostUpdateStore updates,IJobStore jobs,IOperationKeyStore keys) : IHostUpdateAdmission
{
    private readonly SemaphoreSlim _gate=new(1,1);
    public async Task<bool> TryAcceptAsync(HostUpdateRecord row,Job queued,OperationKeyRecord? operation,bool fresh,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(operation is not null && await keys.GetAsync(operation.Owner,operation.Kind,operation.Key,ct) is not null)return false;
            if(fresh){if(!await updates.TryStartAsync(row,ct))return false;}
            else
            {
                var old=await updates.GetAsync(row.Id,ct);if(old?.State is not (HostUpdateState.Staged or HostUpdateState.Interrupted or HostUpdateState.ResolvedByAdmin))return false;
                await updates.UpsertAsync(row,ct);
            }
            await jobs.UpsertAsync(queued,ct);
            if(operation is not null)await keys.TryInsertAsync(operation,ct);
            return true;
        }
        finally{_gate.Release();}
    }
}
