using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>One source file: the program's main file, or a module loaded with <c>import</c>.</summary>
public sealed class Module
{
    /// <summary>The name used to import it; empty for the main file.</summary>
    public required string Name { get; init; }
    public bool IsMain => Name.Length == 0;
    /// <summary>The file's path, or null for source text given without one.</summary>
    public required string? Path { get; init; }
    /// <summary>Where <c>include_bytes</c> and imports of user modules look: the file's directory.</summary>
    public required string Directory { get; init; }
    public required string Source { get; init; }
    public required ProgramSyntax Syntax { get; init; }
    /// <summary>Modules this file imports, by name.</summary>
    public Dictionary<string, Module> Imports { get; } = new();
    /// <summary>Names declared at the top level of this file, without the module name.</summary>
    public Dictionary<string, Symbol> Scope { get; } = new();

    /// <summary>The name a top-level declaration has outside this file: <c>text.put</c>. Names in the main file stay as written.</summary>
    public string Qualify(string name) => IsMain ? name : $"{Name}.{name}";
}

/// <summary>Loads the main file and every module it imports, directly or through other modules.</summary>
public static class ModuleLoader
{
    /// <returns>All files, each module before the files that import it; the main file is last.</returns>
    public static List<Module> Load(string source, string? sourcePath, Platform platform, string libraryDirectory)
    {
        var main = new Module
        {
            Name = "",
            Path = sourcePath,
            Directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath ?? "x")) ?? ".",
            Source = source,
            Syntax = Parser.Parse(source, sourcePath),
        };
        var loaded = new Dictionary<string, Module>();
        var order = new List<Module>();

        void Visit(Module m)
        {
            foreach (var import in m.Syntax.Imports)
            {
                if (!loaded.TryGetValue(import.Name, out var module))
                {
                    var path = Find(import, m, platform, libraryDirectory);
                    var text = File.ReadAllText(path);
                    module = new Module
                    {
                        Name = import.Name,
                        Path = path,
                        Directory = System.IO.Path.GetDirectoryName(path)!,
                        Source = text,
                        Syntax = Parser.Parse(text, path),
                    };
                    // Registered before its imports are visited, so modules may import each other.
                    loaded[import.Name] = module;
                    Visit(module);
                }
                m.Imports[import.Name] = module;
            }
            order.Add(m);
        }

        Visit(main);
        return order;
    }

    /// <summary>
    /// The file for <c>import name</c>: the library's folder for the target platform, the library,
    /// then the importing file's folder. A module in another platform's folder is an error.
    /// </summary>
    private static string Find(ImportDecl import, Module importer, Platform platform, string library)
    {
        string file = import.Name + ".tern";
        string[] candidates =
        [
            System.IO.Path.Combine(library, platform.Name, file),
            System.IO.Path.Combine(library, file),
            System.IO.Path.Combine(importer.Directory, file),
        ];
        foreach (var path in candidates)
            if (File.Exists(path)) return System.IO.Path.GetFullPath(path);

        if (System.IO.Directory.Exists(library))
            foreach (var dir in System.IO.Directory.GetDirectories(library))
                if (File.Exists(System.IO.Path.Combine(dir, file)))
                    throw new CompileError(import.Pos,
                        $"'{import.Name}' is a module for the {System.IO.Path.GetFileName(dir)} platform; this program is compiled for {platform.Name}");

        throw new CompileError(import.Pos,
            $"Cannot find module '{import.Name}': there is no {file} in the library ({library}) or in {importer.Directory}");
    }
}
