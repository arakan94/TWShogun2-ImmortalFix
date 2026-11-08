// Shogun 2 save / startpos editor: set or clear the immortal flag on characters, and repair saves
// corrupted by destroyed immortal characters. See README.md.
//
//   ShogunImmortal list     <file> [filters]              show characters and whether they are immortal
//   ShogunImmortal mortal   <file> [out] [filters]        clear the immortal flag
//   ShogunImmortal immortal <file> [out] [filters]        set the immortal flag
//   ShogunImmortal repair   <file.save> [out]             remove AI leftovers of characters that no longer exist
//   ShogunImmortal cai      <file> [--all]                diagnostic: AI character table vs. world characters
//   ShogunImmortal refs     <file> <value>...             diagnostic: every integer field equal to a value
//   ShogunImmortal array    <file> <offset> [from]        diagnostic: elements of the int array at an offset
//   ShogunImmortal bools    <file>                        diagnostic: every bool in CHARACTER / CHARACTER_DETAILS
//   ShogunImmortal dump     <file> <out.txt>              text dump of the decompressed campaign data
//
// Filters (repeatable, comma-separated values allowed, case- and accent-insensitive substring match):
//   --faction X       only characters whose faction key contains X ("rebels" for the rebel faction)
//   --name X          only characters whose name contains X
//   --not-faction X   skip characters whose faction key contains X
//   --not-name X      skip characters whose name contains X
//   --immortal        only characters that are currently immortal
//   --mortal          only characters that are currently mortal
// Without filters every character is selected. The input file is never modified.
using System.Globalization;
using System.Text;
using ShogunImmortal;

var positional = new List<string>();
var incFaction = new List<string>(); var incName = new List<string>();
var excFaction = new List<string>(); var excName = new List<string>();
bool? onlyState = null;   // --immortal => true, --mortal => false
bool showAll = false;     // --all, for the cai diagnostic
for (int i = 0; i < args.Length; i++)
{
    List<string>? target = args[i] switch
    {
        "--faction" => incFaction, "--name" => incName,
        "--not-faction" => excFaction, "--not-name" => excName,
        _ => null,
    };
    if (target != null)
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"{args[i]} needs a value"); return 1; }
        target.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Fold));
    }
    else if (args[i] is "--immortal" or "--mortal")
    {
        bool want = args[i] == "--immortal";
        if (onlyState != null && onlyState != want) { Console.Error.WriteLine("--immortal and --mortal exclude each other"); return 1; }
        onlyState = want;
    }
    else if (args[i] == "--all") showAll = true;
    else if (args[i].StartsWith("--")) { Console.Error.WriteLine($"Unknown option {args[i]}"); Usage(); return 1; }
    else positional.Add(args[i]);
}
if (positional.Count < 2) { Usage(); return 1; }
string cmd = positional[0], input = positional[1];
string? outArg = positional.ElementAtOrDefault(2);
bool filtered = incFaction.Count + incName.Count + excFaction.Count + excName.Count > 0 || onlyState != null;
if (showAll && cmd != "cai") { Console.Error.WriteLine("--all only applies to cai"); return 1; }

// Files the tool does not support (InvalidDataException) and inputs it cannot open or read - missing, a folder,
// locked by another program, no permission (IOException, UnauthorizedAccessException) - end with a one-line
// error. Anything else would be a bug in the tool and keeps its stack trace.
try
{
    // The input stays open with read-only sharing until the command finishes, so no output path can replace it -
    // whatever its spelling, case, 8.3 alias or link. SamePath below only gives the common case a clear message.
    using var inputLock = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
    var inputBytes = new byte[inputLock.Length];
    inputLock.ReadExactly(inputBytes);
    var outer = EsfFile.Load(inputBytes);
    bool compressed = ((Record)outer.Root).Children.OfType<Record>().Any(r => outer.Name(r.NameIdx) == "COMPRESSED_DATA");
    SaveGame? save = compressed ? SaveGame.Open(outer) : null;
    EsfFile f = save?.Inner ?? outer;

    // Every character with its display name and whether the filters select it. Only list/mortal/immortal call
    // this: it needs the immortal flag mapping (CHARACTER v23), which the diagnostics must not depend on.
    List<(string Faction, string Name, Leaf Flag, bool Selected)> SelectCharacters() => Characters.All(f).Select(c =>
    {
        var (details, flag) = Characters.ImmortalFlag(f, c.Character);
        string key = Characters.NameOf(f, details);
        string name = Characters.DisplayName(key);
        bool selected =
            (incFaction.Count == 0 || incFaction.Any(x => Fold(c.Faction).Contains(x))) &&
            (incName.Count == 0 || incName.Any(x => Fold(key).Contains(x))) &&
            !excFaction.Any(x => Fold(c.Faction).Contains(x)) &&
            !excName.Any(x => Fold(key).Contains(x)) &&
            (onlyState == null || Characters.BoolValue(f, flag) == onlyState);
        return (c.Faction, Name: name, Flag: flag, Selected: selected);
    }).ToList();

    switch (cmd)
    {
        case "dump":
        {
            if (outArg == null) { Usage(); return 1; }
            if (SamePath(outArg, input)) { Console.Error.WriteLine("Refusing to overwrite the input file."); return 1; }
            if (!WriteOutputWith(outArg, s => { using var w = new StreamWriter(s); f.Dump(w, f.Root); })) return 1;
            Console.WriteLine($"Wrote {outArg}");
            return 0;
        }
        case "repair":  // remove AI leftovers of characters that no longer exist (see Repair.cs)
        {
            if (save == null) { Console.Error.WriteLine("repair works on .save files only."); return 1; }
            string output = outArg ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                Path.GetFileNameWithoutExtension(input) + " - repaired" + Path.GetExtension(input));
            if (SamePath(output, input)) { Console.Error.WriteLine("Refusing to overwrite the input file."); return 1; }

            // The writer must reproduce the campaign data exactly before we trust it with structural edits.
            var rt = f.Save();
            if (!rt.AsSpan().SequenceEqual(f.Data))
            {
                int at = rt.AsSpan().CommonPrefixLength(f.Data);
                throw new Exception($"Round-trip check failed: re-serializing the unmodified campaign data changes it " +
                    $"(lengths {f.Data.Length} -> {rt.Length}, first difference at {at}: " +
                    $"{Convert.ToHexString(f.Data, Math.Max(0, at - 8), 24)} vs {Convert.ToHexString(rt, Math.Max(0, at - 8), Math.Min(24, rt.Length - Math.Max(0, at - 8)))})");
            }

            var res = Repair.RemoveOrphanAiCharacters(f);
            foreach (var line in res.Log) Console.WriteLine("  " + line);
            if (res.Components.Count == 0) { Console.WriteLine("Nothing to repair - no file written."); return 0; }

            byte[] result = save.Save(restructured: true);

            // Verify before writing: re-read, no orphans left, and list any remaining references we did not touch.
            var check = SaveGame.Open(EsfFile.Load(result)).Inner;
            var ids = Characters.All(check).Select(c => Characters.Id(check, c.Character)).ToHashSet();
            var cai = Characters.FindBlock(check, check.Root, "CAI_WORLD_CHARACTERS")!;
            int orphans = cai.Items.Count(it => it.OfType<Record>().First(r => check.Name(r.NameIdx) == "CAI_CHARACTER")
                .Children.OfType<Leaf>().Select(l => check.IntValue(l) ?? -1).ElementAt(3) is var id && !ids.Contains(id));
            var leftovers = new List<string>();
            check.WalkInts((leaf, path, idx, v) =>
            {
                if (res.Components.Contains(v) || res.CharacterIds.Contains(v))
                    leftovers.Add($"{v} at {path()}{(idx >= 0 ? $" (element {idx})" : "")}");
            });
            if (!WriteOutput(output, result)) return 1;
            Console.WriteLine($"Wrote {output}");
            Console.WriteLine($"Check: {cai.Items.Count} AI characters, {ids.Count} world characters, {orphans} orphans; {leftovers.Count} remaining references to removed ids.");
            foreach (var l in leftovers) Console.WriteLine("  still referenced: " + l);
            return 0;
        }
        case "refs":    // diagnostic: every integer field (or int array element) equal to one of the given values
        {
            var targets = positional.Skip(2).Select(long.Parse).ToHashSet();
            f.WalkInts((leaf, path, idx, v) =>
            {
                if (targets.Contains(v))
                    Console.WriteLine($"{v,12}  @{leaf.Start,-9} {path()}{(idx >= 0 ? $"  (array element {idx})" : "")}");
            });
            return 0;
        }
        case "cai":     // diagnostic: AI character table vs. characters that exist in the world
        {
            var ids = Characters.All(f).Select(c => Characters.Id(f, c.Character)).ToHashSet();
            var block = Characters.FindBlock(f, f.Root, "CAI_WORLD_CHARACTERS")
                        ?? throw new InvalidDataException("No CAI_WORLD_CHARACTERS");
            int orphans = 0;
            for (int i = 0; i < block.Items.Count; i++)
            {
                var cc = block.Items[i].OfType<Record>().First(r => f.Name(r.NameIdx) == "CAI_CHARACTER");
                var vals = cc.Children.OfType<Leaf>().Select(l => f.IntValue(l) ?? -1).ToList();
                bool exists = ids.Contains(vals[3]);
                if (!exists) orphans++;
                var comp = block.Items[i].OfType<Record>().First(r => f.Name(r.NameIdx) == "CAI_BDI_COMPONENT");
                long compId = comp.Children.OfType<Leaf>().Select(f.IntValue).First() ?? -1;
                if (!exists || showAll)
                    Console.WriteLine($"[{i}] {(exists ? "ok    " : "ORPHAN")} component={compId} character={string.Join(" ", vals)}");
            }
            Console.WriteLine($"{block.Items.Count} AI characters, {ids.Count} world characters, {orphans} AI entries without a world character.");
            return 0;
        }
        case "array":   // diagnostic: elements of the int array whose type byte is at the given offset
        {
            long off = long.Parse(positional[2]);
            Leaf? hit = null;
            f.WalkInts((leaf, path, idx, v) => { if (leaf.Start == off) hit = leaf; });
            if (hit == null || EsfFile.ArrayElementWidth(hit.Type) is not int w) { Console.Error.WriteLine("No int array at that offset"); return 1; }
            var all = new List<long>();
            for (int p = hit.DataStart; p + w <= hit.End; p += w) all.Add(f.ArrayElement(hit, p));
            Console.WriteLine($"{all.Count} elements, sorted={all.SequenceEqual(all.Order())}, distinct={all.Distinct().Count()}, min={all.Min()}, max={all.Max()}");
            int from = positional.Count > 3 ? int.Parse(positional[3]) : 0;
            Console.WriteLine(string.Join(" ", all.Skip(from).Take(20)));
            return 0;
        }
        case "bools":
        {
            foreach (var (faction, ch) in Characters.All(f))
            {
                var det = ch.Children.OfType<Record>().First(r => f.Name(r.NameIdx) == "CHARACTER_DETAILS");
                string db = string.Join("", det.Children.Select((c, i) => c is Leaf l && Characters.IsBool(l) ? $" d{i}={(Characters.BoolValue(f, l) ? 1 : 0)}" : ""));
                string cb = string.Join("", ch.Children.Select((c, i) => c is Leaf l && Characters.IsBool(l) ? $" c{i}={(Characters.BoolValue(f, l) ? 1 : 0)}" : ""));
                Console.WriteLine($"{faction,-32} {Characters.NameOf(f, det),-40} |{db} |{cb}");
            }
            return 0;
        }
        case "list":
        {
            var characters = SelectCharacters();
            var shown = characters.Where(c => c.Selected).ToList();
            foreach (var c in shown)
                Console.WriteLine($"{c.Faction,-36} {c.Name,-36} {(Characters.BoolValue(f, c.Flag) ? "IMMORTAL" : "-")}");
            int immortal = shown.Count(c => Characters.BoolValue(f, c.Flag));
            Console.WriteLine(filtered
                ? $"\n{shown.Count} of {characters.Count} characters match the filters, {immortal} of them immortal."
                : $"\n{characters.Count} characters, {immortal} immortal.");
            return 0;
        }
        case "mortal":
        case "immortal":
        {
            if (save == null) { Console.Error.WriteLine("Writing is only supported for compressed (0xABCA) saves and startpos files."); return 1; }
            bool value = cmd == "immortal";
            var characters = SelectCharacters();
            var selected = characters.Where(c => c.Selected).ToList();
            if (selected.Count == 0) { Console.Error.WriteLine("No characters match the filters - nothing written."); return 1; }

            string output = outArg ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                Path.GetFileNameWithoutExtension(input) + (value ? " - immortal" : " - mortal") + Path.GetExtension(input));
            if (SamePath(output, input)) { Console.Error.WriteLine("Refusing to overwrite the input file."); return 1; }

            var changed = selected.Where(c => Characters.SetBool(f, c.Flag, value)).ToList();
            foreach (var c in changed)
                Console.WriteLine($"  {(value ? "+ immortal" : "- mortal  ")}  {c.Faction,-36} {c.Name}");
            Console.WriteLine($"{selected.Count} of {characters.Count} characters selected, changed {changed.Count}.");

            byte[] result = save.Save();

            // Verify before writing: the re-read campaign data must equal what we patched, byte for byte,
            // and every character must have the expected flag (selected -> value, others untouched).
            var checkInner = SaveGame.Open(EsfFile.Load(result)).Inner;
            if (!checkInner.Data.AsSpan().SequenceEqual(f.Data))
                throw new Exception("Verification failed: re-read data differs from the patched data");
            var after = Characters.All(checkInner).Select(c => Characters.BoolValue(checkInner, Characters.ImmortalFlag(checkInner, c.Character).Flag)).ToList();
            for (int i = 0; i < characters.Count; i++)
                if (characters[i].Selected && after[i] != value)
                    throw new Exception($"Verification failed for {characters[i].Name}");
            if (!WriteOutput(output, result)) return 1;
            Console.WriteLine($"Verified and wrote {output}");
            return 0;
        }
        default:
            Usage();
            return 1;
    }
}
catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

// Path equality as the file system sees it: Windows and macOS file names are case-insensitive.
static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
    OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

// Writes already-verified bytes: to a temporary file first, then moved into place, so the output name never
// holds a partial file. Replacing the (locked) input fails here rather than destroying it.
static bool WriteOutput(string output, byte[] data) => WriteOutputWith(output, s => s.Write(data));

// Same, for output that is produced while writing (dump), so it can be streamed rather than held in memory.
static bool WriteOutputWith(string output, Action<Stream> write)
{
    string full = Path.GetFullPath(output);
    string tmp = full + ".tmp";
    bool moved = false;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            write(s);
        File.Move(tmp, full, overwrite: true);
        moved = true;
        return true;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not write {output}: {e.Message}");
        return false;
    }
    finally
    {
        // Whatever stopped the write - including a bug in `write` that propagates - leaves no partial file behind.
        if (!moved)
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// Case- and accent-insensitive form for matching ("Nazgûl" matches "nazgul").
static string Fold(string s)
{
    var sb = new StringBuilder();
    foreach (char ch in s.Normalize(NormalizationForm.FormD))
        if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            sb.Append(char.ToLowerInvariant(ch));
    return sb.ToString();
}

static void Usage() => Console.WriteLine(
    "usage:\n" +
    "  ShogunImmortal list     <file> [filters]\n" +
    "  ShogunImmortal mortal   <file> [out] [filters]\n" +
    "  ShogunImmortal immortal <file> [out] [filters]\n" +
    "  ShogunImmortal repair   <file.save> [out]\n" +
    "diagnostics:\n" +
    "  ShogunImmortal cai      <file> [--all]\n" +
    "  ShogunImmortal refs     <file> <value>...\n" +
    "  ShogunImmortal array    <file> <offset> [from]\n" +
    "  ShogunImmortal bools    <file>\n" +
    "  ShogunImmortal dump     <file> <out.txt>\n" +
    "filters (repeatable, comma lists ok, case/accent-insensitive substring):\n" +
    "  --faction X   --name X   --not-faction X   --not-name X   --immortal   --mortal");
