// Minimal reader/writer for Total War ESF files using the 0xABCA encoding
// (Shogun 2 / Fall of the Samurai saves and startpos files).
using System.Text;

namespace ShogunImmortal;

abstract class Node
{
    public int Start;   // offset of the type byte in the source buffer
    public int End;     // offset just past the node
}

/// Primitive value or primitive array; kept as a raw byte range of the source.
sealed class Leaf : Node
{
    public byte Type;
    public int DataStart;           // first byte after the type code (and after the size, for arrays)
    public byte[]? Replacement;     // if set, written instead of the original data (arrays only)
    public int SizeWidth;           // byte width of the original array size varint (arrays only)
}

// The game sometimes pads size varints with leading zero groups (e.g. 80 87 C1 85 10), so the original
// width of every size/count field is remembered and kept when re-serializing.

sealed class Record : Node
{
    public byte[] Header = [];      // type byte + name/version info, copied verbatim on write
    public ushort NameIdx;
    public byte Version;
    public List<Node> Children = new();
    public int SizeWidth;
}

sealed class RecordBlock : Node
{
    public byte[] Header = [];
    public ushort NameIdx;
    public byte Version;
    public List<List<Node>> Items = new();
    public int SizeWidth, CountWidth;
    public Dictionary<List<Node>, int> ItemSizeWidths = new(ReferenceEqualityComparer.Instance);
}

sealed class EsfFile
{
    public const uint MagicAbca = 0xABCA;
    public const uint MagicAbcf = 0xABCF;   // older layout with absolute u32 end offsets; read-only here

    public byte[] Data = [];
    public uint Magic;
    public uint Timestamp;
    public int FooterOffset;
    public List<string> Names = new();
    public Dictionary<uint, string> Utf16 = new();
    public Dictionary<uint, string> Ascii = new();
    public Node Root = null!;

    public string Name(ushort idx) => idx < Names.Count ? Names[idx] : $"#{idx}";
    public static string Name(EsfFile f, Node n) => n switch
    {
        Record r => f.Name(r.NameIdx),
        RecordBlock b => f.Name(b.NameIdx),
        _ => "",
    };

    // ---------------------------------------------------------------- reading

    public static EsfFile Load(byte[] data)
    {
        var f = new EsfFile { Data = data };
        f.Magic = BitConverter.ToUInt32(data, 0);
        if (f.Magic != MagicAbca && f.Magic != MagicAbcf)
            throw new InvalidDataException($"Unsupported ESF magic 0x{f.Magic:X} (only 0xABCA/0xABCF are supported)");
        f.Timestamp = BitConverter.ToUInt32(data, 8);
        f.FooterOffset = (int)BitConverter.ToUInt32(data, 12);
        f.ReadFooter();
        int p = 16;
        f.Root = f.ReadNode(ref p);
        if (p != f.FooterOffset)
            throw new InvalidDataException($"Root node ended at {p}, footer is at {f.FooterOffset}");
        return f;
    }

    void ReadFooter()
    {
        int p = FooterOffset;
        int nNames = BitConverter.ToUInt16(Data, p); p += 2;
        for (int i = 0; i < nNames; i++)
        {
            int len = BitConverter.ToUInt16(Data, p); p += 2;
            Names.Add(Encoding.ASCII.GetString(Data, p, len)); p += len;
        }
        uint nU = BitConverter.ToUInt32(Data, p); p += 4;
        for (uint i = 0; i < nU; i++)
        {
            int len = BitConverter.ToUInt16(Data, p); p += 2;
            string s = Encoding.Unicode.GetString(Data, p, len * 2); p += len * 2;
            Utf16[BitConverter.ToUInt32(Data, p)] = s; p += 4;
        }
        uint nA = BitConverter.ToUInt32(Data, p); p += 4;
        for (uint i = 0; i < nA; i++)
        {
            int len = BitConverter.ToUInt16(Data, p); p += 2;
            string s = Encoding.ASCII.GetString(Data, p, len); p += len;
            Ascii[BitConverter.ToUInt32(Data, p)] = s; p += 4;
        }
        // The game pads the end of the file with zero bytes; anything else means we misparsed.
        for (int i = p; i < Data.Length; i++)
            if (Data[i] != 0)
                throw new InvalidDataException($"Unexpected data after footer at {i}");
    }

    public static uint ReadUIntVar(byte[] d, ref int p)
    {
        uint v = 0;
        byte b;
        do { b = d[p++]; v = (v << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
        return v;
    }

    /// Writes a big-endian 7-bit varint, padded with leading zero groups to at least minWidth bytes.
    public static void WriteUIntVar(Stream s, uint v, int minWidth = 1)
    {
        Span<byte> tmp = stackalloc byte[8];
        int n = 0;
        do { tmp[n++] = (byte)(v & 0x7F); v >>= 7; } while (v != 0);
        while (n < minWidth && n < tmp.Length) tmp[n++] = 0;
        for (int i = n - 1; i >= 0; i--)
            s.WriteByte((byte)(tmp[i] | (i > 0 ? 0x80 : 0)));
    }

    /// Size in bytes of the payload of a scalar type code (excluding the code itself).
    public static int ScalarSize(byte t) => t switch
    {
        0x01 or 0x02 or 0x06 => 1,          // bool, int8, uint8
        0x03 or 0x07 or 0x10 => 2,          // int16, uint16, angle
        0x04 or 0x08 or 0x0A => 4,          // int32, uint32, float
        0x05 or 0x09 or 0x0B => 8,          // int64, uint64, double
        0x0C => 8,                          // coord2d
        0x0D => 12,                         // coord3d
        0x0E or 0x0F => 4,                  // utf16 / ascii string (index into footer table)
        0x12 or 0x13 => 0,                  // bool true / bool false
        0x14 or 0x15 or 0x19 or 0x1D => 0,  // uint32 0 / uint32 1 / int32 0 / float 0
        0x16 or 0x1A => 1,                  // uint32 / int32 stored in 1 byte
        0x17 or 0x1B => 2,                  // ... 2 bytes
        0x18 or 0x1C => 3,                  // ... 3 bytes
        _ => throw new InvalidDataException($"Unknown ESF type code 0x{t:X2}"),
    };

    Node ReadNode(ref int p) => Magic == MagicAbcf ? ReadNodeAbcf(ref p) : ReadNodeAbca(ref p);

    Node ReadNodeAbcf(ref int p)
    {
        int start = p;
        byte t = Data[p++];
        if (t is 0x80 or 0x81)
        {
            ushort name = BitConverter.ToUInt16(Data, p);
            byte ver = Data[p + 2];
            p += 3;
            int end = (int)BitConverter.ToUInt32(Data, p); p += 4;
            if (t == 0x80)
            {
                var r = new Record { Start = start, NameIdx = name, Version = ver };
                while (p < end) r.Children.Add(ReadNode(ref p));
                r.End = end;
                return r;
            }
            var b = new RecordBlock { Start = start, NameIdx = name, Version = ver };
            uint count = BitConverter.ToUInt32(Data, p); p += 4;
            for (uint i = 0; i < count; i++)
            {
                int iend = (int)BitConverter.ToUInt32(Data, p); p += 4;
                var item = new List<Node>();
                while (p < iend) item.Add(ReadNode(ref p));
                b.Items.Add(item);
            }
            if (p != end) throw new InvalidDataException($"ABCF block {Name(name)} ended at {p}, expected {end}");
            b.End = end;
            return b;
        }
        if (t >= 0x40)
        {
            int end = (int)BitConverter.ToUInt32(Data, p); p += 4;
            var leaf = new Leaf { Start = start, Type = t, DataStart = p, End = end };
            p = end;
            return leaf;
        }
        {
            var leaf = new Leaf { Start = start, Type = t, DataStart = p };
            p += ScalarSize(t);
            leaf.End = p;
            return leaf;
        }
    }

    Node ReadNodeAbca(ref int p)
    {
        int start = p;
        byte t = Data[p++];
        if ((t & 0x80) != 0)
        {
            ushort name; byte ver;
            // Long form (0x20 bit) - the root record always uses it: u16 name, u8 version.
            if ((t & 0x20) != 0 || start == 16)
            {
                name = BitConverter.ToUInt16(Data, p); p += 2;
                ver = Data[p++];
            }
            else
            {
                ver = (byte)((t & 0x1E) >> 1);
                name = (ushort)(((t & 1) << 8) | Data[p++]);
            }
            byte[] header = Data[start..p];
            int sizeAt = p;
            int size = (int)ReadUIntVar(Data, ref p);
            int sizeWidth = p - sizeAt;
            if ((t & 0x40) == 0)
            {
                int end = p + size;
                var r = new Record { Start = start, Header = header, NameIdx = name, Version = ver, SizeWidth = sizeWidth };
                while (p < end) r.Children.Add(ReadNode(ref p));
                if (p != end) throw new InvalidDataException($"Record {Name(name)} overran at {p}");
                r.End = end;
                return r;
            }
            else
            {
                var b = new RecordBlock { Start = start, Header = header, NameIdx = name, Version = ver, SizeWidth = sizeWidth };
                // Block size counts the bytes after the item count varint.
                int countAt = p;
                uint count = ReadUIntVar(Data, ref p);
                b.CountWidth = p - countAt;
                int end = p + size;
                for (uint i = 0; i < count; i++)
                {
                    int iszAt = p;
                    int isz = (int)ReadUIntVar(Data, ref p);
                    int iend = p + isz;
                    var item = new List<Node>();
                    b.ItemSizeWidths[item] = p - iszAt;
                    while (p < iend) item.Add(ReadNode(ref p));
                    if (p != iend) throw new InvalidDataException($"Block item of {Name(name)} overran at {p}");
                    b.Items.Add(item);
                }
                if (p != end) throw new InvalidDataException($"Record block {Name(name)} size mismatch at {p} (expected {end})");
                b.End = end;
                return b;
            }
        }
        if ((t & 0x40) != 0)
        {
            int sizeAt = p;
            int size = (int)ReadUIntVar(Data, ref p);
            var leaf = new Leaf { Start = start, Type = t, DataStart = p, SizeWidth = p - sizeAt };
            p += size;
            leaf.End = p;
            return leaf;
        }
        {
            var leaf = new Leaf { Start = start, Type = t, DataStart = p };
            p += ScalarSize(t);
            leaf.End = p;
            return leaf;
        }
    }

    // ---------------------------------------------------------------- writing

    /// Re-serializes the tree (recomputing all sizes) followed by the original footer.
    public byte[] Save()
    {
        var ms = new MemoryStream(Data.Length + 1024);
        ms.Write(Data, 0, 16);
        WriteNode(ms, Root);
        int footer = (int)ms.Position;
        ms.Write(Data, FooterOffset, Data.Length - FooterOffset);
        byte[] result = ms.ToArray();
        BitConverter.GetBytes((uint)footer).CopyTo(result, 12);
        return result;
    }

    void WriteNode(Stream s, Node n)
    {
        switch (n)
        {
            case Leaf l when l.Replacement != null:
                s.WriteByte(l.Type);
                WriteUIntVar(s, (uint)l.Replacement.Length, Math.Max(1, l.SizeWidth));
                s.Write(l.Replacement);
                break;
            case Leaf l:
                s.Write(Data, l.Start, l.End - l.Start);
                break;
            case Record r:
            {
                var body = new MemoryStream();
                foreach (var c in r.Children) WriteNode(body, c);
                s.Write(r.Header);
                WriteUIntVar(s, (uint)body.Length, r.SizeWidth);
                body.Position = 0; body.CopyTo(s);
                break;
            }
            case RecordBlock b:
            {
                var body = new MemoryStream();
                foreach (var item in b.Items)
                {
                    var ib = new MemoryStream();
                    foreach (var c in item) WriteNode(ib, c);
                    WriteUIntVar(body, (uint)ib.Length, b.ItemSizeWidths.GetValueOrDefault(item, 1));
                    ib.Position = 0; ib.CopyTo(body);
                }
                s.Write(b.Header);
                WriteUIntVar(s, (uint)body.Length, b.SizeWidth);
                WriteUIntVar(s, (uint)b.Items.Count, b.CountWidth);
                body.Position = 0; body.CopyTo(s);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    public string Describe(Leaf l)
    {
        var d = Data; int p = l.DataStart;
        return l.Type switch
        {
            0x01 => $"bool {d[p] != 0}",
            0x12 => "bool True",
            0x13 => "bool False",
            0x02 => $"i8 {(sbyte)d[p]}",
            0x03 => $"i16 {BitConverter.ToInt16(d, p)}",
            0x04 => $"i32 {BitConverter.ToInt32(d, p)}",
            0x05 => $"i64 {BitConverter.ToInt64(d, p)}",
            0x06 => $"u8 {d[p]}",
            0x07 => $"u16 {BitConverter.ToUInt16(d, p)}",
            0x08 => $"u32 {BitConverter.ToUInt32(d, p)}",
            0x09 => $"u64 {BitConverter.ToUInt64(d, p)}",
            0x0A => $"f32 {BitConverter.ToSingle(d, p)}",
            0x0B => $"f64 {BitConverter.ToDouble(d, p)}",
            0x0C => $"xy ({BitConverter.ToSingle(d, p)}, {BitConverter.ToSingle(d, p + 4)})",
            0x0D => $"xyz ({BitConverter.ToSingle(d, p)}, {BitConverter.ToSingle(d, p + 4)}, {BitConverter.ToSingle(d, p + 8)})",
            0x0E => $"utf16 \"{Utf16.GetValueOrDefault(BitConverter.ToUInt32(d, p), "?")}\"",
            0x0F => $"ascii \"{Ascii.GetValueOrDefault(BitConverter.ToUInt32(d, p), "?")}\"",
            0x10 => $"angle {BitConverter.ToUInt16(d, p)}",
            0x14 => "u32 0",
            0x15 => "u32 1",
            0x16 => $"u32 {d[p]}",
            0x17 => $"u32 {d[p] << 8 | d[p + 1]}",
            0x18 => $"u32 {d[p] << 16 | d[p + 1] << 8 | d[p + 2]}",
            0x19 => "i32 0",
            0x1A => $"i32 {(sbyte)d[p]}",
            0x1B => $"i32 {(short)(d[p] << 8 | d[p + 1])}",
            0x1C => $"i32 {((d[p] << 16 | d[p + 1] << 8 | d[p + 2]) << 8) >> 8}",
            0x1D => "f32 0",
            _ when (l.Type & 0x40) != 0 && l.End - l.DataStart is > 0 and <= 16 =>
                $"array<0x{l.Type & 0x3F:X2}> {l.End - l.DataStart} bytes [{Convert.ToHexString(d, p, l.End - p)}]",
            _ when (l.Type & 0x40) != 0 => $"array<0x{l.Type & 0x3F:X2}> {l.End - l.DataStart} bytes",
            _ => $"0x{l.Type:X2}",
        };
    }

    /// Integer value of a scalar int/uint leaf, or null for other types.
    public long? IntValue(Leaf l)
    {
        var d = Data; int p = l.DataStart;
        return l.Type switch
        {
            0x04 => BitConverter.ToInt32(d, p),
            0x08 => BitConverter.ToUInt32(d, p),
            0x14 or 0x19 => 0,
            0x15 => 1,
            0x16 => d[p],
            0x17 => d[p] << 8 | d[p + 1],
            0x18 => d[p] << 16 | d[p + 1] << 8 | d[p + 2],
            0x1A => (sbyte)d[p],
            0x1B => (short)(d[p] << 8 | d[p + 1]),
            0x1C => ((d[p] << 16 | d[p + 1] << 8 | d[p + 2]) << 8) >> 8,
            _ => null,
        };
    }

    /// Element width of an integer array type: plain little-endian i32/u32 arrays (0x44/0x48) and the
    /// packed big-endian u32/i32 arrays (0x56-0x58 / 0x5A-0x5C, 1-3 bytes per element).
    public static int? ArrayElementWidth(byte t) => t switch
    {
        0x44 or 0x48 => 4,
        0x56 or 0x5A => 1,
        0x57 or 0x5B => 2,
        0x58 or 0x5C => 3,
        _ => null,
    };

    public long ArrayElement(Leaf l, int p)
    {
        var d = Data;
        return l.Type switch
        {
            0x44 => BitConverter.ToInt32(d, p),
            0x48 => BitConverter.ToUInt32(d, p),
            0x56 => d[p],
            0x57 => d[p] << 8 | d[p + 1],
            0x58 => d[p] << 16 | d[p + 1] << 8 | d[p + 2],
            0x5A => (sbyte)d[p],
            0x5B => (short)(d[p] << 8 | d[p + 1]),
            0x5C => ((d[p] << 16 | d[p + 1] << 8 | d[p + 2]) << 8) >> 8,
            _ => throw new InvalidOperationException("not an int array"),
        };
    }

    /// Calls visit(leaf, path, arrayIndex) for every int leaf, and for every element of
    /// 4-byte int/uint arrays (arrayIndex >= 0). The path is built lazily only when asked for.
    public void WalkInts(Action<Leaf, Func<string>, int, long> visit)
    {
        var stack = new List<string>();
        string Path() => string.Join(" > ", stack);
        void Walk(Node n)
        {
            switch (n)
            {
                case Leaf l:
                    if (IntValue(l) is long v) visit(l, Path, -1, v);
                    else if (ArrayElementWidth(l.Type) is int w)
                        for (int p = l.DataStart, i = 0; p + w <= l.End; p += w, i++)
                            visit(l, Path, i, ArrayElement(l, p));
                    break;
                case Record r:
                    stack.Add(Name(r.NameIdx));
                    foreach (var c in r.Children) Walk(c);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case RecordBlock b:
                    for (int i = 0; i < b.Items.Count; i++)
                    {
                        stack.Add($"{Name(b.NameIdx)}[{i}]");
                        foreach (var c in b.Items[i]) Walk(c);
                        stack.RemoveAt(stack.Count - 1);
                    }
                    break;
            }
        }
        Walk(Root);
    }

    public void Dump(TextWriter w, Node n, int depth = 0, int maxDepth = int.MaxValue)
    {
        string ind = new(' ', depth * 2);
        switch (n)
        {
            case Leaf l:
                w.WriteLine($"{ind}{Describe(l)}  @{l.Start}");
                break;
            case Record r:
                w.WriteLine($"{ind}<{Name(r.NameIdx)} v{r.Version}>  @{r.Start}");
                if (depth < maxDepth) foreach (var c in r.Children) Dump(w, c, depth + 1, maxDepth);
                break;
            case RecordBlock b:
                w.WriteLine($"{ind}<{Name(b.NameIdx)}[] v{b.Version} count={b.Items.Count}>  @{b.Start}");
                if (depth < maxDepth)
                    for (int i = 0; i < b.Items.Count; i++)
                    {
                        w.WriteLine($"{ind}  [{i}]");
                        foreach (var c in b.Items[i]) Dump(w, c, depth + 2, maxDepth);
                    }
                break;
        }
    }
}
