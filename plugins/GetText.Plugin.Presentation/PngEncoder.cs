using System.Buffers.Binary;
using System.IO.Compression;

namespace GetText.Plugins.Presentation;

/// <summary>BGRA (上から) を PNG (RGB 8 ビット) にする。外部の部品を使わない (Windows・Mac で同じ)。</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Crc(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFFu)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("画像の大きさが合いません");
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // ビットの深さ
        header[9] = 2; // RGB
        WriteChunk(output, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                var previous = new byte[width * 3];
                var current = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    int src = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        current[x * 3] = bgra[src + x * 4 + 2];
                        current[x * 3 + 1] = bgra[src + x * 4 + 1];
                        current[x * 3 + 2] = bgra[src + x * 4];
                    }
                    // 上の行との差 (Up)。画面の画像は上下に同じ色が続くことが多く、よく縮む
                    row[0] = 2;
                    for (int i = 0; i < current.Length; i++) row[1 + i] = (byte)(current[i] - previous[i]);
                    z.Write(row);
                    (previous, current) = (current, previous);
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        output.Write(four);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc(data, Crc(typeBytes)) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        output.Write(four);
    }
}
