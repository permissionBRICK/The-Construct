namespace Constructd.Core.Domain;

/// <summary>
/// In-guest activity heartbeat posted with the VM-scoped token (plan §4.7). <c>Busy</c> means an
/// agent process, tmux pane or provisioning run is actively working, and keeps the VM alive even
/// with zero client connections.
/// </summary>
public sealed record ActivityReport(
    string VmName,
    bool Busy,
    IReadOnlyList<string> Reasons,
    DateTimeOffset ReportedAt);
