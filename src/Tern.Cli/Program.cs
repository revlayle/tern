using Tern.Compiler;
using Tern.Compiler.Syntax;
using Tern.Compiler.Tools;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Usage: tern [--json] <file.tern> [-o <output.prg>]

        Compiles a Tern program for the Commodore 64. Writes:
          <output>.prg   the program
          <output>.lst   listing with addresses, bytes and cycles
          <output>.vs    VICE label file (load in the monitor with: ll "<output>.vs")
          <output>.lines bytes, cycles and addresses of each source line

        A program with 'export' lines and no main is a binary for another program to call;
        it also writes <name>_api.tern, the module a Tern program imports to call it.

          --json         print the result as JSON: ok, the files written, the report's
                         lines, and diagnostics (file, line, column, severity, message,
                         suggestion)
        """);
    return args.Length < 1 ? 1 : 0;
}

bool json = false;
string? input = null;
string? output = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--json") json = true;
    else if (args[i] == "-o" && i + 1 < args.Length) output = args[++i];
    else if (input is null && !args[i].StartsWith('-')) input = args[i];
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}
if (input is null)
{
    Console.Error.WriteLine("No source file given. Usage: tern [--json] <file.tern> [-o <output.prg>]");
    return 1;
}
output ??= Path.ChangeExtension(input, ".prg");

int Fail(Diagnostic d, string text)
{
    if (json) Console.WriteLine(Diagnostic.ToJson(false, [], null, [d]));
    else Console.Error.WriteLine(d.Line == 0 ? text : $"{d.File}:{d.Line}:{d.Column}: error: {text}");
    return 1;
}

string source;
try
{
    source = File.ReadAllText(input);
}
catch (IOException e)
{
    string text = $"Cannot read {input}: {e.Message}";
    return Fail(new Diagnostic(input, 0, 0, "error", text, null), text);
}

try
{
    var result = TernCompiler.Compile(source, sourcePath: input);
    var files = new List<string>();
    void Write(string path, Action<string> write)
    {
        write(path);
        files.Add(path);
    }
    Write(output, p => File.WriteAllBytes(p, result.Prg));
    Write(Path.ChangeExtension(output, ".lst"), p => File.WriteAllText(p, result.Listing));
    Write(Path.ChangeExtension(output, ".vs"), p => File.WriteAllText(p, result.ViceLabels));
    Write(Path.ChangeExtension(output, ".lines"), p => File.WriteAllText(p, LineTable.Format(LineTable.Rows(result, Path.GetFileName(input)))));
    string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
    foreach (var overlay in result.Overlays)
        Write(Path.Combine(directory, overlay.Name + ".prg"), p => File.WriteAllBytes(p, overlay.Prg));
    if (result.ApiModule is not null)
        Write(Path.Combine(directory, result.ApiModuleName + ".tern"), p => File.WriteAllText(p, result.ApiModule));

    if (json)
    {
        Console.WriteLine(Diagnostic.ToJson(true, files, result.Report, []));
        return 0;
    }
    foreach (var f in files.Skip(4)) Console.WriteLine($"Wrote {f}");
    Console.WriteLine($"Wrote {output}");
    Console.Write(result.Report);
    return 0;
}
catch (CompileError e)
{
    return Fail(Diagnostic.From(e, input), e.Description);
}
