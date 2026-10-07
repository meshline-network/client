using Meshline.Models.Client;
using Meshline.Storage;

namespace Meshline.Cli;

internal static partial class Commands
{
    static HistoryRange? History(Invocation invocation)
    {
        var after = OptionalLong(invocation, "after");
        var before = OptionalLong(invocation, "before");
        if (after is null && before is null) return null;
        var range = new HistoryRange { After = after, Before = before };
        range.Deconstruct(out _, out _);
        return range;
    }

    internal static async Task<object> TakeHistoryAsync<T>(QueryReader<T> reader, int limit, HistoryRange? range, CancellationToken token)
    {
        // Keep the existing latest-history default. Explicit ranges use SDK paging
        // rather than inventing a sequence sentinel or scanning the entire history.
        if (range is null) return await TakeAsync(reader, limit, true, token);
        await using (reader)
        {
            var batch = await reader.ReadNextAsync(limit + 1, token);
            var backward = range.Before is not null;
            var items = backward ? batch.TakeLast(limit).ToArray() : batch.Take(limit).ToArray();
            return new { items, hasMore = batch.Count > limit, source = "local-sdk-snapshot", selection = backward ? "before" : "after", scanned = batch.Count };
        }
    }
}
