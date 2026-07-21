<div class="article">

# Crash Recovery and Invariants

FulcrumFS is designed so that a crash at any moment (a power loss, a kill -9, a host VM yanked out from under it) leaves the repository in a state that converges to correct once recovery runs. This article describes the forward-only write model, the commit point that defines durability, and the rules the cleaner uses to finish interrupted work.

### The recovery philosophy

There is no separate journal or index to replay. The set of files and markers on disk *is* the state. Every operation writes markers in an order chosen so that, no matter where a crash interrupts it, the surviving on-disk state is unambiguous: either the operation had not reached its commit point (and is rolled forward to "never happened") or it had (and is rolled forward to "fully done").

> [!NOTE]
> This is why a FulcrumFS repository directory is safe to copy, restore from backup, or move between machines: there is no external state to keep in sync with the on-disk content. A bit-faithful copy of the directory is a valid repository.

## Forward-Only Writes

Operations only ever move state forward. They never rewrite an earlier marker into a contradictory one, which means a partially complete operation can always be completed by re-running the same logic rather than by undoing it. The cleaner is just "the same logic, run again from scratch".

#### The commit point

For both adds and retirements, the commit point is the appearance of the authoritative delete marker or marker that fetches consult, not the eventual deletion of bytes. A data file may physically linger after its in-group delete marker exists; fetches already treat the variant as gone because they read the delete marker first. This decouples "logically committed" from "physically reclaimed" and lets reclamation be lazy and idempotent.

In practice, this is what lets a fetch in progress when a delete commits keep streaming until the reader closes the file: the delete is logical the moment the marker appears, but the bytes stay around until the cleaner's grace window elapses.

## Marker Roles in Recovery

Two distinct markers cooperate to make deletion crash-safe, as introduced in [Repository Layout](repository-layout.md):

- The **in-group delete marker** `{variantId}.del` is the authoritative commit record. If it exists, the deletion is logically committed.
- The **cleanup hint** `cleanup/{fileId}.{variantId}.del` is the cleaner's re-entry token. It can exist before the commit point is reached.

The relationship between the two is what disambiguates a crash:

#### Hint without delete marker

A cleanup hint with no matching in-group delete marker means the operation never reached its commit point. The cleaner sweeps the stray hint and leaves the variant live. Nothing was lost.

#### Delete marker present

An in-group delete marker means the deletion committed. The cleaner completes any remaining physical reclamation and survivor promotion, then removes the hint. Re-running this is idempotent because the survivor is chosen deterministically.

## Deterministic Recovery of Retirements

A retirement can be interrupted midway through promoting a survivor and re-pointing siblings. Recovery is safe because the survivor-promotion rule is deterministic: the survivor is the earliest by `(CreationTimeUtc, VariantId)`, with the variant ID as a stable tiebreaker. A cleaner finishing an interrupted retirement therefore selects exactly the survivor the foreground operation would have, and the rebase plan marker pins the chosen root so the decision is not re-litigated. See [Retiring Variants](../guides/file-variants.md#retiring-variants) for the promotion rule in context.

## What the Cleaner Does

<xref:FulcrumFS.FileRepo.CleanAsync*> performs recovery and reclamation in one pass:

- Completes interrupted adds and retirements by rolling them forward.
- Removes deferred deletions whose grace period has elapsed.
- Sweeps stray hints and orphaned temporary work.
- Resolves indeterminate files through the caller-supplied resolution callback, which returns <xref:FulcrumFS.IndeterminateResolution.Keep> or <xref:FulcrumFS.IndeterminateResolution.Delete> per <xref:FulcrumFS.FileId>.

> [!IMPORTANT]
> Because every step is idempotent and forward-only, running the cleaner more often simply reclaims space sooner; it never corrupts state. There is no "cleaner is already running" hazard to avoid. See [Repository Cleanup](../guides/cleanup.md) for scheduling guidance.

## Summary

The invariants that hold across any crash are straightforward to state:

- On-disk markers fully describe intended state; there is no external index to desynchronize.
- Writes are forward-only, so interrupted operations are completed, never undone.
- The commit point is a marker's existence, not byte deletion, so reclamation is lazy and idempotent.
- Survivor selection is deterministic, so recovery reproduces the foreground decision exactly.

## Further Reading

- [Transactional Commit Model](commit-model.md) - Commit ordering and the indeterminate state.
- [Repository Layout](repository-layout.md) - The markers referenced throughout this article.
- [Repository Cleanup](../guides/cleanup.md) - Running and scheduling the cleaner.

</div>
