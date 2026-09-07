using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class FakeReleaseInfo : IReleaseInfo
{
    public InstalledRelease Installed { get; set; } = new("0123456789abcdef0123456789abcdef0123456789", "1.0.0-test", null, "unknown");
    public int SchemaVersion { get; set; } = 100;
    public int SchemaMinReadableBy { get; set; }
    public IReadOnlyList<string> ApiFeatures { get; set; } = ["host-admin", "console", "updates", "network"];
}
