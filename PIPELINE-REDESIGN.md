# Plan: Pipeline provider abstraction + per-format selection + auto-variants

## TL;DR
Introduce `IFileProcessingPipelineProvider` implemented by `FileProcessor`, `FileProcessingPipeline`, and a new `FileProcessingPipelineSelector`. Widen every public `AddAsync` / `AddVariantAsync` / `GetOrAddVariantAsync` / `TryAddVariantAsync` overload that currently takes `FileProcessingPipeline` to take the interface. The interface has a single member `GetPipeline(string extension)` called when the source extension is known. `FileProcessingPipelineSelector` maps `FileFormat` (or raw extensions) to nested providers with an optional default fallback. **Pipelines can declare an optional `Variants` collection that runs automatically after the main pipeline against the main output, recursively** (variants-of-variants supported as first-class). Rename `ThrowWhenSourceUnchanged` to `SkipWhenSourceUnchanged`: in any variant context (top-level of `AddVariantAsync` or nested) the runner silently omits the variant from the returned collection and chains children to the parent source; only the main pipeline of `AddAsync` still throws `FileSourceUnchangedException` (its result has a required `Main`). Add calls use **batch-move semantics**: all pipelines run to `temp/` first; moves into `files/` happen as a single batch only after the entire tree succeeds. `FileRepoTransaction.AddAsync` returns `RepoFileGroupInfo`; variant-add family returns `IReadOnlyList<RepoFileInfo>` (or null from `Try*` for collision/timeout).

## Steps

### Phase 1 - Provider abstraction
1. Add `IFileProcessingPipelineProvider` to `FulcrumFS` with `FileProcessingPipeline GetPipeline(string extension)`.
2. Implement on `FileProcessingPipeline` returning `this`.
3. Implement on `FileProcessor`, caching a single-processor pipeline lazily (`_selfPipeline ??= ToPipeline()`). Keep public `ToPipeline()` for callers who need to configure `SourceBufferingMode` / `SkipWhenSourceUnchanged`.
4. Document on the interface: called once per node, with the lowercase normalized extension (leading dot) of the source being given to that node.

### Phase 2 - Variants on pipelines
1. Add `FileProcessingPipeline.Variants { get; init; } = []` of type `IReadOnlyList<FileProcessingVariant>`.
2. New `public sealed record FileProcessingVariant(string VariantId, IFileProcessingPipelineProvider Pipeline)`. No `Optional` flag - skip semantics come from the variant pipeline's own `SkipWhenSourceUnchanged`.
3. Add fluent `WithVariant(string variantId, IFileProcessingPipelineProvider pipeline)` on `FileProcessingPipeline` (returns a new pipeline with the variant appended).
4. Add `WithVariant(...)` on `FileProcessor` (sugar for `ToPipeline().WithVariant(...)`).
5. Validate at construction: no duplicate variant ids within a single pipeline's `Variants` list. (Duplicates across recursion levels are fine - they live under different parent file groups conceptually, but for *this* file group's flat namespace they must be unique. See decision below.)

### Phase 3 - Rename `ThrowWhenSourceUnchanged` -> `SkipWhenSourceUnchanged`
1. Rename property on `FileProcessingPipeline`. Update xmldoc to describe both behaviors:
   - Set on the **main pipeline of `AddAsync`**: throws `FileSourceUnchangedException` to the caller when the source is unchanged (there is no graceful way to omit the required `Main` from `RepoFileGroupInfo`).
   - Set on **any variant pipeline** (including the root variant of `AddVariantAsync` / `GetOrAddVariantAsync` / `TryAddVariantAsync`, and any nested variant): the runner silently skips storage and continues processing the variant's children against the parent source. Skipped variants are omitted from the returned collection.
2. Keep `FileSourceUnchangedException` name unchanged - it's still the mechanism the runner catches internally and that the main-file `AddAsync` path surfaces to callers.
3. Update all internal references and existing usage sites.

### Phase 4 - Wire the Add path with recursive runner + batch-move
1. Change the public parameter type from `FileProcessingPipeline` to `IFileProcessingPipelineProvider` on 8 public overloads (`FileRepoTransaction.AddAsync` x2, `FileRepo.AddVariantAsync` x2, `GetOrAddVariantAsync` x2, `TryAddVariantAsync` x2).
2. Return type changes:
   - `FileRepoTransaction.AddAsync` -> `Task<RepoFileGroupInfo>` (with `FileId` / `Extension` forwarders for migration ergonomics).
   - `FileRepo.AddVariantAsync` -> `Task<IReadOnlyList<RepoFileInfo>>` (pre-order: top-level variant first, then nested children in declaration order).
   - `FileRepo.GetOrAddVariantAsync` -> `Task<IReadOnlyList<RepoFileInfo>>` (every declared variant id appears in the list; entries mix pre-existing and newly-produced files; pre-order).
   - `FileRepo.TryAddVariantAsync` -> `Task<IReadOnlyList<RepoFileInfo>?>` (null if any declared variant id is already taken OR any per-id lock cannot be acquired; pre-order on success).
3. **Lock acquisition for variant-add tree:**
   - Collect every variant id declared in the tree (eagerly validated to be unique at pipeline construction).
   - Sort lexicographically and acquire `_fileSync.LockAsync((fileId, vid))` in sorted order to guarantee deadlock-freedom across concurrent callers with overlapping trees.
   - `Try*`: per-lock timeout=0; on first timeout, release acquired locks and return null.
   - `Add*` / `GetOrAdd*`: wait on each lock.
   - `AddAsync` (main-file path) does NOT need variant locks - the freshly-minted `FileId` is not externally visible until commit.
4. **Per-mode existence handling under lock (re-checked on disk after lock acquisition):**
   - `TryAddVariantAsync`: if ANY declared variant id already exists on disk -> release locks, return null. Strict all-or-nothing.
   - `AddVariantAsync`: same, but throw `InvalidOperationException` naming the colliding variant id.
   - `GetOrAddVariantAsync`: per-node check. Nodes that exist on disk are kept; their children recurse using the existing on-disk file as parent source. Nodes that are missing run their pipeline (sourced from parent's on-disk or freshly staged file).
5. Implement recursive internal `RunNodeAsync(parentSource, provider, variantId?, mode)`:
   - Resolve `pipeline = provider.GetPipeline(parentSource.Extension)`.
   - For `GetOrAdd` when `variantId is not null` and the variant exists on disk: open it as the parent source for children, recurse, add a `RepoFileInfo` entry to results.
   - Otherwise execute pipeline. On success, **stage** the output under `temp/` and remember `(stagedPath, destinationFilename)` for the batch-move phase.
   - On `FileSourceUnchangedException` when `variantId is not null` and `pipeline.SkipWhenSourceUnchanged`: do not stage, do not append to results. Recurse into `pipeline.Variants` with `parentSource` unchanged.
   - On `FileSourceUnchangedException` when `variantId is null` (main pipeline) and `pipeline.SkipWhenSourceUnchanged`: let the exception propagate to the `AddAsync` caller.
   - On other exceptions: propagate (aborts entire add; nothing is moved into `files/`; routine cleanup reclaims temp).
   - On success: recurse into `pipeline.Variants` with the newly produced staged file as their parent source.
6. **Batch-move commit step** runs only after the recursive tree completes without exception:
   - Validate all variant ids in the produced tree are unique (no collision with each other or with existing files under the `FileId` for the `Add` / `Try` modes).
   - Move every staged file into `files/<shard>/<file-id>/<destinationFilename>` in one pass.
   - Transactional case: each move still writes the existing indeterminate marker, preserving today's rollback semantics.
   - Non-transactional case: a rename failure mid-batch is rare (same-volume); throw an aggregate exception listing succeeded/failed moves. Leftover `temp/` files reclaimed by routine cleanup. Partially-moved files under `files/` are not auto-rolled-back in the non-transactional path (acceptable; not worse than today's per-call rename failure model).
7. Collect every stored or pre-existing `RepoFileInfo` into the returned `RepoFileGroupInfo` / list in pre-order.

### Phase 5 - FileProcessingPipelineSelector
1. New sealed class `FileProcessingPipelineSelector : IFileProcessingPipelineProvider` in `Source/FulcrumFS/`.
2. Registration surface:
   - `Add(FileFormat format, IFileProcessingPipelineProvider provider)` - expands `format.Extensions` internally.
   - `Add(IEnumerable<FileFormat> formats, IFileProcessingPipelineProvider provider)` - bulk register.
   - `Add(string extension, IFileProcessingPipelineProvider provider)` - raw escape hatch.
3. Optional `Default { get; init; }`. If no match and no default, throw `FileProcessingException` with a clear message.
4. Validate at registration: duplicate extensions across entries are rejected.
5. Group itself implements the interface, enabling nesting (a group whose default is another group).

### Phase 6 - Tests + README
1. Tests in `Tests/FulcrumFS.Tests`:
   - `FileProcessingPipelineSelector` routes `.pdf` and `.jpg` to different pipelines; both succeed with expected output extensions.
   - Bare `new FileFormatValidationProcessor(...)` passed directly to `AddAsync`.
   - Unmatched extension with no default throws `FileProcessingException`.
   - Auto-variant: main pipeline + one variant pipeline produces `RepoFileGroupInfo` with both files present.
   - Recursive variants: variant-of-variant produces three files (main + variant + sub-variant).
   - `SkipWhenSourceUnchanged` on a variant: variant is omitted from results, but its nested children still run against the unchanged parent source and ARE stored.
   - Non-`FileSourceUnchanged` exception in a variant aborts the whole add (no files moved into `files/`; only `temp/` work present, then cleaned).
   - Batch-move atomicity: simulate a pipeline failure on the 2nd variant in a 3-variant `AddVariantAsync` call; assert `files/<file-id>/` contains none of the call's variants afterwards.
   - `TryAddVariantAsync` strict mode: with a 2-variant tree where the nested id pre-exists, call returns null and the top-level is NOT created.
   - `GetOrAddVariantAsync` partial existence: with a 2-variant tree where the top-level pre-exists, call skips the top-level pipeline, runs the nested pipeline against the existing top-level file, returns both in the result list.
   - Concurrent variant-add deadlock-freedom: two concurrent `AddVariantAsync` calls with overlapping but differently-ordered tree ids both complete without deadlock (sorted lock acquisition).
2. Update root `README.md` Usage snippet to use the bare-processor form. Add a short `FileProcessingPipelineSelector` example with PDF / image / video routing and an inline `.WithVariant("thumbnail", ...)` to show auto-variants.

## Relevant files

- `Source/FulcrumFS/FileProcessor.cs` - implement interface, cache pipeline.
- `Source/FulcrumFS/FileProcessingPipeline.cs` - implement interface (identity).
- `Source/FulcrumFS/IFileProcessingPipelineProvider.cs` - NEW.
- `Source/FulcrumFS/FileProcessingPipelineSelector.cs` - NEW.
- `Source/FulcrumFS/FileRepoTransaction.cs` - widen 2 `AddAsync` params; resolve provider.
- `Source/FulcrumFS/FileRepo.Add.cs` - widen 6 variant-add overloads; resolve provider; recursive runner.
- `Source/FulcrumFS/FileRepo.TransactionOps.cs` - widen `TxnAddAsync` if it surfaces pipeline.
- `Tests/FulcrumFS.Tests/*` - new tests for group routing, bare-processor sugar, no-match error, auto-variants, batch atomicity, strict/per-node existence semantics, deadlock-freedom.
- `README.md` (root) - update Usage snippet, add group example.

## Verification

1. `dotnet build FulcrumFS.slnx` clean (0 errors, 0 warnings).
2. Existing test suite still green via `runTests`.
3. All previous call sites passing `FileProcessingPipeline` still compile.
4. New tests pass (see Phase 6).

## Decisions

- **Interface, not abstract base.** `FileProcessor` and `FileProcessingPipeline` already have meaningful hierarchies; an interface composes cleanly.
- **Name `IFileProcessingPipelineProvider`.** Consistent with `FileProcessor*` / `FileProcessingPipeline*` naming. Short `IPipelineProvider` rejected as too generic in a public namespace.
- **Variants live on `FileProcessingPipeline` (Shape A).** Leaner than a separate `FileProcessingPlan` type; only one new concept (a pipeline may declare follow-ups).
- **Variants are recursive / first-class.** A variant's pipeline can declare its own `Variants`, processed by the same recursive runner.
- **Rename `ThrowWhenSourceUnchanged` -> `SkipWhenSourceUnchanged`.** Symmetric across all variant contexts (top-level of `AddVariantAsync` and nested variants alike): runner skips storage and omits from the returned collection. Only the main pipeline of `AddAsync` throws `FileSourceUnchangedException` to the caller (because `RepoFileGroupInfo.Main` cannot be omitted).
- **Return value conventions for variant-add family:**
  - Empty list = every declared variant in the tree was skipped (`SkipWhenSourceUnchanged` hit), nothing produced.
  - Non-empty list = the variants that were actually written (and pre-existing ones for `GetOrAdd`), in pre-order, with any skipped nodes omitted.
  - `null` (Try only) = variant id collision OR lock acquisition timeout. Semantically distinct from skip.
- **`GetOrAddVariantAsync` post-call existence is not guaranteed** when a pipeline has `SkipWhenSourceUnchanged=true`. The variant may not exist after the call returns if the source was unchanged. Documented as a deliberate trade-off of opting into the skip behavior.
- **No `Optional` flag on variants.** Only `SkipWhenSourceUnchanged` is a recognized soft-skip reason. Other exceptions abort the whole add.
- **Group keyed by `FileFormat`** (expanded to extensions internally); raw-extension overload retained.
- **`AddAsync` returns `RepoFileGroupInfo`** (with `FileId` / `Extension` forwarders). **`AddVariantAsync` / `GetOrAddVariantAsync` / `TryAddVariantAsync` return `IReadOnlyList<RepoFileInfo>`** (first element is the top-level variant, rest are nested children in encounter order; `Try` returns null on collision).
- **Per-mode existence semantics:** `TryAddVariantAsync` is **strict all-or-nothing** (any declared id pre-existing -> null). `AddVariantAsync` matches (throws instead). `GetOrAddVariantAsync` is **per-node** (existing nodes kept, missing nodes filled in; result list mixes pre-existing and newly-produced entries in pre-order with no distinguishing flag exposed on the result type).
- **Sorted upfront lock acquisition** on every variant id in the tree guarantees deadlock-freedom across concurrent callers. `Try*` uses timeout=0 per lock and unwinds on the first timeout.
- **`AddAsync` skips variant locks** - the fresh `FileId` is not externally visible until commit, so no concurrent op can target its variant ids.
- **GetOrAdd staleness is documented, not detected.** GetOrAdd ensures existence, not freshness. If a parent variant is regenerated (e.g. user deleted then re-called), pre-existing child variants are kept even if they were derived from a different parent content. Engine has no derivation lineage on disk to track this.
- **`FileProcessor.ToPipeline()` stays** for users who need to set pipeline-level flags on a single-processor pipeline.
- **Source-compatible widening.** Concrete-to-interface param change is binary-breaking but source-compatible; acceptable at preview stage.
- **`AddAsync` return-type change is source-breaking** for callers using `added.FileId`. Mitigated by forwarder properties on `RepoFileGroupInfo`.
- **Variant id uniqueness validated at pipeline construction** (within one `Variants` list). Across recursion levels there's no flat namespace conflict because each level has a distinct parent source - the runner just stores them as siblings of their respective parent under the same `FileId`. Variant ids in the *final stored set* (the entire tree flattened) must therefore also be unique; the runner detects duplicates at store time and throws.

## Further Considerations

1. **Variant ids in a recursive tree share one flat namespace per `FileId`.** Storage layout is `<base>/files/<shard>/<file-id>/$main.ext` + `<variantId>.ext`. So even though variants form a tree at definition time, on disk they're flat siblings keyed by id. The runner must error if two variants in the tree resolve to the same id. Plan: validate eagerly at pipeline construction where possible; backstop at store time.

2. **Sequential vs parallel variant execution.** Sequential for v1 (simpler reasoning, deterministic test output, easier failure semantics). Parallel can be a later opt-in if video-thumbnail workloads warrant it.

3. **No-match in a group: throw vs. empty pass-through.** Recommend **throw**. Silent pass-through hides config bugs. Opt-in via `Default = FileProcessingPipeline.Empty` is one line for the rare case.

4. **Preset format collections (`FileFormat.AllImages` / `AllVideos`)?** Defer. Out of scope; users assemble arrays explicitly today.

5. **Thread-safety of `FileProcessor` self-pipeline cache.** Simple `??=` race-tolerated; worst case is two transient allocations.
