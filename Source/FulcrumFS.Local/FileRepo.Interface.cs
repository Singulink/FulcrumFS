namespace FulcrumFS;

/// <content>
/// Contains the explicit implementations of the <see cref="IFileRepo"/> members whose public counterparts return the narrower local types. Each forwards to the
/// public member and returns the same object.
/// </content>
partial class FileRepo
{
    /// <inheritdoc/>
    async ValueTask<IFileRepoTransaction> IFileRepo.BeginTransactionAsync() => await BeginTransactionAsync().ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<IFileRepoStagedTransaction> IFileRepo.BeginStagedTransactionAsync() => await BeginStagedTransactionAsync().ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<IRepoFile> IFileRepo.GetAsync(FileId fileId) => await GetAsync(fileId).ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<IRepoFileGroup> IFileRepo.GetGroupAsync(FileId fileId) => await GetGroupAsync(fileId).ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<IRepoFile> IFileRepo.GetVariantAsync(FileId fileId, string variantId) => await GetVariantAsync(fileId, variantId).ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<Stream> IFileRepo.OpenAsync(FileId fileId, FileAccessPattern accessPattern) => await OpenAsync(fileId, accessPattern).ConfigureAwait(false);

    /// <inheritdoc/>
    async ValueTask<Stream> IFileRepo.OpenVariantAsync(FileId fileId, string variantId, FileAccessPattern accessPattern) =>
        await OpenVariantAsync(fileId, variantId, accessPattern).ConfigureAwait(false);

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> IFileRepo.AddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>>.Upcast(AddVariantAsync(fileId, variantId, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> IFileRepo.AddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>>.Upcast(AddVariantAsync(fileId, variantId, sourceVariantId, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>?> IFileRepo.TryAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>?>.Upcast(TryAddVariantAsync(fileId, variantId, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>?> IFileRepo.TryAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>?>.Upcast(TryAddVariantAsync(fileId, variantId, sourceVariantId, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> IFileRepo.GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>>.Upcast(GetOrAddVariantAsync(fileId, variantId, pipeline, cancellationToken));
    }

    /// <inheritdoc/>
    TaskWithProgress<IReadOnlyList<IRepoFile>> IFileRepo.GetOrAddVariantAsync(
        FileId fileId,
        string variantId,
        string? sourceVariantId,
        IFileProcessingPipelineSelector pipeline,
        CancellationToken cancellationToken)
    {
        return TaskWithProgress<IReadOnlyList<IRepoFile>>.Upcast(GetOrAddVariantAsync(fileId, variantId, sourceVariantId, pipeline, cancellationToken));
    }
}
