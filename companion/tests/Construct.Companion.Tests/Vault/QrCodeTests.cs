using System.Security.Cryptography;
using System.Text;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Vault;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Vault;

// Section, table and annex numbers refer to ISO/IEC 18004:2015.
public sealed class QrCodeTests
{
    // Table 7: byte-mode capacity at level M, indexed by version (index 0 unused).
    private static readonly int[] ByteCapacityM =
    [
        0, 14, 26, 42, 62, 84, 106, 122, 152, 180, 213, 251, 287, 331, 362, 412, 450, 504, 560, 624, 666,
        711, 779, 857, 911, 997, 1059, 1125, 1190, 1264, 1370, 1452, 1538, 1628, 1722, 1809, 1911, 1989, 2099, 2213, 2331,
    ];

    // Annex C, Table C.1: the eight valid level-M format words (mask 0..7), most significant bit first.
    private static readonly int[] FormatWordsM = [0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0];

    private const string PairingUrl =
        "https://host.example:7462/vault/pair?client=companion&v=2#token=Qm9vdHN0cmFwLXRva2VuLWZvci10aGUtdmF1bHQtMDE"
        + "&next=https%3A%2F%2Fhost.example%3A7462%2Fvault%2Fsession%3Fvm%3Ddev-01%26scope%3Dread%2Bwrite%26return%3D%252Fvault%252Fdone%23token%3Dc2Vzc2lvbi10b2tlbi1mb3ItdGhlLWNvbXBhbmlvbjI"
        + "&label=Construct%20Companion%20%28Windows%29%20%E2%80%93%20vault%20pairing";

    // Deterministic URL-ish text of a given length.
    private static string Filler(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#&%=?/:.-_~";
        return string.Concat(Enumerable.Range(0, length).Select(i => alphabet[(i * 7 + i / 13) % alphabet.Length]));
    }

    private static int VersionOf(bool[,] symbol) => (symbol.GetLength(0) - 17) / 4;

    [Fact]
    public void PicksTheSmallestVersionThatFits()
    {
        Assert.Equal(21, QrCode.Encode("").GetLength(0));
        for (var version = 1; version <= 40; version++)
        {
            var full = QrCode.Encode(Filler(ByteCapacityM[version]));
            Assert.Equal(17 + 4 * version, full.GetLength(0));
            Assert.Equal(full.GetLength(0), full.GetLength(1));
            if (version < 40) Assert.Equal(version + 1, VersionOf(QrCode.Encode(Filler(ByteCapacityM[version] + 1))));
        }
    }

    [Fact]
    public void CountsUtf8BytesNotCharacters()
    {
        Assert.Equal(1, VersionOf(QrCode.Encode(new string('ü', 7))));    // 14 bytes
        Assert.Equal(2, VersionOf(QrCode.Encode(new string('ü', 8))));    // 16 bytes
        Assert.Equal(1, VersionOf(QrCode.Encode("pässwörd ✓")));           // 14 bytes
    }

    [Fact]
    public void RejectsTextBeyondVersion40()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(Filler(2332)));
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('ü', 1166)));
        Assert.Equal(40, VersionOf(QrCode.Encode(new string('ü', 1165))));
        Assert.Throws<ArgumentNullException>(() => QrCode.Encode(null!));
    }

    // Annex E, Table E.1: alignment pattern centre coordinates for a sample of versions.
    public static TheoryData<int, int[]> AlignmentTable => new()
    {
        { 1, [] },
        { 2, [6, 18] },
        { 6, [6, 34] },
        { 7, [6, 22, 38] },
        { 14, [6, 26, 46, 66] },
        { 21, [6, 28, 50, 72, 94] },
        { 32, [6, 34, 60, 86, 112, 138] },
        { 40, [6, 30, 58, 86, 114, 142, 170] },
    };

    private static readonly string[] Finder = ["#######", "#.....#", "#.###.#", "#.###.#", "#.###.#", "#.....#", "#######"];
    private static readonly string[] Alignment = ["#####", "#...#", "#.#.#", "#...#", "#####"];

    [Theory, MemberData(nameof(AlignmentTable))]
    public void DrawsTheFunctionPatterns(int version, int[] alignmentCentres)
    {
        var symbol = QrCode.Encode(Filler(ByteCapacityM[version]));
        var size = symbol.GetLength(0);
        Assert.Equal(17 + 4 * version, size);

        // 6.3.3/6.3.4: finders in three corners, each with a light separator towards the symbol.
        AssertPattern(symbol, 0, 0, Finder);
        AssertPattern(symbol, 0, size - 7, Finder);
        AssertPattern(symbol, size - 7, 0, Finder);
        for (var i = 0; i < 8; i++)
        {
            Assert.False(symbol[7, i]); Assert.False(symbol[i, 7]);                         // top-left
            Assert.False(symbol[7, size - 1 - i]); Assert.False(symbol[i, size - 8]);       // top-right
            Assert.False(symbol[size - 8, i]); Assert.False(symbol[size - 1 - i, 7]);       // bottom-left
        }

        // 6.3.5: timing patterns alternate between the separators, dark on even coordinates.
        for (var i = 8; i < size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, symbol[6, i]);
            Assert.Equal(i % 2 == 0, symbol[i, 6]);
        }

        // 7.9.1: the dark module.
        Assert.True(symbol[4 * version + 9, 8]);

        // 6.3.6: alignment patterns at every combination of Table E.1 centres except the finder corners.
        var last = alignmentCentres.Length - 1;
        for (var r = 0; r <= last; r++)
            for (var c = 0; c <= last; c++)
                if (!((r == 0 && c == 0) || (r == 0 && c == last) || (r == last && c == 0)))
                    AssertPattern(symbol, alignmentCentres[r] - 2, alignmentCentres[c] - 2, Alignment);
    }

    private static void AssertPattern(bool[,] symbol, int top, int left, string[] pattern)
    {
        for (var r = 0; r < pattern.Length; r++)
            for (var c = 0; c < pattern[r].Length; c++)
                Assert.True(symbol[top + r, left + c] == (pattern[r][c] == '#'), $"module ({top + r}, {left + c})");
    }

    [Fact]
    public void FormatInformationIsDuplicatedAndLevelM()
    {
        foreach (var text in new[] { "", "construct", "pässwörd ✓", PairingUrl, Filler(ByteCapacityM[23]), Filler(ByteCapacityM[40]) })
        {
            var symbol = QrCode.Encode(text);
            var size = symbol.GetLength(0);
            // 7.9.1, Figure 25: bit 14 first. Copy 1 runs along row 8 and up column 8 around the top-left finder.
            var nearTopLeft = Read(symbol, [(8, 0), (8, 1), (8, 2), (8, 3), (8, 4), (8, 5), (8, 7), (8, 8), (7, 8), (5, 8), (4, 8), (3, 8), (2, 8), (1, 8), (0, 8)]);
            // Copy 2: bits 14..8 up from the bottom of column 8, bits 7..0 along row 8 under the top-right finder.
            var split = Read(symbol, [.. Enumerable.Range(1, 7).Select(k => (size - k, 8)), .. Enumerable.Range(0, 8).Select(k => (8, size - 8 + k))]);
            Assert.Equal(nearTopLeft, split);
            Assert.Contains(nearTopLeft, FormatWordsM);
        }
    }

    // Annex D, Table D.1: version information words.
    [Theory]
    [InlineData(7, 0x07C94)]
    [InlineData(8, 0x085BC)]
    [InlineData(14, 0x0E60D)]
    [InlineData(21, 0x15683)]
    [InlineData(32, 0x209D5)]
    [InlineData(40, 0x28C69)]
    public void VersionInformationMatchesAnnexD(int version, int word)
    {
        var symbol = QrCode.Encode(Filler(ByteCapacityM[version]));
        var size = symbol.GetLength(0);
        // 7.10, Figure 27: bit i of the 18-bit word sits at (size - 11 + i % 3, i / 3) above the bottom-left
        // finder and at the transposed position left of the top-right finder. Read bit 17 first.
        var bottomLeft = Read(symbol, [.. Enumerable.Range(0, 18).Reverse().Select(i => (size - 11 + i % 3, i / 3))]);
        var topRight = Read(symbol, [.. Enumerable.Range(0, 18).Reverse().Select(i => (i / 3, size - 11 + i % 3))]);
        Assert.Equal(word, bottomLeft);
        Assert.Equal(word, topRight);
    }

    private static int Read(bool[,] symbol, (int Row, int Col)[] cells) => cells.Aggregate(0, (bits, cell) => bits << 1 | (symbol[cell.Row, cell.Col] ? 1 : 0));

    // Reference symbols from two independent encoders that agree bit for bit at the same version and mask:
    // Nayuki's qrcodegen 1.8.0 (Python), QrCode.encode_segments([QrSegment.make_bytes(text.encode())],
    // Ecc.MEDIUM, minversion=V, maxversion=V, mask=M, boostecl=False), and python-qrcode 8.2. Masks are
    // pinned to the one this encoder chose, since encoders legitimately differ in how they read the penalty
    // rules. Digest: SHA-256 of the rows concatenated as '0'/'1'.
    [Fact]
    public void MatchesIndependentReferenceEncoders()
    {
        string[] construct =   // version 1, mask 3
        [
            "#######.###...#######",
            "#.....#.#.#...#.....#",
            "#.###.#..###..#.###.#",
            "#.###.#.##.##.#.###.#",
            "#.###.#..#.#..#.###.#",
            "#.....#..####.#.....#",
            "#######.#.#.#.#######",
            "........##.##........",
            "#.##.###..###.#..#.##",
            "..#.#.....###..###..#",
            "##.#..##...#.#.#..###",
            ".###.#.#...#...###..#",
            "#####.#.###.###......",
            "........####.####.#..",
            "#######.##.###.##.#..",
            "#.....#.##.....#####.",
            "#.###.#..##.#.#...#..",
            "#.###.#.#.##..##...#.",
            "#.###.#.#...#.#..#...",
            "#.....#...#..#......#",
            "#######.#.#..#.####..",
        ];
        Assert.Equal(construct, Rows(QrCode.Encode("construct")).Select(row => row.Replace('1', '#').Replace('0', '.')));
        Assert.Equal("31511af1db642d2686267b761da1484dfd6b619b2be92c18e01fc38c94bebd9a", Digest(QrCode.Encode(PairingUrl)));                     // version 14, mask 2
        Assert.Equal("d8b9213f05e7dcf20cacc0bfcae565ffb0656dc7d2727af34b4ff9f4be97f8d1", Digest(QrCode.Encode(string.Concat(Enumerable.Repeat("pässwörd ✓ ", 10)))));  // version 8, mask 2
        Assert.Equal("38471329713d500c4349e9e12cf85c5c3b2e7cbe0d15ff79ade8ca323f4ebcf5", Digest(QrCode.Encode(Filler(600))));                    // version 19, mask 2
        Assert.Equal("fd90c54fa7f977ffdeec696ce9ea4c853dc39575d9301003b65a7fe536be49cd", Digest(QrCode.Encode(Filler(2331))));                   // version 40, mask 2
    }

    private static IEnumerable<string> Rows(bool[,] symbol)
    {
        var size = symbol.GetLength(0);
        for (var r = 0; r < size; r++)
        {
            var row = new StringBuilder(size);
            for (var c = 0; c < size; c++) row.Append(symbol[r, c] ? '1' : '0');
            yield return row.ToString();
        }
    }

    private static string Digest(bool[,] symbol) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(Rows(symbol)))));

    // --- Round trip through a real scanner (zbarimg from zbar-tools) ---

    private static string DecodeCase(string name) => name switch
    {
        "short ascii" => "construct",
        "pairing url" => PairingUrl,
        "utf-8" => "pässwörd ✓",
        "utf-8 v8" => string.Concat(Enumerable.Repeat("pässwörd ✓ ", 10)),
        "v3" => Filler(42),
        "v7" => Filler(ByteCapacityM[7]),
        "v10" => Filler(ByteCapacityM[10]),
        "v17" => Filler(480),
        "v19" => Filler(600),
        "v40" => Filler(ByteCapacityM[40]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("short ascii", 1)]
    [InlineData("pairing url", 14)]
    [InlineData("utf-8", 1)]
    [InlineData("utf-8 v8", 8)]
    [InlineData("v3", 3)]
    [InlineData("v7", 7)]
    [InlineData("v10", 10)]
    [InlineData("v17", 17)]
    [InlineData("v19", 19)]
    [InlineData("v40", 40)]
    public async Task ScannerDecodesTheSymbol(string name, int version)
    {
        if (!Has("zbarimg")) return;
        var text = DecodeCase(name);
        var symbol = QrCode.Encode(text);
        Assert.Equal(version, VersionOf(symbol));
        Assert.Equal(text + "\n", await Scan(symbol));
    }

    private static bool Has(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));

    private static async Task<string> Scan(bool[,] symbol)
    {
        var temp = Directory.CreateTempSubdirectory("cc-qr-");
        try
        {
            var path = Path.Combine(temp.FullName, "symbol.pgm");
            await File.WriteAllBytesAsync(path, Pgm(symbol, quietZone: 4, scale: 6));
            var result = await new RuntimeProcessRunner().RunAsync(new ProcessInvocation("zbarimg", ["--raw", "-q", path], Timeout: TimeSpan.FromSeconds(60)));
            Assert.True(result.Code == 0, $"zbarimg exit {result.Code}: {result.Stderr}");
            return result.Stdout;
        }
        finally { temp.Delete(true); }
    }

    // Binary greymap (P5): black modules, white background, with the quiet zone the encoder leaves out.
    private static byte[] Pgm(bool[,] symbol, int quietZone, int scale)
    {
        var size = symbol.GetLength(0);
        var pixels = (size + 2 * quietZone) * scale;
        var header = Encoding.ASCII.GetBytes($"P5\n{pixels} {pixels}\n255\n");
        var image = new byte[header.Length + pixels * pixels];
        header.CopyTo(image, 0);
        for (var y = 0; y < pixels; y++)
            for (var x = 0; x < pixels; x++)
            {
                int row = y / scale - quietZone, col = x / scale - quietZone;
                var dark = row >= 0 && row < size && col >= 0 && col < size && symbol[row, col];
                image[header.Length + y * pixels + x] = dark ? (byte)0 : (byte)255;
            }
        return image;
    }
}
