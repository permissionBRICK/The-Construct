namespace Constructd.Core.Domain;

/// <summary>
/// Host setting <c>repair</c> (design 11.5): <paramref name="Auto"/> starts the repair agent by itself
/// when a provision, reinstall or restore fails with failed steps. A repair asked for by hand runs either way.
/// </summary>
public sealed record RepairConfig(bool Auto = true);
