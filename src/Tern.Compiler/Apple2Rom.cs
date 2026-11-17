namespace Tern.Compiler;

/// <summary>
/// The Apple II's ROM routines and ProDOS's MLI entry, which the startup code calls (<see cref="ProDosSystemBuild"/>)
/// and <c>tern run</c> stands in for (<see cref="Tools.Apple2Machine"/>): one list of their addresses.
/// </summary>
internal static class Apple2Rom
{
    public const int Cout = 0xFDED, Home = 0xFC58, SetNorm = 0xFE84, Init = 0xFB2F, SetVid = 0xFE93, SetKbd = 0xFE89;
    /// <summary>ProDOS's machine language interface, in its global page.</summary>
    public const int Mli = 0xBF00;
}
