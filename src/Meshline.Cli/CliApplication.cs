using System.CommandLine;
using System.CommandLine.Invocation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meshline.Cli;

internal sealed record Invocation(string Command, Dictionary<string, string> Values, string ConfigPath, string? Profile, bool Json, double TimeoutSeconds)
{
    public string Require(string name) => Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value
        : throw new CliException("missing_argument", $"Missing --{name}.", Exit.Usage);
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public bool Flag(string name) => Get(name) == "true";
}

internal sealed record CommandSpec(string Path, string Description, string[] Options, string[] Flags, string[] Arguments)
{
    public string[] RequiredOptions { get; init; } = [];
    public IReadOnlyDictionary<string, string> OptionDescriptions { get; init; } = new Dictionary<string, string>();
}

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var json = args.Any(argument => argument is "--json" or "--json=true");
        var output = new Output(stdout, stderr, json);
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); })
            : null;
        Action interrupt = cancellation.Cancel;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Volatile.Read(ref interrupt)(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var parsed = CliParser.Parse(args, async (invocation, token) =>
            {
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                if (invocation.Command == "interactive")
                    return await Interactive.RunAsync(invocation, output, Console.In, caller.Token,
                        handler => Volatile.Write(ref interrupt, handler ?? cancellation.Cancel));
                return await RunOperationAsync(ct => Commands.ExecuteAsync(invocation, output, ct), invocation.TimeoutSeconds, caller.Token);
            });

            if (parsed.Errors.Count > 0)
                throw new CliException("usage", string.Join(" ", parsed.Errors.Select(error => error.Message)), Exit.Usage);
            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false,
                // Interactive owns Ctrl+C routing. The parser's default handler cancels the
                // entire invocation and can return before session cleanup completes.
                ProcessTerminationTimeout = parsed.CommandResult.Command.Name == "interactive" ? null : TimeSpan.FromSeconds(2)
            }, cancellation.Token);
        }
        catch (Exception exception)
        {
            if (output.Failure.IsCompleted)
            {
                // stdout may be broken while stderr is still usable. A failed output channel
                // must terminate the session instead of being reported as a recoverable command.
                try { await stderr.WriteLineAsync(JsonSerializer.Serialize(new { code = "output_error", message = exception.Message }, Json.Options)); }
                catch (Exception writeError) when (writeError is IOException or ObjectDisposedException) { }
                return Exit.Failure;
            }
            var error = MapError(exception);
            await output.ResultAsync(CommandResult.Failure(error));
            return error.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunOperationAsync(Func<CancellationToken, Task<int>> action, double seconds, CancellationToken cancellationToken, double graceSeconds = 2)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Allow IPC to return its structured timeout result before closing the client transport.
        if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds + graceSeconds));
        try { return await action(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new CliException("operation_timeout", "The operation deadline elapsed. A submitted write may still complete; inspect its status before retrying.", Exit.Pending); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CliException("dependency_canceled", "A dependency canceled the operation before the command deadline. Inspect its state before retrying submitted writes.", Exit.Network); }
    }

    internal static CliException MapError(Exception error) => error switch
    {
        CliException known => known,
        Meshline.Transport.RelayException relay => new("relay_" + relay.Error.Code, relay.Error.Message,
            relay.Error.Code is "unauthorized" or "forbidden" ? Exit.Permission : Exit.Network, relay.Error.Data),
        TimeoutException timeout => new("request_timeout", timeout.Message + " A submitted write may still complete; inspect its status before retrying.", Exit.Pending,
            new { operation = timeout.Data["operation"] as string, timeoutSeconds = timeout.Data["timeoutSeconds"] as double? }),
        OperationCanceledException => new("canceled", "Operation canceled. Submitted operations can still require status reconciliation.", Exit.Canceled),
        System.Security.Cryptography.CryptographicException => new("credential_invalid", "The credential is incorrect, the protection purpose does not match, or protected data was modified.", Exit.Credentials),
        UnauthorizedAccessException => new("permission_denied", error.Message, Exit.Permission),
        HttpRequestException => new("network_error", error.Message, Exit.Network),
        JsonException => new("invalid_json", error.Message, Exit.Usage),
        ArgumentException or FormatException => new("invalid_argument", error.Message, Exit.Usage),
        IOException => new("storage_error", error.Message, Exit.Configuration),
        _ => new("operation_failed", error.Message)
    };
}
