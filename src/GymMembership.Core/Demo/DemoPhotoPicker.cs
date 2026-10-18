using System.IO.Compression;
using System.Text;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

/// <summary>Demo mode has no photo library to open, so "choose a photo" hands back a generated picture.</summary>
public sealed class DemoPhotoPicker : IProofPicker
{
    public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(("demo-photo.png", DemoPng.Make()));
}

/// <summary>A small colour-gradient PNG written by hand: signature, header, one compressed data chunk, end.</summary>
public static class DemoPng
{
    public static byte[] Make(int width = 320, int height = 200)
    {
        var raw = new byte[(width * 3 + 1) * height];   // each row starts with a filter byte (0 = none), then red, green, blue per pixel
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * (width * 3 + 1) + 1 + x * 3;
                raw[i] = (byte)(40 + 200 * x / width); raw[i + 1] = (byte)(90 + 120 * y / height); raw[i + 2] = 160;
            }
        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [.. Be(width), .. Be(height), 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", zipped.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(Be(data.Length)); s.Write(t); s.Write(data); s.Write(Be((int)Crc(t, data)));
    }

    static byte[] Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    static uint Crc(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a.Concat(b)) { c ^= x; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return ~c;
    }
}
