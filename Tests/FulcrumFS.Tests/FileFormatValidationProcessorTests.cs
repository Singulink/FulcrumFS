namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileFormatValidationProcessorTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot_FileFormatValidation");
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    private static readonly FileRepo _repo = new(_repoDir, options => {
        options.DeleteDelay = TimeSpan.Zero;
        options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(120);
    });

    private static bool _initialized;

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void AllowedFileExtensions_FlattensTypeExtensions()
    {
        var processor = new FileFormatValidationProcessor(new(FileFormat.Jpeg, FileFormat.Png, FileFormat.Doc));

        processor.AllowedFileExtensions.ShouldContain(".jpg");
        processor.AllowedFileExtensions.ShouldContain(".png");
        processor.AllowedFileExtensions.ShouldContain(".doc");
        processor.AllowedFileExtensions.Count.ShouldBe(
            FileFormat.Jpeg.Extensions.Count + FileFormat.Png.Extensions.Count + FileFormat.Doc.Extensions.Count);
    }

    [TestMethod]
    public void Ctor_NullOptions_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new FileFormatValidationProcessor(null!));
    }

    [TestMethod]
    public async Task ValidJpeg_PassesThroughUnchanged()
    {
        ResetRepository();

        var processor = new FileFormatValidationProcessor(new(FileFormat.Jpeg, FileFormat.Png));
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);

        await using var txn = await _repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".jpg", leaveOpen: true, processor.ToPipeline(), TestContext.CancellationToken);

        added.FileId.ShouldNotBe(default);
        await txn.CommitAsync();
    }

    [TestMethod]
    public async Task InvalidContent_ThrowsFileProcessingException()
    {
        ResetRepository();

        var processor = new FileFormatValidationProcessor(new(FileFormat.Jpeg));

        // Feed PNG bytes through a processor that only accepts JPEG content under .jpg.
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.png").PathExport);

        var ex = await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(source, ".jpg", leaveOpen: true, processor.ToPipeline(), TestContext.CancellationToken);
        });

        ex.Message.ShouldContain("JPEG");
    }

    [TestMethod]
    public async Task UnknownExtension_ThrowsFileProcessingException()
    {
        ResetRepository();

        var processor = new FileFormatValidationProcessor(new(FileFormat.Jpeg));
        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);

        // The pipeline rejects up-front because .xyz is not in AllowedFileExtensions.
        await Should.ThrowAsync<FileProcessingException>(async () => {
            await using var txn = await _repo.BeginTransactionAsync();
            await txn.AddAsync(source, ".xyz", leaveOpen: true, processor.ToPipeline(), TestContext.CancellationToken);
        });
    }

    private static void ResetRepository()
    {
        lock (_repo)
        {
            if (_initialized)
                return;

            _initialized = true;

            if (_repoDir.Exists)
                _repoDir.Delete(true);

            _repoDir.Create();
        }
    }
}
