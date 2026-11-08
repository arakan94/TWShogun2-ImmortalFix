# ShogunImmortal

A small command-line tool for **Total War: Shogun 2** save games and `startpos.esf` files. It can:

- list characters and show which ones are **immortal**
- set or clear the immortal flag for all characters, or a filtered selection
- **repair** saves corrupted when an immortal character is destroyed

It was built for the *Last Alliance* mod, where immortal heroes whose faction is wiped out can crash the game. It works on any Shogun 2 save or startpos that uses the same file layout (see [Limitations](#limitations)).

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- The [LZMA-SDK](https://www.nuget.org/packages/LZMA-SDK) NuGet package, which is restored automatically on first build

## Running

From this folder:

```
dotnet run -c Release -- <command> <file> [options]
```

or build once and use the executable:

```
dotnet build -c Release
bin\Release\net9.0\ShogunImmortal.exe <command> <file> [options]
```

Save games are normally in `%APPDATA%\The Creative Assembly\Shogun2\save_games`.

**The input file is never modified.** Commands that write produce a new file next to the input, or at the output path you give, and refuse to overwrite the input. Every output is read back and checked before it is written, and nothing is written if the check fails. Still, keep backups of saves you care about.

## Common tasks

List the immortal characters in a save:

```
dotnet run -c Release -- list "Kingdom of Lindon.save" --immortal
```

Make every character mortal, writing `Kingdom of Lindon - mortal.save`:

```
dotnet run -c Release -- mortal "Kingdom of Lindon.save"
```

Make only some characters mortal. This keeps Mordor's and two named heroes' immortality and clears everyone else's:

```
dotnet run -c Release -- mortal "Kingdom of Lindon.save" --not-faction mordor --not-name "gil-galad,elrond"
```

Fix a save that crashes after an immortal character's army was destroyed:

```
dotnet run -c Release -- repair "Kingdom of Lindon corrupted.save"
```

Patch a mod's startpos so new campaigns start without the flag. The game only loads a file named `startpos.esf`, so write to that name in another folder and copy it into the mod, keeping a backup of the original:

```
dotnet run -c Release -- mortal "campaigns\jap_shogun\startpos.esf" "patched\startpos.esf" --faction krakafolk
```

## Commands

| Command | Description |
|---|---|
| `list <file> [filters]` | Shows faction, name and immortal status of every selected character. |
| `mortal <file> [out] [filters]` | Clears the immortal flag on the selected characters. Default output: `<name> - mortal.<ext>`. |
| `immortal <file> [out] [filters]` | Sets the immortal flag on the selected characters. Default output: `<name> - immortal.<ext>`. |
| `repair <file.save> [out]` | Removes AI leftovers of characters that no longer exist (see below). Default output: `<name> - repaired.save`. Writes nothing if there is nothing to repair. |

`list`, `mortal` and `immortal` work on both `.save` files and compressed `startpos.esf` files.

### Filters

Filters apply to `list`, `mortal` and `immortal`. Without filters, every character is selected.

| Filter | Selects |
|---|---|
| `--faction X` | characters whose faction key contains `X` |
| `--name X` | characters whose name contains `X` |
| `--not-faction X` | skips characters whose faction key contains `X` |
| `--not-name X` | skips characters whose name contains `X` |
| `--immortal` | only characters that are currently immortal |
| `--mortal` | only characters that are currently mortal |

- Text filters match part of a word and ignore case and accents, so `nazgul` matches *Nazgûl*.
- You can repeat a filter or give a comma-separated list: `--faction "mordor,lindon"`.
- A character must match at least one `--faction` value and at least one `--name` value (when given), and none of the `--not-…` values.
- The rebel faction is listed as `rebels`.
- Names are matched against the full localisation key, including its name-group prefix (for example `swedishGil-Galad`). `list` shows the name without the prefix, so preview a selection with `list` before writing.

### Diagnostics

These commands are for investigating saves. They don't write save files.

| Command | Description |
|---|---|
| `cai <file> [--all]` | Compares the campaign AI's character table with the characters that exist in the world, and reports orphaned AI entries. `--all` prints every entry. |
| `refs <file> <value>...` | Prints every integer field or integer-array element equal to one of the values, with its record path. Useful for tracing IDs. |
| `array <file> <offset> [from]` | Prints the elements of the integer array whose type byte is at `offset` (as shown by `dump` or `refs`). |
| `bools <file>` | Prints every bool directly in each `CHARACTER` (`cN`) and `CHARACTER_DETAILS` (`dN`) record, by child index. |
| `dump <file> <out.txt>` | Writes the whole decompressed campaign data as indented text. A full save gives a text file of about 200 MB. |

## How it works

### File format

Shogun 2 saves and startpos files are **ESF** files using the `0xABCA` encoding:

- **Records** are named, like `CHARACTER` or `FACTION`, but the fields inside them aren't, so every field is identified by its position.
- **Sizes and counts** are stored as variable-length integers. The game sometimes pads these with leading zero bytes, and the writer keeps each one's original width.
- **The campaign state** is LZMA-compressed inside a `COMPRESSED_DATA` record. The tool decompresses it, edits it and compresses it again. Output files are usually about 10% smaller than the game's, because the compression settings differ; the game reads them normally.

The `0xABCF` encoding, used by some Assembly Kit startpos files, can be read but not written.

### Where characters are

```
CAMPAIGN_SAVE_GAME > CAMPAIGN_ENV > CAMPAIGN_MODEL > WORLD
    > FACTION_ARRAY[] > FACTION > CHARACTER_ARRAY[] > CHARACTER
    > REBEL_FACTION   > FACTION > CHARACTER_ARRAY[] > CHARACTER
```

The rebels are in a separate `REBEL_FACTION` record. A character who defects keeps their original faction key in `CHARACTER_DETAILS`.

### The immortal flag

The immortal flag is the bool at **child index 29 of a `CHARACTER` (v23) record**. This was mapped using the Shogun 2 Assembly Kit, by comparing the `start_pos_characters` table with BOB's compiled startpos:

- In `tut_shogun`, the only row with `immortal=1` is the only compiled character with this bool set.
- In vanilla `jap_shogun`, no row is immortal, and the bool is clear on every character.
- In *Last Alliance* it is set for exactly the mod's named heroes (Gil-galad, Elrond, the Nazgûl, Durin, …).

`CHARACTER_DETAILS` child 17 is **`is_male`**, not immortal. It is 0 for exactly the `is_male=0` rows. Don't confuse the two.

The tool refuses to edit a `CHARACTER` record that isn't version 23, or whose child 29 isn't a bool.

### The corruption that `repair` fixes

When an immortal general's faction is destroyed, he and his army become rebels. If that army is then destroyed:

- the game removes the character from the world but leaves his entry in the campaign AI (`CAI_WORLD_CHARACTERS`), with its handles zeroed
- his AI component ID stays listed in the rebels' AI faction, a "primary generals" analysis, and several AI component lists

Characters who die normally are removed from all of these. Saves made after this point crash on load.

`repair` does the cleanup the game skipped:

1. It removes `CAI_WORLD_CHARACTERS` entries whose character no longer exists *and* whose handles are zeroed. Entries that still have live handles are reported and left alone.
2. It removes the orphan's component ID from ownership rows (`CAI_BDI_COMPONENT_BLOCK_OWNS`) and from the ID lists of `CAI_BDI_COMPONENT`, `CAI_FACTION`, `CAI_PRIMARY_FACTION_GENERALS_ANALYSIS` and `CAI_ANALYSER`. `CAI_ANALYSER`'s second array is a list of (key, ID) pairs, and pairs are removed as a unit.
3. It re-reads the result, confirms no orphans remain, and lists any reference to the removed IDs it didn't handle.

Before editing, `repair` checks that re-writing the unmodified data reproduces it byte for byte. It's been confirmed in-game on one corrupted *Last Alliance* save (Saphrax, `la_krakafolk_hvallfugl`).

To avoid the problem, make immortal characters of minor factions mortal, for example with `mortal --immortal --not-faction …`. The fix that doesn't depend on timing is clearing the flag before their faction dies.

## Limitations

- Only the `0xABCA` encoding can be written; `0xABCF` files can only be read.
- The immortal flag position is only mapped for `CHARACTER` v23 (Shogun 2 1.1.0). Other versions are refused rather than guessed.
- `repair` only handles the AI record types listed above. If it reports remaining references, the save needs more investigation.
- Editing a startpos only affects new campaigns, and existing saves keep their own flags.

## License

MIT, see [LICENSE](LICENSE).
