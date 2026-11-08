namespace ShogunImmortal;

static class Characters
{
    public static bool IsBool(Leaf l) => l.Type is 0x01 or 0x12 or 0x13;

    public static bool BoolValue(EsfFile f, Leaf l) => l.Type switch
    {
        0x12 => true,
        0x13 => false,
        0x01 => f.Data[l.DataStart] != 0,
        _ => throw new InvalidOperationException("not a bool"),
    };

    /// Sets a bool in place without changing the encoded size. Returns true if it changed.
    public static bool SetBool(EsfFile f, Leaf l, bool value)
    {
        if (BoolValue(f, l) == value) return false;
        if (l.Type == 0x01) f.Data[l.DataStart] = (byte)(value ? 1 : 0);
        else f.Data[l.Start] = l.Type = (byte)(value ? 0x12 : 0x13);   // the compact form keeps the value in the type
        return true;
    }

    static Record Child(EsfFile f, Record r, string name) =>
        r.Children.OfType<Record>().FirstOrDefault(c => f.Name(c.NameIdx) == name)
        ?? throw new InvalidDataException($"{f.Name(r.NameIdx)} has no {name}");

    static RecordBlock Block(EsfFile f, Record r, string name) =>
        r.Children.OfType<RecordBlock>().FirstOrDefault(c => f.Name(c.NameIdx) == name)
        ?? throw new InvalidDataException($"{f.Name(r.NameIdx)} has no {name}[]");

    static string Str(EsfFile f, Leaf l) => l.Type == 0x0E
        ? f.Utf16.GetValueOrDefault(BitConverter.ToUInt32(f.Data, l.DataStart), "")
        : "";

    /// CAMPAIGN_SAVE_GAME > CAMPAIGN_ENV > CAMPAIGN_MODEL > WORLD > FACTION_ARRAY[] > FACTION > CHARACTER_ARRAY[] > CHARACTER
    public static IEnumerable<(string Faction, Record Character)> All(EsfFile f)
    {
        // Saves start at CAMPAIGN_SAVE_GAME; a decompressed startpos may start lower down the path.
        Record r = (Record)f.Root;
        foreach (var step in new[] { "CAMPAIGN_ENV", "CAMPAIGN_MODEL", "WORLD" })
            if (f.Name(r.NameIdx) != step && !(step == "CAMPAIGN_ENV" && f.Name(r.NameIdx) is "CAMPAIGN_MODEL" or "WORLD")
                                          && !(step == "CAMPAIGN_MODEL" && f.Name(r.NameIdx) == "WORLD"))
                r = Child(f, r, step);
        var world = r;
        // Regular factions live in FACTION_ARRAY[]; the rebels are a separate WORLD > REBEL_FACTION > FACTION.
        // The rebel faction's own key is "default"; it is reported as "rebels".
        var factions = Block(f, world, "FACTION_ARRAY").Items
            .Select(item => (Rebels: false, Faction: item.OfType<Record>().First(x => f.Name(x.NameIdx) == "FACTION")))
            .Concat(world.Children.OfType<Record>().Where(x => f.Name(x.NameIdx) == "REBEL_FACTION")
                .SelectMany(rf => rf.Children.OfType<Record>().Where(x => f.Name(x.NameIdx) == "FACTION"))
                .Select(x => (Rebels: true, Faction: x)));
        foreach (var (rebels, faction) in factions)
        {
            string key = rebels ? "rebels"
                : faction.Children.OfType<Leaf>().Select(l => Str(f, l)).FirstOrDefault(s => s != "") ?? "?";
            foreach (var citem in Block(f, faction, "CHARACTER_ARRAY").Items)
                foreach (var ch in citem.OfType<Record>().Where(r => f.Name(r.NameIdx) == "CHARACTER"))
                    yield return (key, ch);
        }
    }

    /// Child index of the immortal flag inside a CHARACTER v23 record.
    ///
    /// ESF fields are unnamed, so this was mapped against the Assembly Kit: in tut_shogun the only
    /// start_pos_characters row with immortal=1 (Chosokabe Motochika) is the only compiled character
    /// with this bool set, and vanilla jap_shogun (immortal=0 everywhere) has it clear on all characters.
    /// For reference, CHARACTER_DETAILS child 17 is is_male (0 for exactly the 63 is_male=0 rows).
    public const int ImmortalChildIndex = 29;
    public const int SupportedCharacterVersion = 23;

    public static (Record Details, Leaf Flag) ImmortalFlag(EsfFile f, Record character, int childIndex = ImmortalChildIndex)
    {
        if (character.Version != SupportedCharacterVersion && childIndex == ImmortalChildIndex)
            throw new InvalidDataException(
                $"CHARACTER record is v{character.Version}; the immortal field was only mapped for v{SupportedCharacterVersion} " +
                "(current Shogun 2 saves and startpos files). Nothing was changed.");
        var details = Child(f, character, "CHARACTER_DETAILS");
        if (childIndex >= character.Children.Count || character.Children[childIndex] is not Leaf flag || !IsBool(flag))
            throw new InvalidDataException($"CHARACTER child {childIndex} is not a bool - unexpected layout");
        return (details, flag);
    }

    /// First record block with the given name anywhere below n (depth-first).
    public static RecordBlock? FindBlock(EsfFile f, Node n, string name) => n switch
    {
        RecordBlock b when f.Name(b.NameIdx) == name => b,
        Record r => r.Children.Select(c => FindBlock(f, c, name)).FirstOrDefault(x => x != null),
        RecordBlock b => b.Items.SelectMany(i => i).Select(c => FindBlock(f, c, name)).FirstOrDefault(x => x != null),
        _ => null,
    };

    /// Unique character id (CHARACTER child 3, also stored in CHARACTER_DETAILS).
    public static long Id(EsfFile f, Record character) =>
        character.Children[3] is Leaf l && f.IntValue(l) is long v ? (uint)v
            : throw new InvalidDataException("CHARACTER child 3 is not the id");

    /// Readable forename: the localisation key without its name-group prefix
    /// ("swedishGil-Galad" -> "Gil-Galad", "danishDurin_IV" -> "Durin_IV").
    public static string DisplayName(string key) =>
        System.Text.RegularExpressions.Regex.Replace(key, @"^\p{Ll}+(?=\p{Lu})", "");

    /// Forename localisation key from CHARACTER_DETAILS (first CAMPAIGN_LOCALISATION).
    public static string NameOf(EsfFile f, Record details)
    {
        var loc = details.Children.OfType<Record>().FirstOrDefault(r => f.Name(r.NameIdx) == "CAMPAIGN_LOCALISATION");
        string n = loc?.Children.OfType<Leaf>().Select(l => Str(f, l)).FirstOrDefault() ?? "";
        return n.Replace("names_name_names_", "");
    }
}
