<div class="article">

# Exception Handling

This guide covers the exceptions FulcrumFS raises, when they occur, and the event-based model used for commit and rollback failures (which are not thrown).

### Two failure styles

Most errors surface as exceptions you catch at the call site, which is the natural shape for input validation: a bad upload becomes an exception in the upload handler that you translate into an HTTP 400. Commit and rollback failures are different: because the affected files stay readable and the application has no useful way to "undo" a half-finished commit, they are reported through events and the files are marked indeterminate for later resolution. Understanding which style applies where is the key to handling failures correctly.

## Fetch Errors

Fetching a file or variant that does not exist throws <xref:FulcrumFS.RepoFileNotFoundException>. This applies to <xref:FulcrumFS.FileRepo.GetAsync*>, <xref:FulcrumFS.FileRepo.OpenAsync*>, and <xref:FulcrumFS.FileRepo.GetVariantAsync*>.

```csharp
// "GET /api/documents/{id}" handler.
try
{
    var info = await repo.GetAsync(doc.FileId);
    return Results.File(info.Open(), contentType: doc.ContentType);
}
catch (RepoFileNotFoundException)
{
    // The repository has no file for this row. Most likely the database
    // and repository have drifted apart, which warrants investigation.
    logger.LogError("Document {Id} references missing file {FileId}.", doc.Id, doc.FileId);
    return Results.NotFound();
}
```

## Processing Errors

A failure during add-time processing throws <xref:FulcrumFS.FileProcessingException>. This covers content that fails format validation (a `.jpg` upload that is really an EXE), a <xref:FulcrumFS.FileProcessingPipelineSelector> with no matching pipeline and no default (a file extension the endpoint does not accept), and errors raised by a processor (a corrupt image that ImageSharp cannot decode). Catch it around <xref:FulcrumFS.FileRepoTransaction.AddAsync*> to reject bad uploads.

```csharp
// "POST /api/photos" handler.
try
{
    var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline);
    // ... commit database, commit repository ...
    return Results.Ok();
}
catch (FileProcessingException ex)
{
    // The upload was rejected. The transaction will be rolled back on dispose.
    return Results.BadRequest(new { error = ex.Message });
}
```

> [!TIP]
> Place format validation as the first step in a pipeline so invalid content is rejected before any expensive transformation runs. See [Validating File Formats](file-formats.md).

#### Unchanged main sources

When a main-file pipeline sets <xref:FulcrumFS.FileProcessingPipeline.ThrowWhenMainSourceUnchanged> to `true`, adding content the pipeline leaves unchanged throws <xref:FulcrumFS.FileSourceUnchangedException>. This is useful for detecting an accidental re-add of identical content; for example, a re-import that is supposed to produce a transformed copy can flag the case where the input was already in the target format. See [Processing Pipelines](processing-pipelines.md).

## Variant Collision Errors

Adding a variant that already exists with <xref:FulcrumFS.FileRepo.AddVariantAsync*> throws <xref:System.InvalidOperationException>. Choose the right method for your intent:

- <xref:FulcrumFS.FileRepo.AddVariantAsync*> when the variant is *required* to be new (caller error otherwise).
- <xref:FulcrumFS.FileRepo.TryAddVariantAsync*> when you want to attempt the add and get `null` back on collision.
- <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*> when you want "ensure this variant exists" semantics, for example a lazy thumbnail generator that runs on first request.

See [File Variants](file-variants.md).

## Commit and Rollback Failures

Commit and rollback do not throw on failure. Instead, the affected files are marked indeterminate and remain readable, and the failure is reported through a single event:

- <xref:FulcrumFS.FileRepo.TransactionCompletionFailed> fires when either a <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> or a rollback (explicit or via disposal) cannot fully complete. The handler receives a <xref:FulcrumFS.RepoTransactionCompletionFailureInfo> exposing the failed <xref:FulcrumFS.RepoTransactionCompletionFailureInfo.Operation> (a <xref:FulcrumFS.RepoTransactionCompletionOperation> value of `Commit` or `Rollback`) and the underlying <xref:FulcrumFS.RepoTransactionCompletionFailureInfo.Exception> (an <xref:System.AggregateException> if multiple errors occurred during the step).

Subscribe to log or alert. If you want throwing behavior at the call site, the handler can raise an exception itself - handler exceptions are not caught and will propagate out of the completion call.

```csharp
repo.TransactionCompletionFailed += failure =>
{
    if (failure.Operation is RepoTransactionCompletionOperation.Commit)
        logger.LogError(failure.Exception, "Repository commit failed; affected files are indeterminate and will be resolved during cleanup.");
    else
        logger.LogWarning(failure.Exception, "Repository rollback failed; affected files are indeterminate.");

    return Task.CompletedTask;
};
```

> [!IMPORTANT]
> Indeterminate files left by these failures are resolved later during cleanup, where a callback decides per <xref:FulcrumFS.FileId> whether to <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> each one. This is what closes the loop on commit failures, so make sure cleanup is scheduled in any environment that runs the repository. See [Repository Cleanup](cleanup.md) and [Transactional Commit Model](../concepts/commit-model.md).

## Corruption Detection

FulcrumFS surfaces detected repository corruption through the <xref:FulcrumFS.FileRepo.CorruptionDetected> event. The handler is a <xref:FulcrumFS.RepoCorruptionHandler> that receives a <xref:FulcrumFS.RepoCorruptionInfo> describing the issue: a <xref:FulcrumFS.RepoCorruptionKind>, the affected <xref:FulcrumFS.FileId>, an optional variant ID, and a descriptive message. Every code path that throws because of repository corruption fires this event first, so consumers can observe and repair corruption regardless of which entry point detected it.

The currently detected kinds are:

- <xref:FulcrumFS.RepoCorruptionKind.DanglingAlias> - an alias marker whose source data file is missing. Cannot occur during normal API usage; indicates external interference (a partial backup restore, manual file deletion, a crash recovery failure). Surfaced at the call site as <xref:FulcrumFS.DanglingAliasException> (a subtype of <xref:FulcrumFS.RepoFileNotFoundException>).
- <xref:FulcrumFS.RepoCorruptionKind.MalformedAlias> - an alias marker whose filename cannot be parsed or otherwise violates a structural invariant. The repository treats malformed alias markers as nonexistent.
- <xref:FulcrumFS.RepoCorruptionKind.DuplicateVariantEntry> - more than one file in a file group directory maps to the same logical variant slot (for example, two data files for the main file, or two alias markers for the same variant ID). The slot cannot be safely resolved without manual intervention; throws <xref:FulcrumFS.RepoCorruptedException>.
- <xref:FulcrumFS.RepoCorruptionKind.RebaseInconsistency> - a rebase operation cannot be resumed because required files are missing (the chosen survivor has neither a materialized data file nor an alias marker, or the source data file is missing while the chosen variant is still an unmaterialized alias). Throws <xref:FulcrumFS.RepoCorruptedException>.
- <xref:FulcrumFS.RepoCorruptionKind.OrphanRebaseMarker> - a rebase marker observed with no source data, chosen data, or surviving alias dependents - residue from a rebase whose final marker-delete step was lost. Surfaced for visibility only; no exception is thrown and the marker is physically removed inline.

The handler is asynchronous (`Task`-returning) so consumers can perform I/O-bound logging or alerting work without resorting to sync-over-async. Handlers are awaited sequentially before the detecting operation returns, so keep them fast; any handler exception is not caught and will propagate out of the operation that fired the event.

```csharp
repo.CorruptionDetected += info =>
{
    logger.LogError(
        "Repository corruption detected: kind={Kind}, fileId={FileId}, variantId={VariantId}, message={Message}",
        info.Kind, info.FileId, info.VariantId, info.Message);
    return Task.CompletedTask;
};
```

In addition to the event, dangling aliases are surfaced at the call site:

- <xref:FulcrumFS.FileRepo.GetVariantAsync*> and <xref:FulcrumFS.FileRepo.OpenVariantAsync*> throw <xref:FulcrumFS.DanglingAliasException> (a subtype of <xref:FulcrumFS.RepoFileNotFoundException>).
- <xref:FulcrumFS.FileRepo.GetGroupAsync*> omits the dangling alias from <xref:FulcrumFS.RepoFileGroupInfo.VariantFiles> and exposes it via <xref:FulcrumFS.RepoFileGroupInfo.DanglingAliases>.
- Adding a variant over a dangling alias self-heals: the dangling marker is removed and the add proceeds normally.

See [Variant Aliasing](../concepts/variant-aliasing.md#dangling-aliases) for the full dangling-alias model.

## Next Steps

- [Transactional Commit Model](../concepts/commit-model.md) - The indeterminate state in depth.
- [Repository Cleanup](cleanup.md) - Resolving indeterminate files.
- [Adding and Committing Files](adding-files.md) - Where commit failures originate.

</div>
