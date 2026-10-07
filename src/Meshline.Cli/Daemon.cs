using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Cli;

internal static class Daemon
{
    static string Endpoint(ProfileContext context) => OperatingSystem.IsWindows() ? "meshline-" + context.Settings.Id : Path.Combine(context.DataDirectory, "daemon.sock");

    public static async Task<int> ExecuteAsync(Invocation i, ProfileContext context, Output output, CancellationToken token)
    {
        if (i.Command == "daemon run")
        {
            Vault? vault = null;
            if (i.Flag("unlock-stdin"))
            {
                if (!Console.IsInputRedirected) throw new CliException("private_pipe_required", "--unlock-stdin is reserved for the daemon start child pipe.", Exit.Credentials);
                var line = await Console.In.ReadLineAsync(token) ?? throw new CliException("credential_missing", "Child unlock pipe closed.", Exit.Credentials);
                var master = Convert.FromBase64String(line);
                try { vault = new Vault(master, context.KeyPurpose); }
                finally { CryptographicOperations.ZeroMemory(master); }
            }
            return await RunAsync(context, output, token, vault, i.Flag("unlock-stdin"));
        }
        if (i.Command == "daemon start") return await StartAsync(i, context, output, token);
        var forwarded = await TryForwardAsync(i, context, output, token);
        if (forwarded is { } exitCode) return exitCode;
        await output.ResultAsync(CommandResult.Success(new { running = false }));
        return Exit.Success;
    }

    public static async Task<int?> TryForwardAsync(Invocation i, ProfileContext context, Output output, CancellationToken token)
    {
        await using var stream = await ConnectAsync(context, token);
        if (stream is null) return null;
        // Resolve caller-relative input/output files before executing in the daemon's working directory.
        var values = new Dictionary<string, string>(i.Values);
        foreach (var key in new[] { "out", "draft-file", "invite-file", "avatar-file", "previous-state" })
            if (values.TryGetValue(key, out var path)) values[key] = Path.GetFullPath(path);
        await Ipc.WriteAsync(stream, new IpcRequest(context.Settings.Id, i with { Values = values }), token);
        try
        {
            while (true)
            {
                var response = await Ipc.ReadAsync<IpcResponse>(stream, token);
                if (response.Kind == "exit") return response.ExitCode ?? Exit.Failure;
                if (response.Kind is not ("stdout" or "stderr")) throw new CliException("ipc_protocol", "Unrecognized daemon response.");
                await output.ForwardAsync(response.Line ?? "", response.Kind == "stderr");
            }
        }
        catch (IOException)
        { throw new CliException("daemon_disconnected", "Daemon connection was interrupted. A write may have completed; inspect state before retrying.", Exit.Pending); }
    }

    static async Task<Stream?> ConnectAsync(ProfileContext context, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", Endpoint(context), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(300, token); return pipe; }
            catch (TimeoutException) { pipe.Dispose(); return null; }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint(context)), token); return new NetworkStream(socket, ownsSocket: true); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable || !File.Exists(Endpoint(context)))
        { socket.Dispose(); return null; }
        catch { socket.Dispose(); throw; }
    }

    static async Task<int> StartAsync(Invocation i, ProfileContext context, Output output, CancellationToken token)
    {
        await using (var existing = await ConnectAsync(context, token))
            if (existing is not null) { await output.ResultAsync(CommandResult.Success(new { running = true, alreadyRunning = true })); return Exit.Success; }
        using var vault = await Secrets.OpenAsync(context, token);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Daemon).Assembly.Location);
        foreach (var argument in new[] { "daemon", "run", "--config", context.ConfigPath, "--profile", context.Name, "--json", "--unlock-stdin" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new CliException("daemon_start", "Could not create the daemon process.");
        var master = vault.CopyMasterKey();
        try { await child.StandardInput.WriteLineAsync(Convert.ToBase64String(master)); child.StandardInput.Close(); }
        finally { CryptographicOperations.ZeroMemory(master); }
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync(token);
            if (ready is null || !Json.Read<CommandResult>(ready).Ok)
                throw new CliException("daemon_start", ready ?? "Daemon exited before becoming ready.", Exit.Failure);
            await output.ResultAsync(CommandResult.Success(new { running = true, processId = child.Id, diagnostics = Path.Combine(context.DataDirectory, "daemon.log") }));
            return Exit.Success;
        }
        catch
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
            throw;
        }
    }

    static async Task<int> RunAsync(ProfileContext context, Output startupOutput, CancellationToken token, Vault? vault, bool background)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        StreamWriter? log = null;
        var ready = false;
        try
        {
            var output = startupOutput;
            if (background)
            {
                var path = Path.Combine(context.DataDirectory, "daemon.log");
                var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                PrivateFiles.RestrictFile(path);
                log = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
                output = new Output(log, log, true);
            }
            await using var session = await RuntimeSession.OpenAsync(context, output, lifetime.Token, vault);
            await session.Client.StartAsync(lifetime.Token);
            using var commandGate = new SemaphoreSlim(1);
            var handlers = new List<Task>();
            Socket? listener = null;
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    var path = Endpoint(context);
                    if (Encoding.UTF8.GetByteCount(path) > 100) throw new CliException("ipc_path", "Data directory is too long for a Unix socket.", Exit.Configuration);
                    if (File.Exists(path)) File.Delete(path); // This profile's exclusive session lock is already held.
                    listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    listener.Bind(new UnixDomainSocketEndPoint(path));
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    listener.Listen(32);
                }
                while (!lifetime.IsCancellationRequested)
                {
                    Stream? connection = null;
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            var pipe = new NamedPipeServerStream(Endpoint(context), PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                            connection = pipe;
                            if (!ready) { await startupOutput.ResultAsync(CommandResult.Success(new { running = true, processId = Environment.ProcessId })); ready = true; }
                            await pipe.WaitForConnectionAsync(lifetime.Token);
                        }
                        else
                        {
                            if (!ready) { await startupOutput.ResultAsync(CommandResult.Success(new { running = true, processId = Environment.ProcessId })); ready = true; }
                            connection = new NetworkStream(await listener!.AcceptAsync(lifetime.Token), ownsSocket: true);
                        }
                        handlers.Add(HandleAsync(connection, context, session, commandGate, lifetime, output));
                        connection = null;
                        for (var n = handlers.Count - 1; n >= 0; n--)
                            if (handlers[n].IsCompleted) { await handlers[n]; handlers.RemoveAt(n); }
                    }
                    finally { if (connection is not null) await connection.DisposeAsync(); }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally
            {
                lifetime.Cancel(); listener?.Dispose();
                await Task.WhenAll(handlers);
                if (!OperatingSystem.IsWindows() && File.Exists(Endpoint(context))) File.Delete(Endpoint(context));
            }
            return Exit.Success;
        }
        catch (Exception e) when (background && ready)
        {
            var error = CliApplication.MapError(e);
            await log!.WriteLineAsync(JsonSerializer.Serialize(CommandResult.Failure(error), Json.Options));
            return error.ExitCode;
        }
        finally { vault?.Dispose(); if (log is not null) await log.DisposeAsync(); }
    }

    static async Task HandleAsync(Stream stream, ProfileContext context, RuntimeSession session, SemaphoreSlim commandGate, CancellationTokenSource lifetime, Output diagnostic)
    {
        await using (stream)
        using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        using (var operation = CancellationTokenSource.CreateLinkedTokenSource(canceled.Token))
        using (var writes = new SemaphoreSlim(1))
        {
            Task? disconnect = null;
            var received = false;
            var output = new Output(new IpcWriter(stream, "stdout", writes, canceled.Token), new IpcWriter(stream, "stderr", writes, canceled.Token), true);
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(canceled.Token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await Ipc.ReadAsync<IpcRequest>(stream, readDeadline.Token);
                received = true;
                var i = request.Invocation;
                output = new Output(new IpcWriter(stream, "stdout", writes, canceled.Token), new IpcWriter(stream, "stderr", writes, canceled.Token), i.Json);
                if (request.ProfileId != context.Settings.Id) throw new CliException("ipc_profile", "Daemon profile identity mismatch.", Exit.Configuration);
                if (i.TimeoutSeconds < 0 || i.TimeoutSeconds > 86400 || !double.IsFinite(i.TimeoutSeconds)) throw new CliException("invalid_timeout", "Invalid IPC timeout.", Exit.Usage);
                if (i.TimeoutSeconds > 0) operation.CancelAfter(TimeSpan.FromSeconds(i.TimeoutSeconds));
                disconnect = CancelOnDisconnectAsync(stream, canceled);
                var exitCode = Exit.Success;
                if (i.Command == "daemon status") await output.ResultAsync(CommandResult.Success(new { running = true, processId = Environment.ProcessId, account = session.Client.Options.AccountId }));
                else if (i.Command == "daemon stop")
                {
                    await output.ResultAsync(CommandResult.Success(new { stopping = true }));
                    await Ipc.WriteAsync(stream, new IpcResponse("exit", ExitCode: Exit.Success), canceled.Token);
                    lifetime.Cancel(); return;
                }
                else if (i.Command is "account establish" or "account recover") throw new CliException("stop_daemon_required", "Stop the daemon before establishing or recovering an account.", Exit.Busy);
                else if (!Commands.Catalog.Any(s => s.Path == i.Command) || i.Command.StartsWith("daemon ", StringComparison.Ordinal)) throw new CliException("ipc_command", "Unsupported daemon command.", Exit.Usage);
                else if (i.Command == "watch" || !Commands.NeedsBackground(i.Command) && i.Command is not ("account sync" or "groups sync" or "channels sync")) exitCode = await Commands.ExecuteSessionAsync(i, session, output, operation.Token);
                else
                {
                    await commandGate.WaitAsync(operation.Token);
                    try { exitCode = await Commands.ExecuteSessionAsync(i, session, output, operation.Token); }
                    finally { commandGate.Release(); }
                }
                await Ipc.WriteAsync(stream, new IpcResponse("exit", ExitCode: exitCode), canceled.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested && !canceled.IsCancellationRequested)
            {
                var error = new CliException("operation_timeout", "Daemon operation timed out; a submitted write may still complete.", Exit.Pending);
                try
                {
                    await output.ResultAsync(CommandResult.Failure(error));
                    await Ipc.WriteAsync(stream, new IpcResponse("exit", ExitCode: error.ExitCode), canceled.Token);
                }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException)
                { await diagnostic.DiagnosticAsync(error.Code, error.Message); }
            }
            catch (Exception e) when (e is IOException || e is OperationCanceledException && (canceled.IsCancellationRequested || !received))
            {
                if (received && !lifetime.IsCancellationRequested) await diagnostic.DiagnosticAsync("ipc_disconnected", "A local command connection ended or timed out; inspect state before retrying writes.");
            }
            catch (Exception e)
            {
                var error = e is OperationCanceledException && !canceled.IsCancellationRequested
                    ? new CliException("dependency_canceled", "A dependency canceled the operation before the daemon command deadline. Inspect state before retrying submitted writes.", Exit.Network)
                    : CliApplication.MapError(e);
                try
                {
                    await output.ResultAsync(CommandResult.Failure(error));
                    await Ipc.WriteAsync(stream, new IpcResponse("exit", ExitCode: error.ExitCode), canceled.Token);
                }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException)
                { await diagnostic.DiagnosticAsync(error.Code, error.Message); }
            }
            finally { canceled.Cancel(); if (disconnect is not null) await disconnect; }
        }
    }

    static async Task CancelOnDisconnectAsync(Stream stream, CancellationTokenSource canceled)
    {
        try { _ = await stream.ReadAsync(new byte[1], canceled.Token); canceled.Cancel(); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        catch (IOException) { canceled.Cancel(); }
    }
}
