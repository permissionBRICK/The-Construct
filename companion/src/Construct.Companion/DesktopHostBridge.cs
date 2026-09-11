using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion;

internal sealed class DesktopHostBridge : ICompanionDesktop, IClipboard, IDisposable
{
    private readonly Control dispatcher = new();
    public TrayContext? Tray { get; set; }
    public IPrompts Prompts { get; }
    public DesktopHostBridge(bool diagnostic = false)
    {
        // Selftest has no message loop and must never open an interactive prompt.
        if (diagnostic) Prompts = new DiagnosticPrompts();
        else { _ = dispatcher.Handle; Prompts = new DesktopPrompts(dispatcher); }
    }
    private sealed class DiagnosticPrompts : IPrompts
    {
        public Task<string?> InputAsync(InputPrompt prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Selftest cannot prompt.");
        public Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Selftest cannot prompt.");
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Selftest cannot prompt.");
        public Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Selftest cannot prompt.");
    }
    public Task ActivateAsync(UiActivation activation, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(async ct => { if (Tray is {} tray) await tray.ActivateAsync([activation], ct); }, cancellationToken);
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => Clipboard.SetText(text), cancellationToken);
    public void Dispose() => dispatcher.Dispose();
}
