namespace FulcrumFS;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when an add operation targets a variant ID that still has pending retirement state on disk from a prior
/// <see cref="FileRepo.DeleteVariantsAsync(FileId, ReadOnlySpan{string})"/> call that has not yet completed physical cleanup.
/// </summary>
/// <remarks>
/// <para>
/// This exception signals a <em>state conflict</em>, not a missing file: the retired variant's data file, alias marker, or delete marker still lingers in the
/// repository directory and the slot cannot accept a new add until the cleaner has physically removed the residue. Once cleanup completes the same variant
/// ID becomes addable again.</para>
/// <para>
/// In normal use callers should treat a retired variant ID as permanently gone; <see cref="FileRepo.DeleteVariantsAsync(FileId, ReadOnlySpan{string})"/>
/// is intended for permanent retirement and downstream consumers (for example static file hosts serving repo files by URL) typically depend on stable
/// variant identity. Reusing a retired ID is supported but is an advanced scenario (for example, regenerating variants after a deployment that produced
/// corrupt output) and must be done with awareness that consumers may briefly observe missing variants and will observe different content once the new
/// variant is added. See the <see cref="FileRepo.DeleteVariantsAsync(FileId, ReadOnlySpan{string})"/> remarks for guidance.</para>
/// </remarks>
public class RepoVariantPendingCleanupException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepoVariantPendingCleanupException"/> class with a specified message.
    /// </summary>
    public RepoVariantPendingCleanupException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoVariantPendingCleanupException"/> class with a specified message and an inner exception.
    /// </summary>
    public RepoVariantPendingCleanupException(string message, Exception? innerException) : base(message, innerException) { }
}
