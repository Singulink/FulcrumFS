<div class="article">

# Fetching Files

Once a file is committed, you read it back by its <xref:FulcrumFS.FileId>. This guide covers fetching the main file, opening streams for HTTP responses, retrieving variants such as thumbnails, and reading an entire group at once.

### Always open through the API

Fetch methods return a <xref:FulcrumFS.RepoFileInfo> whose <xref:FulcrumFS.RepoFileInfo.Open*> method opens the file with the sharing options the repository depends on for safe concurrent access and cleanup. Open through <xref:FulcrumFS.RepoFileInfo.Open*> or the repository's open methods rather than opening <xref:FulcrumFS.RepoFileInfo.Path> directly.

> [!WARNING]
> Opening <xref:FulcrumFS.RepoFileInfo.Path> with `File.OpenRead` skips the repository's sharing semantics. Under the default deferred delete mode the file may be flagged for deletion while you hold it open, and an ad-hoc open will not coordinate with the cleaner. Always go through the API.

## Fetching the Main File

<xref:FulcrumFS.FileRepo.GetAsync*> returns a <xref:FulcrumFS.RepoFileInfo> describing the main file. From it you can read metadata such as <xref:FulcrumFS.RepoFileInfo.Extension> and <xref:FulcrumFS.RepoFileInfo.Length>, then open a read-only stream. This is the natural shape for an HTTP file response:

```csharp
// Handler for "GET /api/documents/{id}".
var doc = await dbContext.Documents.FindAsync(id);
var info = await repo.GetAsync(doc.FileId);

return Results.File(info.Open(), contentType: doc.ContentType, fileDownloadName: doc.Name);
```

For a direct stream without the intermediate info object, use <xref:FulcrumFS.FileRepo.OpenAsync*>:

```csharp
await using var stream = await repo.OpenAsync(fileId);
```

If the file does not exist, these throw <xref:FulcrumFS.RepoFileNotFoundException>. See [Exception Handling](exception-handling.md) for the recommended catch pattern.

## Fetching Variants

A specific variant is fetched with <xref:FulcrumFS.FileRepo.GetVariantAsync*>, or opened directly with <xref:FulcrumFS.FileRepo.OpenVariantAsync*>, by passing the variant ID. A typical use is serving a precomputed thumbnail for a gallery view:

```csharp
// Handler for "GET /api/photos/{id}/thumb".
var thumb = await repo.GetVariantAsync(photo.FileId, "thumbnail");

return Results.File(thumb.Open(), contentType: "image/jpeg");
```

If the variant is an alias (because the thumbnail pipeline reported no changes against an image that was already small enough), it resolves transparently to its source's data, so the caller never has to know whether a real file or an alias is on disk. See [Variant Aliasing](../concepts/variant-aliasing.md).

## Fetching a Whole Group

To retrieve the main file and every variant in one call, use <xref:FulcrumFS.FileRepo.GetGroupAsync*>, which returns a <xref:FulcrumFS.RepoFileGroupInfo>. This is the right call whenever the caller needs to see *which* variants exist before deciding what to do, for example:

- A request handler that picks the best available rendition for the current client (the WebP variant if present, falling back to JPEG, then the original).
- A lazy-generation pass that walks the variants for a file and produces any that are missing.
- An admin or archive view that lists or packages every rendition of a file.

```csharp
var group = await repo.GetGroupAsync(fileId);

Console.WriteLine($"Main: {group.MainFile.Extension} ({group.MainFile.Length} bytes)");

foreach (var variant in group.VariantFiles)
    Console.WriteLine($"  {variant.VariantId}: {variant.Extension} ({variant.Length} bytes)");
```

## Next Steps

- [File Variants](file-variants.md) - How variants are produced and addressed.
- [Deleting Files and Variants](deleting-files.md) - Removing files and variants.
- [Exception Handling](exception-handling.md) - Handling not-found and other errors.

</div>
