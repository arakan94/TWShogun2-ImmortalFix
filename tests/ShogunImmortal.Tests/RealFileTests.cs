namespace ShogunImmortal.Tests;

/// Opt-in checks against real game files, which are not part of the repository:
///   SHOGUN_TEST_SAVES     a folder of .save files (for example the game's save_games folder)
///   SHOGUN_ASSEMBLY_KIT   the Shogun 2 Assembly Kit folder (the one containing working_data)
///   SHOGUN_TEST_STARTPOS  a mod's compressed startpos.esf (for example Last Alliance's campaigns\jap_shogun\startpos.esf)
/// Without them these tests are skipped. A variable that is set but points nowhere fails the tests instead,
/// so a typo or a relative path can't quietly turn the checks off.
public class RealFileTests
{
    internal static string? Setting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static string Existing(string name, bool folder)
    {
        string v = Setting(name)!;
        bool found = folder ? Directory.Exists(v) : File.Exists(v);
        Assert.True(found, $"{name} is set to '{v}', but that {(folder ? "folder" : "file")} was not found." +
            (Path.IsPathRooted(v) ? "" :
                $" Relative paths are resolved from the test output folder ({Environment.CurrentDirectory}); use a full path."));
        return v;
    }

    static string SavesDir => Existing("SHOGUN_TEST_SAVES", folder: true);
    static string KitDir => Existing("SHOGUN_ASSEMBLY_KIT", folder: true);
    static string StartposFile => Existing("SHOGUN_TEST_STARTPOS", folder: false);

    /// Campaign data of a save or startpos, decompressed if needed (as the tool does).
    static EsfFile Campaign(byte[] bytes)
    {
        var outer = EsfFile.Load(bytes);
        bool compressed = ((Record)outer.Root).Children.OfType<Record>().Any(r => outer.Name(r.NameIdx) == "COMPRESSED_DATA");
        return compressed ? SaveGame.Open(outer).Inner : outer;
    }

    static IEnumerable<string> Saves() => Directory.EnumerateFiles(SavesDir, "*.save");

    [SavesFact]
    public void Saves_are_rewritten_byte_for_byte()
    {
        foreach (var path in Saves())
        {
            byte[] bytes = File.ReadAllBytes(path);
            var outer = EsfFile.Load(bytes);
            Assert.True(bytes.AsSpan().SequenceEqual(outer.Save()), $"{Path.GetFileName(path)}: outer file changed");
            var inner = SaveGame.Open(outer).Inner;
            Assert.True(inner.Data.AsSpan().SequenceEqual(inner.Save()), $"{Path.GetFileName(path)}: campaign data changed");
        }
    }

    [SavesFact]
    public void Every_character_in_every_save_has_a_readable_immortal_flag()
    {
        foreach (var path in Saves())
        {
            var f = Campaign(File.ReadAllBytes(path));
            var characters = Characters.All(f).ToList();
            Assert.True(characters.Count > 0, $"{Path.GetFileName(path)}: no characters found");
            foreach (var (_, ch) in characters)
                Characters.ImmortalFlag(f, ch);     // throws on an unexpected layout
        }
    }

    [SavesFact]
    public void Repair_leaves_no_orphans_and_no_references_to_removed_ids()
    {
        foreach (var path in Saves())
        {
            string name = Path.GetFileName(path);
            var save = SaveGame.Open(EsfFile.Load(File.ReadAllBytes(path)));
            var res = Repair.RemoveOrphanAiCharacters(save.Inner);
            if (res.Components.Count == 0) continue;

            var g = SaveGame.Open(EsfFile.Load(save.Save(restructured: true))).Inner;
            var ids = Characters.All(g).Select(c => Characters.Id(g, c.Character)).ToHashSet();
            foreach (var item in Characters.FindBlock(g, g.Root, "CAI_WORLD_CHARACTERS")!.Items)
            {
                var vals = item.OfType<Record>().First(r => g.Name(r.NameIdx) == "CAI_CHARACTER")
                    .Children.OfType<Leaf>().Select(l => g.IntValue(l) ?? -1).ToList();
                Assert.True(ids.Contains(vals[3]) || vals.Take(3).Any(v => v != 0),
                    $"{name}: AI entry for missing character {vals[3]} with zeroed handles remains");
            }
            var leftovers = new List<string>();
            g.WalkInts((leaf, p, idx, v) =>
            {
                if (res.Components.Contains(v) || res.CharacterIds.Contains(v)) leftovers.Add($"{v} at {p()}");
            });
            Assert.True(leftovers.Count == 0, $"{name}: {string.Join("; ", leftovers)}");
        }
    }

    [StartposFact]
    public void Mod_startpos_round_trips_and_its_flags_can_be_cleared()
    {
        // What `mortal` does to a startpos: it must be a compressed file the writer reproduces exactly.
        byte[] bytes = File.ReadAllBytes(StartposFile);
        var outer = EsfFile.Load(bytes);
        Assert.True(bytes.AsSpan().SequenceEqual(outer.Save()), "outer file changed");
        var save = SaveGame.Open(outer);
        var f = save.Inner;
        Assert.True(f.Data.AsSpan().SequenceEqual(f.Save()), "campaign data changed");

        var flags = Characters.All(f).Select(c => Characters.ImmortalFlag(f, c.Character).Flag).ToList();
        Assert.NotEmpty(flags);
        foreach (var flag in flags) Characters.SetBool(f, flag, false);

        var g = SaveGame.Open(EsfFile.Load(save.Save())).Inner;
        Assert.True(f.Data.AsSpan().SequenceEqual(g.Data), "patched campaign data changed on recompression");
        Assert.DoesNotContain(Characters.All(g), c => Characters.BoolValue(g, Characters.ImmortalFlag(g, c.Character).Flag));
    }

    // The immortal flag mapping (CHARACTER child 29), checked against the Assembly Kit's compiled startpos files.

    static EsfFile Startpos(string campaign) => Campaign(File.ReadAllBytes(
        Path.Combine(KitDir, "working_data", "campaigns", campaign, "startpos.esf")));

    static List<(Record Character, bool Flag)> Child29(EsfFile f) => Characters.All(f)
        .Select(c => (c.Character, Flag: c.Character.Children[Characters.ImmortalChildIndex] is Leaf l && Characters.IsBool(l)
            ? Characters.BoolValue(f, l)
            : throw new InvalidDataException("child 29 is not a bool")))
        .ToList();

    [KitFact]
    public void Tutorial_startpos_has_only_Motochika_immortal()
    {
        // tut_shogun's start_pos_characters table marks exactly one character immortal.
        var f = Startpos("tut_shogun");
        var immortal = Child29(f).Where(c => c.Flag).ToList();
        var only = Assert.Single(immortal);
        var details = only.Character.Children.OfType<Record>().First(r => f.Name(r.NameIdx) == "CHARACTER_DETAILS");
        Assert.Contains("Motochika", Characters.NameOf(f, details));
    }

    [KitFact]
    public void Vanilla_grand_campaign_has_no_immortals()
    {
        var all = Child29(Startpos("jap_shogun"));
        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, c => c.Flag);
    }
}

public sealed class SavesFactAttribute : FactAttribute
{
    public SavesFactAttribute()
    {
        if (RealFileTests.Setting("SHOGUN_TEST_SAVES") == null) Skip = "Set SHOGUN_TEST_SAVES to the full path of a folder of .save files to run this test.";
    }
}

public sealed class StartposFactAttribute : FactAttribute
{
    public StartposFactAttribute()
    {
        if (RealFileTests.Setting("SHOGUN_TEST_STARTPOS") == null) Skip = "Set SHOGUN_TEST_STARTPOS to the full path of a compressed startpos.esf to run this test.";
    }
}

public sealed class KitFactAttribute : FactAttribute
{
    public KitFactAttribute()
    {
        if (RealFileTests.Setting("SHOGUN_ASSEMBLY_KIT") == null) Skip = "Set SHOGUN_ASSEMBLY_KIT to the full path of the Shogun 2 Assembly Kit folder to run this test.";
    }
}
