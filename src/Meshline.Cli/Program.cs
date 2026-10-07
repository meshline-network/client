using Meshline.Cli;
using System.Text;

// Pipe clients exchange UTF-8 regardless of the Windows console code page.
// Wrap only redirected handles; do not change the shared terminal's code page.
if (Console.IsInputRedirected)
    Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false));
if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
if (Console.IsErrorRedirected)
    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
return await CliApplication.RunAsync(args, Console.Out, Console.Error);
