using System.Text;

namespace ShogunImmortal.Tests;

/// Encodes small 0xABCA ESF files byte by byte. It is written independently of Esf.cs, so tests compare
/// the tool's reader and writer against a second implementation rather than against themselves.
/// Every method returns the complete encoding of one node; records and blocks take their children's bytes.
sealed class EsfBuilder
{
    readonly List<string> names = new();
    readonly List<string> strings = new();

    ushort NameIdx(string name)
    {
        int i = names.IndexOf(name);
        if (i < 0) { names.Add(name); i = names.Count - 1; }
        return (ushort)i;
    }

    // ------------------------------------------------------------ records

    /// A record. Versions 0-15 use the short header, higher versions the long one (as CHARACTER v23 does).
    /// sizeWidth > 1 pads the size varint with leading zero groups, as the game sometimes does.
    public byte[] Rec(string name, int version, params byte[][] children) => Rec(name, version, 1, children);

    public byte[] Rec(string name, int version, int sizeWidth, params byte[][] children)
    {
        byte[] body = Concat(children);
        return Concat(Header(name, version, block: false), Varint((uint)body.Length, sizeWidth), body);
    }

    /// A record block; each item is the concatenated bytes of its children (see Item).
    public byte[] Block(string name, int version, params byte[][] items) => Block(name, version, 1, 1, 1, items);

    public byte[] Block(string name, int version, int sizeWidth, int countWidth, int itemSizeWidth, params byte[][] items)
    {
        // The block size counts the bytes after the item count.
        byte[] body = Concat(items.Select(i => Concat(Varint((uint)i.Length, itemSizeWidth), i)).ToArray());
        return Concat(Header(name, version, block: true), Varint((uint)body.Length, sizeWidth),
            Varint((uint)items.Length, countWidth), body);
    }

    public static byte[] Item(params byte[][] children) => Concat(children);

    byte[] Header(string name, int version, bool block)
    {
        ushort idx = NameIdx(name);
        if (version < 16 && idx < 512)
            return [(byte)(0x80 | (block ? 0x40 : 0) | (version << 1) | (idx >> 8)), (byte)idx];
        return [(byte)(block ? 0xE0 : 0xA0), (byte)idx, (byte)(idx >> 8), (byte)version];
    }

    // ------------------------------------------------------------ leaves

    public static byte[] Bool(bool v) => [(byte)(v ? 0x12 : 0x13)];
    public static byte[] BoolByte(bool v) => [0x01, (byte)(v ? 1 : 0)];
    public static byte[] U32(uint v) => Concat([0x08], BitConverter.GetBytes(v));

    /// The game's compact encodings of a u32 (0x14-0x18).
    public static byte[] U32Opt(uint v) => v switch
    {
        0 => [0x14],
        1 => [0x15],
        < 1 << 8 => [0x16, (byte)v],
        < 1 << 16 => [0x17, (byte)(v >> 8), (byte)v],
        < 1 << 24 => [0x18, (byte)(v >> 16), (byte)(v >> 8), (byte)v],
        _ => U32(v),
    };

    public byte[] Utf16(string s)
    {
        int i = strings.IndexOf(s);
        if (i < 0) { strings.Add(s); i = strings.Count - 1; }
        return Concat([0x0E], BitConverter.GetBytes((uint)i));
    }

    /// Little-endian u32 array (0x48).
    public static byte[] U32Array(params uint[] values) =>
        Array(0x48, values.SelectMany(BitConverter.GetBytes).ToArray());

    /// Packed big-endian u32 array with 2 bytes per element (0x57).
    public static byte[] Packed2Array(params uint[] values) =>
        Array(0x57, values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray());

    public static byte[] U8Array(byte[] data) => Array(0x46, data);

    /// Any integer array type: little-endian i32/u32 (0x44/0x48), or packed big-endian with 1-3 bytes
    /// per element (0x56-0x58 unsigned, 0x5A-0x5C signed, two's complement).
    public static byte[] IntArray(byte type, params long[] values)
    {
        int width = type switch
        {
            0x44 or 0x48 => 4,
            0x56 or 0x5A => 1, 0x57 or 0x5B => 2, 0x58 or 0x5C => 3,
            _ => throw new ArgumentException($"0x{type:X2} is not an int array type"),
        };
        var data = new List<byte>();
        foreach (long v in values)
        {
            if (width == 4) data.AddRange(BitConverter.GetBytes((uint)v));
            else for (int shift = (width - 1) * 8; shift >= 0; shift -= 8) data.Add((byte)(v >> shift));
        }
        return Array(type, data.ToArray());
    }

    static byte[] Array(byte type, byte[] data, int sizeWidth = 1) =>
        Concat([type], Varint((uint)data.Length, sizeWidth), data);

    // ------------------------------------------------------------ file

    /// A complete file: header, root record (always the long header form), footer, then zero padding.
    public byte[] File(string rootName, int rootVersion, byte[][] children, int padding = 0)
    {
        byte[] body = Concat(children);
        ushort idx = NameIdx(rootName);
        byte[] root = Concat([0x80, (byte)idx, (byte)(idx >> 8), (byte)rootVersion], Varint((uint)body.Length, 1), body);

        var footer = new MemoryStream();
        var w = new BinaryWriter(footer);
        w.Write((ushort)names.Count);
        foreach (var n in names) { w.Write((ushort)n.Length); w.Write(Encoding.ASCII.GetBytes(n)); }
        w.Write((uint)strings.Count);
        for (int i = 0; i < strings.Count; i++)
        {
            w.Write((ushort)strings[i].Length); w.Write(Encoding.Unicode.GetBytes(strings[i])); w.Write((uint)i);
        }
        w.Write(0u);    // no ascii strings
        w.Flush();

        var header = new byte[16];
        BitConverter.GetBytes(EsfFile.MagicAbca).CopyTo(header, 0);
        BitConverter.GetBytes(0x5EED5EEDu).CopyTo(header, 8);
        BitConverter.GetBytes((uint)(16 + root.Length)).CopyTo(header, 12);
        return Concat(header, root, footer.ToArray(), new byte[padding]);
    }

    // ------------------------------------------------------------ helpers

    /// Big-endian 7-bit varint, padded with leading zero groups to `width` bytes.
    public static byte[] Varint(uint v, int width = 1)
    {
        var groups = new List<byte>();
        do { groups.Insert(0, (byte)(v & 0x7F)); v >>= 7; } while (v != 0);
        while (groups.Count < width) groups.Insert(0, 0);
        for (int i = 0; i < groups.Count - 1; i++) groups[i] |= 0x80;
        return groups.ToArray();
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
