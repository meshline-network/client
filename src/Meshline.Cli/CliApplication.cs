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
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var root = new RootCommand("Meshline reference client. Account, contacts, conversations, groups and channels.");
            var config = new Option<string>("--config") { Description = "Application configuration JSON path; defaults to config.json next to the application.", Recursive = true, DefaultValueFactory = _ => Configuration.DefaultPath };
            var profile = new Option<string>("--profile") { Description = "Local account profile; defaults to the application's defaultProfile. For account create/import, names a new profile and must not already exist.", Recursive = true };
            var jsonOption = new Option<bool>("--json") { Description = "Write structured JSON results (NDJSON for watch).", Recursive = true };
            var timeout = new Option<double>("--timeout") { Description = "Operation deadline in seconds; 0 disables it (watch defaults to no deadline).", Recursive = true, DefaultValueFactory = _ => 60 };
            root.Options.Add(config); root.Options.Add(profile); root.Options.Add(jsonOption); root.Options.Add(timeout);
            var nodes = new Dictionary<string, Command> { [""] = root };
            foreach (var spec in Commands.Catalog)
            {
                var parts = spec.Path.Split(' ');
                var path = "";
                var node = root as Command;
                foreach (var part in parts)
                {
                    var parent = node;
                    path = path.Length == 0 ? part : path + " " + part;
                    if (!nodes.TryGetValue(path, out node))
                    {
                        node = new Command(part, path == spec.Path ? spec.Description : $"Manage {part}.");
                        nodes[path] = node;
                        parent.Subcommands.Add(node);
                    }
                }
                var options = spec.Options.ToDictionary(name => name, name => new Option<string>("--" + name)
                {
                    Description = spec.OptionDescriptions.GetValueOrDefault(name) ?? name.Replace('-', ' '),
                    Required = spec.RequiredOptions.Contains(name)
                });
                var flags = spec.Flags.ToDictionary(name => name, name => new Option<bool>("--" + name) { Description = spec.OptionDescriptions.GetValueOrDefault(name) ?? name.Replace('-', ' ') });
                var arguments = spec.Arguments.ToDictionary(name => name, name => new Argument<string>(name));
                foreach (var option in options.Values) node.Options.Add(option);
                foreach (var flag in flags.Values) node.Options.Add(flag);
                foreach (var argument in arguments.Values) node.Arguments.Add(argument);
                node.SetAction(async (parsed, token) =>
                {
                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (name, option) in options) if (parsed.GetValue(option) is { } value) values[name] = value;
                    foreach (var (name, flag) in flags) if (parsed.GetValue(flag)) values[name] = "true";
                    foreach (var (name, argument) in arguments) if (parsed.GetValue(argument) is { } value) values[name] = value;
                    var seconds = parsed.GetValue(timeout);
                    var explicitTimeout = args.Any(a => a == "--timeout" || a.StartsWith("--timeout=", StringComparison.Ordinal));
                    if (!explicitTimeout && spec.Path is "watch" or "daemon run") seconds = 0;
                    if (seconds < 0 || !double.IsFinite(seconds) || seconds > 86400) throw new CliException("invalid_timeout", "Timeout must be between 0 and 86400 seconds.", Exit.Usage);
                    var invocation = new Invocation(spec.Path, values, Path.GetFullPath(parsed.GetValue(config)!), parsed.GetValue(profile)!, parsed.GetValue(jsonOption), seconds);
                    using var caller = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                    return await RunOperationAsync(ct => Commands.ExecuteAsync(invocation, output, ct), seconds, caller.Token);
                });
            }
            var parsed = root.Parse(args);
            if (parsed.Errors.Count > 0)
                throw new CliException("usage", string.Join(" ", parsed.Errors.Select(error => error.Message)), Exit.Usage);
            return await parsed.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false }, cancellation.Token);
        }
        catch (Exception exception)
        {
            var error = MapError(exception);
            await output.ResultAsync(CommandResult.Failure(error));
            return error.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunOperationAsync(Func<CancellationToken, Task<int>> action, double seconds, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Allow IPC to return its structured timeout result before closing the client transport.
        if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 2));
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
