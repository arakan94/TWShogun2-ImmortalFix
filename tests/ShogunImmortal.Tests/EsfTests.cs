using static ShogunImmortal.Tests.EsfBuilder;

namespace ShogunImmortal.Tests;

public class EsfTests
{
    [Fact]
    public void Unmodified_file_is_rewritten_byte_for_byte()
    {
        // Covers short and long record headers, compact ints, padded size/count varints, the footer and
        // the zero padding after it. `repair` relies on this before it edits anything.
        byte[] data = Campaign.Build([Campaign.Saphrax]);
        var f = EsfFile.Load(data);
        Assert.Equal(data, f.Save());
    }

    [Fact]
    public void Removing_an_item_recomputes_sizes_and_keeps_padded_widths()
    {
        var f = EsfFile.Load(Campaign.Build());
        var lindon = (RecordBlock)Characters.FindBlock(f, f.Root, "CHARACTER_ARRAY")!;
        lindon.Items.RemoveAt(1);   // Elrond

        var g = EsfFile.Load(f.Save());
        Assert.Equal(Campaign.AllDefault.Select(c => (long)c.Id).Where(id => id != 102),
            Characters.All(g).Select(c => Characters.Id(g, c.Character)));
        Assert.Equal(3, Campaign.FindRecord(g, g.Root, "WORLD").SizeWidth);
        var block = (RecordBlock)Characters.FindBlock(g, g.Root, "CHARACTER_ARRAY")!;
        Assert.Equal(2, block.CountWidth);
        Assert.All(block.Items, item => Assert.Equal(2, block.ItemSizeWidths[item]));
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 1)]
    [InlineData(16383u, 1)]
    [InlineData(16384u, 1)]
    [InlineData(5u, 3)]
    [InlineData(300u, 5)]
    [InlineData(0x0FFFFFFFu, 1)]
    public void Varints_match_the_reference_encoding(uint value, int width)
    {
        var ms = new MemoryStream();
        EsfFile.WriteUIntVar(ms, value, width);
        Assert.Equal(Varint(value, width), ms.ToArray());
        int p = 0;
        Assert.Equal(value, EsfFile.ReadUIntVar(ms.ToArray(), ref p));
        Assert.Equal((int)ms.Length, p);
    }

    [Fact]
    public void Compact_integer_encodings_are_decoded()
    {
        uint[] values = [0, 1, 200, 40_000, 3_000_000, 4_000_000_000];
        var b = new EsfBuilder();
        var f = EsfFile.Load(b.File("ROOT", 0, values.Select(U32Opt).ToArray()));
        Assert.Equal(values.Select(v => (long?)v), ((Record)f.Root).Children.Cast<Leaf>().Select(f.IntValue));
    }

    [Theory]
    [InlineData(new byte[] { 0x1A, 0x7F }, 127L)]
    [InlineData(new byte[] { 0x1A, 0xFF }, -1L)]
    [InlineData(new byte[] { 0x1A, 0x80 }, -128L)]
    [InlineData(new byte[] { 0x1B, 0x7F, 0xFF }, 32767L)]
    [InlineData(new byte[] { 0x1B, 0xFF, 0xFE }, -2L)]
    [InlineData(new byte[] { 0x1B, 0x80, 0x00 }, -32768L)]
    [InlineData(new byte[] { 0x1C, 0x7F, 0xFF, 0xFF }, 8388607L)]
    [InlineData(new byte[] { 0x1C, 0xFF, 0xFF, 0xFF }, -1L)]
    [InlineData(new byte[] { 0x1C, 0x80, 0x00, 0x00 }, -8388608L)]
    [InlineData(new byte[] { 0x19 }, 0L)]
    [InlineData(new byte[] { 0x04, 0xFB, 0xFF, 0xFF, 0xFF }, -5L)]
    [InlineData(new byte[] { 0x18, 0xFF, 0xFF, 0xFF }, 16777215L)]          // unsigned forms are not sign-extended
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF }, 4294967295L)]
    public void Signed_compact_integers_are_sign_extended(byte[] leaf, long expected)
    {
        var f = EsfFile.Load(new EsfBuilder().File("ROOT", 0, [leaf]));
        Assert.Equal(expected, f.IntValue((Leaf)((Record)f.Root).Children[0]));
    }

    /// Every integer array type with its extreme values. Shared with the encoder test in RepairTests.
    public static TheoryData<int, long[]> IntArrayCases => new()
    {
        { 0x56, [0, 1, 255] },
        { 0x57, [0, 256, 65535] },
        { 0x58, [0, 65536, 16777215] },
        { 0x5A, [0, 127, -1, -128] },
        { 0x5B, [0, 32767, -1, -32768] },
        { 0x5C, [0, 8388607, -1, -8388608] },
        { 0x44, [0, int.MaxValue, -1, int.MinValue] },
        { 0x48, [0, int.MaxValue, uint.MaxValue] },
    };

    [Theory]
    [MemberData(nameof(IntArrayCases))]
    public void Int_arrays_of_every_width_are_decoded(int type, long[] values)
    {
        var f = EsfFile.Load(new EsfBuilder().File("ROOT", 0, [IntArray((byte)type, values)]));
        Assert.Equal(values, Campaign.Elements(f, Campaign.Arrays((Record)f.Root)[0]));
    }

    [Fact]
    public void Int_arrays_are_decoded_in_both_byte_orders()
    {
        var b = new EsfBuilder();
        var f = EsfFile.Load(b.File("ROOT", 0, [U32Array(1, 70_000, 3), Packed2Array(1, 40_000, 3)]));
        var arrays = Campaign.Arrays((Record)f.Root);
        Assert.Equal([1L, 70_000, 3], Campaign.Elements(f, arrays[0]));   // little-endian u32
        Assert.Equal([1L, 40_000, 3], Campaign.Elements(f, arrays[1]));   // packed big-endian, 2 bytes
    }

    [Fact]
    public void Unknown_magic_is_rejected()
    {
        byte[] data = Campaign.Build();
        data[0] = 0xAB;
        Assert.Throws<InvalidDataException>(() => EsfFile.Load(data));
    }

    [Fact]
    public void Data_after_the_footer_is_rejected()
    {
        // Zero padding is normal; anything else means the file was misparsed.
        byte[] data = [.. Campaign.Build(), 0x01];
        Assert.Throws<InvalidDataException>(() => EsfFile.Load(data));
    }
}
