namespace ShogunImmortal;

/// A Shogun 2 .save: an outer ESF whose COMPRESSED_DATA record holds an LZMA-packed inner ESF
/// with the actual campaign state.
sealed class SaveGame
{
    public EsfFile Outer = null!;
    public EsfFile Inner = null!;
    Leaf dataLeaf = null!;
    Leaf sizeLeaf = null!;
    byte[] props = [];

    public static SaveGame Open(EsfFile outer)
    {
        var root = (Record)outer.Root;
        var cd = root.Children.OfType<Record>().FirstOrDefault(r => outer.Name(r.NameIdx) == "COMPRESSED_DATA")
                 ?? throw new InvalidDataException("No COMPRESSED_DATA record - is this a Shogun 2 save?");
        var data = (Leaf)cd.Children[0];
        var info = cd.Children.OfType<Record>().First(r => outer.Name(r.NameIdx) == "COMPRESSED_DATA_INFO");
        var sizeLeaf = (Leaf)info.Children[0];
        var propLeaf = (Leaf)info.Children[1];
        long size = outer.IntValue(sizeLeaf) ?? throw new InvalidDataException($"Unexpected size type 0x{sizeLeaf.Type:X2}");
        byte[] props = outer.Data[propLeaf.DataStart..propLeaf.End];
        byte[] inner = Lzma.Decompress(outer.Data.AsSpan(data.DataStart, data.End - data.DataStart), props, size);
        return new SaveGame { Outer = outer, Inner = EsfFile.Load(inner), dataLeaf = data, sizeLeaf = sizeLeaf, props = props };
    }

    /// Re-packs the campaign data and returns the new file bytes.
    /// restructured = false: Inner.Data was only patched in place (same size).
    /// restructured = true:  nodes were added/removed/replaced, so the inner tree is re-serialized
    ///                       and the stored uncompressed size is updated.
    public byte[] Save(bool restructured = false)
    {
        byte[] inner = restructured ? Inner.Save() : Inner.Data;
        WriteSize(inner.Length);
        dataLeaf.Replacement = Lzma.Compress(inner, props);
        return Outer.Save();
    }

    void WriteSize(int size)
    {
        var d = Outer.Data; int p = sizeLeaf.DataStart;
        switch (sizeLeaf.Type)
        {
            case 0x08: BitConverter.GetBytes((uint)size).CopyTo(d, p); break;
            case 0x18 when size < 1 << 24: d[p] = (byte)(size >> 16); d[p + 1] = (byte)(size >> 8); d[p + 2] = (byte)size; break;
            case 0x17 when size < 1 << 16: d[p] = (byte)(size >> 8); d[p + 1] = (byte)size; break;
            default: throw new InvalidDataException($"Cannot store size {size} in a type 0x{sizeLeaf.Type:X2} field");
        }
    }
}
