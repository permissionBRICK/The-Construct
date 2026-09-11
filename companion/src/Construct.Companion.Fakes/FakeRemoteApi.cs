using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeRemoteApi : IRemoteApi
{
    public Exception? Failure { get; set; }
    public string Fingerprint { get; set; } = "test-fingerprint";
    public List<RemoteRequest> Requests { get; } = [];
    public Queue<RemoteResponse> Responses { get; } = new();
    public Task<RemoteResponse> SendAsync(RemoteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.VerifyPin(Fingerprint)) throw new InvalidOperationException("Certificate pin rejected.");
        Requests.Add(request);
        if (Failure is not null) throw Failure;
        return Task.FromResult(Responses.Dequeue());
    }
}
