ShogunImmortal
==============

A small command-line tool for Total War: Shogun 2 save games and startpos.esf
files. It can:

  - list characters and show which ones are immortal
  - set or clear the immortal flag for all characters, or a filtered selection
  - repair saves corrupted when an immortal character is destroyed

It was built for the Last Alliance mod, where immortal heroes whose faction is
wiped out can crash the game. It works on any Shogun 2 save or startpos that
uses the same file layout (see Limitations).

Source code, releases and the full README: see the project page on GitHub.
License: MIT.


GETTING STARTED
---------------

You don't need to install .NET or anything else.

1. Extract the zip anywhere, for example C:\Tools\ShogunImmortal.
2. Open a terminal (PowerShell or Command Prompt) in that folder. In File
   Explorer, type "powershell" in the address bar and press Enter.
3. Run a command, for example:

       .\ShogunImmortal.exe list "C:\path\to\Kingdom of Lindon.save" --immortal

The tool is command-line only, so double-clicking the exe just opens and
closes a window.

If Windows SmartScreen warns about an unrecognised app, choose
"More info" then "Run anyway". The exe isn't code-signed.

Usage:

    ShogunImmortal.exe <command> <file> [options]

Save games are normally in:

    %APPDATA%\The Creative Assembly\Shogun2\save_games

THE INPUT FILE IS NEVER MODIFIED. Commands that write produce a new file next
to the input, or at the output path you give, and refuse to overwrite the
input. Every output is read back and checked before it is written, and nothing
is written if the check fails. Still, keep backups of saves you care about.


COMMON TASKS
------------

List the immortal characters in a save:

    ShogunImmortal.exe list "Kingdom of Lindon.save" --immortal

Make every character mortal, writing "Kingdom of Lindon - mortal.save":

    ShogunImmortal.exe mortal "Kingdom of Lindon.save"

Make only some characters mortal. This keeps Mordor's and two named heroes'
immortality and clears everyone else's:

    ShogunImmortal.exe mortal "Kingdom of Lindon.save" --not-faction mordor --not-name "gil-galad,elrond"

Fix a save that crashes after an immortal character's army was destroyed:

    ShogunImmortal.exe repair "Kingdom of Lindon corrupted.save"

Patch a mod's startpos so new campaigns start without the flag. The game only
loads a file named startpos.esf, so write to that name in another folder and
copy it into the mod, keeping a backup of the original:

    ShogunImmortal.exe mortal "campaigns\jap_shogun\startpos.esf" "patched\startpos.esf" --faction krakafolk


COMMANDS
--------

  list <file> [filters]
      Shows faction, name and immortal status of every selected character.

  mortal <file> [out] [filters]
      Clears the immortal flag on the selected characters.
      Default output: <name> - mortal.<ext>

  immortal <file> [out] [filters]
      Sets the immortal flag on the selected characters.
      Default output: <name> - immortal.<ext>

  repair <file.save> [out]
      Removes AI leftovers of characters that no longer exist (see below).
      Default output: <name> - repaired.save
      Writes nothing if there is nothing to repair.

list, mortal and immortal work on both .save files and compressed startpos.esf
files.


FILTERS
-------

Filters apply to list, mortal and immortal. Without filters, every character
is selected.

  --faction X        characters whose faction key contains X
  --name X           characters whose name contains X
  --not-faction X    skips characters whose faction key contains X
  --not-name X       skips characters whose name contains X
  --immortal         only characters that are currently immortal
  --mortal           only characters that are currently mortal

  - Text filters match part of a word and ignore case and accents, so "nazgul"
    matches Nazgul with the circumflex.
  - You can repeat a filter or give a comma-separated list:
    --faction "mordor,lindon"
  - A character must match at least one --faction value and at least one
    --name value (when given), and none of the --not-... values.
  - The rebel faction is listed as "rebels".
  - Names are matched against the full localisation key, including its
    name-group prefix (for example "swedishGil-Galad"). "list" shows the name
    without the prefix, so preview a selection with "list" before writing.


DIAGNOSTICS
-----------

These commands are for investigating saves. They don't write save files.

  cai <file> [--all]
      Compares the campaign AI's character table with the characters that
      exist in the world, and reports orphaned AI entries. --all prints every
      entry.

  refs <file> <value>...
      Prints every integer field or integer-array element equal to one of the
      values, with its record path. Useful for tracing IDs.

  array <file> <offset> [from]
      Prints the elements of the integer array whose type byte is at <offset>
      (as shown by "dump" or "refs").

  bools <file>
      Prints every bool directly in each CHARACTER (cN) and CHARACTER_DETAILS
      (dN) record, by child index.

  dump <file> <out.txt>
      Writes the whole decompressed campaign data as indented text. A full
      save gives a text file of about 200 MB.


HOW IT WORKS
------------

File format

Shogun 2 saves and startpos files are ESF files using the 0xABCA encoding:

  - Records are named, like CHARACTER or FACTION, but the fields inside them
    aren't, so every field is identified by its position.
  - Sizes and counts are stored as variable-length integers. The game
    sometimes pads these with leading zero bytes, and the writer keeps each
    one's original width.
  - The campaign state is LZMA-compressed inside a COMPRESSED_DATA record. The
    tool decompresses it, edits it and compresses it again. Output files are
    usually about 10% smaller than the game's, because the compression
    settings differ; the game reads them normally.

The 0xABCF encoding, used by some Assembly Kit startpos files, can be read but
not written.

Where characters are

    CAMPAIGN_SAVE_GAME > CAMPAIGN_ENV > CAMPAIGN_MODEL > WORLD
        > FACTION_ARRAY[] > FACTION > CHARACTER_ARRAY[] > CHARACTER
        > REBEL_FACTION   > FACTION > CHARACTER_ARRAY[] > CHARACTER

The rebels are in a separate REBEL_FACTION record. A character who defects
keeps their original faction key in CHARACTER_DETAILS.

The immortal flag

The immortal flag is the bool at child index 29 of a CHARACTER (v23) record.
This was mapped using the Shogun 2 Assembly Kit, by comparing the
start_pos_characters table with BOB's compiled startpos:

  - In tut_shogun, the only row with immortal=1 is the only compiled character
    with this bool set.
  - In vanilla jap_shogun, no row is immortal, and the bool is clear on every
    character.
  - In Last Alliance it is set for exactly the mod's named heroes (Gil-galad,
    Elrond, the Nazgul, Durin, ...).

CHARACTER_DETAILS child 17 is is_male, not immortal. It is 0 for exactly the
is_male=0 rows. Don't confuse the two.

The tool refuses to edit a CHARACTER record that isn't version 23, or whose
child 29 isn't a bool.

The corruption that "repair" fixes

When an immortal general's faction is destroyed, he and his army become
rebels. If that army is then destroyed:

  - the game removes the character from the world but leaves his entry in the
    campaign AI (CAI_WORLD_CHARACTERS), with its handles zeroed
  - his AI component ID stays listed in the rebels' AI faction, a "primary
    generals" analysis, and several AI component lists

Characters who die normally are removed from all of these. Saves made after
this point crash on load.

"repair" does the cleanup the game skipped:

  1. It removes CAI_WORLD_CHARACTERS entries whose character no longer exists
     and whose handles are zeroed. Entries that still have live handles are
     reported and left alone.
  2. It removes the orphan's component ID from ownership rows
     (CAI_BDI_COMPONENT_BLOCK_OWNS) and from the ID lists of CAI_BDI_COMPONENT,
     CAI_FACTION, CAI_PRIMARY_FACTION_GENERALS_ANALYSIS and CAI_ANALYSER.
     CAI_ANALYSER's second array is a list of (key, ID) pairs, and pairs are
     removed as a unit.
  3. It re-reads the result, confirms no orphans remain, and lists any
     reference to the removed IDs it didn't handle.

Before editing, "repair" checks that re-writing the unmodified data reproduces
it byte for byte. It's been confirmed in-game on one corrupted Last Alliance
save (Saphrax, la_krakafolk_hvallfugl).

To avoid the problem, make immortal characters of minor factions mortal, for
example with "mortal --immortal --not-faction ...". The fix that doesn't
depend on timing is clearing the flag before their faction dies.


LIMITATIONS
-----------

  - Only the 0xABCA encoding can be written; 0xABCF files can only be read.
  - The immortal flag position is only mapped for CHARACTER v23 (Shogun 2
    1.1.0). Other versions are refused rather than guessed.
  - "repair" only handles the AI record types listed above. If it reports
    remaining references, the save needs more investigation.
  - Editing a startpos only affects new campaigns, and existing saves keep
    their own flags.
