namespace FulcrumFS;

/// <summary>
/// Identifies the kind of repository corruption surfaced via the <see cref="FileRepo.CorruptionDetected"/> event.
/// </summary>
/// <remarks>
/// Convention: every code path that throws because of detected repository corruption MUST fire the
/// <see cref="FileRepo.CorruptionDetected"/> event (with the appropriate <see cref="RepoCorruptionKind"/>) before throwing, so consumers can observe and
/// repair corruption regardless of which entry point happened to detect it. The corresponding exception types are
/// <see cref="DanglingAliasException"/> (for <see cref="DanglingAlias"/>) and <see cref="RepoCorruptedException"/> (the umbrella for hard-fail kinds such
/// as <see cref="DuplicateVariantEntry"/> and <see cref="RebaseInconsistency"/>).
/// </remarks>
public enum RepoCorruptionKind
{
    /// <summary>
    /// The corruption kind is unknown or unspecified. This is the default value and should not be used by event consumers as a meaningful discriminator.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A variant alias was encountered whose source data file is missing. See <see cref="DanglingAliasException"/> and
    /// <see cref="DanglingAliasInfo"/> for the data carried alongside this kind.
    /// </summary>
    DanglingAlias,

    /// <summary>
    /// An alias marker was encountered whose filename cannot be parsed (or otherwise violates a structural invariant). The repository treats malformed alias
    /// markers as nonexistent.
    /// </summary>
    MalformedAlias,

    /// <summary>
    /// More than one file in a file group directory maps to the same logical variant slot (for example, two data files for the main file, or two alias
    /// markers for the same variant ID). Operations that surface this kind throw <see cref="RepoCorruptedException"/>; the slot cannot be safely resolved
    /// without manual intervention.
    /// </summary>
    DuplicateVariantEntry,

    /// <summary>
    /// A rebase operation cannot be resumed because required files are missing (for example, the chosen survivor has neither a materialized data file nor
    /// an alias marker, or the source data file is missing while the chosen variant is still an unmaterialized alias). Operations that surface this kind
    /// throw <see cref="RepoCorruptedException"/>.
    /// </summary>
    RebaseInconsistency,

    /// <summary>
    /// A rebase marker was observed whose source data, chosen data, and surviving alias dependents are all gone - leaving the marker with no derivable
    /// source extension and nothing to fork. The marker is residue from a rebase whose final marker-delete step was skipped or lost, and the cleaner will
    /// physically remove it. Surfaced for visibility only; no exception is thrown.
    /// </summary>
    OrphanRebaseMarker,
}
