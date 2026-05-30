namespace FulcrumFS;

/// <summary>
/// Extension methods that build the <see cref="RepoCorruptionInfo"/> for each corruption kind and dispatch it to a nullable corruption handler. Co-locates
/// every message template and the multicast invocation loop so callsites in <see cref="FileRepo"/> and <see cref="RepoFileSystem"/> read as a single
/// <c>handler.RaiseXxxAsync(...)</c> call without per-event sink properties or repeated null-check / message-composition boilerplate.
/// </summary>
internal static class RepoCorruptionEvents
{
    public static Task RaiseDanglingAliasAsync(this RepoCorruptionHandler? handler, FileId fileId, string variantId, string? sourceVariantId)
    {
        if (handler is null)
            return Task.CompletedTask;

        string sourceDescription = sourceVariantId is null ? "main file" : $"variant '{sourceVariantId}'";
        string message = $"File ID '{fileId}' variant '{variantId}' is an alias whose source ({sourceDescription}) was not found.";
        return InvokeAllAsync(handler, new RepoCorruptionInfo(RepoCorruptionKind.DanglingAlias, fileId, variantId, message));
    }

    public static Task RaiseMalformedAliasAsync(this RepoCorruptionHandler? handler, FileId fileId, string markerFileName)
    {
        if (handler is null)
            return Task.CompletedTask;

        string message = $"File ID '{fileId}' alias marker '{markerFileName}' is malformed; treating as nonexistent.";
        return InvokeAllAsync(handler, new RepoCorruptionInfo(RepoCorruptionKind.MalformedAlias, fileId, variantId: null, message));
    }

    public static Task RaiseDuplicateVariantEntryAsync(this RepoCorruptionHandler? handler, FileId fileId, string? variantId, string fileName1, string fileName2)
    {
        if (handler is null)
            return Task.CompletedTask;

        string slotDescription = variantId is null ? "main file" : $"variant '{variantId}'";
        string message = $"File ID '{fileId}' has multiple files mapping to {slotDescription}: '{fileName1}', '{fileName2}'.";
        return InvokeAllAsync(handler, new RepoCorruptionInfo(RepoCorruptionKind.DuplicateVariantEntry, fileId, variantId, message));
    }

    public static Task RaiseRebaseInconsistencyAsync(this RepoCorruptionHandler? handler, FileId fileId, string sourceVariantId, string chosenVariantId, string reason)
    {
        if (handler is null)
            return Task.CompletedTask;

        string message =
            $"File ID '{fileId}' rebase of source variant '{sourceVariantId}' onto '{chosenVariantId}' cannot proceed: {reason}";
        return InvokeAllAsync(handler, new RepoCorruptionInfo(RepoCorruptionKind.RebaseInconsistency, fileId, sourceVariantId, message));
    }

    public static Task RaiseOrphanRebaseMarkerAsync(this RepoCorruptionHandler? handler, FileId fileId, string sourceVariantId, string chosenVariantId)
    {
        if (handler is null)
            return Task.CompletedTask;

        string message =
            $"File ID '{fileId}' had an orphaned rebase marker for source variant '{sourceVariantId}' onto '{chosenVariantId}': " +
            $"no source data, chosen data, or surviving aliases remained to drive the rebase. The marker has been removed.";
        return InvokeAllAsync(handler, new RepoCorruptionInfo(RepoCorruptionKind.OrphanRebaseMarker, fileId, sourceVariantId, message));
    }

    private static async Task InvokeAllAsync(RepoCorruptionHandler handler, RepoCorruptionInfo info)
    {
        if (handler.HasSingleTarget)
        {
            await handler.Invoke(info).ConfigureAwait(false);
            return;
        }

        foreach (var singleHandler in handler.GetInvocationList().Cast<RepoCorruptionHandler>())
            await singleHandler.Invoke(info).ConfigureAwait(false);
    }
}
