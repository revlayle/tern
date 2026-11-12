using System.Text;
using System.Text.RegularExpressions;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

[CollectionDefinition("Skill", DisableParallelization = true)]
public class SkillCollection;

/// <summary>
/// M19 part 2: the Tern skill's examples (<c>.claude/skills/tern/*.md</c>) compile and the tool output it
/// shows is what the tools print, so the skill cannot drift from the compiler. In each file, in order:
/// <list type="bullet">
/// <item>a <c>```tern</c> block is written to the file its first line names (<c>// name.tern</c>), or
/// <c>example.tern</c> (a name may have a folder: <c>// alt/ship.tern</c>); with <c>fn main</c> or <c>export</c> it
/// must compile with no warnings, otherwise it is a module, which a program compiled after it (a block or a command) must import. A <c>```tern error</c> block must not compile, and the <c>```text</c> block after it
/// is what <c>tern name.tern</c> prints.</item>
/// <item>a <c>```console</c> block holds commands (<c>$ tern ...</c>) each followed by its output; a line
/// <c>...</c> stands for any number of lines.</item>
/// <item>a <c>```lst</c> block's lines appear one after another in the listing of the last program compiled.</item>
/// </list>
/// The commands run in a new folder per file. <c>TERN_UPDATE_SKILL=1</c> writes the outputs into the files.
/// </summary>
[Collection("Skill")]
public class SkillTests
{
    internal static string SkillDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, ".claude", "skills", "tern");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException(".claude/skills/tern not found");
        }
    }

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SkillDirectory, "*.md").Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(f));
        return data;
    }

    private sealed record Block(string Info, int Start, int End, List<string> Lines)
    {
        public string Language => Info.Split(' ', 2)[0];
    }

    /// <summary>The fenced blocks of a Markdown file; Start and End are the fence lines' indexes.</summary>
    private static List<Block> Blocks(string[] lines)
    {
        var blocks = new List<Block>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("```")) continue;
            string info = lines[i][3..].Trim();
            int end = i + 1;
            while (end < lines.Length && !lines[end].StartsWith("```")) end++;
            if (end == lines.Length) throw new InvalidDataException($"line {i + 1}: block is not closed");
            blocks.Add(new Block(info, i, end, lines[(i + 1)..end].ToList()));
            i = end;
        }
        return blocks;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_examples_compile_and_the_tools_print_what_the_skill_shows(string file)
    {
        string path = Path.Combine(SkillDirectory, file);
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        bool update = Environment.GetEnvironmentVariable("TERN_UPDATE_SKILL") == "1";
        var blocks = Blocks(lines);
        var replacements = new List<(Block Block, List<string> Lines)>();
        var problems = new List<string>();

        string work = Path.Combine(Path.GetTempPath(), "tern-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string saved = Environment.CurrentDirectory;
        Environment.CurrentDirectory = work;
        try
        {
            string? lastProgram = null;
            // Module blocks (no main, no export) not yet imported by a program that was compiled, with where they are.
            var modules = new Dictionary<string, string>();
            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                string where = $"{file}:{block.Start + 1}";
                if (block.Language == "tern")
                {
                    var m = Regex.Match(block.Lines.FirstOrDefault() ?? "", @"^// (\S+\.tern)\b");
                    string name = m.Success ? m.Groups[1].Value : "example.tern";
                    if (Path.GetDirectoryName(name) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
                    File.WriteAllText(name, string.Join("\n", block.Lines) + "\n");
                    string text = string.Join("\n", block.Lines);
                    bool program = Regex.IsMatch(text, @"^\s*fn main\(", RegexOptions.Multiline) || Regex.IsMatch(text, @"^\s*export\b", RegexOptions.Multiline);
                    if (!program && block.Info != "tern error") modules[Path.GetFullPath(name)] = where;
                    if (block.Info == "tern error")
                    {
                        var (code, output) = Tern(["tern", name]);
                        var next = b + 1 < blocks.Count ? blocks[b + 1] : null;
                        if (code == 0) problems.Add($"{where}: {name} compiles, but the block is marked 'tern error'");
                        else if (next is null || next.Language != "text") problems.Add($"{where}: a 'tern error' block needs a 'text' block after it with the error; it is:\n{string.Join("\n", output)}");
                        else Compare(next, output);
                        if (next?.Language == "text") b++;
                    }
                    else if (program)
                    {
                        // The skill's programs have no warnings (roadmap "Warnings").
                        var (code, output) = Tern(["tern", "--warnings-as-errors", name]);
                        if (code != 0) problems.Add($"{where}: {name} does not compile:\n{string.Join("\n", output)}");
                        else { lastProgram = name; Compiled(name); }
                    }
                }
                else if (block.Language == "console")
                {
                    var actual = new List<string>();
                    var updated = new List<string>();
                    bool differs = false;
                    List<string>? current = null;
                    foreach (var line in block.Lines)
                    {
                        if (line.StartsWith("$ "))
                        {
                            if (current is not null) Check(current);
                            current = [line];
                            continue;
                        }
                        if (current is null) { problems.Add($"{where}: a console block starts with a '$ ' command"); break; }
                        current.Add(line);
                    }
                    if (current is not null) Check(current);
                    if (differs)
                    {
                        if (update) replacements.Add((block, updated));
                        else problems.Add($"{where}: the output differs. It is:\n{string.Join("\n", actual)}");
                    }

                    void Check(List<string> command)
                    {
                        var args = Arguments(command[0][2..]);
                        actual.Add(command[0]);
                        updated.Add(command[0]);
                        if (args.Count == 0 || args[0] != "tern") { problems.Add($"{where}: '{command[0]}' is not a tern command"); return; }
                        var (code, output) = Tern(args);
                        actual.AddRange(output);
                        var expected = command.Skip(1).ToList();
                        if (Matches(expected, output)) updated.AddRange(expected);
                        else { differs = true; updated.AddRange(Updated(expected, output)); }
                        var input = args.Skip(1).FirstOrDefault(a => a.EndsWith(".tern"));
                        if (args.Count > 1 && args[1] is not ("run" or "bench") && input is not null) lastProgram = input;
                        if (code == 0) foreach (var f in args.Where(a => a.EndsWith(".tern") && File.Exists(a))) Compiled(f);
                    }
                }
                else if (block.Language == "lst")
                {
                    if (lastProgram is null) { problems.Add($"{where}: no program was compiled before this listing"); continue; }
                    var listing = File.ReadAllText(Path.ChangeExtension(lastProgram, ".lst")).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
                    var want = block.Lines.Select(l => l.TrimEnd()).ToList();
                    bool found = Enumerable.Range(0, Math.Max(0, listing.Count - want.Count + 1)).Any(i => listing.Skip(i).Take(want.Count).SequenceEqual(want));
                    if (!found) problems.Add($"{where}: these lines are not in {Path.ChangeExtension(lastProgram, ".lst")}, which is:\n{string.Join("\n", listing)}");
                }

                void Compare(Block expectedBlock, List<string> output)
                {
                    if (Matches(expectedBlock.Lines, output)) return;
                    if (update) replacements.Add((expectedBlock, Updated(expectedBlock.Lines, output)));
                    else problems.Add($"{file}:{expectedBlock.Start + 1}: the output differs. It is:\n{string.Join("\n", output)}");
                }
            }
            // Review M19 part 2, suggestion 6: a module example must be compiled through a program that imports it.
            foreach (var where in modules.Values)
                problems.Add($"{where}: this module is not imported by any program the skill compiles after it, so it is not checked");

            // A file was compiled: so were the modules of its folder that it imports, directly or not.
            void Compiled(string path)
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                foreach (Match i in Regex.Matches(File.ReadAllText(path), @"^\s*import\s+(\w+)", RegexOptions.Multiline))
                {
                    string module = Path.Combine(folder, i.Groups[1].Value + ".tern");
                    if (modules.Remove(module)) Compiled(module);
                }
            }
        }
        finally
        {
            Environment.CurrentDirectory = saved;
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        if (update && replacements.Count > 0)
        {
            var result = lines.ToList();
            foreach (var (block, newLines) in replacements.OrderByDescending(r => r.Block.Start))
            {
                result.RemoveRange(block.Start + 1, block.End - block.Start - 1);
                result.InsertRange(block.Start + 1, newLines);
            }
            File.WriteAllText(path, string.Join("\n", result));
        }
        Assert.True(problems.Count == 0, string.Join("\n\n", problems));
    }

    /// <summary>Runs a tern command line in the current directory: the exit code and what it printed.</summary>
    private static (int Code, List<string> Output) Tern(IReadOnlyList<string> args)
    {
        var output = new StringWriter();
        int code = Cli.Run(args.Skip(1).ToArray(), output, output);
        var lines = output.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        return (code, lines);
    }

    /// <summary>Splits a command line as a shell does for plain words and '...' or "..." quotes.</summary>
    internal static List<string> Arguments(string line)
    {
        var args = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '\'' or '"')
            {
                int close = line.IndexOf(c, i + 1);
                if (close < 0) throw new InvalidDataException($"unclosed quote in: {line}");
                word.Append(line, i + 1, close - i - 1);
                inWord = true;
                i = close;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) args.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else { word.Append(c); inWord = true; }
        }
        if (inWord) args.Add(word.ToString());
        return args;
    }

    /// <summary>Whether the output matches the expected lines, where a line "..." stands for any number of lines.</summary>
    internal static bool Matches(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var want = expected.Select(l => l.TrimEnd()).ToList();
        var got = actual.Select(l => l.TrimEnd()).ToList();
        return Match(0, 0);

        bool Match(int e, int a)
        {
            if (e == want.Count) return a == got.Count;
            if (want[e] == "...")
                for (int skip = a; skip <= got.Count; skip++)
                    if (Match(e + 1, skip)) return true;
            return a < got.Count && want[e] == got[a] && Match(e + 1, a + 1);
        }
    }

    /// <summary>
    /// The actual output for the file, keeping each "..." of the expected output: the lines before the first
    /// "..." and after the last keep their count.
    /// </summary>
    private static List<string> Updated(IReadOnlyList<string> expected, List<string> actual)
    {
        int first = expected.ToList().IndexOf("...");
        if (first < 0) return actual;
        int tail = expected.Count - 1 - expected.ToList().LastIndexOf("...");
        if (first + tail > actual.Count) return actual;
        return [.. actual.Take(first), "...", .. actual.Skip(actual.Count - tail)];
    }

    [Fact]
    public void The_checker_reads_quotes_and_skipped_lines()
    {
        Assert.Equal(["tern", "bench", "--code", "x = 1; y = $C0", "a b"], Arguments("tern bench --code 'x = 1; y = $C0' \"a b\""));
        Assert.True(Matches(["a", "...", "d"], ["a", "b", "c", "d"]));
        Assert.True(Matches(["a", "...", "b"], ["a", "b"]));
        Assert.False(Matches(["a", "...", "d"], ["a", "b", "c"]));
        Assert.False(Matches(["a", "b"], ["a", "b", "c"]));
    }
}
