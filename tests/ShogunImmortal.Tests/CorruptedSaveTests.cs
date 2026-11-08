using System.Security.Cryptography;

namespace ShogunImmortal.Tests;

/// Regression test on the one real save known to have the corruption `repair` fixes.
///
/// Fixtures/Kingdom of Lindon corrupted.save (Last Alliance): Saphrax (la_krakafolk_hvallfugl) was immortal;
/// his faction was destroyed, he became a rebel, and his army was then destroyed. The game removed him from
/// the world but left his AI entry, and the save crashed on load. The repaired save was confirmed to load
/// and play in-game on 2026-11-08; the expected campaign hash below is that repaired save's.
public class CorruptedSaveTests
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kingdom of Lindon corrupted.save");

    const long SaphraxCharacter = 2130402496;
    const long SaphraxComponent = 50473;
    const string RepairedCampaignSha256 = "1F0D1CEE3502D00305BC2FC424E852572C984AA55E53D937D1D7CA137D555AAA";

    static SaveGame Open() => SaveGame.Open(EsfFile.Load(File.ReadAllBytes(Fixture)));

    [Fact]
    public void Save_has_exactly_the_known_orphan()
    {
        var f = Open().Inner;
        var ids = Characters.All(f).Select(c => Characters.Id(f, c.Character)).ToHashSet();
        Assert.Equal(735, ids.Count);
        Assert.DoesNotContain(SaphraxCharacter, ids);
        Assert.Equal(736, Characters.FindBlock(f, f.Root, "CAI_WORLD_CHARACTERS")!.Items.Count);
        Assert.Equal(f.Data, f.Save());     // the writer reproduces this save exactly
    }

    [Fact]
    public void Repair_removes_Saphrax_and_every_reference_to_him()
    {
        var save = Open();
        var f = save.Inner;
        var flagsBefore = Campaign.Flags(f);

        var res = Repair.RemoveOrphanAiCharacters(f);
        Assert.Equal([SaphraxComponent], res.Components);
        Assert.Equal([SaphraxCharacter], res.CharacterIds);
        Assert.Equal(1, res.Log.Count(l => l.StartsWith("removed AI character entry")));
        Assert.Equal(1, res.Log.Count(l => l.StartsWith("removed 1 ownership row")));
        Assert.Equal(1, res.Log.Count(l => l.StartsWith("removed 1 pair(s)")));
        Assert.Equal(19, res.Log.Count);    // the entry, 1 ownership row, 16 id lists and the analyser's pairs
        Assert.DoesNotContain(res.Log, l => l.StartsWith("left alone"));

        var g = SaveGame.Open(EsfFile.Load(save.Save(restructured: true))).Inner;
        Assert.Equal(735, Characters.FindBlock(g, g.Root, "CAI_WORLD_CHARACTERS")!.Items.Count);
        var leftovers = new List<string>();
        g.WalkInts((leaf, path, idx, v) => { if (v is SaphraxComponent or SaphraxCharacter) leftovers.Add(path()); });
        Assert.Empty(leftovers);
        Assert.Equal(flagsBefore, Campaign.Flags(g));       // characters untouched (43 immortal)

        // Byte for byte the campaign data of the repaired save that was confirmed to work in-game.
        Assert.Equal(RepairedCampaignSha256, Convert.ToHexString(SHA256.HashData(g.Data)));
    }
}
