using SevenZip;
using LzmaDecoder = SevenZip.Compression.LZMA.Decoder;
using LzmaEncoder = SevenZip.Compression.LZMA.Encoder;

namespace ShogunImmortal;

static class Lzma
{
    public static byte[] Decompress(ReadOnlySpan<byte> data, byte[] props, long outSize)
    {
        var dec = new LzmaDecoder();
        dec.SetDecoderProperties(props);
        using var input = new MemoryStream(data.ToArray());
        using var output = new MemoryStream((int)outSize);
        dec.Code(input, output, data.Length, outSize, null);
        if (output.Length != outSize)
            throw new InvalidDataException($"LZMA produced {output.Length} bytes, expected {outSize}");
        return output.ToArray();
    }

    /// Compresses using the same lc/lp/pb/dictionary settings as the given 5-byte header.
    public static byte[] Compress(byte[] data, byte[] props)
    {
        int b = props[0];
        int lc = b % 9, lp = b / 9 % 5, pb = b / 45;
        int dict = BitConverter.ToInt32(props, 1);
        var enc = new LzmaEncoder();
        enc.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits,
             CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes,
             CoderPropID.MatchFinder, CoderPropID.EndMarker],
            [dict, pb, lc, lp, 2, 64, "bt4", false]);
        using var input = new MemoryStream(data);
        using var output = new MemoryStream(data.Length / 3);
        enc.Code(input, output, data.Length, -1, null);
        return output.ToArray();
    }
}
