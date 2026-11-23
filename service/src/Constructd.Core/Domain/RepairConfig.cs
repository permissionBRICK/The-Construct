namespace Constructd.Core.Domain;

/// <summary>
/// Host setting <c>repair</c> (design 11.5): <paramref name="Auto"/> starts the repair agent by itself
/// when a provision, reinstall or restore fails with failed steps. A repair asked for by hand runs either way.
/// The repair runs as a T3 Code thread in the VM, where the user follows and answers it in T3 Code (owner,
/// 2026-11-21); <paramref name="Chat"/> runs the agent directly on the VM instead, answered in Construct's own
/// chat under the failed job. A VM without a running T3 Code gets the direct agent either way.
/// </summary>
public sealed record RepairConfig(bool Auto = true, bool Chat = false);
