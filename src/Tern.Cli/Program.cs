using Tern.Compiler;
using Tern.Compiler.Syntax;
using Tern.Compiler.Tools;

const string Usage = """
    Usage:
      tern [--json] <file.tern> [-o <output.prg>]
      tern run <file.tern> [options]
      tern bench [options] <file.tern>... | --code <statements>...

    tern <file.tern> compiles a Tern program for the Commodore 64. Writes:
      <output>.prg   the program
      <output>.lst   listing with addresses, bytes and cycles
      <output>.vs    VICE label file (load in the monitor with: ll "<output>.vs")
      <output>.lines bytes, cycles and addresses of each source line

    A program with 'export' lines and no main is a binary for another program to call;
    it also writes <name>_api.tern, the module a Tern program imports to call it.

      --json           print the result as JSON: ok, the files written, the report's
                       lines, and diagnostics (file, line, column, severity, message,
                       suggestion)

    tern run <file.tern> compiles the program and runs it in the built-in emulator until
    main returns, then prints the cycles taken and the registers. Memory is plain RAM (no
    VIC-II, SID or CIA); CHROUT, GETIN and PLOT have stand-ins, other ROM calls stop the run.
    Only the program file is loaded: overlays are not, so a call into one stops at a brk.
      --cycles <n>     stop after n cycles (default 20000000)
      --irq <n>        raise an IRQ every n cycles, through the vector at $FFFE
      --dump <what>    show memory after the run: a variable (score, main.count), a
                       variable with a byte count (table:4), $C000-$C0FF or $C000:16;
                       may be given more than once
      --screen         show the screen at $0400 as text
      --profile        show the cycles of each function and each source line
      --json           print the result as JSON

    tern bench compiles and runs each variant (as tern run does) and compares them:
    program bytes, the measured function's bytes, its calls and cycles (with what it
    calls), and the memory chosen for its variables and the program's globals.
      <file.tern>      a variant: a whole program
      --code <stmts>   a variant: statements run as the body of main (';' or newlines
                       between statements); may be given more than once
      --decl <decls>   declarations before main for every --code variant (globals,
                       imports, functions); may be given more than once
      --fn <name>      measure this function instead of main
      --dump <what>    show a value after each run, to check the variants agree (as for
                       tern run)
      --cycles <n>, --irq <n>, --json  as for tern run
    """;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length < 1 ? 1 : 0;
}
return args[0] switch
{
    "run" => RunCommand(args[1..]),
    "bench" => BenchCommand(args[1..]),
    _ => CompileCommand(args),
};

// ---------- tern <file.tern> ----------

int CompileCommand(string[] arguments)
{
    bool json = false;
    string? input = null, output = null;
    for (int i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] == "--json") json = true;
        else if (arguments[i] == "-o" && i + 1 < arguments.Length) output = arguments[++i];
        else if (input is null && !arguments[i].StartsWith('-')) input = arguments[i];
        else return Usage1($"Unknown argument: {arguments[i]}");
    }
    if (input is null) return Usage1("No source file given");
    output ??= Path.ChangeExtension(input, ".prg");

    var result = CompileFile(input, json);
    if (result is null) return 1;
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

// ---------- tern run ----------

int RunCommand(string[] arguments)
{
    bool json = false, screen = false, profile = false;
    string? input = null;
    long cycles = new RunOptions().MaxCycles, irq = 0;
    var dumps = new List<string>();
    for (int i = 0; i < arguments.Length; i++)
    {
        string a = arguments[i];
        bool hasValue = i + 1 < arguments.Length;
        if (a == "--json") json = true;
        else if (a == "--screen") screen = true;
        else if (a == "--profile") profile = true;
        else if (a == "--dump" && hasValue) dumps.Add(arguments[++i]);
        else if (a == "--cycles" && hasValue && long.TryParse(arguments[i + 1], out cycles) && cycles > 0) i++;
        else if (a == "--irq" && hasValue && long.TryParse(arguments[i + 1], out irq) && irq > 0) i++;
        else if (input is null && !a.StartsWith('-')) input = a;
        else return Usage1($"Unknown or incomplete argument: {a}");
    }
    if (input is null) return Usage1("No source file given");

    var program = CompileFile(input, json);
    if (program is null) return 1;
    List<MemoryDump> parsed;
    try { parsed = dumps.Select(d => MemoryDump.Parse(d, program)).ToList(); }
    catch (FormatException e) { return Fail(json, new Diagnostic(input, 0, 0, "error", "--dump " + e.Message, null), "--dump " + e.Message); }
    RunResult run;
    try { run = Runner.Run(program, new RunOptions { MaxCycles = cycles, IrqEvery = irq }); }
    catch (InvalidOperationException e) { return Fail(json, new Diagnostic(input, 0, 0, "error", e.Message, null), e.Message); }
    var lines = profile ? LineTable.Rows(program, Path.GetFileName(input), run.Profile) : null;
    Console.Write(json ? RunReport.Json(run, parsed, screen, lines) + Environment.NewLine : RunReport.Text(run, parsed, screen, lines));
    return run.Returned ? 0 : 2;
}

// ---------- tern bench ----------

int BenchCommand(string[] arguments)
{
    bool json = false;
    string? function = null;
    long cycles = new RunOptions().MaxCycles, irq = 0;
    var declarations = new List<string>();
    var dumps = new List<string>();
    var variants = new List<(string? Path, string? Code)>();
    for (int i = 0; i < arguments.Length; i++)
    {
        string a = arguments[i];
        bool hasValue = i + 1 < arguments.Length;
        if (a == "--json") json = true;
        else if (a == "--code" && hasValue) variants.Add((null, arguments[++i]));
        else if (a == "--decl" && hasValue) declarations.Add(arguments[++i]);
        else if (a == "--fn" && hasValue) function = arguments[++i];
        else if (a == "--dump" && hasValue) dumps.Add(arguments[++i]);
        else if (a == "--cycles" && hasValue && long.TryParse(arguments[i + 1], out cycles) && cycles > 0) i++;
        else if (a == "--irq" && hasValue && long.TryParse(arguments[i + 1], out irq) && irq > 0) i++;
        else if (!a.StartsWith('-')) variants.Add((a, null));
        else return Usage1($"Unknown or incomplete argument: {a}");
    }
    if (variants.Count == 0) return Usage1("No variant given: name .tern files or give --code");

    var results = new List<BenchResult>();
    int snippets = 0;
    foreach (var (path, code) in variants)
    {
        BenchVariant variant;
        if (code is not null) variant = BenchVariant.Snippet($"code {++snippets}", declarations, code);
        else
        {
            try { variant = new BenchVariant(Path.GetFileName(path!), File.ReadAllText(path!), path); }
            catch (IOException e) { return Fail(json, new Diagnostic(path!, 0, 0, "error", $"Cannot read {path}: {e.Message}", null), $"Cannot read {path}: {e.Message}"); }
        }
        results.Add(Bench.Measure(variant, function, dumps, new RunOptions { MaxCycles = cycles, IrqEvery = irq }));
    }
    Console.Write(json ? Bench.Json(results) + Environment.NewLine : Bench.Text(results));
    return results.All(r => r.Error is null && r.Returned) ? 0 : 1;
}

// ---------- shared ----------

CompileResult? CompileFile(string input, bool json)
{
    string source;
    try
    {
        source = File.ReadAllText(input);
    }
    catch (IOException e)
    {
        string text = $"Cannot read {input}: {e.Message}";
        Fail(json, new Diagnostic(input, 0, 0, "error", text, null), text);
        return null;
    }
    try
    {
        return TernCompiler.Compile(source, sourcePath: input);
    }
    catch (CompileError e)
    {
        Fail(json, Diagnostic.From(e, input), e.Description);
        return null;
    }
}

static int Fail(bool json, Diagnostic d, string text)
{
    if (json) Console.WriteLine(Diagnostic.ToJson(false, [], null, [d]));
    else Console.Error.WriteLine(d.Line == 0 ? text : $"{d.File}:{d.Line}:{d.Column}: error: {text}");
    return 1;
}

static int Usage1(string problem)
{
    Console.Error.WriteLine($"{problem}. Run 'tern --help' for the options.");
    return 1;
}
