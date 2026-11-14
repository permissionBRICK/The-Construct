using System.Text;
namespace Construct.Companion.Core.Vault;

// QR Code Model 2 encoder after ISO/IEC 18004:2015, written from scratch because Core takes no packages.
// The whole text goes into one byte-mode segment as UTF-8, at error correction level M, in the smallest
// version that fits. No ECI designator is written, as in common generators (python-qrcode, qrcodegen):
// some older readers mishandle ECI, while decoders such as ZXing and zbar recognise UTF-8 in plain byte
// mode. The pairing URLs this is for are ASCII anyway, which reads the same under every charset guess.
// Section numbers below refer to the 2015 edition.
public static class QrCode
{
    private const int MinVersion = 1, MaxVersion = 40;

    // Returns the symbol as [row, col], true = dark, without the quiet zone (4 light modules on every
    // side, 6.3.8), which the renderer must add. Throws ArgumentException when the text needs more than
    // the 2331 bytes of version 40-M.
    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = Encoding.UTF8.GetBytes(text);
        var version = SmallestVersion(data.Length)
            ?? throw new ArgumentException($"Text needs {data.Length} UTF-8 bytes; a QR code at level M holds at most {ByteCapacity(MaxVersion)}.", nameof(text));
        var codewords = Interleave(version, EncodeData(data, version));
        var symbol = new Symbol(version);
        symbol.PlaceCodewords(codewords);
        symbol.ApplyBestMask();
        return symbol.Modules;
    }

    private static int? SmallestVersion(int byteCount)
    {
        for (var version = MinVersion; version <= MaxVersion; version++)
            if (byteCount <= ByteCapacity(version)) return version;
        return null;
    }

    // Bytes one byte-mode segment can carry: mode indicator and count, then 8 bits per byte.
    private static int ByteCapacity(int version) => (DataCodewords(version) * 8 - 4 - CountBits(version)) / 8;

    // Table 3: the byte-mode character count indicator is 8 bits up to version 9, 16 bits after.
    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    // --- Error correction table (Table 9, level M only) ---

    // Error correction codewords per block, indexed by version (index 0 unused).
    private static readonly int[] EccPerBlock =
    [
        0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
    ];

    // Number of error correction blocks, indexed by version (index 0 unused).
    private static readonly int[] BlockCount =
    [
        0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49,
    ];

    private static int TotalCodewords(int version) => DataModules(version) / 8;
    private static int DataCodewords(int version) => TotalCodewords(version) - EccPerBlock[version] * BlockCount[version];

    // Table 1, "data modules": everything that is not a function pattern, format or version information.
    // The remainder after the last whole codeword (0, 3, 4 or 7 bits) stays light before masking.
    private static int DataModules(int version)
    {
        var size = SizeOf(version);
        var modules = size * size;
        modules -= 3 * 8 * 8;                  // finder patterns with their separators (6.3.3, 6.3.4)
        modules -= 2 * (size - 16);            // timing patterns (6.3.5)
        modules -= 2 * 15 + 1;                 // both format information copies and the dark module (7.9)
        if (version >= 7) modules -= 2 * 18;   // both version information blocks (7.10)
        var axis = AlignmentPositions(version).Length;
        if (axis > 0)
        {
            modules -= 25 * (axis * axis - 3);   // alignment patterns minus the three corners (6.3.6)
            modules += 2 * 5 * (axis - 2);       // the ones on row/column 6 share 5 modules with timing
        }
        return modules;
    }

    // 6.3.1: version V is (17 + 4V) modules square.
    private static int SizeOf(int version) => 17 + 4 * version;

    // Annex E, Table E.1: row/column coordinates of alignment pattern centres. The first is always 6, the
    // last size - 7, and the ones between are evenly spaced by an even step (version 32 is the one
    // exception the formula needs); this reproduces the table.
    private static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];
        var count = version / 7 + 2;
        var step = version == 32 ? 26 : (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var positions = new int[count];
        positions[0] = 6;
        for (var i = count - 1; i >= 1; i--) positions[i] = SizeOf(version) - 7 - (count - 1 - i) * step;
        return positions;
    }

    // --- Data encoding (7.4) ---

    // Mode indicator, count, the bytes, terminator, zero bits to the byte boundary, then the alternating
    // pad codewords 0xEC 0x11 (7.4.10) until the version's data capacity is full.
    private static byte[] EncodeData(byte[] data, int version)
    {
        var capacityBits = DataCodewords(version) * 8;
        var bits = new BitBuffer(capacityBits);
        bits.Append(0b0100, 4);                    // Table 2: byte mode
        bits.Append(data.Length, CountBits(version));
        foreach (var value in data) bits.Append(value, 8);
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));  // terminator, shortened when full (7.4.9)
        bits.Append(0, (8 - bits.Length % 8) % 8);
        for (var pad = 0; bits.Length < capacityBits; pad ^= 1) bits.Append(pad == 0 ? 0xEC : 0x11, 8);
        return bits.ToBytes();
    }

    private sealed class BitBuffer(int capacity)
    {
        private readonly bool[] bits = new bool[capacity];
        public int Length { get; private set; }
        // Appends the low `count` bits of value, most significant first.
        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) bits[Length++] = ((value >> i) & 1) != 0;
        }
        public byte[] ToBytes()
        {
            var bytes = new byte[Length / 8];
            for (var i = 0; i < Length; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            return bytes;
        }
    }

    // --- Error correction and interleaving (7.5, 7.6) ---

    // Splits the data codewords into blocks (the later blocks carry one more data codeword when the
    // split is uneven, as in Table 9), appends each block's Reed-Solomon codewords, then interleaves:
    // the i-th data codeword of every block in turn, then the i-th error correction codeword of every block.
    private static byte[] Interleave(int version, byte[] data)
    {
        var blockCount = BlockCount[version];
        var eccLength = EccPerBlock[version];
        var longBlocks = DataCodewords(version) % blockCount;
        var shortLength = DataCodewords(version) / blockCount;
        var generator = ReedSolomon.Generator(eccLength);

        var blocks = new byte[blockCount][];
        var eccs = new byte[blockCount][];
        for (int b = 0, offset = 0; b < blockCount; b++)
        {
            var length = shortLength + (b >= blockCount - longBlocks ? 1 : 0);
            blocks[b] = data[offset..(offset + length)];
            eccs[b] = ReedSolomon.Remainder(blocks[b], generator);
            offset += length;
        }

        var result = new List<byte>(TotalCodewords(version));
        for (var i = 0; i <= shortLength; i++)
            foreach (var block in blocks)
                if (i < block.Length) result.Add(block[i]);
        for (var i = 0; i < eccLength; i++)
            foreach (var ecc in eccs) result.Add(ecc[i]);
        return [.. result];
    }

    // Reed-Solomon over GF(2^8) with the field polynomial x^8 + x^4 + x^3 + x^2 + 1 (7.5.2, Annex A).
    private static class ReedSolomon
    {
        // g(x) = (x - a^0)(x - a^1)...(x - a^(n-1)) with a = 2; coefficients highest power first, g[0] = 1.
        public static byte[] Generator(int degree)
        {
            var g = new byte[degree + 1];
            g[0] = 1;
            byte root = 1;
            for (var i = 0; i < degree; i++)
            {
                // Multiply by (x + root); subtraction is XOR in GF(2^8).
                for (var k = i + 1; k >= 1; k--) g[k] ^= Multiply(g[k - 1], root);
                root = Multiply(root, 2);
            }
            return g;
        }

        // The error correction codewords: the remainder of data(x) * x^n divided by g(x).
        public static byte[] Remainder(byte[] data, byte[] generator)
        {
            var degree = generator.Length - 1;
            var remainder = new byte[degree];
            foreach (var value in data)
            {
                var factor = (byte)(value ^ remainder[0]);
                Array.Copy(remainder, 1, remainder, 0, degree - 1);
                remainder[degree - 1] = 0;
                for (var i = 0; i < degree; i++) remainder[i] ^= Multiply(generator[i + 1], factor);
            }
            return remainder;
        }

        // Carry-less multiplication reduced by 0x11D.
        private static byte Multiply(byte x, byte y)
        {
            var product = 0;
            for (var i = 7; i >= 0; i--)
            {
                product = (product << 1) ^ ((product >> 7) * 0x11D);
                if (((y >> i) & 1) != 0) product ^= x;
            }
            return (byte)product;
        }
    }

    // --- The symbol (6.3, 7.7 - 7.10) ---

    private sealed class Symbol
    {
        private readonly int version, size;
        private readonly bool[,] modules, reserved;   // reserved = function module, never masked

        public bool[,] Modules => modules;

        public Symbol(int version)
        {
            this.version = version;
            size = SizeOf(version);
            modules = new bool[size, size];
            reserved = new bool[size, size];
            DrawFunctionPatterns();
        }

        private void DrawFunctionPatterns()
        {
            // 6.3.5: timing patterns on row 6 and column 6, dark on even coordinates. Drawn first across
            // the whole width; finders and alignment patterns overwrite the ends.
            for (var i = 0; i < size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(3, size - 4);
            DrawFinder(size - 4, 3);

            var positions = AlignmentPositions(version);
            var last = positions.Length - 1;
            for (var r = 0; r < positions.Length; r++)
                for (var c = 0; c < positions.Length; c++)
                {
                    var corner = (r == 0 && c == 0) || (r == 0 && c == last) || (r == last && c == 0);
                    if (!corner) DrawAlignment(positions[r], positions[c]);
                }

            DrawFormat(0);   // reserves the format areas and the dark module; the real bits come with the mask
            DrawVersion();
        }

        // 6.3.3 and 6.3.4: 7x7 finder pattern (dark ring, light ring, dark 3x3 core) inside a light separator.
        private void DrawFinder(int centerRow, int centerCol)
        {
            for (var dr = -4; dr <= 4; dr++)
                for (var dc = -4; dc <= 4; dc++)
                {
                    int row = centerRow + dr, col = centerCol + dc;
                    if (row < 0 || row >= size || col < 0 || col >= size) continue;
                    var ring = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    Set(row, col, ring is not (2 or 4));
                }
        }

        // 6.3.6: 5x5 alignment pattern, dark ring, light ring, dark centre.
        private void DrawAlignment(int centerRow, int centerCol)
        {
            for (var dr = -2; dr <= 2; dr++)
                for (var dc = -2; dc <= 2; dc++)
                    Set(centerRow + dr, centerCol + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
        }

        // 7.9: 15 bits, both copies, plus the dark module at (4V + 9, 8) (7.9.1, Figure 25). Bit 14 is the
        // most significant; the top-left copy runs along row 8 then up column 8, the other copy is split
        // between the bottom of column 8 and the right end of row 8.
        private void DrawFormat(int mask)
        {
            var bits = FormatBits(mask);
            bool Bit(int i) => ((bits >> i) & 1) != 0;
            for (var i = 0; i <= 5; i++) Set(i, 8, Bit(i));
            Set(7, 8, Bit(6));
            Set(8, 8, Bit(7));
            Set(8, 7, Bit(8));
            for (var i = 9; i <= 14; i++) Set(8, 14 - i, Bit(i));

            for (var i = 0; i <= 7; i++) Set(8, size - 1 - i, Bit(i));
            for (var i = 8; i <= 14; i++) Set(size - 15 + i, 8, Bit(i));
            Set(size - 8, 8, true);
        }

        // 7.9.1, Annex C: level M is 00, then the 3-bit mask; BCH(15,5) with generator 0x537, XOR 0x5412.
        private static int FormatBits(int mask)
        {
            var data = 0b00 << 3 | mask;
            var remainder = data;
            for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            return (data << 10 | remainder) ^ 0x5412;
        }

        // 7.10, Annex D: versions 7+ carry the version as 6 bits plus a BCH(18,6) remainder (generator
        // 0x1F25), in a 6x3 block above the bottom-left finder and its transpose left of the top-right one.
        private void DrawVersion()
        {
            if (version < 7) return;
            var remainder = version;
            for (var i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            var bits = version << 12 | remainder;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, dark);
                Set(b, a, dark);
            }
        }

        private void Set(int row, int col, bool dark)
        {
            modules[row, col] = dark;
            reserved[row, col] = true;
        }

        // 7.7.3: codeword bits, most significant first, fill two-column strips from the bottom-right,
        // moving up then down in turn and skipping the vertical timing column. Leftover remainder bits stay light.
        public void PlaceCodewords(byte[] codewords)
        {
            var bit = 0;
            var upward = true;
            for (var right = size - 1; right >= 1; right -= 2, upward = !upward)
            {
                if (right == 6) right = 5;
                for (var step = 0; step < size; step++)
                {
                    var row = upward ? size - 1 - step : step;
                    for (var col = right; col >= right - 1; col--)
                    {
                        if (reserved[row, col] || bit >= codewords.Length * 8) continue;
                        modules[row, col] = ((codewords[bit / 8] >> (7 - bit % 8)) & 1) != 0;
                        bit++;
                    }
                }
            }
        }

        // 7.8.2: try every mask with its format information and keep the lowest penalty (first on a tie).
        public void ApplyBestMask()
        {
            var best = 0;
            var bestPenalty = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormat(mask);
                var penalty = Penalty.Score(modules);
                if (penalty < bestPenalty) (best, bestPenalty) = (mask, penalty);
                ApplyMask(mask);   // XOR again to undo
            }
            ApplyMask(best);
            DrawFormat(best);
        }

        // Table 10: data mask conditions, i = row, j = column. Function modules are never masked.
        private void ApplyMask(int mask)
        {
            for (var i = 0; i < size; i++)
                for (var j = 0; j < size; j++)
                    if (!reserved[i, j] && MaskCondition(mask, i, j)) modules[i, j] = !modules[i, j];
        }

        private static bool MaskCondition(int mask, int i, int j) => mask switch
        {
            0 => (i + j) % 2 == 0,
            1 => i % 2 == 0,
            2 => j % 3 == 0,
            3 => (i + j) % 3 == 0,
            4 => (i / 2 + j / 3) % 2 == 0,
            5 => i * j % 2 + i * j % 3 == 0,
            6 => (i * j % 2 + i * j % 3) % 2 == 0,
            _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
        };
    }

    // 7.8.3, Table 11: the four penalty rules for a masked symbol.
    private static class Penalty
    {
        private const int N1 = 3, N2 = 3, N3 = 40, N4 = 10;

        public static int Score(bool[,] modules)
        {
            var size = modules.GetLength(0);
            var score = 0;
            var line = new bool[size];
            for (var i = 0; i < size; i++)
            {
                for (var k = 0; k < size; k++) line[k] = modules[i, k];
                score += Runs(line) + FinderLike(line);
                for (var k = 0; k < size; k++) line[k] = modules[k, i];
                score += Runs(line) + FinderLike(line);
            }
            return score + Blocks(modules) + Balance(modules);
        }

        // Rule 1: each run of 5 + k same-coloured modules in a row or column costs N1 + k.
        private static int Runs(bool[] line)
        {
            var score = 0;
            var run = 1;
            for (var k = 1; k <= line.Length; k++)
            {
                if (k < line.Length && line[k] == line[k - 1]) { run++; continue; }
                if (run >= 5) score += N1 + run - 5;
                run = 1;
            }
            return score;
        }

        // Rule 2: each 2x2 block of one colour costs N2 (blocks may overlap).
        private static int Blocks(bool[,] modules)
        {
            var size = modules.GetLength(0);
            var score = 0;
            for (var r = 0; r + 1 < size; r++)
                for (var c = 0; c + 1 < size; c++)
                {
                    var color = modules[r, c];
                    if (modules[r, c + 1] == color && modules[r + 1, c] == color && modules[r + 1, c + 1] == color) score += N2;
                }
            return score;
        }

        // Rule 3: dark-light-dark-dark-dark-light-dark (1:1:3:1:1) with 4 light modules before or after it
        // costs N3 per occurrence. Outside the symbol counts as light (the quiet zone).
        private static readonly bool[] LightBefore = [false, false, false, false, true, false, true, true, true, false, true];
        private static readonly bool[] LightAfter = [true, false, true, true, true, false, true, false, false, false, false];

        private static int FinderLike(bool[] line)
        {
            var score = 0;
            for (var start = -4; start + LightBefore.Length <= line.Length + 4; start++)
            {
                if (Matches(line, start, LightBefore)) score += N3;
                if (Matches(line, start, LightAfter)) score += N3;
            }
            return score;
        }

        private static bool Matches(bool[] line, int start, bool[] pattern)
        {
            for (var k = 0; k < pattern.Length; k++)
            {
                var index = start + k;
                var dark = index >= 0 && index < line.Length && line[index];
                if (dark != pattern[k]) return false;
            }
            return true;
        }

        // Rule 4: N4 for every full 5 % step the dark share deviates from 50 %.
        private static int Balance(bool[,] modules)
        {
            var total = modules.Length;
            var dark = 0;
            foreach (var module in modules) if (module) dark++;
            return N4 * (Math.Abs(dark * 20 - total * 10) / total);
        }
    }
}
