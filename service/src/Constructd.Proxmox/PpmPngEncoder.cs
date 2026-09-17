using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Proxmox;

/// <summary>Bounded QEMU P6 RGB screenshots, without a platform-specific imaging library.</summary>
public static class PpmPngEncoder
{
    public const int MaxPpmBytes = ConsoleSessionRules.MaxPixels * 3 + 4096;
    public sealed record Pixels(int Width, int Height, ReadOnlyMemory<byte> Rgb);
    public static Pixels Read(byte[] ppm)
    {
        if (ppm.Length > MaxPpmBytes) throw new ConsoleTransportException(true);
        var offset = 0;
        string Token()
        {
            while (offset < ppm.Length)
            {
                if (ppm[offset] == '#') { while (offset < ppm.Length && ppm[offset] != '\n') offset++; }
                else if (Space(ppm[offset])) offset++;
                else break;
            }
            var start = offset;
            while (offset < ppm.Length && !Space(ppm[offset]) && ppm[offset] != '#') offset++;
            if (offset - start is < 1 or > 16) throw new ConsoleTransportException();
            return Encoding.ASCII.GetString(ppm, start, offset - start);
        }
        if (Token() != "P6" || !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var height) || Token() != "255")
            throw new ConsoleTransportException();
        if (width < 1 || height < 1) throw new ConsoleTransportException();
        if ((long)width * height > ConsoleSessionRules.MaxPixels) throw new ConsoleTransportException(true);
        if (offset >= ppm.Length || !Space(ppm[offset])) throw new ConsoleTransportException();
        // Only the header delimiter is whitespace. A pixel may itself be a newline or '#'.
        if (ppm[offset++] == '\r' && offset < ppm.Length && ppm[offset] == '\n') offset++;
        if (ppm.Length - offset != (long)width * height * 3) throw new ConsoleTransportException();
        return new(width, height, ppm.AsMemory(offset));
    }
    private static bool Space(byte b) => b is 9 or 10 or 11 or 12 or 13 or 32;
    public static byte[] Encode(Pixels pixels)
    {
        if (!ConsoleSessionRules.Dimensions(pixels.Width, pixels.Height, 65535, 65535) ||
            pixels.Rgb.Length != (long)pixels.Width * pixels.Height * 3) throw new ConsoleTransportException();
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, pixels.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), pixels.Height);
        header[8] = 8; header[9] = 2; // Eight-bit RGB, no palette or interlace.
        Chunk(png, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (var row = 0; row < pixels.Height; row++)
            { zlib.WriteByte(0); zlib.Write(pixels.Rgb.Span.Slice(row * pixels.Width * 3, pixels.Width * 3)); }
        if (compressed.Length + 57 > ConsoleSessionRules.MaxScreenshotBytes) throw new ConsoleTransportException(true);
        Chunk(png, "IDAT"u8, compressed.ToArray()); Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }
    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, data.Length); output.Write(value); output.Write(type); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in type) crc = CrcByte(crc, b);
        foreach (var b in data) crc = CrcByte(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); output.Write(value);
    }
    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }
}
