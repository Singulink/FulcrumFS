<div class="article">

# Crash Recovery and Invariants

FulcrumFS is designed so that a crash at any moment (a process killed with kill -9, an operating system crash, a power loss, a host VM yanked out from under it) leaves the repository in a state that converges to correct once recovery runs. This article describes the forward-only write model, the commit point that defines durability, the flushes to disk that carry it through a power loss, and the rules the cleaner uses to finish interrupted work.

### The recovery philosophy

There is no write-ahead log or index to replay. The set of files and markers on disk *is* the state. Every operation writes markers in an order chosen so that, no matter where a crash interrupts it, the surviving on-disk state is unambiguous: either the operation had not reached its commit point (and is rolled forward to "never happened") or it had (and is rolled forward to "fully done").

> [!NOTE]
> This is why a FulcrumFS repository directory is safe to copy, restore from backup, or move between machines: there is no external state to keep in sync with the on-disk content. A bit-faithful copy of the directory is a valid repository.

## Forward-Only Writes

Operations only ever move state forward. They never rewrite an earlier marker into a contradictory one, which means a partially complete operation can always be completed by re-running the same logic rather than by undoing it. The cleaner is just "the same logic, run again from scratch".

#### The commit point

For both adds and retirements, the commit point is the appearance of the authoritative delete marker or marker that fetches consult, not the eventual deletion of bytes. A data file may physically linger after its in-group delete marker exists; fetches already treat the variant as gone because they read the delete marker first. This decouples "logically committed" from "physically reclaimed" and lets reclamation be lazy and idempotent. A main file's delete marker in the cleanup directory, or a committed delete journal that lists it, is the commit point of its deletion in the same way, but fetches of a main file do not consult it: a deleted main file stays readable until the cleaner removes it, by design (see [Deleting Files and Variants](../guides/deleting-files.md)).

In practice, this is what lets a fetch in progress when a delete commits keep streaming until the reader closes the file: the delete is logical the moment the marker appears, but the bytes stay around until the cleaner's grace window elapses.

## Marker Roles in Recovery

Two distinct markers cooperate to make deletion crash-safe, as introduced in [Repository Layout](repository-layout.md):

- The **in-group delete marker** `{variantId}.del` is the authoritative commit record. If it exists, the deletion is logically committed.
- The **cleanup hint** `cleanup/{fileId} {variantId}.del` is the cleaner's re-entry token. It can exist before the commit point is reached.

The relationship between the two is what disambiguates a crash:

#### Hint without delete marker

A cleanup hint with no matching in-group delete marker means the operation never reached its commit point. The cleaner sweeps the stray hint and leaves the variant live. Nothing was lost.

#### Delete marker present

An in-group delete marker means the deletion committed. The cleaner completes any remaining physical reclamation and survivor promotion, then removes the hint. Re-running this is idempotent because the survivor is chosen deterministically.

## Delete Journals

A [staged transaction](commit-model.md#staged-transactions) records its deletions in one journal file in the cleanup directory instead of one marker per file, on a repository of [format version](repository-layout.md#format-version) 2 or later. On a version 1 repository it writes the same per-file markers as an ordinary transaction instead, and after a crash the cleaner resolves its files like any other [indeterminate](commit-model.md#the-indeterminate-state) file. The journal is written and flushed to disk before staging returns and is held open by the transaction. When the transaction commits, the journal is renamed from `{journalId}.staged` to `{journalId}.journal`, a single atomic operation however many files it lists, and the rename is flushed to disk too. The journal ends with the number of file IDs it lists and a checksum of them, so a journal that a crash left half-written, or that was damaged on disk, is never taken for a valid one.

An ordinary transaction that commits two or more deletions in a deferred delete mode, on a repository of format version 2 or later, records them in a journal too, at commit time and in one go: it writes the journal under its staged name, flushes it, renames it to committed and flushes the rename, all while it still holds the indeterminate markers of its files, and only then removes those markers. A crash before the rename leaves a staged journal and the files' indeterminate markers, which the cleaner resolves through the callback; a crash after it leaves the committed journal, which deletes the files, and possibly some of their indeterminate markers, which do not hold the deletion back: the cleaner removes them once the journal has deleted the files, or earlier through the callback. If the journal cannot be written, the transaction records each deletion with a delete marker of its own instead.

The cleaner handles each state of a journal as follows.

#### Staged and held

The transaction that wrote the journal is still running and can still commit or roll back. The cleaner skips the journal, the same way it skips an indeterminate marker that a transaction holds open.

#### Staged and not held

The process ended between staging and committing (or while an ordinary transaction was writing its journal), so whether the application committed its database is unknown. The cleaner treats every listed file that still exists like an indeterminate file and asks the resolution callback: <xref:FulcrumFS.IndeterminateResolution.Keep> leaves the file alone, and <xref:FulcrumFS.IndeterminateResolution.Delete> deletes it (or, with a delete delay, gives it a delete marker that starts the delay). Once every file is resolved the journal is removed; a pass without a callback leaves it for one that has one.

#### Staged and torn

A staged journal that fails validation was never completely written and flushed, so staging never returned and the application never committed. The cleaner deletes the journal and leaves the files alone. A torn journal left by an ordinary transaction's commit is discarded the same way: that transaction's files still have their indeterminate markers, which the cleaner resolves through the callback.

#### Committed

The deletion committed, and the journal is the delete record of every file it lists. Once it is older than the delete delay, the cleaner deletes each listed file that still exists and then the journal. A file that is already gone needs nothing, so a pass that stops halfway simply finishes on the next one. In <xref:FulcrumFS.DeleteMode.Immediate> mode a committing staged transaction deletes the files itself and removes the journal straight away; the cleaner only sees the journal if that was interrupted. (An ordinary transaction writes no journal in that mode.)

#### Committed and damaged

A committed journal was complete and flushed to disk before it was renamed, so if it fails validation the storage was damaged or something outside the library changed it. The cleaner deletes nothing on its say-so: it leaves the journal in place, finishes the rest of the pass and then throws an <xref:System.IO.IOException> that names the journal.

## Deterministic Recovery of Retirements

A retirement can be interrupted midway through promoting a survivor and re-pointing siblings. Recovery is safe because the survivor-promotion rule is deterministic: the survivor is the earliest by `(CreationTimeUtc, VariantId)`, with the variant ID as a stable tiebreaker. A cleaner finishing an interrupted retirement therefore selects exactly the survivor the foreground operation would have, and the rebase plan marker pins the chosen root so the decision is not re-litigated. See [Retiring Variants](../guides/file-variants.md#retiring-variants) for the promotion rule in context.

## What the Cleaner Does

<xref:FulcrumFS.FileRepo.CleanAsync*> performs recovery and reclamation in one pass:

- Completes interrupted adds and retirements by rolling them forward.
- Removes deferred deletions whose grace period has elapsed, including the files listed in committed delete journals.
- Sweeps stray hints and orphaned temporary work.
- Resolves indeterminate files, and the files listed in delete journals whose staged transaction did not finish, through the caller-supplied resolution callback, which returns <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> per <xref:FulcrumFS.FileId>. If a transaction starts deleting a file while the pass is resolving it and the callback answers <xref:FulcrumFS.IndeterminateResolution.Keep>, the file's marker is left in place, because it now records that deletion. If the callback answers <xref:FulcrumFS.IndeterminateResolution.Delete>, the pass deletes the file, or records its deletion, before it removes the marker, and the transaction's own deletion of the file then has nothing left to do.

> [!IMPORTANT]
> Because every step is idempotent and forward-only, running the cleaner more often simply reclaims space sooner; it never corrupts state. There is no "cleaner is already running" hazard to avoid. See [Repository Cleanup](../guides/cleanup.md) for scheduling guidance.

## Durability Across Power Loss

A process crash leaves intact everything the operating system has already accepted. A power loss or an operating system crash can also drop writes the file system had not yet committed to disk. FulcrumFS flushes to disk at the points where a call that returned relies on what it wrote, so the guarantees in this article hold in that case too. Flushing is always on.

#### Why a few flushes are enough

Journalled file systems (ext4, XFS, NTFS, APFS) apply metadata operations such as creating, renaming and deleting files atomically and in order, but commit their journal lazily (ext4 every five seconds by default). After a power loss the file system is therefore at an earlier consistent point in time: recent operations may be missing, but a later operation never survives without the earlier ones. Flushing a file forces the journal up to and including that file, so everything that happened before it is committed with it. On Unix the library also flushes the directories whose entries changed, which is what POSIX requires to make a new or renamed entry durable.

#### What is flushed

- **Adds.** Before <xref:FulcrumFS.FileRepoTransaction.AddAsync*> returns, every data file it stored (the main file and the data files of its variants) is flushed at its final location, together with the directories that received it. A variant added later is flushed before it is moved into its file group.
- **File deletions.** The marker that records a pending deletion is flushed before <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*> returns. In the deferred delete modes, <xref:FulcrumFS.FileRepoTransaction.CommitAsync*> flushes the record of the deletion before it removes that pending marker: the file's delete marker, or the delete journal of a transaction that commits two or more deletions on a version 2 repository, whose content is flushed before its rename to committed and the rename after it. The cleaner does the same with the delete marker when it resolves an indeterminate file to <xref:FulcrumFS.IndeterminateResolution.Delete> with a delete delay.
- **Staged deletions.** The delete journal of a staged transaction is flushed, once for all of its files, before <xref:FulcrumFS.FileRepoStagedTransaction.StageAsync*> returns, and its rename to committed is flushed before <xref:FulcrumFS.FileRepoStage.CommitAsync*> goes on to commit anything else. On a version 1 repository, which has no journals, the marker of each file is flushed before staging returns and each deletion is committed as in an ordinary transaction.
- **Variant retirements.** The in-group delete markers (the commit point) are flushed before anything else happens, and a promoted survivor's copy is flushed before it is renamed into a data file, so a survivor can never be a torn copy.

Temporary work files, cleanup hints written before a commit point, rebase plan markers and deletions themselves are not flushed. Recovery either recomputes what they record or, by the ordering above, they can only come back after a power loss together with a marker that leads the cleaner to finish the work.

#### What a power loss can still do

- **Undo a recent deletion.** A file or variant deleted shortly before the power loss can come back on disk. It only ever comes back together with the marker that records its deletion, so the cleaner deletes it again on its next pass.
- **Lose a variant added in the last moments.** On Windows, a variant add flushes the variant's data before moving it into place but not the move itself, because a reader may already have the moved file open. A variant added just before a power loss can therefore be missing afterwards. Variants are recreatable, for example with <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*>, so this is accepted.

## Summary

The invariants that hold across any crash are straightforward to state:

- On-disk markers fully describe intended state; there is no external index to desynchronize.
- Writes are forward-only, so interrupted operations are completed, never undone.
- The commit point is a marker's existence, not byte deletion, so reclamation is lazy and idempotent.
- Survivor selection is deterministic, so recovery reproduces the foreground decision exactly.
- What a returned call relies on is flushed to disk first, so the invariants hold across power loss and operating system crashes, not only process crashes.

## Further Reading

- [Transactional Commit Model](commit-model.md) - Commit ordering and the indeterminate state.
- [Repository Layout](repository-layout.md) - The markers referenced throughout this article.
- [Repository Cleanup](../guides/cleanup.md) - Running and scheduling the cleaner.

</div>
