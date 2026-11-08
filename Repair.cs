namespace ShogunImmortal;

/// Cleans up AI entries the game leaves behind when an immortal character is destroyed.
///
/// Observed failure: an immortal general whose faction died became a rebel; when his army was destroyed
/// the game removed the character from the world but left his CAI_WORLD_CHARACTERS entry (with its
/// handles zeroed) and his AI component id in the AI's component lists. Normally dead characters are
/// removed from all of those. This mirrors that cleanup:
///   * remove CAI_WORLD_CHARACTERS entries whose character no longer exists and whose handles are zeroed
///   * remove their component id from CAI_BDI_COMPONENT_BLOCK_OWNS rows and from the component-id lists
///     of the AI record types where it was seen (CAI_BDI_COMPONENT, CAI_FACTION,
///     CAI_PRIMARY_FACTION_GENERALS_ANALYSIS, CAI_ANALYSER - whose second array holds (key, id) pairs)
/// Anything else still referring to the removed ids is reported, not touched.
static class Repair
{
    static readonly HashSet<string> ListOwners =
        ["CAI_BDI_COMPONENT", "CAI_FACTION", "CAI_PRIMARY_FACTION_GENERALS_ANALYSIS"];

    public sealed record Result(HashSet<long> Components, HashSet<long> CharacterIds, List<string> Log);

    public static Result RemoveOrphanAiCharacters(EsfFile f)
    {
        var log = new List<string>();
        var worldIds = Characters.All(f).Select(c => Characters.Id(f, c.Character)).ToHashSet();
        var block = Characters.FindBlock(f, f.Root, "CAI_WORLD_CHARACTERS")
                    ?? throw new InvalidDataException("No CAI_WORLD_CHARACTERS");
        var dead = new HashSet<long>();
        var deadChars = new HashSet<long>();

        for (int i = block.Items.Count - 1; i >= 0; i--)
        {
            var item = block.Items[i];
            var cc = item.OfType<Record>().First(r => f.Name(r.NameIdx) == "CAI_CHARACTER");
            var vals = cc.Children.OfType<Leaf>().Select(l => f.IntValue(l) ?? -1).ToList();
            if (worldIds.Contains(vals[3])) continue;
            var comp = item.OfType<Record>().First(r => f.Name(r.NameIdx) == "CAI_BDI_COMPONENT");
            long compId = comp.Children.OfType<Leaf>().Select(f.IntValue).First()
                          ?? throw new InvalidDataException("CAI_BDI_COMPONENT has no id");
            if (vals[0] != 0 || vals[1] != 0 || vals[2] != 0)
            {
                log.Add($"left alone: AI entry [{i}] for missing character {vals[3]} still has live handles");
                continue;
            }
            log.Add($"removed AI character entry [{i}] (character {vals[3]}, AI component {compId})");
            block.Items.RemoveAt(i);
            dead.Add(compId);
            deadChars.Add(vals[3]);
        }
        if (dead.Count > 0) Scrub(f, f.Root, dead, log, []);
        return new Result(dead, deadChars, log);
    }

    static void Scrub(EsfFile f, Node n, HashSet<long> dead, List<string> log, List<string> path)
    {
        switch (n)
        {
            case Record r:
            {
                string name = f.Name(r.NameIdx);
                path.Add(name);
                var arrays = r.Children.OfType<Leaf>().Where(l => EsfFile.ArrayElementWidth(l.Type) != null).ToList();
                if (name == "CAI_ANALYSER" && arrays.Count >= 2)
                {
                    RemoveElements(f, arrays[0], dead, 1, log, path);
                    RemoveElements(f, arrays[1], dead, 2, log, path);   // (key, id) pairs
                }
                else if (ListOwners.Contains(name))
                    foreach (var a in arrays) RemoveElements(f, a, dead, 1, log, path);
                foreach (var c in r.Children) Scrub(f, c, dead, log, path);
                path.RemoveAt(path.Count - 1);
                break;
            }
            case RecordBlock b:
            {
                string name = f.Name(b.NameIdx);
                if (name == "CAI_BDI_COMPONENT_BLOCK_OWNS")
                {
                    int removed = b.Items.RemoveAll(row =>
                        row.OfType<Leaf>().FirstOrDefault() is Leaf l && f.IntValue(l) is long v && dead.Contains(v));
                    if (removed > 0) log.Add($"removed {removed} ownership row(s) in {string.Join(" > ", path)}");
                }
                for (int i = 0; i < b.Items.Count; i++)
                {
                    path.Add($"{name}[{i}]");
                    foreach (var c in b.Items[i]) Scrub(f, c, dead, log, path);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            }
        }
    }

    /// Drops dead ids from an int array. stride 2 = (key, id) pairs: the pair goes when its id is dead.
    static void RemoveElements(EsfFile f, Leaf l, HashSet<long> dead, int stride, List<string> log, List<string> path)
    {
        int w = EsfFile.ArrayElementWidth(l.Type)!.Value;
        var vals = new List<long>();
        for (int p = l.DataStart; p + w <= l.End; p += w) vals.Add(f.ArrayElement(l, p));
        if (stride == 2 && vals.Count % 2 != 0) throw new InvalidDataException("Paired array with odd length");

        var keep = new List<long>();
        int removed = 0;
        for (int i = 0; i < vals.Count; i += stride)
        {
            if (dead.Contains(vals[i + stride - 1])) { removed++; continue; }
            for (int k = 0; k < stride; k++) keep.Add(vals[i + k]);
        }
        if (removed == 0) return;
        if (stride == 2 && vals.Where((v, i) => i % 2 == 0).Any(dead.Contains))
            throw new InvalidDataException($"Dead id in key position of a paired array in {string.Join(" > ", path)}");

        if (keep.Count == 0)
        {
            l.Type = 0x48;          // the game writes empty int arrays as an empty u32 array
            l.Replacement = [];
        }
        else
        {
            var bytes = new byte[keep.Count * w];
            for (int i = 0; i < keep.Count; i++) Encode(l.Type, keep[i], bytes.AsSpan(i * w, w));
            l.Replacement = bytes;
        }
        log.Add($"removed {removed} {(stride == 2 ? "pair(s)" : "id(s)")} from a {vals.Count}-element list in {string.Join(" > ", path)}");
    }

    internal static void Encode(byte type, long v, Span<byte> dst)
    {
        switch (type)
        {
            case 0x44: BitConverter.TryWriteBytes(dst, (int)v); break;
            case 0x48: BitConverter.TryWriteBytes(dst, (uint)v); break;
            default:   // packed big-endian
                for (int i = dst.Length - 1; i >= 0; i--) { dst[i] = (byte)v; v >>= 8; }
                break;
        }
    }
}
