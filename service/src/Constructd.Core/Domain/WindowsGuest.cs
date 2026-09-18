using System.Text.Json.Serialization;
namespace Constructd.Core.Domain;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowsUnattend(string AdminPassword, string Hostname = "WINVM", string Locale = "en-US",
    string TimeZone = "UTC", string? FirstLogonScript = null, IReadOnlyDictionary<string, string>? Files = null);

public sealed record WindowsImage(string Product, string Edition, string ImageName, int Index, string Build, bool Evaluation = false);
public sealed record WindowsMedia(string Product, string Language, IReadOnlyList<WindowsImage> Images, bool Prepared = false, string? OriginalId = null);
public sealed record WindowsSelection(string Product, string Edition);
