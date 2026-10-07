using Meshline;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Text.Json;
using ClientInvite = Meshline.Models.Client.GroupInvite;
using ProtocolInvite = Meshline.Models.Protocol.GroupInvite;

namespace Meshline.Cli;

internal static partial class Commands
{
    static IReadOnlyList<CommandSpec> GroupCatalog =>
    [
        new("groups create", "Create an encrypted group.", ["relay", "name", "description", "capacity", "invite-policy"], [], []),
        new("groups list", "Read locally known groups.", ["relay", "membership", "role", "limit"], [], []),
        new("groups show", "Refresh group state from its relay.", ["relay"], [], ["id"]),
        new("groups sync", "Refresh the home account timeline, then await a fresh group synchronization pass.", ["relay"], [], ["id"]),
        new("groups update", "Update group metadata.", ["relay", "name", "description", "capacity", "invite-policy"], ["clear-description"], ["id"]),
        new("groups close", "Close a group owned by this account.", ["relay"], [], ["id"]),
        new("groups send", "Send an encrypted group message.", ["relay", "text", "draft-file"], [], ["id"]),
        new("groups messages", "Read local group history within optional exclusive sequence bounds.", ["sender", "limit", "after", "before"], [], ["id"]),
        new("groups nickname", "Set or clear your group nickname.", ["relay", "nickname"], ["clear"], ["id"]),
        new("groups invite create", "Create an invitation, optionally bound to an account.", ["relay", "expires", "account", "max-uses", "out"], [], ["id"]),
        new("groups invite list", "Read a page of group invitations.", ["relay", "limit", "cursor"], [], ["id"]),
        new("groups invite show", "Read an invitation's state.", ["relay", "invite"], [], ["id"]),
        new("groups invite revoke", "Revoke an invitation.", ["relay", "invite"], [], ["id"]),
        new("groups join", "Apply using a saved group invitation.", ["invite-file"], [], []),
        new("groups applications list", "Read a page of admission applications.", ["relay", "limit", "cursor"], [], ["id"]),
        new("groups applications approve", "Approve comma-separated account IDs.", ["relay", "accounts"], [], ["id"]),
        new("groups applications reject", "Reject comma-separated account IDs.", ["relay", "accounts"], [], ["id"]),
        new("groups members list", "Read locally synchronized group members.", ["relay", "role", "search", "limit"], [], ["id"]),
        new("groups members remove", "Remove comma-separated account IDs.", ["relay", "accounts"], [], ["id"]),
        new("groups members role", "Assign an allowed group role.", ["relay", "account", "role"], [], ["id"]),
        new("groups bans list", "Read locally synchronized bans.", ["relay", "search", "limit"], [], ["id"]),
        new("groups bans add", "Ban comma-separated account IDs.", ["relay", "accounts"], [], ["id"]),
        new("groups bans remove", "Unban comma-separated account IDs.", ["relay", "accounts"], [], ["id"]),
        new("groups transfer", "Transfer group ownership to a member.", ["relay", "account"], [], ["id"]),
        new("groups leave", "Leave a group.", ["relay"], [], ["id"]),
        new("groups keys rotate", "Rotate the group secret.", ["relay"], ["owner-member-key"], ["id"]),
        new("groups keys request", "Request group key recovery for this account.", ["relay"], [], ["id"]),
        new("groups keys requests", "Read a page of group key recovery requests.", ["relay", "limit", "cursor"], [], ["id"]),
        new("groups keys withdraw", "Withdraw your pending key recovery request.", ["relay"], [], ["id"]),
        new("groups keys approve", "Approve comma-separated key recovery accounts.", ["relay", "accounts"], [], ["id"]),
        new("groups keys reject", "Reject comma-separated key recovery accounts.", ["relay", "accounts"], [], ["id"])
    ];

    static async Task<int> ExecuteGroupAsync(Invocation i, MeshlineClient client, Output output, CancellationToken token)
    {
        var groups = client.GroupManager;
        object? result;
        switch (i.Command)
        {
            case "groups create":
                result = await groups.CreateGroupAsync(i.Get("relay") ?? HomeRelay(client), new()
                {
                    Name = i.Require("name"),
                    Description = i.Get("description"),
                    MemberCapacity = OptionalLong(i, "capacity") ?? 32,
                    InvitePolicy = EnumValue(i, "invite-policy", GroupInvitePolicy.Administrators)
                }, token); break;
            case "groups list":
                result = await TakeAsync(await groups.GetGroupsAsync(OptionalEnum<GroupMembershipState>(i, "membership"), OptionalEnum<GroupRole>(i, "role"), i.Get("relay"), token), Limit(i, 200), false, token); break;
            case "groups join":
                var envelope = Json.Read<InviteFile>(await File.ReadAllTextAsync(i.Require("invite-file"), token));
                var document = ProtocolModel.FromJson<ProtocolInvite>(envelope.Document.GetRawText()) ?? throw new CliException("invalid_invite", "Missing invitation.", Exit.Usage);
                var invite = new ClientInvite(envelope.RelayId, document);
                await groups.ApplyToGroupAsync(invite, token); result = new { applied = invite.Group }; break;
            case "groups messages":
                var range = History(i);
                result = await TakeHistoryAsync(await groups.GetMessagesAsync(i.Require("id"), i.Get("sender"), range, token), Limit(i), range, token); break;
            default:
                var group = await ResolveGroupAsync(i, client, token);
                result = new { group, operation = i.Command, completed = true };
                switch (i.Command)
                {
                    case "groups show": result = await groups.GetGroupAsync(group, token); break;
                    case "groups sync":
                        var accountSync = await client.MessageManager.SynchronizeAsync(HomeRelay(client), token);
                        if (accountSync.State != ResourceSyncState.CaughtUp) return await SyncResultAsync("account", accountSync, output);
                        return await SyncResultAsync("group", await groups.SynchronizeAsync(group, token), output);
                    case "groups update":
                        result = await groups.UpdateGroupAsync(group, new Meshline.Models.Client.GroupUpdate
                        {
                            Name = StringUpdate(i, "name"),
                            Description = StringUpdate(i, "description"),
                            MemberCapacity = OptionalLong(i, "capacity") is { } capacity ? capacity : default(FieldUpdate<long>),
                            InvitePolicy = i.Get("invite-policy") is not null ? EnumValue(i, "invite-policy", GroupInvitePolicy.Administrators) : default(FieldUpdate<GroupInvitePolicy>)
                        }, token); break;
                    case "groups close": await groups.CloseGroupAsync(group, token); break;
                    case "groups send":
                        ExactlyOne(i, "text", "draft-file");
                        var draft = i.Get("draft-file") is { } file ? Json.Read<GroupMessageDraft>(await File.ReadAllTextAsync(file, token)) : new() { Body = new() { ContentType = "text/plain", Text = i.Require("text") } };
                        result = await groups.SendMessageAsync(group, draft, token); break;
                    case "groups nickname":
                        if (i.Flag("clear") == (i.Get("nickname") is not null)) throw new CliException("invalid_argument", "Choose --nickname or --clear.", Exit.Usage);
                        await groups.SetNicknameAsync(group, i.Get("nickname"), token); break;
                    case "groups invite create":
                        if (i.Get("account") is not null && i.Get("max-uses") is not null) throw new CliException("invalid_argument", "Bound invitations cannot specify --max-uses.", Exit.Usage);
                        var created = i.Get("account") is { } account ? await groups.CreateInviteAsync(group, account, Expiry(i), token) : await groups.CreateInviteAsync(group, Expiry(i), OptionalLong(i, "max-uses"), token);
                        var exported = new InviteFile(created.Group.RelayId, JsonDocument.Parse(created.Document.ToJson()).RootElement.Clone());
                        if (i.Get("out") is { } path) await PrivateFiles.WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(exported, Json.Options), false, token);
                        result = exported; break;
                    case "groups invite list": result = await groups.GetInvitesAsync(group, Page(i), token); break;
                    case "groups invite show": result = await groups.GetInviteAsync(new(group, i.Require("invite")), token); break;
                    case "groups invite revoke": await groups.RevokeInviteAsync(new(group, i.Require("invite")), token); break;
                    case "groups applications list": result = await groups.GetApplicationsAsync(group, Page(i), token); break;
                    case "groups applications approve": await groups.ApproveApplicationsAsync(group, Accounts(i), token); break;
                    case "groups applications reject": await groups.RejectApplicationsAsync(group, Accounts(i), token); break;
                    case "groups members list": result = await TakeAsync(await groups.GetMembersAsync(group, OptionalEnum<GroupRole>(i, "role"), i.Get("search"), token), Limit(i, 200), false, token); break;
                    case "groups members remove": await groups.RemoveMembersAsync(group, Accounts(i), token); break;
                    case "groups members role": _ = i.Require("role"); await groups.SetRoleAsync(group, i.Require("account"), EnumValue(i, "role", GroupRole.Member), token); break;
                    case "groups bans list": result = await TakeAsync(await groups.GetBansAsync(group, i.Get("search"), token), Limit(i, 200), false, token); break;
                    case "groups bans add": await groups.BanAsync(group, Accounts(i), token); break;
                    case "groups bans remove": await groups.UnbanAsync(group, Accounts(i), token); break;
                    case "groups transfer": await groups.TransferOwnershipAsync(group, i.Require("account"), token); break;
                    case "groups leave": await groups.LeaveGroupAsync(group, token); break;
                    case "groups keys rotate": await groups.RotateSecretAsync(group, i.Flag("owner-member-key"), token); break;
                    case "groups keys request": result = await groups.RequestKeyRecoveryAsync(group, token); break;
                    case "groups keys requests": result = await groups.GetKeyRecoveryRequestsAsync(group, Page(i), token); break;
                    case "groups keys withdraw": await groups.WithdrawKeyRecoveryAsync(group, token); break;
                    case "groups keys approve": await groups.ApproveKeyRecoveryAsync(group, Accounts(i), token); break;
                    case "groups keys reject": await groups.RejectKeyRecoveryAsync(group, Accounts(i), token); break;
                    default: throw new CliException("unknown_command", "Unknown group command.", Exit.Usage);
                }
                break;
        }
        await output.ResultAsync(CommandResult.Success(result));
        return Exit.Success;
    }

    sealed record InviteFile(string RelayId, JsonElement Document);
    static T? OptionalEnum<T>(Invocation i, string key) where T : struct, Enum => i.Get(key) is null ? null : EnumValue<T>(i, key, default);
    static string[] Accounts(Invocation i) => i.Require("accounts").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    static PageRequest Page(Invocation i) => new() { Limit = Limit(i), Cursor = i.Get("cursor") };
    static async Task<GroupRef> ResolveGroupAsync(Invocation i, MeshlineClient client, CancellationToken token)
    {
        if (i.Get("relay") is { } relay) return new() { RelayId = relay, GroupId = i.Require("id") };
        await using var reader = await client.GroupManager.GetGroupsAsync(cancellationToken: token);
        while (true)
        {
            var batch = await reader.ReadNextAsync(256, token);
            var found = batch.FirstOrDefault(g => g.Ref.GroupId == i.Require("id"));
            if (found is not null) return found.Ref;
            if (batch.Count == 0) throw new CliException("relay_required", "Unknown local group. Supply --relay.", Exit.Usage);
        }
    }
}
