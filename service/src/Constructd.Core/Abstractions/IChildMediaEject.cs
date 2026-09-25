namespace Constructd.Core.Abstractions;

/// <summary>Live CD eject for any guest OS. Works running or off, keeps the drives and boot order, and checks immutable VM identity at the mutation boundary.</summary>
public interface IChildMediaEject
{
    /// <summary>Empties the install drive, plus auxiliary and guest-agent drives unless <paramref name="installOnly"/>.</summary>
    Task EjectMediaAsync(string name, string incarnation, bool installOnly, CancellationToken ct);
}
