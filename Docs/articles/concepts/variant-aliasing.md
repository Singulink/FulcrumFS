<div class="article">

# Variant Aliasing

When a variant pipeline produces output that is byte-identical to its source, storing a second copy is wasted disk space. FulcrumFS handles that case by writing a zero-byte *alias* marker instead of a duplicate data file, then resolving reads through the marker transparently. This article explains the aliasing model: how alias markers are written, how chains stay flat, and what an unhealthy repository would look like.

### Vocabulary

A *variant* is an additional file in a group, addressed by a variant ID. A *source* is the variant (or the main file) that another variant was produced from. An *alias* is a variant that resolves to its source's data file instead of holding its own copy.

## How Aliasing Works

The "256x256 thumbnail" pipeline applied to a 256x256 upload is the canonical case: the output is the input, so writing a separate file accomplishes nothing. If the pipeline sets <xref:FulcrumFS.FileProcessingPipeline.AliasWhenVariantSourceUnchanged> to `true` and the processor reports no changes against its source, the repository writes a zero-byte alias marker pointing at the source instead of a copy. The variant remains first-class and addressable; reads transparently return the source's bytes.

> [!NOTE]
> Callers fetching an aliased variant cannot tell it apart from a real one: <xref:FulcrumFS.FileRepo.GetVariantAsync*> and <xref:FulcrumFS.FileRepo.OpenVariantAsync*> return the source's data either way. A gallery view never has to know whether a thumbnail is its own file or an alias; the URL works the same.

## Chain Compression

Aliases never point at other aliases. Every alias marker names a *real data file* as its source. When the static variant tree would produce an alias whose source is itself an alias, the marker is written to point straight through to the resolved root.

This guarantees:

- **No cycles.** Alias chains cannot loop back on themselves.
- **O(1) resolution.** Reading a variant is a single direct path construction plus one stat, with no directory scan.
- **Stable behavior under retirement.** When a source variant is retired, the survivor-promotion rule (see [File Variants](../guides/file-variants.md#retiring-variants)) re-points dependents at the new root in one rebase, not through a multi-hop chain.

## Further Reading

- [File Variants](../guides/file-variants.md) - Declaring, adding, and retiring variants.
- [Repository Layout](repository-layout.md) - How alias markers and data files are named on disk.
- [Crash Recovery and Invariants](crash-recovery.md) - How an interrupted retirement converges.

</div>
