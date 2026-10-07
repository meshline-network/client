using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Meshline.Cli;

internal sealed record IpcRequest(string ProfileId, Invocation Invocation);
internal sealed record IpcResponse(string Kind, string? Line = null, int? ExitCode = null);

internal static class Ipc
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (bytes.Length > MaximumBytes) throw new CliException("ipc_size", "IPC frame exceeds 4 MiB. Reduce the query limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumBytes) throw new CliException("ipc_size", "Invalid IPC frame size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return Json.Read<T>(Encoding.UTF8.GetString(bytes));
    }
}

internal sealed class IpcWriter(Stream stream, string kind, SemaphoreSlim gate, CancellationToken token) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override async Task WriteLineAsync(string? value)
    {
        await gate.WaitAsync(token);
        try { await Ipc.WriteAsync(stream, new IpcResponse(kind, value), token); }
        finally { gate.Release(); }
    }
}
