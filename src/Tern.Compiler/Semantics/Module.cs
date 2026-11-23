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
    /// <summary>The <c>import</c> in the main file that loads this module (its arguments set the module's parameters), or null.</summary>
    public ImportDecl? ImportedBy { get; set; }
    /// <summary>The overlay or bank area the main file places this module in (<c>import name @ area</c>, <c>@ area[n]</c>), or null for a resident module.</summary>
    public string? OverlayArea => ImportedBy?.Area;
    /// <summary>Placed in a bank of a bank area (<c>@ area[n]</c>, spec 8.5): in ROM, so its variables are resident.</summary>
    public bool InBank => ImportedBy?.Bank is not null;
    /// <summary>The bank's number, once the checker has worked it out.</summary>
    public int? Bank => ImportedBy?.BankNumber;
    /// <summary>
    /// What is present as a whole in its area: an overlay module alone, or the modules of one bank
    /// ("levels[1]"). Two units of one area are never present together (spec 8.5); null for a resident module.
    /// </summary>
    public string? Unit => OverlayArea is null ? null : InBank ? Backend.CodePlace.BankUnit(OverlayArea, Bank) : Name;
    /// <summary>A module found in the compiler's library folders (spec 2.2): never warned about (spec 13).</summary>
    public bool IsLibrary { get; init; }
    /// <summary>Set by the checker once the file's top-level declarations are in <see cref="Scope"/>.</summary>
    public bool Declared { get; set; }

    /// <summary>The name a top-level declaration has outside this file: <c>text.put</c>. Names in the main file stay as written.</summary>
    public string Qualify(string name) => IsMain ? name : $"{Name}.{name}";
}

/// <summary>Loads the main file and every module it imports, directly or through other modules.</summary>
public static class ModuleLoader
{
    /// <returns>All files, each module before the files that import it; the main file is last.</returns>
    /// <param name="errors">Takes every error, and the loader goes on without a module it cannot load (spec 13). Null: the first error is thrown.</param>
    /// <param name="openFile">
    /// The text of a module open in an editor, by its full path, which is used in place of the file on disk;
    /// null for a file that is not open (a language server's unsaved buffers, M28). Null: every module is read from disk.
    /// </param>
    public static List<Module> Load(string source, string? sourcePath, Platform platform, string libraryDirectory, ErrorList? errors = null,
        Func<string, string?>? openFile = null)
    {
        var files = new SourceFiles(openFile);
        errors?.AddFile(sourcePath);
        var main = new Module
        {
            Name = "",
            Path = sourcePath,
            Directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath ?? "x")) ?? ".",
            Source = source,
            Syntax = Parser.Parse(source, sourcePath, errors),
        };
        // A main file in another platform's folder (spec 2.2): its imports would come from that platform's modules.
        if (sourcePath is not null && PlatformOfFolder(main.Directory) is { } other && other != platform.Name)
            throw new CompileError(new SourcePos(1, 1, sourcePath),
                $"This file is in the folder for the {other} platform ({main.Directory}); this program is compiled for {platform.Name}. Use --target {other} for it, or a main file outside {other}/");
        var loaded = new Dictionary<string, Module>();
        var order = new List<Module>();

        void Visit(Module m)
        {
            foreach (var import in m.Syntax.Imports)
            {
                try { Import(m, import); }
                catch (CompileError e) when (errors is not null)
                {
                    // Uses of the module add no errors of their own.
                    errors.Add(e);
                    m.Syntax.BrokenNames.Add((import.Name, import.Pos));
                }
            }
            order.Add(m);
        }

        void Import(Module m, ImportDecl import)
        {
            {
                if (import.Area is not null && !m.IsMain)
                    throw new CompileError(import.AreaPos,
                        $"Only the program's main file places modules in overlay areas, since the areas belong to the whole program. Import '{import.Name}' plainly here and write 'import {import.Name} @ {import.Area}' in the main file");
                if (import.Args.Count > 0 && !m.IsMain)
                    throw new CompileError(import.Args[0].Pos,
                        $"Only the program's main file can set the parameters of '{import.Name}', since a module is loaded once. Import it plainly here and set {import.Args[0].Name} in the main file");
                if (!loaded.TryGetValue(import.Name, out var module))
                {
                    // The main file has rules no module has (spec 2.2), so it is not a module too. Checked on
                    // the file the import would load: a library module of the same name comes first.
                    string? mainPath = sourcePath is null ? null : System.IO.Path.GetFullPath(sourcePath);
                    bool IsMainFile(string p) => mainPath is not null && string.Equals(p, mainPath, StringComparison.OrdinalIgnoreCase);
                    CompileError MainImported() => new(import.Pos, m.IsMain
                        ? $"'{import.Name}' is this file, the program's main file, which cannot be imported. Remove this import"
                        : $"'{import.Name}' is the program's main file, which cannot be imported. Move what {m.Name} needs from it into a module that both import");
                    string path;
                    try { path = Find(import, m, platform, libraryDirectory, IsMainFile, files); }
                    catch (CompileError) when (IsMainFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(m.Directory, import.Name + ".tern"))))
                    {
                        throw MainImported();   // the main file has not been saved yet
                    }
                    if (IsMainFile(path)) throw MainImported();
                    var text = files.Read(path);
                    errors?.AddFile(path);
                    module = new Module
                    {
                        Name = import.Name,
                        Path = path,
                        Directory = System.IO.Path.GetDirectoryName(path)!,
                        Source = text,
                        Syntax = Parser.Parse(text, path, errors),
                        IsLibrary = InLibrary(System.IO.Path.GetDirectoryName(path)!, libraryDirectory),
                    };
                    // Registered before its imports are visited, so modules may import each other.
                    loaded[import.Name] = module;
                    Visit(module);
                }
                if (m.IsMain) module.ImportedBy = import;
                m.Imports[import.Name] = module;
            }
        }

        Visit(main);
        return order;
    }

    /// <summary>
    /// The file for <c>import name</c>: the library's folder for the target platform, the library, then the
    /// program's folder for the target and the program's folder (spec 2.2). The program's folder is the
    /// importing file's, or the one above when that is a platform folder. A module only in another
    /// platform's folder, of the library or of the program, is an error.
    /// </summary>
    private static string Find(ImportDecl import, Module importer, Platform platform, string library, Func<string, bool> isMainFile, SourceFiles files)
    {
        string file = import.Name + ".tern";
        bool inLibrary = InLibrary(importer.Directory, library);
        string program = !inLibrary && PlatformOfFolder(importer.Directory) is not null
            ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(importer.Directory))! : importer.Directory;
        var candidates = new List<string> { System.IO.Path.Combine(library, platform.Name, file), System.IO.Path.Combine(library, file) };
        if (!inLibrary) candidates.Add(System.IO.Path.Combine(program, platform.Name, file));
        candidates.Add(System.IO.Path.Combine(program, file));
        for (int i = 0; i < candidates.Count; i++)
        {
            var path = candidates[i];
            if (!files.Exists(path)) continue;
            // File names ignore case on Windows. The module name must match the file exactly, so that
            // a module is loaded once and the program also compiles where case matters.
            var dir = System.IO.Path.GetDirectoryName(path)!;
            var actual = files.IsOpen(path) ? file : System.IO.Path.GetFileName(System.IO.Directory.GetFiles(dir, file).Single());
            if (actual != file)
                throw new CompileError(import.Pos,
                    $"The module file is named {actual}; write 'import {System.IO.Path.GetFileNameWithoutExtension(actual)}'");
            var full = System.IO.Path.GetFullPath(path);
            // A program's own module may not have a library module's name (spec 2.2), so the library
            // never silently hides it. A main file of that name is never imported, and is allowed.
            if (i < 2 && !inLibrary)
                foreach (var own in candidates.Skip(2).Select(System.IO.Path.GetFullPath))
                    if (files.Exists(own) && !isMainFile(own) && !string.Equals(own, full, StringComparison.OrdinalIgnoreCase))
                        throw new CompileError(import.Pos,
                            $"'{import.Name}' is both a library module ({full}) and your file {own}. Rename your file, e.g. to my_{file}, and import it by its new name");
            return full;
        }

        if (System.IO.Directory.Exists(library))
            foreach (var dir in System.IO.Directory.GetDirectories(library))
                if (files.Exists(System.IO.Path.Combine(dir, file)))
                    throw new CompileError(import.Pos,
                        $"'{import.Name}' is a module for the {System.IO.Path.GetFileName(dir)} platform; this program is compiled for {platform.Name}. Remove the import, or use a module for {platform.Name}");
        if (!inLibrary)
            foreach (var other in Platform.All.Where(p => p != platform))
                if (System.IO.Path.Combine(program, other.Name, file) is var there && files.Exists(there))
                    throw new CompileError(import.Pos,
                        $"'{import.Name}' is a module for the {other.Name} platform ({System.IO.Path.GetFullPath(there)}); this program is compiled for {platform.Name}. Add {platform.Name}/{file}, or remove the import");

        throw new CompileError(import.Pos,
            $"Cannot find module '{import.Name}': there is no {file} in the library ({library}) or in {program}. Check the spelling, or put {file} next to the importing file");
    }

    /// <summary>The platform a folder is named after (a platform folder, spec 2.2), or null.</summary>
    private static string? PlatformOfFolder(string folder) =>
        Platform.ByName(System.IO.Path.GetFileName(System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar)))?.Name;

    /// <summary>Module files: a file open in an editor, else the file on disk.</summary>
    private sealed class SourceFiles(Func<string, string?>? openFile)
    {
        public bool IsOpen(string path) => openFile?.Invoke(System.IO.Path.GetFullPath(path)) is not null;
        public bool Exists(string path) => IsOpen(path) || File.Exists(path);
        public string Read(string path) => openFile?.Invoke(System.IO.Path.GetFullPath(path)) ?? File.ReadAllText(path);
    }

    /// <summary>Whether a folder is the library or one of its platform folders.</summary>
    private static bool InLibrary(string folder, string library)
    {
        var lib = System.IO.Path.GetFullPath(library).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var dir = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return string.Equals(dir, lib, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetDirectoryName(dir), lib, StringComparison.OrdinalIgnoreCase);
    }
}
