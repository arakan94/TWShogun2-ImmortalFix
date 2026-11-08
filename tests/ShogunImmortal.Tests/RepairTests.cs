using static ShogunImmortal.Tests.Campaign;

namespace ShogunImmortal.Tests;

public class RepairTests
{
    static readonly long[] LiveComponents = AllDefault.Select(c => (long)c.Component).ToArray();

    [Fact]
    public void Orphan_with_zeroed_handles_is_removed_with_every_reference_to_it()
    {
        var f = EsfFile.Load(Build([Saphrax]));
        var res = Repair.RemoveOrphanAiCharacters(f);
        Assert.Equal([1199L], res.Components);
        Assert.Equal([199L], res.CharacterIds);

        var g = EsfFile.Load(f.Save());
        Assert.Equal(AllDefault.Count(), Characters.FindBlock(g, g.Root, "CAI_WORLD_CHARACTERS")!.Items.Count);
        Assert.Equal(AllDefault.Count(), Characters.FindBlock(g, g.Root, "CAI_BDI_COMPONENT_BLOCK_OWNS")!.Items.Count);

        // Lists that also hold live ids keep them, in order and in their original encoding.
        Assert.Equal(LiveComponents, Elements(g, Arrays(FindRecord(g, g.Root, "CAI_FACTION"))[0]));
        Assert.Equal([LiveComponents[1]], Elements(g, Arrays(FindRecord(g, g.Root, "CAI_BDI_COMPONENT"))[0]));
        var analyser = Arrays(FindRecord(g, g.Root, "CAI_ANALYSER"));
        Assert.All(analyser, a => Assert.Equal(0x57, a.Type));
        Assert.Equal(LiveComponents, Elements(g, analyser[0]));
        Assert.Equal(LiveComponents.SelectMany((c, i) => new[] { 500L + i, c }), Elements(g, analyser[1]));

        // A list that held only the orphan becomes an empty u32 array, as the game writes it.
        var primary = Arrays(FindRecord(g, g.Root, "CAI_PRIMARY_FACTION_GENERALS_ANALYSIS"))[0];
        Assert.Equal(0x48, primary.Type);
        Assert.Empty(Elements(g, primary));

        var leftovers = new List<string>();
        g.WalkInts((leaf, path, idx, v) => { if (v is 1199 or 199) leftovers.Add(path()); });
        Assert.Empty(leftovers);

        // Characters themselves are untouched.
        Assert.Equal(AllDefault.ToDictionary(c => (long)c.Id, c => c.Immortal), Flags(g));
    }

    [Fact]
    public void Orphan_with_live_handles_is_left_alone()
    {
        byte[] data = Build([Saphrax with { LiveHandles = true }]);
        var f = EsfFile.Load(data);
        var res = Repair.RemoveOrphanAiCharacters(f);
        Assert.Empty(res.Components);
        Assert.Contains(res.Log, l => l.StartsWith("left alone"));
        Assert.Equal(data, f.Save());
    }

    [Fact]
    public void Clean_campaign_is_not_changed()
    {
        byte[] data = Build();
        var f = EsfFile.Load(data);
        var res = Repair.RemoveOrphanAiCharacters(f);
        Assert.Empty(res.Components);
        Assert.Empty(res.Log);
        Assert.Equal(data, f.Save());
    }

    [Fact]
    public void Several_orphans_are_removed_together()
    {
        var f = EsfFile.Load(Build([Saphrax, new(198, 1198)]));
        var res = Repair.RemoveOrphanAiCharacters(f);
        Assert.Equal([1198L, 1199L], res.Components.Order());

        var g = EsfFile.Load(f.Save());
        Assert.Equal(LiveComponents, Elements(g, Arrays(FindRecord(g, g.Root, "CAI_FACTION"))[0]));
    }

    [Theory]
    [MemberData(nameof(EsfTests.IntArrayCases), MemberType = typeof(EsfTests))]
    public void Array_elements_are_encoded_like_the_reference_encoder(int type, long[] values)
    {
        int w = EsfFile.ArrayElementWidth((byte)type)!.Value;
        var data = new byte[values.Length * w];
        for (int i = 0; i < values.Length; i++) Repair.Encode((byte)type, values[i], data.AsSpan(i * w, w));
        Assert.Equal(EsfBuilder.IntArray((byte)type, values)[^data.Length..], data);
    }

    [Theory]
    [InlineData(0x44)]
    [InlineData(0x57)]
    [InlineData(0x58)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void A_shortened_list_keeps_its_encoding(int type)
    {
        var f = EsfFile.Load(Build([Saphrax], factionListType: (byte)type));
        Repair.RemoveOrphanAiCharacters(f);
        var g = EsfFile.Load(f.Save());
        var list = Arrays(FindRecord(g, g.Root, "CAI_FACTION"))[0];
        Assert.Equal(type, list.Type);
        Assert.Equal(LiveComponents, Elements(g, list));
    }

    [Fact]
    public void Repaired_save_reopens()
    {
        var save = SaveGame.Open(EsfFile.Load(Save(Build([Saphrax]))));
        Repair.RemoveOrphanAiCharacters(save.Inner);
        var g = SaveGame.Open(EsfFile.Load(save.Save(restructured: true))).Inner;
        Assert.Equal(AllDefault.Count(), Characters.FindBlock(g, g.Root, "CAI_WORLD_CHARACTERS")!.Items.Count);
    }
}
