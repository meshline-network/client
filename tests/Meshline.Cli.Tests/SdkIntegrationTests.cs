using Meshline.Cli;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class SdkIntegrationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Relay = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Group = "grp_AAAAAAAAAAAAAAAAAAAAAA";
    const string Channel = "chan_AAAAAAAAAAAAAAAAAAAAAA";

    sealed class SessionFixture : IAsyncDisposable
    {
        public TemporaryProfile Profile { get; } = new();
        public RuntimeSession Session { get; private set; } = null!;

        public static async Task<SessionFixture> OpenAsync()
        {
            var fixture = new SessionFixture();
            try
            {
                var context = fixture.Profile.Context;
                await Secrets.GenerateKeyFileAsync(context.Settings.Protection.KeyFile!, Token);
                context.Settings.ProtectedKey = await Secrets.CreateAsync(context, Token);
                using (var vault = await Secrets.OpenAsync(context, Token)) await Identity.CreateAsync(context, vault, Token);
                await Configuration.SaveProfileAsync(context, true, Token);
                fixture.Session = await RuntimeSession.OpenAsync(context, new(TextWriter.Null, TextWriter.Null, true), Token);
                return fixture;
            }
            catch { fixture.Profile.Dispose(); throw; }
        }

        // Only test fixtures seed SDK projections. Production code always uses SDK APIs.
        public async Task SeedAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Profile.Context.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync(Token);
        }

        public async Task<JsonElement> RunAsync(string command, params (string Name, string Value)[] values)
        {
            var invocation = new Invocation(command, values.ToDictionary(pair => pair.Name, pair => pair.Value), Profile.Context.ConfigPath, Profile.Context.Name, true, 60);
            using var stdout = new StringWriter();
            Assert.Equal(Exit.Success, await Commands.ExecuteSessionAsync(invocation, Session, new(stdout, TextWriter.Null, true), Token));
            return JsonSerializer.Deserialize<JsonElement>(stdout.ToString()).GetProperty("data");
        }

        public async ValueTask DisposeAsync()
        {
            try { await Session.DisposeAsync(); }
            finally { Profile.Dispose(); }
        }
    }

    static async Task AddDirectAsync(SessionFixture fixture, string sender, long sequence)
        => await fixture.SeedAsync("""
            INSERT INTO Messages (LocalSequence, Sender, MessageId, SenderDeviceId, Recipient, CreatedAt, PayloadType, PayloadJson, IsDirect)
            VALUES ($sequence, $sender, $id, 'dev_AAAAAAAAAAAAAAAAAAAAAA', $recipient, 1, 'meshline.message.direct', $payload, 1)
            """, ("$sequence", sequence), ("$sender", sender), ("$id", Identifiers.CreateMessageId()),
            ("$recipient", fixture.Session.Client.Options.AccountId), ("$payload", new DirectMessage { Body = new() { ContentType = "text/plain", Text = sequence.ToString() } }.ToJson()));

    [Theory]
    [InlineData("messages list")]
    [InlineData("groups messages")]
    [InlineData("channels posts")]
    public async Task History_pages_keep_the_adjacent_items_and_resume_without_duplicates(string command)
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var sender = fixture.Session.Client.Options.AccountId;
        if (command == "groups messages")
            await fixture.SeedAsync("""
                INSERT INTO Groups (GroupId, RelayId, MemberCapacity, MemberCount, InvitePolicy, Status, Membership, Sequence, Epoch, LocallyClosed)
                VALUES ($id, $relay, 32, 1, 0, 0, 0, 0, 0, 0)
                """, ("$id", Group), ("$relay", Relay));
        if (command == "channels posts")
            await fixture.SeedAsync("INSERT INTO Channels (ChannelId, RelayId, IsFollowed, Revision, SyncSequence) VALUES ($id, $relay, 1, 0, 0)", ("$id", Channel), ("$relay", Relay));
        foreach (var sequence in new[] { 10L, 20, 30, 40, 50 })
        {
            if (command == "messages list") await AddDirectAsync(fixture, sender, sequence);
            else if (command == "groups messages")
                await fixture.SeedAsync("""
                    INSERT INTO GroupEvents (GroupId, Sequence, PayloadJson, Epoch, MessageId, Sender, SenderDeviceId, CreatedAt, DecryptedPayloadJson, IsMessage)
                    VALUES ($id, $sequence, '{}', 0, $message, $sender, 'dev_AAAAAAAAAAAAAAAAAAAAAA', 1, $payload, 1)
                    """, ("$id", Group), ("$sequence", sequence), ("$message", Identifiers.CreateMessageId()), ("$sender", sender),
                    ("$payload", new GroupMessage { Body = new() { ContentType = "text/plain", Text = sequence.ToString() } }.ToJson()));
            else
                await fixture.SeedAsync("""
                    INSERT INTO ChannelPosts (ChannelId, Sequence, MessageId, Author, AcceptedAt, PostJson, AppliedThrough, IsDeleted)
                    VALUES ($id, $sequence, $message, $sender, 1, $payload, $sequence, 0)
                    """, ("$id", Channel), ("$sequence", sequence), ("$message", Identifiers.CreateMessageId()), ("$sender", sender),
                    ("$payload", new ChannelPost { ChannelId = Channel, MessageId = Identifiers.CreateMessageId(), Body = new() { ContentType = "text/plain", Text = sequence.ToString() }, DeviceSignature = [.. new byte[64]] }.ToJson()));
        }
        (string, string) scope = command == "messages list" ? ("peer", sender) : ("id", command == "groups messages" ? Group : Channel);
        var backward = await fixture.RunAsync(command, scope, ("before", "50"), ("limit", "2"));
        Assert.Equal(new[] { 30L, 40L }, Positions(backward));
        Assert.True(backward.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, backward.GetProperty("scanned").GetInt32());
        Assert.Equal(new[] { 10L, 20L }, Positions(await fixture.RunAsync(command, scope, ("before", "30"), ("limit", "2"))));
        var forward = await fixture.RunAsync(command, scope, ("after", "10"), ("limit", "2"));
        Assert.Equal(new[] { 20L, 30L }, Positions(forward));
        Assert.Equal(new[] { 40L, 50L }, Positions(await fixture.RunAsync(command, scope, ("after", "30"), ("limit", "2"))));
        Assert.Equal(new[] { 40L, 50L }, Positions(await fixture.RunAsync(command, scope, ("limit", "2"))));
        var bounded = await fixture.RunAsync(command, scope, ("after", "20"), ("before", "50"), ("limit", "2"));
        Assert.Equal(new[] { 30L, 40L }, Positions(bounded));
        Assert.False(bounded.GetProperty("hasMore").GetBoolean());
        var conversationId = scope.Item2;
        Assert.Equal(new[] { 30L, 40L }, Positions(await fixture.RunAsync("conversations messages", ("id", conversationId), ("before", "50"), ("limit", "2"))));
    }

    static long[] Positions(JsonElement result) => result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("localSequence").GetInt64()).ToArray();

    [Theory]
    [InlineData("messages list")]
    [InlineData("contacts list")]
    [InlineData("contacts requests")]
    [InlineData("conversations list")]
    [InlineData("messages outbox")]
    [InlineData("groups list")]
    [InlineData("channels list")]
    public async Task Invalid_limit_does_not_leave_a_database_snapshot_open(string command)
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var error = await Assert.ThrowsAsync<CliException>(() => fixture.RunAsync(command, ("limit", "0")));
        Assert.Equal(Exit.Usage, error.ExitCode);
        await AddDirectAsync(fixture, fixture.Session.Client.Options.AccountId, 10);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Profile.Context.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(Token);
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA busy_timeout = 1";
        await checkpoint.ExecuteNonQueryAsync(Token);
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        // An abandoned query snapshot prevents truncation even after the command has failed.
        Assert.Equal(0L, await checkpoint.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task Invalid_history_ranges_and_read_positions_fail_without_mutating_read_state()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        foreach (var command in new[] { "messages list", "conversations messages", "groups messages", "channels posts" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunAsync(command, ("after", "-1"), ("id", Group)));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunAsync(command, ("after", "20"), ("before", "20"), ("id", Group)));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunAsync(command, ("before", "9007199254740992"), ("id", Group)));
        }
        var account = fixture.Session.Client.Options.AccountId;
        await AddDirectAsync(fixture, account, 10);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunAsync("conversations mark-read", ("id", account), ("sequence", "0")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunAsync("conversations mark-read", ("id", account), ("sequence", "11")));
    }

    [Fact]
    public async Task Explicit_read_position_leaves_later_arrivals_unread_and_legacy_mark_all_still_works()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        using var peer = new TemporaryProfile();
        await Secrets.GenerateKeyFileAsync(peer.Context.Settings.Protection.KeyFile!, Token);
        peer.Context.Settings.ProtectedKey = await Secrets.CreateAsync(peer.Context, Token);
        using var vault = await Secrets.OpenAsync(peer.Context, Token);
        var sender = (await Identity.CreateAsync(peer.Context, vault, Token)).AccountId;
        await AddDirectAsync(fixture, sender, 10);
        await AddDirectAsync(fixture, sender, 20);
        var page = await fixture.RunAsync("conversations messages", ("id", sender), ("after", "0"), ("limit", "2"));
        await AddDirectAsync(fixture, sender, 30);
        var marked = await fixture.RunAsync("conversations mark-read", ("id", sender), ("sequence", Positions(page)[^1].ToString()));
        Assert.Equal("through-local-sequence", marked.GetProperty("semantics").GetString());
        Assert.Equal(1, marked.GetProperty("conversation").GetProperty("unreadCount").GetInt64());
        var all = await fixture.RunAsync("conversations mark-read", ("id", sender));
        Assert.Equal("latest-local-at-call-time", all.GetProperty("semantics").GetString());
        Assert.Equal(0, all.GetProperty("conversation").GetProperty("unreadCount").GetInt64());
    }

    [Theory]
    [InlineData(MessageSendState.TargetAccepted, "relay")]
    [InlineData(MessageSendState.TargetAccepted, "target")]
    [InlineData(MessageSendState.Failed, "target")]
    [InlineData(MessageSendState.Canceled, "queued")]
    public async Task Send_wait_uses_retained_SDK_outcomes_without_needing_a_new_event(MessageSendState state, string wait)
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var queued = await SeedSendAsync(fixture, state);
        using var stdout = new StringWriter();
        var pending = Commands.WaitForSendAsync(fixture.Session.Client.MessageManager, queued, wait, new(stdout, TextWriter.Null, true), Token);
        if (state is MessageSendState.Failed or MessageSendState.Canceled)
        {
            var error = await Assert.ThrowsAsync<CliException>(() => pending);
            Assert.Equal("message_failed", error.Code);
            Assert.Equal(state, Assert.IsType<MessageSendStatus>(error.Details).State);
        }
        else
        {
            Assert.Equal(Exit.Success, await pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Equal(state.ToString(), JsonSerializer.Deserialize<JsonElement>(stdout.ToString()).GetProperty("data").GetProperty("status").GetProperty("state").GetString());
        }
    }

    static async Task<MessageSendStatus> SeedSendAsync(SessionFixture fixture, MessageSendState state)
    {
        var id = Identifiers.CreateMessageId();
        var account = fixture.Session.Client.Options.AccountId;
        await fixture.SeedAsync("""
            INSERT INTO MessageOutbox (MessageId, Recipient, CreatedAt, IsDirect, State, RelayId, RequestJson, NextAttemptAt)
            VALUES ($id, $recipient, 1, 1, $state, $relay, '{}', 0)
            """, ("$id", id), ("$recipient", account), ("$state", (int)state), ("$relay", Relay));
        return new() { MessageId = id, Recipient = account, CreatedAt = DateTimeOffset.UnixEpoch, State = MessageSendState.Queued };
    }

    [Fact]
    public async Task Canceling_a_send_wait_keeps_the_send_and_missing_records_are_not_success()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var queued = await SeedSendAsync(fixture, MessageSendState.Queued);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wait = Commands.WaitForSendAsync(fixture.Session.Client.MessageManager, queued, "target", new(TextWriter.Null, TextWriter.Null, true), cancel.Token);
        await cancel.CancelAsync();
        var error = await Assert.ThrowsAsync<CliException>(() => wait);
        Assert.Equal("message_pending", error.Code);
        Assert.Equal(MessageSendState.Queued, (await fixture.Session.Client.MessageManager.GetSendStatusAsync(queued.MessageId, Token))!.State);
        await fixture.SeedAsync("DELETE FROM MessageOutbox WHERE MessageId = $id", ("$id", queued.MessageId));
        error = await Assert.ThrowsAsync<CliException>(() => Commands.WaitForSendAsync(fixture.Session.Client.MessageManager, queued, "target", new(TextWriter.Null, TextWriter.Null, true), Token));
        Assert.Equal("message_status_unavailable", error.Code);
        Assert.Equal(Exit.Pending, error.ExitCode);
    }

    [Fact]
    public async Task Status_is_local_and_reports_real_idle_observations_for_each_resource_kind()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        foreach (var (option, id, kind) in new[] { ("relay", Relay, "account"), ("group", Group, "group"), ("channel", Channel, "channel") })
        {
            var result = (await fixture.RunAsync("status", (option, id))).GetProperty("synchronization");
            Assert.Equal("Idle", result.GetProperty("state").GetString());
            Assert.Equal(kind, result.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("lastSynchronizedAt").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, (await fixture.RunAsync("status")).GetProperty("synchronization").ValueKind);
        var invalid = await Assert.ThrowsAsync<CliException>(() => fixture.RunAsync("status", ("group", Group), ("channel", Channel)));
        Assert.Equal(Exit.Usage, invalid.ExitCode);
        foreach (var command in new[] { "account sync", "groups sync", "channels sync" }) Assert.False(Commands.NeedsBackground(command));
    }

    [Fact]
    public async Task Blocked_sync_is_not_success_and_exception_details_remain_serializable()
    {
        Exception failure;
        try { throw new TimeoutException("Local test request deadline"); }
        catch (Exception error) { failure = error; }
        failure.Data["operation"] = "relay.http.group.sync";
        failure.Data["timeoutSeconds"] = 15d;
        var status = new ResourceSyncStatus { Resource = Group, State = ResourceSyncState.Blocked, BlockReason = ResourceSyncBlockReason.Connection, Error = failure };
        var blocked = await Assert.ThrowsAsync<CliException>(() => Commands.SyncResultAsync("group", status, new(TextWriter.Null, TextWriter.Null, true)));
        Assert.Equal("sync_incomplete", blocked.Code);
        Assert.Equal(Exit.Pending, blocked.ExitCode);
        var json = JsonSerializer.SerializeToElement(CommandResult.Failure(blocked), Json.Options);
        Assert.Equal("request_timeout", json.GetProperty("error").GetProperty("details").GetProperty("error").GetProperty("code").GetString());
        using var stdout = new StringWriter();
        Assert.Equal(Exit.Success, await Commands.SyncResultAsync("group", new() { Resource = Group, State = ResourceSyncState.CaughtUp, HasRetentionGap = true }, new(stdout, TextWriter.Null, true)));
        Assert.True(JsonSerializer.Deserialize<JsonElement>(stdout.ToString()).GetProperty("data").GetProperty("hasRetentionGap").GetBoolean());
    }
}
