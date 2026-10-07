using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Channels;

namespace Meshline.Cli;

internal static class Interactive
{
    internal static bool Supports(string command) => command != "interactive"
        && !command.StartsWith("daemon ", StringComparison.Ordinal)
        && !command.StartsWith("secrets ", StringComparison.Ordinal)
        && command is not ("account create" or "account import" or "account establish" or "account recover" or "account export");

    public static async Task<int> RunAsync(Invocation invocation, Output output, TextReader input,
        CancellationToken token, Action<Action?> setInterrupt)
    {
        RuntimeSession? session = null;
        try
        {
            await CliApplication.RunOperationAsync(async startup =>
            {
                var context = await Configuration.ResolveAsync(invocation.ConfigPath, invocation.Profile, startup);
                session = await RuntimeSession.OpenAsync(context, output, startup);
                await session.Client.StartAsync(startup);
                return Exit.Success;
            }, invocation.TimeoutSeconds, token, graceSeconds: 0);
            var defaults = invocation with { Profile = session!.Context.Name };
            return await RunLoopAsync(defaults, output, input,
                (i, ct) => Commands.ExecuteInteractiveAsync(i, session, output, ct), token, setInterrupt,
                showPrompt: !Console.IsInputRedirected && !invocation.Json);
        }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    internal static async Task<int> RunLoopAsync(Invocation defaults, Output output, TextReader input,
        Func<Invocation, CancellationToken, Task<int>> execute, CancellationToken token,
        Action<Action?> setInterrupt, bool showPrompt = false)
    {
        var interrupts = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationTokenSource? current = null;
        Task<int>? command = null;
        long sequence = 0;
        Task<string?>? read = null;
        var prompt = showPrompt && !defaults.Json;
        Task PromptAsync() => prompt ? output.PromptAsync(defaults.Profile!) : Task.CompletedTask;

        async Task CompleteAsync()
        {
            var exitCode = await command!;
            command = null;
            current!.Dispose(); current = null;
            await output.EventAsync("interactive.command.completed", new { sequence, exitCode });
        }

        try
        {
            setInterrupt(() => interrupts.Writer.TryWrite(true));
            await output.EventAsync("interactive.ready", new { profile = defaults.Profile, synchronization = "background-no-completion-barrier" });
            await PromptAsync();
            // Console.In's synchronized ReadLineAsync can block synchronously. Keep exactly one
            // background read, which never owns SDK resources. Do not wait for an uninterruptible
            // terminal/pipe read on shutdown; EOF or process exit releases it.
            read = ReadLineAsync(input);
            var interrupted = interrupts.Reader.WaitToReadAsync(lifetime.Token).AsTask();
            while (true)
            {
                var pending = command is null
                    ? new Task[] { read, interrupted, output.Failure }
                    : new Task[] { read, interrupted, output.Failure, command };
                await Task.WhenAny(pending).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (output.Failure.IsCompleted) throw new IOException("Interactive output channel failed.", await output.Failure);
                if (command?.IsCompleted == true) { await CompleteAsync(); await PromptAsync(); }
                if (interrupted.IsCompleted)
                {
                    while (interrupts.Reader.TryRead(out _)) { }
                    if (current is not null) current.Cancel();
                    else await PromptAsync();
                    interrupted = interrupts.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                }
                if (!read.IsCompleted) continue;
                var line = (await read)?.Trim();
                if (line is null || line == "exit") break;
                if (line == "cancel")
                {
                    if (current is not null) current.Cancel();
                    else await PromptAsync();
                }
                else if (line.Length == 0) { if (current is null) await PromptAsync(); }
                else if (command is not null)
                    await output.DiagnosticAsync("interactive_busy", "A command is running. Use cancel or exit; this input was not executed or queued.");
                else
                {
                    current = CancellationTokenSource.CreateLinkedTokenSource(token);
                    sequence++;
                    command = ExecuteLineAsync(line, defaults, output, execute, current.Token);
                }
                read = ReadLineAsync(input);
            }
            return Exit.Success;
        }
        finally
        {
            setInterrupt(null);
            lifetime.Cancel();
            try
            {
                if (command is not null)
                {
                    current!.Cancel();
                    await CompleteAsync();
                }
            }
            finally
            {
                current?.Dispose();
                // Observe a late stdin error after a signal/exit without starting another reader.
                if (read is not null) _ = read.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    static Task<string?> ReadLineAsync(TextReader input) => Task.Run(input.ReadLine);

    internal static async Task<int> ExecuteLineAsync(string line, Invocation defaults, Output output,
        Func<Invocation, CancellationToken, Task<int>> execute, CancellationToken token)
    {
        try
        {
            var args = CommandLineParser.SplitCommandLine(line).ToArray();
            if (args.FirstOrDefault() == "help") args = [.. args.Skip(1), "--help"];
            var parsed = CliParser.Parse(args, (invocation, ct) =>
                CliApplication.RunOperationAsync(t => execute(invocation, t), invocation.TimeoutSeconds, ct, graceSeconds: 0), defaults);
            if (parsed.Errors.Count > 0)
                throw new CliException("usage", string.Join(" ", parsed.Errors.Select(error => error.Message)), Exit.Usage);
            // The parser writes help/version text; command results go directly through Output.
            using var help = new StringWriter();
            var exitCode = await parsed.InvokeAsync(new InvocationConfiguration
            { Output = help, Error = help, EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null }, token);
            if (help.GetStringBuilder().Length > 0)
            {
                if (output.IsJson) await output.ResultAsync(CommandResult.Success(new { help = help.ToString() }));
                else await output.ForwardAsync(help.ToString().TrimEnd(), diagnostic: false);
            }
            return exitCode;
        }
        catch (Exception error) when (!output.Failure.IsCompleted)
        {
            var mapped = CliApplication.MapError(error);
            await output.ResultAsync(CommandResult.Failure(mapped));
            return mapped.ExitCode;
        }
    }
}
