<div class="article">

# File Variants

A variant is an alternate rendition of a file stored in the same group: a thumbnail for a photo, a transcoded MP4 for a video upload, an optimized JPEG alongside a PNG original. This guide covers declaring variants on a pipeline so they are produced at add time, and adding variants after the fact when you need to generate a new rendition lazily or backfill an existing repository.

### The variant lifecycle model

Main files and variants serve different roles, and the difference shapes how each is created and removed:

- The **main file** is the transactional unit. Its <xref:FulcrumFS.FileId> is what a database row references, and it can only be added or deleted inside a <xref:FulcrumFS.FileRepoTransaction>. The commit ordering rule applies to it (see [Transactional Commit Model](../concepts/commit-model.md)).
- A **variant** is auxiliary content tied to a main file. Its lifetime is bound to the main file: when the main file is deleted, all of its variants are deleted with it. Variants are addressed by a string ID within the group, *not* by a separate <xref:FulcrumFS.FileId>, so the database never has to reference one.

Because variants are auxiliary, the variant add APIs (<xref:FulcrumFS.FileRepo.AddVariantAsync*>, <xref:FulcrumFS.FileRepo.TryAddVariantAsync*>, <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*>) sit on <xref:FulcrumFS.FileRepo> directly and commit on their own, outside any transaction. A variant generated on demand needs no transactional ceremony because the database does not record its existence.

#### Variants are immutable, like main files

Anything written into a FulcrumFS repository should be treated as immutable. There is no "replace this file" operation by design.

- To **"update" a main file**, delete the old one and add a new one. The add returns a new <xref:FulcrumFS.FileId>, which you write onto the owning database row, replacing the old reference. The old file is then deleted in the same transaction.
- To **"update" a variant**, give the new content a new variant ID and (optionally) retire the old one once nothing reads it anymore. Do not try to drop and re-add the same variant ID to swap content.

> [!IMPORTANT]
> If you find yourself wanting to repeatedly change the content stored under a single variant ID, that content does not belong in a variant at all. Model it as a separate main file with its own <xref:FulcrumFS.FileId> that the database tracks, so each version can be transactionally added and deleted. Variants are for stable, semi-permanent renditions of the main file (thumbnails, transcodes, format conversions), not for content with churn.

#### Retirement should be deliberate

Deleting (retiring) a variant with <xref:FulcrumFS.FileRepo.DeleteVariantsAsync*> is appropriate when the variant is genuinely no longer needed, for example dropping a 128px thumbnail when the front-end has fully moved to 256px. It is not the way to refresh a variant's content. See [Retiring Variants](#retiring-variants) below.

### Two ways to create variants

Variants can be produced automatically when the main file is added, by attaching variant pipelines to the main pipeline, or they can be added later with the explicit variant add methods. Both end up as addressable entries in the file group, fetched the same way.

The "produce at add time" approach is best when every file needs the same set of variants up front (a gallery that always renders thumbnails). The "add later" approach is best for variants generated on demand (a "download as JPEG" button that materializes a JPEG only when first requested) or for backfilling an existing repository with a new variant kind.

## Declaring Variants on a Pipeline

Attach a variant to a pipeline or a processor with <xref:FulcrumFS.FileProcessingPipeline.WithVariant*> (or <xref:FulcrumFS.FileProcessor.WithVariant*>), giving it a variant ID and the pipeline that produces it. When the main file is added, each declared variant is generated from the main result.

```csharp
// On upload, store the original image and a 256x256 thumbnail at the same time.
var pipeline = new ImageProcessor(ImageProcessingOptions.Preserve)
    .WithVariant(
        "thumbnail",
        new ImageProcessor(thumbnailOptions).ToPipeline(aliasWhenVariantSourceUnchanged: true));

var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline);

// added.MainFile      -> the original image
// added.VariantFiles  -> [ thumbnail ]
```

The declared variants are exposed through <xref:FulcrumFS.FileProcessingPipeline.Variants>, and the produced files appear in <xref:FulcrumFS.RepoFileGroupInfo.VariantFiles> on the result.

#### Aliasing identical variants

If a variant pipeline reports no changes relative to its source and the pipeline's <xref:FulcrumFS.FileProcessingPipeline.AliasWhenVariantSourceUnchanged> is `true`, the repository stores a zero-byte alias to the source instead of a duplicate. The variant remains fully addressable; reads transparently resolve to the source's bytes. This is exactly what you want for a thumbnail pipeline applied to an image that is already 256x256: there is no point storing the same bytes twice. See [Variant Aliasing](../concepts/variant-aliasing.md).

> [!TIP]
> Use descriptive variant IDs that encode their purpose, for example `"thumbnail-256"` or `"web-jpeg"`. The IDs are stable identifiers your code passes back to <xref:FulcrumFS.FileRepo.GetVariantAsync*>, so picking meaningful names up front makes call sites self-documenting and makes it easy to add new sizes later without ambiguity.

## Adding Variants Later

To add a variant to an existing file, use one of the variant add methods. Each accepts a <xref:FulcrumFS.FileId>, a new variant ID, an optional source variant ID, and a pipeline:

- <xref:FulcrumFS.FileRepo.AddVariantAsync*> throws <xref:System.InvalidOperationException> if the variant already exists. Use it when adding the variant is the whole point of the call and a collision is a programming error.
- <xref:FulcrumFS.FileRepo.TryAddVariantAsync*> returns `null` instead of throwing on collision. Use it when you want to attempt the add and react to a race.
- <xref:FulcrumFS.FileRepo.GetOrAddVariantAsync*> returns the existing variant when one is present, otherwise adds it. Use it for "ensure this variant exists" patterns, such as a lazy thumbnail generator that runs on the first request for a thumbnail and reuses it thereafter.

These methods sit on <xref:FulcrumFS.FileRepo> rather than on <xref:FulcrumFS.FileRepoTransaction> because a variant add does not need to participate in a database transaction: the database row already references the main file, and the variant is just additional content under that ID.

A lazy thumbnail endpoint is the canonical example:

```csharp
// "GET /api/photos/{id}/thumb" - generate the thumbnail on first request.
var thumb = await repo.GetOrAddVariantAsync(
    photo.FileId,
    variantId: "thumbnail",
    sourceVariantId: null,                                // Source = the main file.
    new ImageProcessor(thumbnailOptions));

return Results.File(thumb.Open(), contentType: "image/jpeg");
```

Passing `null` for the source variant ID uses the main file as the source. Passing an existing variant ID uses that variant (resolving through aliases to its underlying data), which is useful for chaining: a small thumbnail can be generated from a larger thumbnail rather than re-decoding the full-resolution original.

## Retiring Variants

Retire one or more variants from a file group with <xref:FulcrumFS.FileRepo.DeleteVariantsAsync*>, passing the <xref:FulcrumFS.FileId> and the set of variant IDs to remove. Retirement is a stand-alone repository operation, not part of a <xref:FulcrumFS.FileRepoTransaction>, because the variants being removed are not referenced by the database.

```csharp
// The front-end no longer uses the 128px thumbnail; drop it from every photo.
await repo.DeleteVariantsAsync(photo.FileId, ["thumbnail-128"]);
```

The call has a few properties worth relying on:

- **Unlisted dependents are preserved.** If a variant you did *not* name was internally aliased against one you did, the system promotes a survivor to a real data file and re-points the surviving siblings at it. Nothing observable is lost: a `"thumbnail-small"` aliased against a now-retired `"thumbnail-medium"` keeps working, transparently backed by its own data file afterward.
- **Order independence.** The input is deduplicated and sorted internally, so the outcome does not depend on the order of the IDs. IDs that were never added or are already retired are silent no-ops, as is an empty list.
- **Deterministic survivor selection.** When a promotion is required, the chosen survivor is the earliest by `(CreationTimeUtc, VariantId)`. The variant ID is a stable tiebreaker so that a cleaner finishing an interrupted retirement picks exactly the same survivor the foreground would have.

> [!IMPORTANT]
> The preserve-unlisted-dependents rule is what makes retirement safe to invoke from a migration script or feature-toggle cleanup. If you only know the variant IDs *you* want to drop and not which ones internally point at them, just pass yours; anything that still depends on what you dropped quietly gets promoted to a real data file behind the scenes.

Retirement only removes the listed variants. To remove the main file along with all of its variants, use the transactional <xref:FulcrumFS.FileRepoTransaction.DeleteAsync*> instead (see [Deleting Files and Variants](deleting-files.md)).

## Fetching Variants

Read a variant with <xref:FulcrumFS.FileRepo.GetVariantAsync*> or <xref:FulcrumFS.FileRepo.OpenVariantAsync*>, or enumerate all variants with <xref:FulcrumFS.FileRepo.GetGroupAsync*>. See [Fetching Files](fetching-files.md).

## Next Steps

- [Image Processing](image-processing.md) - Producing image variants like thumbnails.
- [Video Processing](video-processing.md) - Producing transcodes and poster frames.
- [Deleting Files and Variants](deleting-files.md) - Deleting a main file with its variants.
- [Variant Aliasing](../concepts/variant-aliasing.md) - How identical variants are stored as zero-byte aliases.

</div>
