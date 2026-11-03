using Tern.Compiler;
using Tern.Compiler.Syntax;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Usage: tern <file.tern> [-o <output.prg>]

        Compiles a Tern program for the Commodore 64. Writes:
          <output>.prg   the program
          <output>.lst   listing with addresses, bytes and cycles
          <output>.vs    VICE label file (load in the monitor with: ll "<output>.vs")

        A program with 'export' lines and no main is a binary for another program to call;
        it also writes <name>_api.tern, the module a Tern program imports to call it.
        """);
    return args.Length < 1 ? 1 : 0;
}

string input = args[0];
string output = Path.ChangeExtension(input, ".prg");
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) output = args[++i];
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}

string source;
try
{
    source = File.ReadAllText(input);
}
catch (IOException e)
{
    Console.Error.WriteLine($"Cannot read {input}: {e.Message}");
    return 1;
}

try
{
    var result = TernCompiler.Compile(source, sourcePath: input);
    File.WriteAllBytes(output, result.Prg);
    File.WriteAllText(Path.ChangeExtension(output, ".lst"), result.Listing);
    File.WriteAllText(Path.ChangeExtension(output, ".vs"), result.ViceLabels);
    if (result.ApiModule is not null)
    {
        string api = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, result.ApiModuleName + ".tern");
        File.WriteAllText(api, result.ApiModule);
        Console.WriteLine($"Wrote {api}");
    }
    Console.WriteLine($"Wrote {output}");
    Console.Write(result.Report);
    return 0;
}
catch (CompileError e)
{
    Console.Error.WriteLine($"{e.Pos.File ?? input}:{e.Pos.Line}:{e.Pos.Column}: error: {e.Description}");
    return 1;
}
