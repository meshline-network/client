using Meshline.Cli;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Cli.Tests;

public sealed class InteractiveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static Invocation Defaults => new("interactive", [], Path.Combine(Path.GetTempPath(), "config.json"), "agent", true, 60);

    sealed class Input : TextReader
    {
        readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        int readers;
        public int MaximumReaders { get; private set; }
        public void Send(string line) => Assert.True(lines.Writer.TryWrite(line));
        public void End() => lines.Writer.TryComplete();
        public override string? ReadLine()
        {
            var count = Interlocked.Increment(ref readers);
            MaximumReaders = Math.Max(MaximumReaders, count);
            try
            {
                while (lines.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    if (lines.Reader.TryRead(out var line)) return line;
                return null;
            }
            finally { Interlocked.Decrement(ref readers); }
        }
        protected override void Dispose(bool disposing) { End(); base.Dispose(disposing); }
    }

    sealed class Lines : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<string> Written { get; } = new();
        public bool Broken { get; set; }
        public override Task WriteLineAsync(string? value)
        {
            if (Broken) throw new IOException("Test output closed.");
            Written.Enqueue(value!);
            Assert.True(lines.Writer.TryWrite(value!));
            return Task.CompletedTask;
        }
        public async Task<JsonElement> ReadAsync()
            => JsonSerializer.Deserialize<JsonElement>(await lines.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        public async Task<JsonElement> CompletedAsync(int sequence, int exitCode)
        {
            var completed = await ReadAsync();
            Assert.Equal("interactive.command.completed", completed.GetProperty("type").GetString());
            Assert.Equal(sequence, completed.GetProperty("data").GetProperty("sequence").GetInt32());
            Assert.Equal(exitCode, completed.GetProperty("data").GetProperty("exitCode").GetInt32());
            return completed;
        }
    }

    [Fact]
    public async Task Shared_parser_handles_quotes_help_errors_and_fixed_profile_without_ending_the_session()
    {
        using var input = new Input();
        using var stdout = new Lines();
        using var stderr = new Lines();
        var output = new Output(stdout, stderr, true);
        var invocations = new List<Invocation>();
        var task = Interactive.RunLoopAsync(Defaults, output, input, async (i, ct) =>
        {
            invocations.Add(i);
            await output.ResultAsync(CommandResult.Success(new { text = i.Get("text") }));
            return 0;
        }, Token, _ => { });
        Assert.Equal("interactive.ready", (await stdout.ReadAsync()).GetProperty("type").GetString());
        var sequence = 0;
        input.Send("messages send --to recipient --text \"你好 hello $HOME > file\"");
        Assert.Equal("你好 hello $HOME > file", (await stdout.ReadAsync()).GetProperty("data").GetProperty("text").GetString());
        await stdout.CompletedAsync(++sequence, 0);
        Assert.Equal("agent", invocations[0].Profile);
        Assert.Equal(Defaults.ConfigPath, invocations[0].ConfigPath);

        foreach (var command in new[] { "help", "help messages send", "messages send --help" })
        {
            input.Send(command);
            Assert.Contains("--", (await stdout.ReadAsync()).GetProperty("data").GetProperty("help").GetString());
            await stdout.CompletedAsync(++sequence, 0);
        }
        foreach (var command in new[] { "nonsense", "status --profile other", "status --config other.json", "status --json=false",
            "account create", "account import", "account establish", "account recover", "account export", "secrets generate-key", "secrets rewrap", "daemon start", "interactive" })
        {
            input.Send(command);
            Assert.Equal("usage", (await stdout.ReadAsync()).GetProperty("error").GetProperty("code").GetString());
            await stdout.CompletedAsync(++sequence, Exit.Usage);
        }
        input.Send("status");
        Assert.True((await stdout.ReadAsync()).GetProperty("ok").GetBoolean());
        await stdout.CompletedAsync(++sequence, 0);
        Assert.Equal(2, invocations.Count);
        input.Send("exit");
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Empty(stderr.Written);
        Assert.Equal(1, input.MaximumReaders);
    }

    [Fact]
    public async Task Watch_cancel_busy_empty_lines_interrupt_and_deadline_preserve_the_session()
    {
        using var input = new Input();
        using var stdout = new Lines();
        using var stderr = new Lines();
        var output = new Output(stdout, stderr, true);
        Action? interrupt = null;
        var cleaned = 0;
        var calls = 0;
        var task = Interactive.RunLoopAsync(Defaults, output, input, async (i, ct) =>
        {
            calls++;
            if (i.Command == "watch" || i.TimeoutSeconds < 1)
            {
                await output.EventAsync("test.running", new { timeout = i.TimeoutSeconds });
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { Interlocked.Increment(ref cleaned); }
            }
            await output.ResultAsync(CommandResult.Success(new { calls }));
            return 0;
        }, Token, handler => interrupt = handler);
        await stdout.ReadAsync();
        input.Send("watch");
        Assert.Equal(0, (await stdout.ReadAsync()).GetProperty("data").GetProperty("timeout").GetDouble());
        input.Send(""); input.Send("status");
        Assert.Equal("interactive_busy", (await stderr.ReadAsync()).GetProperty("code").GetString());
        Assert.Equal(0, cleaned);
        input.Send("cancel");
        Assert.Equal("canceled", (await stdout.ReadAsync()).GetProperty("error").GetProperty("code").GetString());
        await stdout.CompletedAsync(1, Exit.Canceled);
        Assert.Equal(1, cleaned);
        input.Send("watch");
        await stdout.ReadAsync();
        interrupt!();
        Assert.Equal("canceled", (await stdout.ReadAsync()).GetProperty("error").GetProperty("code").GetString());
        await stdout.CompletedAsync(2, Exit.Canceled);
        input.Send("status --timeout 0.05");
        await stdout.ReadAsync();
        Assert.Equal("operation_timeout", (await stdout.ReadAsync()).GetProperty("error").GetProperty("code").GetString());
        await stdout.CompletedAsync(3, Exit.Pending);
        input.Send("cancel"); input.Send("");
        input.Send("status");
        Assert.Equal(4, (await stdout.ReadAsync()).GetProperty("data").GetProperty("calls").GetInt32());
        await stdout.CompletedAsync(4, 0);
        input.Send("exit");
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Null(interrupt);
        Assert.Equal(1, input.MaximumReaders);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("eof")]
    [InlineData("signal")]
    public async Task Shutdown_cancels_the_active_command_and_waits_for_cleanup(string mode)
    {
        using var input = new Input();
        using var stdout = new Lines();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var output = new Output(stdout, TextWriter.Null, true);
        var cleaned = false;
        var task = Interactive.RunLoopAsync(Defaults, output, input, async (i, ct) =>
        {
            await output.EventAsync("test.running", null);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cleaned = true; }
            return 0;
        }, lifetime.Token, _ => { });
        await stdout.ReadAsync(); input.Send("watch"); await stdout.ReadAsync();
        if (mode == "exit") input.Send("exit");
        else if (mode == "eof") input.End();
        else lifetime.Cancel();
        if (mode == "signal") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        else Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(cleaned);
        Assert.Equal("canceled", (await stdout.ReadAsync()).GetProperty("error").GetProperty("code").GetString());
        await stdout.CompletedAsync(1, Exit.Canceled);
    }

    [Fact]
    public async Task Broken_output_ends_the_session_instead_of_becoming_a_recoverable_command_error()
    {
        using var input = new Input();
        using var stdout = new Lines();
        var output = new Output(stdout, TextWriter.Null, true);
        var task = Interactive.RunLoopAsync(Defaults, output, input, async (i, ct) =>
        { await output.ResultAsync(CommandResult.Success(null)); return 0; }, Token, _ => { });
        await stdout.ReadAsync(); stdout.Broken = true;
        input.Send("status");
        await Assert.ThrowsAsync<IOException>(() => task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(output.Failure.IsCompleted);
    }

    sealed class BrokenInput : TextReader
    {
        public override string? ReadLine() => throw new IOException("Test input closed unexpectedly.");
    }

    [Fact]
    public async Task Input_failure_terminates_the_loop_instead_of_spinning_or_treating_it_as_EOF()
    {
        using var input = new BrokenInput();
        using var stdout = new Lines();
        Action? interrupt = null;
        var task = Interactive.RunLoopAsync(Defaults, new(stdout, TextWriter.Null, true), input,
            (_, _) => throw new InvalidOperationException("No command should execute."), Token, handler => interrupt = handler);
        await Assert.ThrowsAsync<IOException>(() => task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Null(interrupt);
        Assert.Single(stdout.Written);
    }

    [Fact]
    public async Task Entry_timeout_is_inherited_and_pending_send_details_are_preserved()
    {
        using var stdout = new StringWriter();
        var output = new Output(stdout, TextWriter.Null, true);
        var defaults = Defaults with { TimeoutSeconds = 0.05 };
        Assert.Equal(Exit.Pending, await Interactive.ExecuteLineAsync("status", defaults, output, async (i, ct) =>
        {
            Assert.Equal(0.05, i.TimeoutSeconds);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }, Token));
        Assert.Contains("operation_timeout", stdout.ToString());
        stdout.GetStringBuilder().Clear();
        Assert.Equal(Exit.Pending, await Interactive.ExecuteLineAsync("messages send --to peer --text hello", defaults, output,
            (_, _) => throw new CliException("message_pending", "Already submitted.", Exit.Pending, new { messageId = "retained-id" }), Token));
        var error = JsonSerializer.Deserialize<JsonElement>(stdout.ToString()).GetProperty("error");
        Assert.Equal("message_pending", error.GetProperty("code").GetString());
        Assert.Equal("retained-id", error.GetProperty("details").GetProperty("messageId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_prompts_use_stderr_and_JSON_suppresses_them(bool json)
    {
        using var input = new Input();
        using var stdout = new Lines();
        using var stderr = new StringWriter();
        var task = Interactive.RunLoopAsync(Defaults with { Json = json }, new(stdout, stderr, json), input,
            (_, _) => throw new InvalidOperationException("No command should execute."), Token, _ => { }, showPrompt: true);
        await stdout.ReadAsync();
        input.Send("exit");
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Single(stdout.Written);
        Assert.Equal(json ? "" : "meshline[agent]> ", stderr.ToString());
    }

    [Fact]
    public async Task Commands_reuse_the_unlocked_session_and_never_attempt_daemon_IPC()
    {
        using var fixture = new TemporaryProfile();
        var context = fixture.Context;
        await Secrets.GenerateKeyFileAsync(context.Settings.Protection.KeyFile!, Token);
        context.Settings.ProtectedKey = await Secrets.CreateAsync(context, Token);
        using (var vault = await Secrets.OpenAsync(context, Token)) await Identity.CreateAsync(context, vault, Token);
        await Configuration.SaveProfileAsync(context, true, Token);
        using var input = new Input();
        using var stdout = new Lines();
        var output = new Output(stdout, TextWriter.Null, true);
        await using (var session = await RuntimeSession.OpenAsync(context, output, Token))
        {
            File.Delete(context.Settings.Protection.KeyFile!);
            var task = Interactive.RunLoopAsync(Defaults with { ConfigPath = context.ConfigPath, Profile = context.Name }, output, input,
                (i, ct) => Commands.ExecuteInteractiveAsync(i, session, output, ct), Token, _ => { });
            await stdout.ReadAsync();
            var sequence = 0;
            foreach (var command in new[] { "status", "conversations list", "account show", "contacts list", "watch" })
            {
                input.Send(command);
                var response = await stdout.ReadAsync();
                if (command == "watch")
                {
                    Assert.Equal("watch.ready", response.GetProperty("type").GetString());
                    input.Send("cancel");
                    await stdout.ReadAsync();
                    await stdout.CompletedAsync(++sequence, Exit.Canceled);
                }
                else { Assert.True(response.GetProperty("ok").GetBoolean()); await stdout.CompletedAsync(++sequence, 0); }
                Assert.Equal("profile_busy", Assert.Throws<CliException>(() => FileLock.Acquire(context.LockPath)).Code);
                Assert.False(File.Exists(Path.Combine(context.DataDirectory, "daemon.sock")));
            }
            input.Send("exit");
            Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
        using var reopened = FileLock.Acquire(context.LockPath);
    }

    [Fact]
    public async Task Startup_failure_releases_the_session_lock_and_busy_profile_is_explicit()
    {
        using var fixture = new TemporaryProfile();
        using var stdout = new StringWriter();
        var args = new[] { "interactive", "--config", fixture.Context.ConfigPath, "--profile", fixture.Context.Name, "--json", "--timeout", "0.1" };
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, Token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, Token);
        using (var vault = await Secrets.OpenAsync(fixture.Context, Token)) await Identity.CreateAsync(fixture.Context, vault, Token);
        await Configuration.SaveProfileAsync(fixture.Context, true, Token);
        using (FileLock.Acquire(fixture.Context.LockPath))
        {
            Assert.Equal(Exit.Busy, await CliApplication.RunAsync(args, stdout, TextWriter.Null));
            Assert.Contains("profile_busy", stdout.ToString());
        }
        stdout.GetStringBuilder().Clear();
        Assert.NotEqual(0, await CliApplication.RunAsync(args, stdout, TextWriter.Null));
        Assert.DoesNotContain("interactive.ready", stdout.ToString());
        using var reopened = FileLock.Acquire(fixture.Context.LockPath);
    }
}
