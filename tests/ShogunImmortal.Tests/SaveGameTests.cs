namespace ShogunImmortal.Tests;

public class SaveGameTests
{
    [Fact]
    public void Campaign_data_is_decompressed()
    {
        byte[] inner = Campaign.Build();
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(inner)));
        Assert.Equal(inner, save.Inner.Data);
    }

    [Fact]
    public void Unmodified_save_reopens_with_the_same_campaign_data()
    {
        byte[] inner = Campaign.Build();
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(inner)));
        Assert.Equal(inner, SaveGame.Open(EsfFile.Load(save.Save())).Inner.Data);
    }

    [Fact]
    public void Flag_edits_survive_recompression()
    {
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(Campaign.Build())));
        var f = save.Inner;
        foreach (var (_, ch) in Characters.All(f))
            Characters.SetBool(f, Characters.ImmortalFlag(f, ch).Flag, false);

        var reopened = SaveGame.Open(EsfFile.Load(save.Save())).Inner;
        Assert.Equal(f.Data, reopened.Data);
        Assert.All(Campaign.Flags(reopened).Values, Assert.False);
    }

    [Fact]
    public void Restructured_save_stores_the_new_uncompressed_size()
    {
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(Campaign.Build())));
        var f = save.Inner;
        ((RecordBlock)Characters.FindBlock(f, f.Root, "CHARACTER_ARRAY")!).Items.RemoveAt(0);
        byte[] expected = f.Save();
        Assert.True(expected.Length < f.Data.Length);

        // Decompression checks the stored size, so a stale size would fail here.
        var reopened = SaveGame.Open(EsfFile.Load(save.Save(restructured: true))).Inner;
        Assert.Equal(expected, reopened.Data);
        Assert.Equal(5, Characters.All(reopened).Count());
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x17)]
    [InlineData(0x18)]
    public void Stored_size_keeps_its_encoding_after_a_restructuring_edit(int sizeType)
    {
        byte[] inner = Campaign.Build();
        Assert.True(inner.Length < 1 << 16);    // fits every size type tested here
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(inner, (byte)sizeType)));
        var f = save.Inner;
        ((RecordBlock)Characters.FindBlock(f, f.Root, "CHARACTER_ARRAY")!).Items.RemoveAt(0);
        byte[] expected = f.Save();

        var outer = EsfFile.Load(save.Save(restructured: true));
        var size = (Leaf)Campaign.FindRecord(outer, outer.Root, "COMPRESSED_DATA_INFO").Children[0];
        Assert.Equal(sizeType, size.Type);
        Assert.Equal(expected.Length, outer.IntValue(size)!.Value);
        Assert.Equal(expected, SaveGame.Open(outer).Inner.Data);
    }

    [Fact]
    public void A_size_too_big_for_its_field_is_refused()
    {
        var save = SaveGame.Open(EsfFile.Load(Campaign.Save(Campaign.Build(), sizeType: 0x17)));
        var f = save.Inner;
        // Grow the campaign data past 65535 bytes, the most a 2-byte size can hold.
        Campaign.Arrays(Campaign.FindRecord(f, f.Root, "CAI_FACTION"))[0].Replacement = new byte[70_000];
        var e = Assert.Throws<InvalidDataException>(() => save.Save(restructured: true));
        Assert.Contains("Cannot store size", e.Message);
    }

    [Fact]
    public void A_file_without_compressed_data_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SaveGame.Open(EsfFile.Load(Campaign.Build())));
    }
}
