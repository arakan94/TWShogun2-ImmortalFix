using static ShogunImmortal.Tests.EsfBuilder;

namespace ShogunImmortal.Tests;

/// A character in a synthetic campaign. ByteBool stores the immortal flag in the 2-byte form (0x01 xx)
/// instead of the compact 0x12/0x13 form; BrokenLayout puts a non-bool at child 29.
sealed record Hero(uint Id, string NameKey, bool Immortal, bool ByteBool = false, int Version = 23, bool BrokenLayout = false)
{
    public uint Component => Id + 1000;     // its AI component id
}

/// An AI entry left behind for a character that is no longer in the world (the corruption `repair` fixes).
/// With LiveHandles its handles are non-zero, which `repair` must leave alone.
sealed record Orphan(uint CharacterId, uint Component, bool LiveHandles = false);

/// Builds a small campaign laid out like a Shogun 2 save:
///   CAMPAIGN_SAVE_GAME > CAMPAIGN_ENV > CAMPAIGN_MODEL > WORLD > FACTION_ARRAY[] > FACTION > CHARACTER_ARRAY[] > CHARACTER
///   WORLD > REBEL_FACTION > FACTION ("default")
///   CAMPAIGN_ENV > CAI_INTERFACE with the AI records `repair` touches.
/// Some size and count varints are padded, as the game writes them, and the file ends in zero padding.
static class Campaign
{
    public static readonly (string Key, Hero[] Characters)[] DefaultFactions =
    [
        ("la_lindon", [
            new(101, "swedishGil-Galad", Immortal: true),
            new(102, "swedishElrond", Immortal: true, ByteBool: true),
            new(103, "englishBob", Immortal: false),
        ]),
        ("la_mordor", [
            new(104, "frenchNazgûl", Immortal: true),
            new(105, "frenchMouth", Immortal: false, ByteBool: true),
        ]),
    ];

    public static readonly Hero[] DefaultRebels = [new(106, "germanSaphrax", Immortal: true)];

    public static IEnumerable<Hero> AllDefault => DefaultFactions.SelectMany(f => f.Characters).Concat(DefaultRebels);

    /// The orphan used by the repair tests: character 199 is gone from the world, its AI entry is not.
    public static readonly Orphan Saphrax = new(199, 1199);

    /// factionListType is the array encoding of CAI_FACTION's component list (u32 by default).
    public static byte[] Build(Orphan[]? orphans = null,
        (string Key, Hero[] Characters)[]? factions = null, Hero[]? rebels = null, byte factionListType = 0x48)
    {
        orphans ??= [];
        factions ??= DefaultFactions;
        rebels ??= DefaultRebels;
        var b = new EsfBuilder();
        var everyone = factions.SelectMany(f => f.Characters).Concat(rebels).ToList();

        byte[] Character(Hero c)
        {
            var children = new List<byte[]>
            {
                U32Opt(0), U32Opt(0), U32Opt(0),
                U32(c.Id),                                                  // 3: id
                b.Rec("CHARACTER_DETAILS", 3,
                    b.Rec("CAMPAIGN_LOCALISATION", 0, b.Utf16("names_name_names_" + c.NameKey), b.Utf16("")),
                    Bool(true)),
            };
            for (uint i = 5; i < 29; i++) children.Add(U32Opt(i));          // filler fields
            children.Add(c.BrokenLayout ? U32Opt(29) : c.ByteBool ? BoolByte(c.Immortal) : Bool(c.Immortal)); // 29: immortal
            children.Add(Bool(false));
            return b.Rec("CHARACTER", c.Version, children.ToArray());
        }

        byte[] Faction(string key, Hero[] characters, bool padded) => b.Rec("FACTION", 2,
            b.Utf16(key),
            padded
                ? b.Block("CHARACTER_ARRAY", 0, sizeWidth: 1, countWidth: 2, itemSizeWidth: 2, characters.Select(c => Item(Character(c))).ToArray())
                : b.Block("CHARACTER_ARRAY", 0, characters.Select(c => Item(Character(c))).ToArray()));

        byte[] AiEntry(uint characterId, uint component, bool liveHandles, uint[] componentList) => Item(
            b.Rec("CAI_CHARACTER", 1,
                liveHandles ? U32Opt(7) : U32Opt(0), liveHandles ? U32Opt(8) : U32Opt(0), liveHandles ? U32Opt(9) : U32Opt(0),
                U32(characterId)),
            b.Rec("CAI_BDI_COMPONENT", 2, U32(component), U32Array(componentList)));

        var liveComps = everyone.Select(c => c.Component).ToArray();
        var allComps = liveComps.Concat(orphans.Select(o => o.Component)).ToArray();
        var orphanComps = orphans.Select(o => o.Component).ToArray();

        // The first live component lists the orphans' components and one live one, so `repair` must
        // rewrite that list rather than empty it.
        var aiEntries = everyone.Select((c, i) => AiEntry(c.Id, c.Component, liveHandles: true,
                i == 0 ? [.. orphanComps, liveComps[1]] : []))
            .Concat(orphans.Select(o => AiEntry(o.CharacterId, o.Component, o.LiveHandles, [])))
            .ToArray();

        byte[] ai = b.Rec("CAI_INTERFACE", 0,
            b.Block("CAI_WORLD_CHARACTERS", 0, aiEntries),
            b.Block("CAI_BDI_COMPONENT_BLOCK_OWNS", 0, allComps.Select(c => Item(U32(c), U32Opt(7))).ToArray()),
            b.Rec("CAI_FACTION", 4, U32Opt(1), IntArray(factionListType, allComps.Select(c => (long)c).ToArray())),
            b.Rec("CAI_PRIMARY_FACTION_GENERALS_ANALYSIS", 0, U32Array(orphanComps)),
            b.Rec("CAI_ANALYSER", 0,
                Packed2Array(allComps),
                Packed2Array(allComps.SelectMany((c, i) => new[] { (uint)(500 + i), c }).ToArray())));   // (key, id) pairs

        byte[] world = b.Rec("WORLD", 0, sizeWidth: 3,
            b.Block("FACTION_ARRAY", 0, factions.Select((f, i) => Item(Faction(f.Key, f.Characters, padded: i == 0))).ToArray()),
            b.Rec("REBEL_FACTION", 0, Faction("default", rebels, padded: false)));

        return b.File("CAMPAIGN_SAVE_GAME", 1,
            [b.Rec("CAMPAIGN_ENV", 0, b.Rec("CAMPAIGN_MODEL", 0, world), ai)],
            padding: 7);
    }

    /// Wraps campaign data in a .save container: COMPRESSED_DATA holds it LZMA-packed. sizeType is how the
    /// uncompressed size is stored: 0x08 (u32), or the compact 0x17 (2 bytes) / 0x18 (3 bytes) forms.
    public static byte[] Save(byte[] inner, byte sizeType = 0x08)
    {
        byte[] props = [0x5D, 0x00, 0x00, 0x01, 0x00];  // lc=3 lp=0 pb=2, 64 KiB dictionary
        uint n = (uint)inner.Length;
        byte[] size = sizeType switch
        {
            0x08 => U32(n),
            0x17 when n < 1 << 16 => [0x17, (byte)(n >> 8), (byte)n],
            0x18 when n < 1 << 24 => [0x18, (byte)(n >> 16), (byte)(n >> 8), (byte)n],
            _ => throw new ArgumentException($"Cannot store {n} as type 0x{sizeType:X2}"),
        };
        var b = new EsfBuilder();
        return b.File("CAMPAIGN_SAVE_GAME", 1,
        [
            b.Rec("SAVE_GAME_HEADER", 0, b.Utf16("Test save")),
            b.Rec("COMPRESSED_DATA", 0,
                U8Array(Lzma.Compress(inner, props)),
                b.Rec("COMPRESSED_DATA_INFO", 0, size, U8Array(props))),
        ]);
    }

    // ------------------------------------------------------------ lookups for assertions

    public static Record FindRecord(EsfFile f, Node n, string name) => TryFindRecord(f, n, name)
        ?? throw new InvalidOperationException($"No {name} record");

    static Record? TryFindRecord(EsfFile f, Node n, string name) => n switch
    {
        Record r when f.Name(r.NameIdx) == name => r,
        Record r => r.Children.Select(c => TryFindRecord(f, c, name)).FirstOrDefault(x => x != null),
        RecordBlock blk => blk.Items.SelectMany(i => i).Select(c => TryFindRecord(f, c, name)).FirstOrDefault(x => x != null),
        _ => null,
    };

    public static List<long> Elements(EsfFile f, Leaf l)
    {
        int w = EsfFile.ArrayElementWidth(l.Type) ?? throw new InvalidOperationException($"0x{l.Type:X2} is not an int array");
        var r = new List<long>();
        for (int p = l.DataStart; p + w <= l.End; p += w) r.Add(f.ArrayElement(l, p));
        return r;
    }

    public static List<Leaf> Arrays(Record r) =>
        r.Children.OfType<Leaf>().Where(l => EsfFile.ArrayElementWidth(l.Type) != null).ToList();

    /// Immortal flag of every character, by id.
    public static Dictionary<long, bool> Flags(EsfFile f) => Characters.All(f).ToDictionary(
        c => Characters.Id(f, c.Character),
        c => Characters.BoolValue(f, Characters.ImmortalFlag(f, c.Character).Flag));
}
