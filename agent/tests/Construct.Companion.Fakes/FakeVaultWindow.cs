using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Vault;
namespace Construct.Companion.Fakes;

// The Key Vault window's native side: Entries scripts what the user types into the next add/edit
// dialog (null cancels it); every dialog, copy and pairing code is recorded.
public sealed class FakeVaultWindow : IVaultWindow
{
    public Queue<VaultSecretInput?> Entries { get; } = new();
    public List<VaultSecretView?> Editors { get; } = [];
    public List<string?> EditorProblems { get; } = [];
    public List<(Secret Text, bool Sensitive)> Copies { get; } = [];
    public bool ClipboardAvailable { get; set; } = true;
    public List<VaultPairing> Pairings { get; } = [];
    public Task EditSecretAsync(VaultSecretView? existing, Func<VaultSecretInput, string?> save, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Editors.Add(existing);
        if (Entries.TryDequeue(out var input) && input is not null) EditorProblems.Add(save(input));
        return Task.CompletedTask;
    }
    public Task<bool> CopyAsync(Secret text, bool sensitive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ClipboardAvailable) Copies.Add((text, sensitive));
        return Task.FromResult(ClipboardAvailable);
    }
    public Task ShowPairingAsync(VaultPairing pairing, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Pairings.Add(pairing); return Task.CompletedTask; }
}
