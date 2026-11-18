using System.Text;

namespace Escena.Remote;

/// <summary>
/// Generador de codis QR (mode byte, correcció d'errors M, versions 1–10: fins a 213 bytes), prou per a una adreça web.
/// Segueix l'estàndard ISO/IEC 18004 (mateix procediment que el generador de referència de Project Nayuki).
/// </summary>
public sealed class QrCode
{
    public int Size { get; }
    private readonly bool[,] _modules;   // [y, x]
    private readonly bool[,] _function;  // mòduls fixos (no es fan servir per a dades ni s'emmascaren)

    public bool this[int x, int y] => _modules[y, x];

    // Nivell M, versions 1–10: codewords de correcció per bloc, i blocs (curts, llargs) amb les dades de cada un.
    private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    private static readonly int[] NumBlocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
    private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
    private static readonly int[][] AlignmentPositions =
    {
        Array.Empty<int>(), Array.Empty<int>(),
        new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    private static int DataCodewords(int version) => TotalCodewords[version] - EccPerBlock[version] * NumBlocks[version];

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        int version = 1;
        while (true)
        {
            int countBits = version <= 9 ? 8 : 16;
            int needed = 4 + countBits + data.Length * 8;
            if (needed <= DataCodewords(version) * 8) break;
            if (++version > 10) throw new ArgumentException("Text massa llarg per al codi QR.");
        }

        // Bits: mode byte (0100), comptador, dades, terminador i farciment.
        var bits = new List<bool>();
        void Append(int value, int length) { for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (byte b in data) Append(b, 8);
        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        return new QrCode(version, AddEccAndInterleave(codewords, version));
    }

    private QrCode(int version, byte[] allCodewords)
    {
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns(version);
        DrawCodewords(allCodewords);

        // Tria la màscara amb menys penalització.
        int best = 0;
        long bestPenalty = long.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            long p = Penalty();
            if (p < bestPenalty) { best = mask; bestPenalty = p; }
            ApplyMask(mask); // la treu (XOR)
        }
        ApplyMask(best);
        DrawFormatBits(best);
    }

    // ---------------- Correcció d'errors (Reed-Solomon sobre GF(256)) ----------------

    private static byte[] AddEccAndInterleave(byte[] data, int version)
    {
        int numBlocks = NumBlocks[version], eccLen = EccPerBlock[version];
        int raw = TotalCodewords[version];
        int numShort = numBlocks - raw % numBlocks;
        int shortLen = raw / numBlocks; // inclou la correcció
        var divisor = RsDivisor(eccLen);
        var blocks = new List<(byte[] Data, byte[] Ecc)>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int len = shortLen - eccLen + (i < numShort ? 0 : 1);
            var d = data.AsSpan(k, len).ToArray();
            k += len;
            blocks.Add((d, RsRemainder(d, divisor)));
        }
        var result = new List<byte>(raw);
        int maxData = blocks.Max(b => b.Data.Length);
        for (int i = 0; i < maxData; i++)
            foreach (var b in blocks) if (i < b.Data.Length) result.Add(b.Data[i]);
        for (int i = 0; i < eccLen; i++)
            foreach (var b in blocks) result.Add(b.Ecc[i]);
        return result.ToArray();
    }

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= GfMul(divisor[i], factor);
        }
        return result;
    }

    private static byte GfMul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    // ---------------- Dibuix ----------------

    private void Set(int x, int y, bool dark, bool function = true)
    {
        _modules[y, x] = dark;
        if (function) _function[y, x] = true;
    }

    private void DrawFunctionPatterns(int version)
    {
        for (int i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var pos = AlignmentPositions[version];
        foreach (int ax in pos)
            foreach (int ay in pos)
            {
                bool nearFinder = (ax == 6 && ay == 6) || (ax == 6 && ay == pos[^1]) || (ax == pos[^1] && ay == 6);
                if (!nearFinder) DrawAlignment(ax, ay);
            }

        DrawFormatBits(0); // reserva les zones (es tornen a escriure amb la màscara triada)
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = Size - 11 + i % 3, b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) Set(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    /// <summary>Informació de format (nivell M = 00 i màscara), amb BCH(15,5), a les dues còpies.</summary>
    private void DrawFormatBits(int mask)
    {
        int data = 0b00 << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

        for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
        Set(8, Size - 8, true); // mòdul fosc
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_function[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _modules[y, x] = !_modules[y, x];
            }
    }

    /// <summary>Penalització simplificada (tirades llargues, blocs 2×2 i equilibri de foscos); només serveix per triar màscara.</summary>
    private long Penalty()
    {
        long p = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < Size; a++)
            {
                int run = 1;
                for (int b = 1; b < Size; b++)
                {
                    bool cur = pass == 0 ? _modules[a, b] : _modules[b, a];
                    bool prev = pass == 0 ? _modules[a, b - 1] : _modules[b - 1, a];
                    if (cur == prev) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
        int dark = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_modules[y, x]) dark++;
                if (x < Size - 1 && y < Size - 1)
                {
                    bool c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) p += 3;
                }
            }
        int total = Size * Size;
        p += (Math.Abs(dark * 20 - total * 10) + total - 1) / total * 10;
        return p;
    }

    /// <summary>Imatge del codi (negre sobre blanc, amb marge de 4 mòduls) per posar a la interfície.</summary>
    public System.Windows.Media.Imaging.BitmapSource ToBitmap(int scale = 8)
    {
        int border = 4, dim = (Size + border * 2) * scale;
        var pixels = new byte[dim * dim];
        for (int py = 0; py < dim; py++)
            for (int px = 0; px < dim; px++)
            {
                int x = px / scale - border, y = py / scale - border;
                bool dark = x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];
                pixels[py * dim + px] = dark ? (byte)0 : (byte)255;
            }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(dim, dim, 96, 96,
            System.Windows.Media.PixelFormats.Gray8, null, pixels, dim);
        bmp.Freeze();
        return bmp;
    }
}
