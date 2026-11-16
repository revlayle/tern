namespace Tern.Compiler.Tools;

/// <summary>
/// A program <c>tern run</c> cannot run: a binary without main, or a cartridge whose mapper the run model
/// does not have. The command line reports its message as the user's problem; any other exception from a
/// run is an internal error.
/// </summary>
public sealed class RunError(string message) : Exception(message);
