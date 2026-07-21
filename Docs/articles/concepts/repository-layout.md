<div class="article">

# Repository Layout

A FulcrumFS repository is a normal directory tree. This article describes how that tree is organized, how a <xref:FulcrumFS.FileId> maps to a path, and what each kind of on-disk marker means. Understanding the layout helps when inspecting a repository in a file manager, reasoning about backups, or building a side script that walks the directory directly.

### Why the layout matters

Stored files stay browsable in standard file managers and remain compatible with backup, replication, and file-system features like compression and encryption. An operator can open the repository in Explorer to see exactly what is there; a backup tool sees plain files, no special database to coordinate with. The layout is also what makes crash recovery deterministic: the set of files and markers present on disk fully describes the intended state, with no sidecar index or database to fall out of sync.

> [!TIP]
> When debugging a production issue, opening the repository directory and looking at the file group for a particular <xref:FulcrumFS.FileId> is often the fastest way to see what is going on. The marker file names are designed to be human-readable for exactly this reason.

## Top-Level Structure

A repository base directory contains a small number of well-known entries, created and repaired by <xref:FulcrumFS.FileRepo.EnsureCreated*>:

- An info file that marks the directory as a FulcrumFS repository.
- A lock file used to coordinate an instance session.
- A `files` directory holding the file groups.
- A `temp` directory for in-progress processing work.
- A `cleanup` directory holding the cleaner's re-entry tokens.

Foreign files and folders placed alongside these are left untouched. If a non-empty directory contains foreign content and is not already a repository, <xref:FulcrumFS.FileRepo.EnsureCreated*> throws rather than adopting it.

> [!CAUTION]
> Do not manually add, rename, or delete files inside a live repository directory. The library treats the on-disk markers as authoritative state; an ad-hoc edit (especially renaming a data file or removing a delete marker) can produce subtle inconsistencies that survive crashes. If you need to clean up, do it through the API.

## File Groups

Every <xref:FulcrumFS.FileId> owns a *file group*: a directory holding the main file plus any variants and markers as siblings. The group lives at a sharded path of the form `files/{shard}/{file-id}/`, where the shard keeps any single directory from accumulating too many children and preserves good file-system performance as a repository scales to millions of files.

How file IDs are generated is controlled by <xref:FulcrumFS.FileRepoOptions.FileIdMode>. The <xref:FulcrumFS.FileId> type itself is a GUID wrapper that can be created sequentially with <xref:FulcrumFS.FileId.CreateSequential*> or securely with <xref:FulcrumFS.FileId.CreateSecure*>, and it round-trips to and from strings and <xref:System.Guid>.

> [!NOTE]
> <xref:FulcrumFS.FileId.CreateSequential*> is the right default for files stored in a database, because the resulting IDs cluster well in B-tree indexes the same way sequential GUIDs do. Choose <xref:FulcrumFS.FileId.CreateSecure*> only when the ID is exposed in a URL that should not be guessable.

## Files and Markers

Inside a group directory the following kinds of entries can appear as siblings.

#### Data files

A data file is named `{variantId}.{ext}`. The main file uses a reserved `$main` sentinel as its variant ID, so a photo group typically contains a file like `$main.jpg` alongside variants such as `thumbnail.jpg`. Because normalized variant IDs cannot contain a period and cannot equal `$main`, a caller can never collide with the main file or make a variant name ambiguous to parse.

#### Alias markers

An alias marker is a zero-byte file named `{variantId}.{sourceVariantId}.{sourceExt}.alias`. All of its information lives in the file name. An alias lets a variant resolve transparently to another variant's data file instead of storing a duplicate copy. A data file and an alias marker for the same variant ID are mutually exclusive; the add path enforces this before any rename.

#### In-group delete markers

An in-group delete marker is named `{variantId}.del`. It is the authoritative "pending cleanup" marker that fetch operations read in order to fail fast while the underlying data file may still linger on disk.

#### Rebase plan markers

A rebase plan marker is named `{sourceVariantId}.{chosenVariantId}.rebase`. It pins the chosen new root of a subtree once a retirement begins mutating the directory. See [Variant Aliasing](variant-aliasing.md) for how aliases and rebases work together.

## Cleanup Hints

Separate from in-group delete markers, the cleaner uses hint files under `cleanup/{fileId}.{variantId}.del` as its re-entry token. A hint without a matching in-group delete marker means nothing committed, so the cleaner simply sweeps the stray hint and leaves the variant live. This separation is central to crash recovery, covered in [Crash Recovery and Invariants](crash-recovery.md).

## Further Reading

Continue with the conceptual articles that build on this layout:

- [Transactional Commit Model](commit-model.md) - How adds and deletes commit and recover.
- [Variant Aliasing](variant-aliasing.md) - Aliases and chain compression.
- [Crash Recovery and Invariants](crash-recovery.md) - The forward-only state machine and cleaner recovery rules.

</div>
