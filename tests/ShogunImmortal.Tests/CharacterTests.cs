namespace ShogunImmortal.Tests;

public class CharacterTests
{
    [Fact]
    public void Finds_characters_of_every_faction_including_rebels()
    {
        var f = EsfFile.Load(Campaign.Build());
        Assert.Equal(
            [("la_lindon", 101L), ("la_lindon", 102), ("la_lindon", 103), ("la_mordor", 104), ("la_mordor", 105), ("rebels", 106)],
            Characters.All(f).Select(c => (c.Faction, Characters.Id(f, c.Character))));
    }

    [Fact]
    public void Immortal_flag_is_read_from_child_29_in_both_bool_encodings()
    {
        var f = EsfFile.Load(Campaign.Build());
        Assert.Equal(Campaign.AllDefault.ToDictionary(c => (long)c.Id, c => c.Immortal), Campaign.Flags(f));
    }

    [Fact]
    public void Setting_a_flag_patches_in_place_and_reads_back()
    {
        byte[] data = Campaign.Build();
        var f = EsfFile.Load(data);
        var flags = Characters.All(f).Select(c => Characters.ImmortalFlag(f, c.Character).Flag).ToList();
        var before = flags.Select(l => Characters.BoolValue(f, l)).ToList();

        for (int i = 0; i < flags.Count; i++)
        {
            Assert.True(Characters.SetBool(f, flags[i], !before[i]));
            Assert.Equal(!before[i], Characters.BoolValue(f, flags[i]));
            Assert.False(Characters.SetBool(f, flags[i], !before[i]));    // already set: no change reported
        }

        Assert.Equal(data.Length, f.Data.Length);
        var g = EsfFile.Load(f.Data);
        Assert.Equal(Campaign.AllDefault.ToDictionary(c => (long)c.Id, c => !c.Immortal), Campaign.Flags(g));
    }

    [Fact]
    public void Other_character_versions_are_refused()
    {
        var f = EsfFile.Load(Campaign.Build(factions: [("la_lindon", [new(101, "swedishGil-Galad", true, Version: 22)])]));
        var ch = Characters.All(f).First().Character;
        var e = Assert.Throws<InvalidDataException>(() => Characters.ImmortalFlag(f, ch));
        Assert.Contains("v22", e.Message);
    }

    [Fact]
    public void A_non_bool_at_child_29_is_refused()
    {
        var f = EsfFile.Load(Campaign.Build(factions: [("la_lindon", [new(101, "swedishGil-Galad", true, BrokenLayout: true)])]));
        var ch = Characters.All(f).First().Character;
        Assert.Throws<InvalidDataException>(() => Characters.ImmortalFlag(f, ch));
    }

    [Fact]
    public void Names_are_read_from_the_localisation_key()
    {
        var f = EsfFile.Load(Campaign.Build());
        var names = Characters.All(f).Select(c => Characters.NameOf(f, Characters.ImmortalFlag(f, c.Character).Details));
        Assert.Equal(Campaign.AllDefault.Select(c => c.NameKey), names);
    }

    [Theory]
    [InlineData("swedishGil-Galad", "Gil-Galad")]
    [InlineData("danishDurin_IV", "Durin_IV")]
    [InlineData("frenchNazgûl", "Nazgûl")]
    [InlineData("Saphrax", "Saphrax")]
    [InlineData("lowercaseonly", "lowercaseonly")]
    public void Display_name_drops_the_name_group_prefix(string key, string expected) =>
        Assert.Equal(expected, Characters.DisplayName(key));
}
