using Meshline;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;

namespace Meshline.Cli;

internal static partial class Commands
{
    static IReadOnlyList<CommandSpec> ChannelCatalog =>
    [
        new("channels create", "Create a public channel.", ["relay", "name", "description", "moderators"], [], []),
        new("channels list", "Read locally followed channels.", ["relay", "limit"], [], []),
        new("channels show", "Read a channel descriptor from its relay.", ["relay"], [], ["id"]),
        new("channels sync", "Await a fresh channel synchronization pass without following it.", ["relay"], [], ["id"]),
        new("channels update", "Update a channel descriptor.", ["relay", "name", "description", "moderators"], ["clear-description", "clear-moderators"], ["id"]),
        new("channels close", "Close an owned channel.", ["relay"], [], ["id"]),
        new("channels follow", "Follow and synchronize a public channel.", ["relay"], [], ["id"]),
        new("channels unfollow", "Stop following a public channel.", ["relay"], [], ["id"]),
        new("channels publish", "Publish a public post.", ["relay", "text", "draft-file"], [], ["id"]),
        new("channels edit", "Edit a post using its ORIGINAL sequence.", ["relay", "sequence", "text", "draft-file"], ["clear-attachments"], ["id"]),
        new("channels delete", "Delete a post using its original sequence.", ["relay", "sequence"], [], ["id"]),
        new("channels report", "Report a post to its relay.", ["relay", "sequence", "reason"], [], ["id"]),
        new("channels posts", "Read local nondeleted posts within optional exclusive sequence bounds.", ["author", "limit", "after", "before"], [], ["id"]),
        new("channels history", "Load a page of historical posts from the relay.", ["relay", "limit", "cursor"], [], ["id"])
    ];

    static async Task<int> ExecuteChannelAsync(Invocation i, MeshlineClient client, Output output, CancellationToken token)
    {
        var channels = client.ChannelManager;
        object? result;
        switch (i.Command)
        {
            case "channels create": result = await channels.CreateChannelAsync(i.Get("relay") ?? HomeRelay(client), i.Require("name"), i.Get("description"), CommaList(i.Get("moderators")), token); break;
            case "channels list": result = await TakeAsync(await channels.GetFollowedAsync(i.Get("relay"), token), Limit(i, 200), false, token); break;
            case "channels posts":
                var range = History(i);
                result = await TakeHistoryAsync(await channels.GetPostsAsync(i.Require("id"), i.Get("author"), range, token), Limit(i), range, token); break;
            default:
                var channel = await ResolveChannelAsync(i, client, token);
                result = new { channel, operation = i.Command, completed = true };
                switch (i.Command)
                {
                    case "channels show": result = await channels.GetChannelAsync(channel, token); break;
                    case "channels sync": return await SyncResultAsync("channel", await channels.SynchronizeAsync(channel, token), output);
                    case "channels update":
                        if (i.Flag("clear-moderators") && i.Get("moderators") is not null) throw new CliException("invalid_argument", "Choose --moderators or --clear-moderators.", Exit.Usage);
                        result = await channels.UpdateChannelAsync(channel, new()
                        {
                            Name = StringUpdate(i, "name"),
                            Description = StringUpdate(i, "description"),
                            Moderators = i.Flag("clear-moderators") ? new FieldUpdate<IReadOnlyList<string>>(Array.Empty<string>()) : i.Get("moderators") is { } moderators ? new FieldUpdate<IReadOnlyList<string>>(CommaList(moderators)!) : default
                        }, token); break;
                    case "channels close": await channels.CloseChannelAsync(channel, token); break;
                    case "channels follow": await channels.FollowAsync(channel, token); break;
                    case "channels unfollow": await channels.UnfollowAsync(channel, token); break;
                    case "channels publish":
                        ExactlyOne(i, "text", "draft-file");
                        result = await channels.PublishPostAsync(channel, await PostDraftAsync(i, token), token); break;
                    case "channels edit":
                        ExactlyOne(i, "text", "draft-file");
                        var draft = await PostDraftAsync(i, token);
                        result = await channels.EditPostAsync(PostRef(i, channel), new()
                        {
                            Body = draft.Body is { } body ? body : FieldUpdate<MessageBody>.Delete,
                            Attachments = i.Flag("clear-attachments") ? new FieldUpdate<IReadOnlyList<ContentReference>>(Array.Empty<ContentReference>()) : i.Get("draft-file") is not null ? new FieldUpdate<IReadOnlyList<ContentReference>>(draft.Attachments) : default
                        }, token); break;
                    case "channels delete": await channels.DeletePostAsync(PostRef(i, channel), token); break;
                    case "channels report": await channels.ReportPostAsync(PostRef(i, channel), i.Require("reason"), token); break;
                    case "channels history": result = await channels.LoadChannelHistoryAsync(channel, Page(i), token); break;
                    default: throw new CliException("unknown_command", "Unknown channel command.", Exit.Usage);
                }
                break;
        }
        await output.ResultAsync(CommandResult.Success(result));
        return Exit.Success;
    }

    static string[]? CommaList(string? value) => value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    static ChannelPostRef PostRef(Invocation i, ChannelRef channel) => new() { Channel = channel, Sequence = OptionalLong(i, "sequence") ?? throw new CliException("missing_argument", "Missing --sequence.", Exit.Usage) };
    static async Task<ChannelPostDraft> PostDraftAsync(Invocation i, CancellationToken token) => i.Get("draft-file") is { } file ? Json.Read<ChannelPostDraft>(await File.ReadAllTextAsync(file, token)) : new() { Body = new() { ContentType = "text/plain", Text = i.Require("text") } };
    static async Task<ChannelRef> ResolveChannelAsync(Invocation i, MeshlineClient client, CancellationToken token)
    {
        if (i.Get("relay") is { } relay) return new() { RelayId = relay, ChannelId = i.Require("id") };
        await using var reader = await client.ChannelManager.GetFollowedAsync(cancellationToken: token);
        while (true)
        {
            var batch = await reader.ReadNextAsync(256, token);
            var found = batch.FirstOrDefault(c => c.Ref.ChannelId == i.Require("id"));
            if (found is not null) return found.Ref;
            if (batch.Count == 0) throw new CliException("relay_required", "Unknown followed channel. Supply --relay.", Exit.Usage);
        }
    }
}
