namespace Tern.Compiler.Tests;

/// <summary>
/// A test class whose tests write files: a new temporary folder for each test (xUnit makes a new instance per
/// test), deleted after it (review code-quality-tests 7: one copy instead of one per class).
/// </summary>
public abstract class TempFolderTest : IDisposable
{
    protected readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern-");

    public void Dispose()
    {
        _dir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }
}
