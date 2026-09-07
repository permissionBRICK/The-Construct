namespace Constructd.Core.Logic;
public static class IsoSignature
{
    public static bool IsPrimaryDescriptor(ReadOnlySpan<byte> sector) =>
        sector.Length >= 7 && sector[0] == 1 && sector.Slice(1, 5).SequenceEqual("CD001"u8) && sector[6] == 1;
}
