using Meshline;
using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Cli;

internal static partial class Commands
{
    static IReadOnlyList<CommandSpec> SessionCatalog =>
    [
        new("relays list", "Discover registered relays through the configured RPC.", [], [], []),
        new("status", "Show local SDK state and synchronization progress for one resource.", ["relay", "group", "channel"], [], []),
        new("account sync", "Await a fresh account timeline synchronization pass on one relay.", ["relay"], [], []),
        new("account establish", "Establish this identity and device on a relay.", ["relay", "certificate-days", "route-days"], [], []),
        new("account recover", "Explicitly recover account authority on a new device.", ["relay", "previous-state", "device-state-revision", "route-revision", "certificate-days", "route-days"], [], []),
        new("account migrate", "Move the home route to another registered relay.", ["relay"], [], []),
        new("devices list", "Read current account device state; --out saves the protocol state for recovery.", ["account", "out"], [], []),
        new("devices renew", "Renew this device and publish the device state.", ["days"], [], []),
        new("devices remove", "Remove a device authorization.", [], [], ["id"]),
        new("account profile show", "Read network account profile details such as nickname and bio; --account selects another account.", ["account"], [], []),
        new("account profile update", "Update and publish this account's network profile details.", ["nickname", "bio", "avatar-file", "public-discovery"], ["clear-nickname", "clear-bio", "clear-avatar"], []),
        new("contacts invite", "Create a contact invitation; --out writes its protocol document.", ["expires", "out"], [], []),
        new("contacts add", "Request contact access by --account or --invite-file.", ["account", "invite-file", "note"], [], []),
        new("contacts requests", "Read locally synchronized contact requests.", ["account", "direction", "limit"], [], []),
        new("contacts accept", "Accept an incoming contact request.", [], [], ["account"]),
        new("contacts dismiss", "Dismiss a contact request.", [], [], ["account"]),
        new("contacts list", "Read local contacts.", ["search", "limit"], [], []),
        new("contacts show", "Read one local contact.", [], [], ["account"]),
        new("contacts alias", "Set or clear a local contact alias.", ["alias"], ["clear"], ["account"]),
        new("contacts remove", "Remove a contact.", [], [], ["account"]),
        new("conversations list", "Read SDK conversation summaries, optionally unread only.", ["kind", "limit"], ["unread", "has-messages"], []),
        new("conversations show", "Read one SDK conversation summary.", [], [], ["id"]),
        new("conversations messages", "Read local messages, optionally within exclusive sequence bounds; does not mark read.", ["limit", "after", "before"], [], ["id"]),
        new("conversations mark-read", "Mark through --sequence inclusively; omission marks the latest readable message at call time.", ["sequence"], [], ["id"]),
        new("messages send", "Queue a direct message and wait for relay acceptance by default.", ["to", "text", "draft-file", "wait"], [], []),
        new("messages list", "Read local direct history, optionally within exclusive local-sequence bounds.", ["peer", "limit", "after", "before"], [], []),
        new("messages show", "Read a direct message by sender and message ID.", ["sender"], [], ["id"]),
        new("messages outbox", "Read pending sends and retained terminal send outcomes.", ["to", "state", "limit"], [], []),
        new("messages status", "Read send state; absent is not proof of delivery.", [], [], ["id"]),
        new("messages cancel", "Cancel an SDK message only while still queued.", [], [], ["id"]),
        new("watch", "Stream SDK events as NDJSON; no replay and no automatic mark-read.", [], [], [])
    ];

    static async Task<int> ExecuteWithSessionAsync(Invocation invocation, ProfileContext context, Output output, CancellationToken token)
    {
        if (invocation.Command == "relays list")
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            var registry = new RpcRelayRegistry(http, new() { Context = context.Network, RpcUrl = new(context.Settings.RpcUrl) });
            var items = new List<object>();
            await foreach (var relay in registry.GetRelaysAsync(token)) items.Add(relay);
            await output.ResultAsync(CommandResult.Success(new { items }));
            return Exit.Success;
        }
        var forwarded = await Daemon.TryForwardAsync(invocation, context, output, token);
        if (forwarded is { } exitCode) return exitCode;
        await using var session = await RuntimeSession.OpenAsync(context, output, token);
        if (NeedsBackground(invocation.Command))
            await session.Client.StartAsync(token);
        return await ExecuteSessionAsync(invocation, session, output, token);
    }

    internal static bool NeedsBackground(string command) => command is not (
        "account establish" or "account recover" or "account sync" or "groups sync" or "channels sync" or "status" or "conversations list" or "conversations show" or "conversations messages" or "conversations mark-read"
        or "contacts list" or "contacts show" or "contacts requests" or "contacts alias" or "contacts dismiss" or "contacts invite"
        or "messages list" or "messages show" or "messages outbox" or "messages status" or "messages cancel"
        or "groups list" or "groups messages" or "groups members list" or "groups bans list" or "channels list" or "channels posts");

    internal static async Task<int> ExecuteSessionAsync(Invocation i, RuntimeSession session, Output output, CancellationToken token)
    {
        // Validate before any SDK query acquires a snapshot that needs disposal.
        if (i.Get("limit") is not null) _ = Limit(i);
        var client = session.Client;
        if (i.Command.StartsWith("groups ", StringComparison.Ordinal)) return await ExecuteGroupAsync(i, client, output, token);
        if (i.Command.StartsWith("channels ", StringComparison.Ordinal)) return await ExecuteChannelAsync(i, client, output, token);
        var messages = client.MessageManager;
        object? result;
        switch (i.Command)
        {
            case "status": result = await StatusAsync(client, i, token); break;
            case "account sync": return await SyncResultAsync("account", await messages.SynchronizeAsync(i.Get("relay") ?? HomeRelay(client), token), output);
            case "account establish":
                await client.EstablishAccountAsync(new() { RelayId = i.Get("relay"), CertificateValidity = Days(i, "certificate-days", 365), RouteValidity = Days(i, "route-days", 365) }, token);
                result = await StatusAsync(client, null, token); break;
            case "account recover":
                await client.RecoverAccountAsync(new()
                {
                    RelayId = i.Get("relay"),
                    CertificateValidity = Days(i, "certificate-days", 365),
                    RouteValidity = Days(i, "route-days", 365),
                    PreviousDeviceState = i.Get("previous-state") is { } stateFile ? await ReadProtocolAsync<AccountDeviceState>(stateFile, token) : null,
                    DeviceStateRevision = OptionalLong(i, "device-state-revision"),
                    RouteRevision = OptionalLong(i, "route-revision")
                }, token);
                result = await StatusAsync(client, null, token); break;
            case "account migrate":
                await client.ChangeHomeRelayAsync(i.Require("relay"), token); result = await StatusAsync(client, null, token); break;
            case "devices list":
                var deviceState = await client.DeviceManager.GetDeviceStateAsync(i.Get("account"), token);
                if (i.Get("out") is { } statePath && deviceState is not null) await ProtocolResultAsync(deviceState, statePath, token);
                result = new { state = deviceState, devices = deviceState?.Certificates.Select(certificate => new { id = certificate.GetDeviceId(client.Context), certificate }) }; break;
            case "devices renew":
                var renewed = await client.DeviceManager.RenewDeviceAsync(Days(i, "days", 365), token);
                var published = await client.DeviceManager.PublishDeviceStateAsync(HomeRelay(client), cancellationToken: token);
                result = new { device = renewed, publication = published }; break;
            case "devices remove": await client.DeviceManager.RemoveDeviceAsync(i.Require("id"), token); result = new { removed = i.Require("id") }; break;
            case "account profile show": result = await client.ProfileManager.GetProfileAsync(i.Get("account"), token); break;
            case "account profile update":
                if (i.Flag("clear-avatar") && i.Get("avatar-file") is not null) throw new CliException("invalid_argument", "Choose --avatar-file or --clear-avatar.", Exit.Usage);
                result = await client.ProfileManager.UpdateProfileAsync(new()
                {
                    Nickname = StringUpdate(i, "nickname"),
                    Bio = StringUpdate(i, "bio"),
                    Avatar = i.Flag("clear-avatar") ? FieldUpdate<ContentReference>.Delete : i.Get("avatar-file") is { } avatar ? await ReadProtocolAsync<ContentReference>(avatar, token) : default(FieldUpdate<ContentReference>),
                    PublicDiscovery = i.Get("public-discovery") is { } discover ? bool.Parse(discover) : default(FieldUpdate<bool>)
                }, token); break;
            case "contacts invite":
                var invite = await messages.CreateInviteAsync(Expiry(i), token);
                result = await ProtocolResultAsync(invite, i.Get("out"), token); break;
            case "contacts add":
                ExactlyOne(i, "account", "invite-file");
                result = i.Get("invite-file") is { } file
                    ? await messages.AddContactAsync(await ReadProtocolAsync<ContactInvite>(file, token), i.Get("note"), token)
                    : await messages.AddContactAsync(i.Require("account"), i.Get("note"), token); break;
            case "contacts requests": result = await TakeAsync(await messages.GetContactRequestsAsync(i.Get("account"), EnumValue(i, "direction", ContactRequestDirection.All), token), Limit(i, 200), false, token); break;
            case "contacts accept": result = await messages.AcceptContactRequestAsync(i.Require("account"), token); break;
            case "contacts dismiss": await messages.DismissContactRequestAsync(i.Require("account"), token); result = new { dismissed = i.Require("account") }; break;
            case "contacts list": result = await TakeAsync(await messages.GetContactsAsync(i.Get("search"), token), Limit(i, 200), false, token); break;
            case "contacts show": result = await messages.GetContactAsync(i.Require("account"), token); break;
            case "contacts alias":
                if (i.Flag("clear") == (i.Get("alias") is not null)) throw new CliException("invalid_argument", "Choose --alias or --clear.", Exit.Usage);
                result = await messages.SetAliasAsync(i.Require("account"), i.Get("alias"), token); break;
            case "contacts remove": await messages.RemoveContactAsync(i.Require("account"), token); result = new { removed = i.Require("account") }; break;
            case "conversations list":
                result = await TakeAsync(await client.GetConversationsAsync(new() { Kind = EnumValue(i, "kind", ConversationKind.All), UnreadOnly = i.Flag("unread"), HasMessages = i.Flag("has-messages") ? true : null }, token), Limit(i, 200), false, token); break;
            case "conversations show": result = await client.GetConversationAsync(i.Require("id"), token); break;
            case "conversations messages":
                var range = History(i);
                var conversation = await client.GetConversationAsync(i.Require("id"), token) ?? throw new CliException("conversation_not_found", "No local conversation with that ID.");
                result = conversation.Kind switch
                {
                    ConversationKind.Direct => await TakeHistoryAsync(await messages.GetMessageHistoryAsync(conversation.ConversationId, range, token), Limit(i), range, token),
                    ConversationKind.Group => await TakeHistoryAsync(await client.GroupManager.GetMessagesAsync(conversation.ConversationId, range: range, cancellationToken: token), Limit(i), range, token),
                    ConversationKind.Channel => await TakeHistoryAsync(await client.ChannelManager.GetPostsAsync(conversation.ConversationId, range: range, cancellationToken: token), Limit(i), range, token),
                    _ => throw new CliException("conversation_kind", "Unsupported conversation kind.")
                }; break;
            case "conversations mark-read":
                var sequence = OptionalLong(i, "sequence");
                if (sequence is { } position) await client.MarkReadAsync(i.Require("id"), position, token);
                else await client.MarkReadAsync(i.Require("id"), token);
                result = new { conversation = await client.GetConversationAsync(i.Require("id"), token), semantics = sequence is null ? "latest-local-at-call-time" : "through-local-sequence", localSequence = sequence }; break;
            case "messages send": return await SendAsync(i, client, output, token);
            case "messages list":
                var directRange = History(i);
                result = await TakeHistoryAsync(await messages.GetMessageHistoryAsync(i.Get("peer"), directRange, token), Limit(i), directRange, token); break;
            case "messages show": result = await messages.GetMessageAsync(new() { Sender = i.Require("sender"), MessageId = i.Require("id") }, token); break;
            case "messages outbox": result = await TakeAsync(await messages.GetOutboxAsync(i.Get("to"), EnumValue(i, "state", MessageSendState.All), token), Limit(i, 200), false, token); break;
            case "messages status": result = new { status = await messages.GetSendStatusAsync(i.Require("id"), token), absenceDoesNotProveDelivery = true }; break;
            case "messages cancel": result = new { canceled = await messages.CancelMessageAsync(i.Require("id"), token) }; break;
            case "watch": return await WatchAsync(client, output, token);
            default: throw new CliException("unknown_command", "Unknown session command.", Exit.Usage);
        }
        await output.ResultAsync(CommandResult.Success(result));
        return Exit.Success;
    }

    static string HomeRelay(MeshlineClient client) => client.Route?.RelayId ?? throw new CliException("account_not_established", "Run account establish first.", Exit.Configuration);
    static int Limit(Invocation i, int fallback = 50) => ParseBoundedInt(i.Get("limit"), fallback, 1, 10000);
    static int ParseBoundedInt(string? value, int fallback, int min, int max)
    {
        var number = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        if (number < min || number > max) throw new CliException("invalid_argument", $"Value must be between {min} and {max}.", Exit.Usage);
        return number;
    }
    static long? OptionalLong(Invocation i, string key) => i.Get(key) is { } value ? long.Parse(value, CultureInfo.InvariantCulture) : null;
    static TimeSpan Days(Invocation i, string key, int fallback) => TimeSpan.FromDays(ParseBoundedInt(i.Get(key), fallback, 1, 3650));
    static T EnumValue<T>(Invocation i, string key, T fallback) where T : struct, Enum => i.Get(key) is { } value && Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
        : i.Get(key) is null ? fallback : throw new CliException("invalid_argument", $"Invalid --{key} value.", Exit.Usage);
    static DateTimeOffset Expiry(Invocation i) => i.Get("expires") is { } expires ? DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.UtcNow.AddDays(1);
    static FieldUpdate<string> StringUpdate(Invocation i, string key)
    {
        if (i.Flag("clear-" + key) && i.Get(key) is not null) throw new CliException("invalid_argument", $"--{key} conflicts with --clear-{key}.", Exit.Usage);
        return i.Flag("clear-" + key) ? FieldUpdate<string>.Delete : i.Get(key) is { } value ? value : default(FieldUpdate<string>);
    }
    static void ExactlyOne(Invocation i, string first, string second)
    {
        if ((i.Get(first) is null) == (i.Get(second) is null)) throw new CliException("invalid_argument", $"Choose exactly one of --{first} and --{second}.", Exit.Usage);
    }
    static async Task<T> ReadProtocolAsync<T>(string path, CancellationToken token) where T : ProtocolModel => ProtocolModel.FromJson<T>(await File.ReadAllTextAsync(path, token)) ?? throw new CliException("invalid_json", "Expected a protocol document.", Exit.Usage);
    static async Task<object> ProtocolResultAsync(ProtocolModel document, string? path, CancellationToken token)
    {
        var protocolJson = document.ToJson();
        if (path is not null) await PrivateFiles.WriteAtomicAsync(path, Encoding.UTF8.GetBytes(protocolJson), false, token);
        return new { document = JsonDocument.Parse(protocolJson).RootElement.Clone(), path = path is null ? null : Path.GetFullPath(path) };
    }
    internal static async Task<object> TakeAsync<T>(QueryReader<T> reader, int limit, bool latest, CancellationToken token)
    {
        await using (reader)
        {
            var items = new Queue<T>();
            long scanned = 0;
            while (true)
            {
                var batch = await reader.ReadNextAsync(latest ? 256 : limit + 1, token);
                foreach (var item in batch) { scanned++; items.Enqueue(item); if (latest && items.Count > limit) items.Dequeue(); }
                if (!latest || batch.Count == 0) break;
            }
            return new { items = items.Take(limit).ToArray(), hasMore = scanned > limit, source = "local-sdk-snapshot", selection = latest ? "latest" : "first", scanned };
        }
    }

    static async Task<int> SendAsync(Invocation i, MeshlineClient client, Output output, CancellationToken token)
    {
        ExactlyOne(i, "text", "draft-file");
        var wait = i.Get("wait") ?? "relay";
        if (wait is not ("queued" or "relay" or "target")) throw new CliException("invalid_argument", "--wait must be queued, relay or target.", Exit.Usage);
        var draft = i.Get("draft-file") is { } file ? Json.Read<DirectMessageDraft>(await File.ReadAllTextAsync(file, token))
            : new DirectMessageDraft { Body = new() { ContentType = "text/plain", Text = i.Require("text") } };
        var status = await client.MessageManager.SendMessageAsync(i.Require("to"), draft, token);
        return await WaitForSendAsync(client.MessageManager, status, wait, output, token);
    }

    internal static async Task<int> WaitForSendAsync(MessageManager messages, MessageSendStatus status, string wait, Output output, CancellationToken token)
    {
        var target = wait switch
        {
            "queued" => MessageSendState.Queued,
            "relay" => MessageSendState.RelayAccepted,
            "target" => MessageSendState.TargetAccepted,
            _ => throw new CliException("invalid_argument", "--wait must be queued, relay or target.", Exit.Usage)
        };
        try
        {
            status = await messages.WaitForSendStatusAsync(status.MessageId, target, token)
                ?? throw new CliException("message_status_unavailable", "The send record is unavailable. This does not prove delivery; reconcile the message ID before retrying.", Exit.Pending, status);
            if (status.State is MessageSendState.Failed or MessageSendState.Canceled)
                throw new CliException("message_failed", "The SDK send operation failed or was canceled.", Exit.Failure, status);
            await output.ResultAsync(CommandResult.Success(new { status, waitedFor = wait }));
            return Exit.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw new CliException("message_pending", "Send wait ended; the SDK operation can still complete. Inspect this message ID before retrying.", Exit.Pending, status); }
    }

    static async Task<int> WatchAsync(MeshlineClient client, Output output, CancellationToken token)
    {
        var events = Channel.CreateBounded<(string Type, object Data)>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        void Enqueue(string type, object data)
        {
            if (!events.Writer.TryWrite((type, data))) events.Writer.TryComplete(new CliException("watch_overflow", "Event reader fell behind. Query current conversations and restart watch."));
        }
        void Conversation(object? sender, ConversationChangedEventArgs e) => Enqueue("conversation.changed", new { e.ConversationId, e.ChangeKind });
        void Received(object? sender, MessageReceivedEventArgs e) => Enqueue("message.received", e.Messages);
        void Sent(object? sender, MessageSendStatusChangedEventArgs e) => Enqueue("message.send-status", e.Status);
        void AccountSync(object? sender, ResourceSyncStatus e) => Enqueue("sync.changed", SyncStatus("account", e));
        void GroupSync(object? sender, ResourceSyncStatus e) => Enqueue("sync.changed", SyncStatus("group", e));
        void ChannelSync(object? sender, ResourceSyncStatus e) => Enqueue("sync.changed", SyncStatus("channel", e));
        client.ConversationChanged += Conversation;
        client.MessageManager.MessageReceived += Received;
        client.MessageManager.SendStatusChanged += Sent;
        client.MessageManager.SyncStatusChanged += AccountSync;
        client.GroupManager.SyncStatusChanged += GroupSync;
        client.ChannelManager.SyncStatusChanged += ChannelSync;
        try
        {
            await output.EventAsync("watch.ready", new { replay = false, synchronization = "background-no-completion-barrier" });
            await foreach (var item in events.Reader.ReadAllAsync(token)) await output.EventAsync(item.Type, item.Data);
            return Exit.Success;
        }
        finally
        {
            client.ConversationChanged -= Conversation;
            client.MessageManager.MessageReceived -= Received;
            client.MessageManager.SendStatusChanged -= Sent;
            client.MessageManager.SyncStatusChanged -= AccountSync;
            client.GroupManager.SyncStatusChanged -= GroupSync;
            client.ChannelManager.SyncStatusChanged -= ChannelSync;
            events.Writer.TryComplete();
        }
    }
}
