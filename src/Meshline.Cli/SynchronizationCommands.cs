using Meshline;
using Meshline.Models.Client;

namespace Meshline.Cli;

internal static partial class Commands
{
    static async Task<object> StatusAsync(MeshlineClient client, Invocation? invocation, CancellationToken token)
    {
        var relay = invocation?.Get("relay");
        var group = invocation?.Get("group");
        var channel = invocation?.Get("channel");
        if (new[] { relay, group, channel }.Count(value => value is not null) > 1)
            throw new CliException("invalid_argument", "Choose at most one of --relay, --group or --channel.", Exit.Usage);
        object? synchronization = null;
        if (group is not null) synchronization = SyncStatus("group", await client.GroupManager.GetSyncStatusAsync(group, token));
        else if (channel is not null) synchronization = SyncStatus("channel", await client.ChannelManager.GetSyncStatusAsync(channel, token));
        else if ((relay ?? client.Route?.RelayId) is { } accountRelay)
            synchronization = SyncStatus("account", await client.MessageManager.GetSyncStatusAsync(accountRelay, token));
        return new
        {
            client.Options.AccountId,
            lifecycle = client.LifecycleState,
            client.Route,
            device = client.Device is null ? null : new { id = client.Device.GetDeviceId(client.Context), certificate = client.Device },
            client.DeviceState,
            synchronization
        };
    }

    internal static object SyncStatus(string kind, ResourceSyncStatus status)
    {
        var error = status.Error is null ? null : CliApplication.MapError(status.Error);
        return new
        {
            kind,
            status.Resource,
            status.State,
            status.LastSynchronizedAt,
            status.BlockReason,
            error = error is null ? null : new ErrorInfo(error.Code, error.Message, error.Details),
            status.HasRetentionGap
        };
    }

    internal static async Task<int> SyncResultAsync(string kind, ResourceSyncStatus status, Output output)
    {
        var result = SyncStatus(kind, status);
        if (status.State != ResourceSyncState.CaughtUp)
            throw new CliException("sync_incomplete", "The synchronization pass did not catch up. Inspect the resource's block reason and retry after resolving it.", Exit.Pending, result);
        await output.ResultAsync(CommandResult.Success(result));
        return Exit.Success;
    }
}
