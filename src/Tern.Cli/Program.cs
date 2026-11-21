using System.Text;
using Tern.Compiler.Tools;

// JSON is UTF-8 (RFC 8259): a message that quotes a non-ASCII character reaches the reader whole, not as the
// console code page's '?' (review R3, suggestion 2). UTF8Encoding(false): no byte order mark before the JSON.
if (args.Contains("--json")) Console.OutputEncoding = new UTF8Encoding(false);
return Cli.Run(args, Console.Out, Console.Error);
