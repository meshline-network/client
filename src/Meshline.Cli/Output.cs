using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Cli;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = Create(false);
    public static readonly JsonSerializerOptions Pretty = Create(true);

    static JsonSerializerOptions Create(bool indent) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indent,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(), new ProtocolJsonConverterFactory() }
    };

    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new CliException("invalid_json", "Expected a JSON object.", Exit.Usage);
}

internal static class Exit
{
    public const int Success = 0, Failure = 1, Usage = 2, Configuration = 3,
        Credentials = 4, Network = 5, Pending = 6, Busy = 7, Permission = 8, Canceled = 130;
}

internal sealed class CliException(string code, string message, int exitCode = Exit.Failure, object? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
    public object? Details { get; } = details;
}

internal sealed record ErrorInfo(string Code, string Message, object? Details = null);
internal sealed record CommandResult(int SchemaVersion, bool Ok, object? Data, ErrorInfo? Error)
{
    public static CommandResult Success(object? data) => new(1, true, data, null);
    public static CommandResult Failure(CliException error) => new(1, false, null, new(error.Code, error.Message, error.Details));
}

internal sealed class Output(TextWriter stdout, TextWriter stderr, bool json)
{
    readonly SemaphoreSlim gate = new(1);
    readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsJson { get; } = json;
    public Task<Exception> Failure => failure.Task;

    async Task WriteAsync(Func<Task> write)
    {
        await gate.WaitAsync();
        try { await write(); }
        catch (Exception error) { failure.TrySetResult(error); throw; }
        finally { gate.Release(); }
    }

    public Task ForwardAsync(string line, bool diagnostic) => WriteAsync(async () =>
    { var writer = diagnostic ? stderr : stdout; await writer.WriteLineAsync(line); await writer.FlushAsync(); });

    public Task ResultAsync(CommandResult result) => WriteAsync(async () =>
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(result, IsJson ? Json.Options : Json.Pretty));
            await stdout.FlushAsync();
        });

    public Task EventAsync(string type, object? data) => WriteAsync(async () =>
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, type, data }, Json.Options));
            await stdout.FlushAsync();
        });

    public Task DiagnosticAsync(string code, string message) => WriteAsync(async () =>
    { await stderr.WriteLineAsync(JsonSerializer.Serialize(new { code, message }, Json.Options)); await stderr.FlushAsync(); });

    public Task PromptAsync(string profile) => WriteAsync(async () =>
    { await stderr.WriteAsync($"meshline[{profile}]> "); await stderr.FlushAsync(); });
}
