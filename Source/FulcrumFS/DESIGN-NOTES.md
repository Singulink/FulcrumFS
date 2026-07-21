# FulcrumFS Variant Storage: Design Invariants

Internal reference for the variant alias and retirement subsystem. These are the durable invariants the
implementation relies on; the step-by-step implementation plans that produced this design have been retired
(see git history for `ALIAS-MARKERS.md` and `VARIANT-RETIREMENT.md` if the rationale is needed).

## On-disk layout

A file group lives in `files/{shard}/{file-id}/` and holds data files, alias markers, and in-group markers
as siblings:

- **Data file** - `{variantId}.{ext}` (the main file uses the `$main` sentinel as its `variantId`).
- **Alias marker** - `{variantId}.{sourceVariantId}.{sourceExt}.alias`. Zero-byte; all information is in the
  filename. `sourceVariantId` is either a normalized variant ID or the `$main` sentinel.
- **In-group delete marker** - `{variantId}.del`. Authoritative "pending cleanup" marker read by fetch to
  fail-fast while data files may still linger.
- **Rebase plan marker** - `{sourceVariantId}.{chosenVariantId}.rebase`. Pins the chosen new root of a
  subtree once a retirement rebase begins mutating the directory.

Cleaner coordination uses `cleanup/{shard}/{fileId}.{variantId}.del` hints as the cleaner re-entry token,
separate from the in-group delete marker.

## Naming and parsing invariants

- Normalized variant IDs cannot contain `.`, so an alias marker's `NameWithoutExtension` splits on `.` into
  exactly three segments `[variantId, sourceVariantId, sourceExt]`. Parsing is unambiguous.
- `$main` cannot pass `VariantId.IsValidAndNormalized`, so a caller can never collide with the main-file
  sentinel.
- A real `{variantId}.{ext}` data file and a `{variantId}.*.alias` marker for the same `variantId` are
  mutually exclusive. The add path enforces this before any move.

## Alias resolution invariants

- **Chain compression:** every alias marker's `(sourceVariantId, sourceExt)` names a *real data file*, never
  another alias. Enforced at write time by inheriting the parent staged node's root variant/extension; an
  alias whose static-tree source is itself aliased compresses through to the resolved root.
- **No cycles:** follows trivially from chain compression (aliases never point through other aliases).
- **O(1) resolution:** resolution constructs the source path directly (`{groupDir}/{sourceVariantId}.{sourceExt}`)
  with a single stat, no `readdir`. Read-time defense: if the direct path is missing or unexpectedly lands on
  another `.alias` file, fall back to glob and log a corruption warning; if still missing, treat as dangling.
- **Dangling alias (source missing):** `GetVariantAsync` throws `RepoFileNotFoundException`; `GetGroupAsync`
  omits the entry rather than throwing.

## Pipeline property semantics

- `AliasWhenVariantSourceUnchanged` (variant-only): when a variant pipeline produces no changes, write an
  alias marker instead of omitting the variant. The variant becomes first-class addressable.
- `ThrowWhenMainSourceUnchanged` (main-only): when the transactional main pipeline produces no changes, throw
  `FileSourceUnchangedException` to the caller.
- A variant run never throws `FileSourceUnchangedException` unless `AliasWhenVariantSourceUnchanged` is set; a
  main run never throws it unless `ThrowWhenMainSourceUnchanged` is set.

## Add-path invariants

- The lock set includes the resolved source variant (through aliases, the resolved root) so the source cannot
  be retired underneath an in-flight add. Locks are acquired in sorted order; main (`null`) sorts first.
- `GetOrAdd` fast path treats an `.alias` marker as "exists" and resolves it directly. A dangling marker
  aborts the fast path and falls through to the locked path so the variant can be recreated.
- Only the variant IDs being *created* (the root variant ID plus declared nested variants) participate in
  collision detection. The source variant ID is locked and gated against retirement but excluded from
  collision detection, since it is required to already exist to serve as a source. Naming an existing source
  therefore works with all three add methods (real-data or alias variants alike).

## Retirement (DeleteVariantsAsync) invariants

The caller passes the full set of variant IDs they own and want retired. Any variant *not* listed that
depends on a listed variant is preserved by promoting a survivor to a real data file and re-pointing
siblings.

- **No accidental data loss:** a caller cannot retire a variant another consumer depends on without listing
  it; the system preserves the rest.
- **Order independence:** inputs are deduped and sorted internally; the outcome is invariant under input
  ordering. Empty list and already-retired/never-added IDs are silent no-ops.
- **One rebase per affected subtree per call:** with the full delete set known up front, each subtree
  rebases at most once.
- **Survivor-promotion rule:** the chosen survivor is the earliest by `(CreationTimeUtc, VariantId)`.
  `VariantId` is the deterministic tiebreaker so cleaner recovery picks the same survivor the foreground
  would have.

## Crash-safety / write-order invariants

The foreground write order is strictly forward-only. There is no rollback; the only cleanup on failure is
lock release. A crash at any checkpoint leaves a state the cleaner (or the next overlapping delete) can drive
to completion.

1. **Pre-commit (inert scaffolding):** write all cleanup-dir hints. A hint without a matching in-group
   delete marker means nothing; the cleaner sweeps it and the variant stays live. The caller never received
   success, so this state must NOT be rolled forward.
2. **Commit point:** write all in-group delete markers. Fetches now observe every listed variant as retired. A
   crash at or after this point rolls *forward* to completion.
3. **Post-commit, per subtree needing rebase:** write the `{source}.{chosen}.rebase` marker BEFORE
   materializing `chosen` (rename alias -> data file after streaming), then re-point siblings. The marker
   must precede materialization: once the rename mutates the directory, deterministic recomputation is no
   longer unambiguous, so the marker is what lets recovery attribute a half-renamed subtree. A crash after
   commit but before a subtree's marker exists is safe - the directory is still pristine for that subtree and
   `chosen` is recomputed deterministically.
4. **Teardown (immediate mode):** delete retired alias markers, delete retired data files, then tear down
   markers in the order: rebase markers -> in-group delete markers (a delete marker must outlive its data file) ->
   cleanup-dir hints last (releasing the cleaner re-entry token).

**Rebase marker absence after commit is safe** because the survivor-promotion rule is fully deterministic; the
cleaner recomputes the same `chosen` from a pristine committed state.

## Cleaner recovery invariants

On finding a cleanup hint `{fileId}.{variantId}.del`:

1. Take ownership via the exclusive-marker takeover. If another holder is active (foreground in-flight or
   another cleaner), skip.
2. If the in-group delete marker for the hint's variant is *absent*, the operation never committed -> sweep the
   stray hint and stop; the variant stays live.
3. Resume each `.rebase` marker in the group via the same code the foreground runs (resume from
   materialization, or from sibling re-point, or recompute `chosen` if no marker but the source has a delete marker
   with surviving aliases).
4. Tear down remaining state: data file, alias marker, in-group delete marker (after data is gone), cleanup hint.

All operations are idempotent - re-running against partially-completed state converges. The on-disk state
(which variants have delete markers + which `.rebase` markers exist) fully describes the intended outcome; no
policy tag is encoded in marker filenames.

## Dirty-state detection

Dirty state == any in-group `.del` delete marker or any `.rebase` marker present in the group directory. Both the
add fast-path and the delete planner already enumerate the group directory, so detection is two extra
extension filters folded into an existing pass - no background scan, no sidecar index.

- **Delete** drives completion only for *overlapping* state (a listed variant that is the `chosen` or
  `source` of a prior incomplete rebase). Non-overlapping prior rebases are left for the cleaner.
- **Add** detects but never drives completion; an existing delete marker for the ID being added throws
  `RepoVariantPendingCleanupException`. Unrelated in-flight rebases never touch the ID being added (you
  cannot alias a source that has a delete marker).

## Foreground reentrancy (delete entry-scan)

On `DeleteVariantsAsync` entry, after acquiring locks and before writing new state:

1. Drop IDs that already have delete markers from the active set (idempotent).
2. For each `.rebase` marker overlapping the touched subtrees, attempt to take over its cleanup hint:
   takeover succeeds -> prior foreground crashed, run the cleaner resume logic synchronously; takeover fails
   -> another foreground op is genuinely in flight, throw a transient retry exception.
3. Proceed with the new delete. Non-overlapping `.rebase` markers are ignored (left for the cleaner).
