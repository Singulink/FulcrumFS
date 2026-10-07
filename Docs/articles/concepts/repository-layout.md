<div class="article">

# Repository Layout

A FulcrumFS repository is a normal directory tree. This article describes how that tree is organized, how a <xref:FulcrumFS.FileId> maps to a path, and what each kind of on-disk marker means. Understanding the layout helps when inspecting a repository in a file manager, reasoning about backups, or building a side script that walks the directory directly.

### Why the layout matters

Stored files stay browsable in standard file managers and remain compatible with backup, replication, and file-system features like compression and encryption. An operator can open the repository in Explorer to see exactly what is there; a backup tool sees plain files, no special database to coordinate with. The layout is also what makes crash recovery deterministic: the set of files and markers present on disk fully describes the intended state, with no sidecar index or database to fall out of sync.

> [!TIP]
> When debugging a production issue, opening the repository directory and looking at the file group for a particular <xref:FulcrumFS.FileId> is often the fastest way to see what is going on. The marker file names are designed to be human-readable for exactly this reason.

## Top-Level Structure

A repository base directory contains a small number of well-known entries, created and repaired by <xref:FulcrumFS.FileRepo.EnsureCreated*>:

- `fulcrumfs.info`, the info file that marks the directory as a FulcrumFS repository and records its [format version](#format-version).
- `fulcrumfs.lock`, a lock file that the <xref:FulcrumFS.FileRepo> instance using the repository holds open, so that only one instance uses it at a time.
- A `files` directory holding the file groups.
- A `temp` directory for in-progress processing work.
- A `cleanup` directory holding the markers of operations that are in progress or waiting for the cleaner, and delete journals.

Foreign files and folders placed alongside these are left untouched. If a non-empty directory contains foreign content and is not already a repository, <xref:FulcrumFS.FileRepo.EnsureCreated*> throws rather than adopting it.

> [!CAUTION]
> Do not manually add, rename, or delete files inside a live repository directory. The library treats the on-disk markers as authoritative state; an ad-hoc edit (especially renaming a data file or removing a delete marker) can produce subtle inconsistencies that survive crashes. If you need to clean up, do it through the API.

## Format Version

The info file records the version of the on-disk format, so that a library never misreads a repository written by a newer one. A library never changes the format of a repository on its own: an existing repository keeps its version until the application configures a newer one.

#### What the info file declares

The info file is a small text file of `key=value` lines. Three keys describe the format:

- `version` is the format version of the repository, and the version a library must support for full access: opening it with a <xref:FulcrumFS.FileRepo> to add, delete and fetch files.
- `read-compat-version` is the version a library must support to read files from the repository with a read-only <xref:FulcrumFS.FileRepo>. When it is missing, it is the same as `version`.
- `clean-compat-version` is the version a library must support to clean the repository. When it is missing, it is the same as `version`.

A format change that only adds new kinds of entries, without changing how files are stored, keeps `read-compat-version` at 1. Version 2 is such a change: it adds [delete journals](#delete-journals), which record many deletions in one file, to the cleanup directory, which a FulcrumFS 1.0.x cleaner does not know and would leave undeleted, so a version 2 info file declares `read-compat-version=1` and `clean-compat-version=2`. A version 1 info file, as FulcrumFS 1.0.x writes it, has only `version=1` and the time the repository was created.

#### Choosing the version

The format version is chosen with <xref:FulcrumFS.FileRepoOptions.MinimumFormatVersion?displayProperty=nameWithType>, which every repository must set, usually through the constructor. It is the lowest version the repository has once a <xref:FulcrumFS.FileRepo> has opened it, not an exact version:

```csharp
var repo = new FileRepo(baseDir, RepoFormatVersion.Version2, options => {
    options.DeleteMode = DeleteMode.DeferredUntilClean;
});
```

There are three choices:

| Version | Use it when | Effect |
|---|---|---|
| <xref:FulcrumFS.RepoFormatVersion.Version1> | Any process that uses or cleans the repositories still runs FulcrumFS 1.0.x. | Repositories stay compatible with FulcrumFS 1.0.x. Transactions record their deletions with a marker per file. |
| <xref:FulcrumFS.RepoFormatVersion.Version2> | Every process that uses or cleans the repositories runs FulcrumFS 2.0 or later. | Staged transactions, and ordinary ones that commit two or more deletions in a deferred delete mode, record their deletions in one delete journal. FulcrumFS 1.0.x refuses the repositories. |
| <xref:FulcrumFS.RepoFormatVersion.Latest> | The application owns the repositories exclusively. | Repositories always have the newest format of the library in use, and are upgraded when the library is updated to one with a newer format. |

The option governs only the creation and upgrade of repositories:

- <xref:FulcrumFS.FileRepo.EnsureCreated*> creates a new repository at the configured version.
- An existing repository with an older version is upgraded when the <xref:FulcrumFS.FileRepo> is initialized, which <xref:FulcrumFS.FileRepo.EnsureCreated*> and <xref:FulcrumFS.FileRepo.EnsureInitialized*> both do, and the first operation does if neither has been called. The upgrade is made while the instance holds the repository lock and before any operation runs: the info file is replaced in one atomic step and flushed to disk, keeping the time the repository was created.
- A repository with a newer version that the library supports is used with full access and is not changed.

The option is not a precondition: an older repository is upgraded, not refused. <xref:FulcrumFS.FileRepo.FormatVersion?displayProperty=nameWithType> reports the version a repository actually has, which is the configured version or a newer one. Nothing behaves differently for the application on a version 1 repository: a feature that needs a newer format falls back to behavior that is the same from the caller's point of view. Today that is one feature, the delete journal: on a version 1 repository a staged transaction records its deletions with a marker per file, exactly as an ordinary transaction does, and an ordinary transaction commits each of its deletions with a delete marker of its own. A future feature that cannot fall back will throw a <xref:System.NotSupportedException> that names the version it needs.

> [!NOTE]
> The package version and the format version are independent: updating to FulcrumFS 2.0 changes nothing on disk until the application chooses <xref:FulcrumFS.RepoFormatVersion.Version2> or <xref:FulcrumFS.RepoFormatVersion.Latest>. Options bound from configuration must set <xref:FulcrumFS.FileRepoOptions.MinimumFormatVersion> too: there is no default, and the <xref:FulcrumFS.FileRepo> constructor throws an <xref:System.ArgumentException> that names the choices when it is missing.

#### Upgrading repositories used by several processes

An application that shares its repositories with other processes, such as a cleanup job deployed on its own or a second application, upgrades them in two steps:

1. Update every process that uses or cleans the repositories to FulcrumFS 2.0 or later, keeping <xref:FulcrumFS.RepoFormatVersion.Version1>. Processes on different library versions work side by side on version 1 repositories.
2. Once every process has been updated, set <xref:FulcrumFS.RepoFormatVersion.Version2> in the applications that write to the repositories. Each repository is upgraded the next time one of them initializes it.

> [!IMPORTANT]
> Upgrading is one-way. Once a repository has been upgraded, a FulcrumFS 1.0.x process fails with a <xref:System.NotSupportedException> when it opens or cleans the repository, and nothing downgrades it.

#### Older libraries and newer repositories

A library refuses full access to a repository whose `version` is newer than it supports, and its cleaner refuses a repository whose `clean-compat-version` is newer than it supports. Both throw a <xref:System.NotSupportedException> and change nothing. A <xref:FulcrumFS.FileRepo> opened in [read-only mode](../guides/read-only-repositories.md) only needs the `read-compat-version` to be supported, so a library can still read files from a newer repository whose format change only added entries. FulcrumFS 1.0.x has no read-only mode.

#### The cleaner never changes the format

A clean pass never writes the info file. A <xref:FulcrumFS.FileRepoCleaner> does not hold the repository lock, so it could not upgrade a repository safely, and it cleans every repository whose `clean-compat-version` it supports, whatever its version. <xref:FulcrumFS.FileRepo.CleanAsync*?displayProperty=nameWithType> initializes its <xref:FulcrumFS.FileRepo> first, like any other operation, so the minimum format version configured on that instance applies to it as usual.

## File Groups

Every <xref:FulcrumFS.FileId> owns a *file group*: a directory holding the main file plus any variants and markers as siblings. The group lives at a sharded path of the form `files/{shard}/{file-id}/`, where the shard keeps any single directory from accumulating too many children and preserves good file-system performance as a repository scales to millions of files.

How file IDs are generated is controlled by <xref:FulcrumFS.FileRepoOptions.FileIdMode>. The <xref:FulcrumFS.FileId> type itself is a GUID wrapper that can be created sequentially with <xref:FulcrumFS.FileId.CreateSequential*> or securely with <xref:FulcrumFS.FileId.CreateSecure*>, and it round-trips to and from strings and <xref:System.Guid>. A sequential ID is a version 7 GUID holding its creation time in milliseconds (see <xref:FulcrumFS.FileId.Timestamp>), a 4-bit counter of the IDs created in that millisecond and random bits, so up to 16 IDs per millisecond carry the current time and the IDs from one <xref:FulcrumFS.FileIdGenerator> sort in the order they were created. Each repository has a generator of its own, while <xref:FulcrumFS.FileId.CreateSequential*> uses the process-wide <xref:FulcrumFS.FileIdGenerator.Shared?displayProperty=nameWithType>.

> [!NOTE]
> <xref:FulcrumFS.FileId.CreateSequential*> is the right default for files stored in a database, because the resulting IDs are created in increasing order and so append to a B-tree index instead of scattering across it, provided the column keeps that order: see [Storing file IDs in a database](#storing-file-ids-in-a-database) for the encoding to use (SQL Server's `uniqueidentifier` orders by the last six bytes and does not). Choose <xref:FulcrumFS.FileId.CreateSecure*> only when the ID is exposed in a URL that should not be guessable.

#### Storing file IDs in a database

Store file IDs in a fixed 16-byte binary column, written with <xref:FulcrumFS.FileId.ToOrderedBytes*> and read back with <xref:FulcrumFS.FileId.FromOrderedBytes*>: `binary(16)` on SQL Server, and `bytea` or `BLOB` on other databases. The ordered encoding puts the version nibble in front of the timestamp, so under the byte-by-byte comparison that databases apply to binary values, sequential IDs sort in the order they were created and after every secure ID. An index over the column then receives new sequential IDs at its end, where B-tree inserts append, instead of splitting pages throughout the index.

The encoding matters most for a repository that switches its <xref:FulcrumFS.FileRepoOptions.FileIdMode> from <xref:FulcrumFS.FileIdMode.Secure> to <xref:FulcrumFS.FileIdMode.Sequential>. In the RFC 9562 byte order that <xref:System.Guid.ToByteArray(System.Boolean)?displayProperty=nameWithType> returns with `bigEndian: true`, a version 7 ID starts with the high byte of its timestamp, which is `0x01` from late 2004 to late 2039, while the first bytes of version 4 IDs are random: the new sequential IDs would all land in a narrow band in the middle of the old ones, where every insert splits a page. SQL Server's `uniqueidentifier` type is no alternative, because it orders values by their last six bytes first, which are random in both kinds of ID.

> [!WARNING]
> The ordered encoding is not RFC 9562 byte order. Do not mix the two in one column: the same ID has different bytes in each, so lookups miss and the order is inconsistent.

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

## Cleanup Directory

The `cleanup` directory is flat and holds the state that the cleaner acts on. It can contain these entries:

- `{fileId}.ind` - Indeterminate marker. A transaction that adds or deletes the file holds it open until the transaction has finished; one that no transaction holds marks the file as indeterminate, for the cleaner to resolve through the resolution callback.
- `{fileId}.del` - Delete marker of a main file. The deletion has committed, and the cleaner deletes the file group once the marker is older than the delete delay. On a version 2 repository, where most deletions are recorded in delete journals, a delete marker is written when an ordinary transaction commits a single deletion (or cannot write its journal), when an immediate deletion could not complete, and by the cleaner when it resolves a file to <xref:FulcrumFS.IndeterminateResolution.Delete> with a delete delay.
- `{fileId} {variantId}.del` - Retirement hint of a variant (a space separates the two IDs), the cleaner's re-entry token for a variant retirement, described below.
- `{journalId}.staged` and `{journalId}.journal` - Delete journals, described below.
- `cleanup.lock` - Held by the cleaner during a clean pass, so that only one pass runs at a time.

#### Variant retirement hints

Separate from in-group delete markers, the cleaner uses hint files under `cleanup/{fileId} {variantId}.del` as its re-entry token. A hint without a matching in-group delete marker means nothing committed, so the cleaner simply sweeps the stray hint and leaves the variant live. This separation is central to crash recovery, covered in [Crash Recovery and Invariants](crash-recovery.md).

#### Delete journals

On a repository of [format version](#format-version) 2 or later, a [staged transaction](commit-model.md#staged-transactions) records its deletions in a journal file in the cleanup directory, named `cleanup/{journalId}.staged` while the transaction is staged and renamed to `cleanup/{journalId}.journal` when it commits. An ordinary transaction that commits two or more deletions in a deferred delete mode records them in a journal as well, written under the staged name when it commits and renamed to the committed name once it has been flushed to disk. A version 1 repository never contains journals. The journal ID is a version 7 GUID, so journals sort by the time they were written. A journal is a small UTF-8 text file: a header line, one lowercase file ID per line, and an `END` line with the number of IDs and a CRC-32 checksum of the ID lines. See [Delete Journals](crash-recovery.md#delete-journals) for what the cleaner does with a journal in each state.

## Further Reading

Continue with the conceptual articles that build on this layout:

- [Transactional Commit Model](commit-model.md) - How adds and deletes commit and recover.
- [Variant Aliasing](variant-aliasing.md) - Aliases and chain compression.
- [Crash Recovery and Invariants](crash-recovery.md) - The forward-only state machine and cleaner recovery rules.

</div>
