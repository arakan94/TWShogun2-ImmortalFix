using System.Diagnostics;
using System.Security.Cryptography;

namespace ShogunImmortal.Tests;

/// Runs the built tool as a process on synthetic saves in a temporary folder.
public sealed class CliTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "ShogunImmortalTests", Guid.NewGuid().ToString("N"));
    readonly string input;
    readonly byte[] inputHash;

    public CliTests()
    {
        Directory.CreateDirectory(dir);
        input = Path.Combine(dir, "Lindon.save");
        File.WriteAllBytes(input, Campaign.Save(Campaign.Build()));
        inputHash = SHA256.HashData(File.ReadAllBytes(input));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ShogunImmortal.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.Result);
    }

    void AssertInputUnchanged() => Assert.Equal(inputHash, SHA256.HashData(File.ReadAllBytes(input)));

    void AssertNoTempFiles() => Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories));

    static Dictionary<long, bool> FlagsOf(string save) =>
        Campaign.Flags(SaveGame.Open(EsfFile.Load(File.ReadAllBytes(save))).Inner);

    // ------------------------------------------------------------ list

    [Fact]
    public void List_shows_every_character()
    {
        var (code, output, _) = Run("list", "Lindon.save");
        Assert.Equal(0, code);
        Assert.Contains("6 characters, 4 immortal.", output);
        Assert.Contains("Gil-Galad", output);
        Assert.DoesNotContain("swedishGil-Galad", output);
        Assert.Contains("rebels", output);
    }

    [Theory]
    [InlineData("4 of 6 characters match the filters, 4 of them immortal.", "--immortal")]
    [InlineData("2 of 6 characters match the filters, 0 of them immortal.", "--mortal")]
    [InlineData("1 of 6 characters match the filters, 1 of them immortal.", "--name", "NAZGUL")]  // case and accents ignored
    [InlineData("2 of 6 characters match the filters, 1 of them immortal.", "--faction", "mordor")]
    [InlineData("4 of 6 characters match the filters, 3 of them immortal.", "--not-faction", "mordor")]
    [InlineData("3 of 6 characters match the filters, 2 of them immortal.", "--faction", "lindon,rebels", "--not-name", "elrond")]
    [InlineData("1 of 6 characters match the filters, 1 of them immortal.", "--faction", "rebels", "--immortal")]
    public void List_filters(string expected, params string[] filters)
    {
        var (code, output, _) = Run(["list", "Lindon.save", .. filters]);
        Assert.Equal(0, code);
        Assert.Contains(expected, output);
    }

    // ------------------------------------------------------------ mortal / immortal

    [Fact]
    public void Mortal_writes_the_default_output_and_leaves_the_input_alone()
    {
        var (code, output, _) = Run("mortal", "Lindon.save");
        Assert.Equal(0, code);
        Assert.Contains("Verified and wrote", output);
        Assert.All(FlagsOf(Path.Combine(dir, "Lindon - mortal.save")).Values, Assert.False);
        AssertInputUnchanged();
        AssertNoTempFiles();
    }

    [Fact]
    public void Mortal_with_exclusions_keeps_the_excluded_characters_immortal()
    {
        var (code, _, _) = Run("mortal", "Lindon.save", "out.save", "--not-faction", "mordor", "--not-name", "elrond");
        Assert.Equal(0, code);
        Assert.Equal(new Dictionary<long, bool>
        {
            [101] = false, [102] = true, [103] = false, [104] = true, [105] = false, [106] = false,
        }, FlagsOf(Path.Combine(dir, "out.save")));
    }

    [Fact]
    public void Immortal_sets_the_flag_on_the_selection()
    {
        var (code, _, _) = Run("immortal", "Lindon.save", "--faction", "mordor");
        Assert.Equal(0, code);
        var flags = FlagsOf(Path.Combine(dir, "Lindon - immortal.save"));
        Assert.True(flags[104]);
        Assert.True(flags[105]);
        Assert.False(flags[103]);
    }

    [Fact]
    public void Output_can_go_into_a_new_folder_and_replace_an_existing_file()
    {
        Assert.Equal(0, Run("mortal", "Lindon.save", Path.Combine("new", "folder", "out.save")).Code);
        Assert.Equal(0, Run("immortal", "Lindon.save", Path.Combine("new", "folder", "out.save")).Code);
        Assert.All(FlagsOf(Path.Combine(dir, "new", "folder", "out.save")).Values, Assert.True);
        AssertNoTempFiles();
    }

    [Fact]
    public void No_matching_characters_writes_nothing()
    {
        var (code, _, err) = Run("mortal", "Lindon.save", "--faction", "nobody");
        Assert.Equal(1, code);
        Assert.Contains("No characters match", err);
        Assert.False(File.Exists(Path.Combine(dir, "Lindon - mortal.save")));
    }

    // ------------------------------------------------------------ never overwrite the input

    public static TheoryData<string, string> InputSpellings()
    {
        var data = new TheoryData<string, string>();
        var spellings = new List<string> { "Lindon.save", Path.Combine("sub", "..", "Lindon.save") };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) spellings.Add("LINDON.SAVE");
        foreach (var cmd in new[] { "mortal", "immortal", "repair", "dump" })
            foreach (var s in spellings) data.Add(cmd, s);
        return data;
    }

    [Theory]
    [MemberData(nameof(InputSpellings))]
    public void Refuses_to_overwrite_the_input(string command, string output)
    {
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var (code, _, err) = Run(command, "Lindon.save", output);
        Assert.Equal(1, code);
        Assert.Contains("Refusing to overwrite the input file.", err);
        AssertInputUnchanged();
        AssertNoTempFiles();
    }

    [Fact]
    public void A_write_that_fails_reports_it_without_a_stack_trace()
    {
        if (!OperatingSystem.IsWindows()) return;   // elsewhere a read-only file can still be replaced
        string target = Path.Combine(dir, "readonly.save");
        File.WriteAllText(target, "x");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        var (code, _, err) = Run("mortal", "Lindon.save", "readonly.save");
        Assert.Equal(1, code);
        Assert.Contains("Could not write readonly.save", err);
        Assert.DoesNotContain("Unhandled exception", err);
        Assert.Equal("x", File.ReadAllText(target));
        AssertNoTempFiles();
    }

    // ------------------------------------------------------------ options

    [Theory]
    [InlineData("--all only applies to cai", "list", "Lindon.save", "--all")]
    [InlineData("--immortal and --mortal exclude each other", "list", "Lindon.save", "--immortal", "--mortal")]
    [InlineData("Unknown option --bogus", "list", "Lindon.save", "--bogus")]
    [InlineData("--faction needs a value", "list", "Lindon.save", "--faction")]
    public void Bad_options_are_rejected(string message, params string[] args)
    {
        var (code, _, err) = Run(args);
        Assert.Equal(1, code);
        Assert.Contains(message, err);
    }

    // ------------------------------------------------------------ files the tool cannot handle

    [Theory]
    [InlineData("list")]
    [InlineData("mortal")]
    public void Unsupported_character_version_gives_a_one_line_error(string command)
    {
        File.WriteAllBytes(Path.Combine(dir, "old.save"), Campaign.Save(Campaign.Build(
            factions: [("la_lindon", [new(101, "swedishGil-Galad", true, Version: 22)])])));
        var (code, _, err) = Run(command, "old.save");
        Assert.Equal(1, code);
        Assert.StartsWith("Error: CHARACTER record is v22", err);
        Assert.DoesNotContain("   at ", err);
        Assert.Empty(Directory.EnumerateFiles(dir, "old - *"));
    }

    [Fact]
    public void Diagnostics_still_work_on_unsupported_character_versions()
    {
        File.WriteAllBytes(Path.Combine(dir, "old.save"), Campaign.Save(Campaign.Build(
            factions: [("la_lindon", [new(101, "swedishGil-Galad", true, Version: 22)])])));
        var (code, output, _) = Run("bools", "old.save");
        Assert.Equal(0, code);
        Assert.Contains("c29=1", output);
    }

    [Theory]
    [InlineData("missing.save", "Error: Could not find file")]
    [InlineData("notasave.txt", "Error: Unsupported ESF magic")]
    public void Unreadable_input_gives_a_one_line_error(string file, string message)
    {
        File.WriteAllText(Path.Combine(dir, "notasave.txt"), "hello, this is not an ESF file");
        var (code, _, err) = Run("list", file);
        Assert.Equal(1, code);
        Assert.StartsWith(message, err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public void A_folder_as_input_gives_a_one_line_error()
    {
        Directory.CreateDirectory(Path.Combine(dir, "folder.save"));
        var (code, _, err) = Run("list", "folder.save");
        Assert.Equal(1, code);
        Assert.StartsWith("Error: ", err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public void An_input_locked_by_another_program_gives_a_one_line_error()
    {
        if (!OperatingSystem.IsWindows()) return;   // file locks are advisory elsewhere
        using (new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (code, _, err) = Run("list", "Lindon.save");
            Assert.Equal(1, code);
            Assert.StartsWith("Error: ", err);
            Assert.DoesNotContain("   at ", err);
        }
    }

    // ------------------------------------------------------------ repair and diagnostics

    [Fact]
    public void Repair_fixes_an_orphaned_ai_character()
    {
        File.WriteAllBytes(Path.Combine(dir, "corrupted.save"), Campaign.Save(Campaign.Build([Campaign.Saphrax])));
        var (code, output, _) = Run("repair", "corrupted.save");
        Assert.Equal(0, code);
        Assert.Contains("removed AI character entry", output);
        Assert.Contains("6 AI characters, 6 world characters, 0 orphans; 0 remaining references", output);

        var (_, cai, _) = Run("cai", "corrupted - repaired.save");
        Assert.Contains("0 AI entries without a world character", cai);
    }

    [Fact]
    public void Repair_writes_nothing_for_a_clean_save()
    {
        var (code, output, _) = Run("repair", "Lindon.save");
        Assert.Equal(0, code);
        Assert.Contains("Nothing to repair", output);
        Assert.False(File.Exists(Path.Combine(dir, "Lindon - repaired.save")));
    }

    [Fact]
    public void Cai_reports_orphans()
    {
        File.WriteAllBytes(Path.Combine(dir, "corrupted.save"), Campaign.Save(Campaign.Build([Campaign.Saphrax])));
        var (code, output, _) = Run("cai", "corrupted.save");
        Assert.Equal(0, code);
        Assert.Contains("ORPHAN component=1199", output);
        Assert.Contains("7 AI characters, 6 world characters, 1 AI entries without a world character.", output);
    }

    [Fact]
    public void Dump_writes_the_campaign_tree()
    {
        var (code, _, _) = Run("dump", "Lindon.save", "dump.txt");
        Assert.Equal(0, code);
        string text = File.ReadAllText(Path.Combine(dir, "dump.txt"));
        Assert.Contains("<CHARACTER v23>", text);
        Assert.Contains("<CAI_WORLD_CHARACTERS[] v0 count=6>", text);
        AssertNoTempFiles();
    }
}
