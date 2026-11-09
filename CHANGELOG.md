# FulcrumFS Version History

## Version 2.2

The repository options now work with the .NET options system: they can be bound from configuration, registered with `AddFileRepo` and `AddFileRepoCleaner`, and validated when the application starts. The breaking changes only affect apps that subclass the options classes or use a base directory whose names are not friendly.

### Breaking changes

- `FileRepoOptions` and `FileRepoCleaningOptions` are sealed
- The `BaseDirectory` of both options, and the constructors that take one, throw an `ArgumentException` for a path with names that are not friendly, such as one that ends with a space or a dot, or `CON` on Windows

### Fixes

- A variant add that followed a failed attempt whose working files could not be deleted, such as a file still held open on Windows, failed with "already exists"
- A failed alias variant add could leave its working directory in the repository's temp directory and its source stream open
- An add with `leaveOpen: false` that failed before processing started left the source stream open
- An add whose cleanup also failed threw the cleanup's exception instead of its own, and could leave its working directory behind
- Binding `FileRepoOptions` from configuration without `MinimumFormatVersion` threw from the binder; the version now stays unset and the `FileRepo` constructor reports it
- `ImageProcessingOptions.SourceValidation` with a minimum greater than its maximum made every image fail; setting it now throws
- The options' `BaseDirectory` is annotated as nullable, matching that it is `null` while it is not set
- `ImageProcessingOptions.ReorientMode` rejects undefined values, and `DocumentPdfConversionProcessingOptions.SourceFormats` rejects `null` entries

### New

- New `AddFileRepo` and `AddFileRepoCleaner` registration methods, which validate the options when the application starts
- The options can be bound from configuration, including `BaseDirectory` with the reflection-based binder
- New `IsFrozen` and `Validate()` on `FileRepoOptions` and `FileRepoCleaningOptions`
- New `FileRepo.BaseDirectory` and `FileRepoCleaner.BaseDirectory`
- New `RepoFormatVersion.Unspecified`, the value of `MinimumFormatVersion` while it is not set

Requires Singulink.IO.FileSystem 3.2 or later.

## Version 2.1

### Fixes

- A deletion's indeterminate marker could be removed while a transaction still relied on it, so a crash after the application's database commit could leave the file with nothing for the cleaner to resolve
- Deleting a file that another transaction is adding could lose the deletion's record; it now waits briefly and then throws `RepoFileBusyException`
- Committing a disposed transaction returned normally without committing anything; it now throws `ObjectDisposedException`
- A `TransactionCompletionFailed` handler that acted on its transaction deadlocked when a file added and then deleted in that transaction could not be removed
- On a storage volume that stopped answering, a commit or rollback waited `MaxAccessWaitOrRetryTime` once for each file instead of once
- When a repository initialized again after a health check, the working files of adds in progress were deleted, so those adds failed
- A deletion recorded in a committed delete journal was reported as a failed commit when finishing it threw
- Updated ImageSharp to 4.1.3, which fixes security advisories GHSA-j3p4-wp97-rph4, GHSA-gwg2-r3hj-4w44 and GHSA-wmxv-xphr-5c9g

### New

- New `FileId.ToOrderedBytes` and `FileId.FromOrderedBytes` for storing IDs in ordered binary columns, with sequential IDs sorting by creation

## Version 2.0.2

- Fixed: the `FileId` equality operators threw for `null` operands

## Version 2.0.1

- The assemblies are strong-named

## Version 2.0

First release of the 2.x line; see the documentation.
