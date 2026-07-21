namespace FulcrumFS;

[PrefixTestClass]
public sealed class PipelineProviderTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();
    private static readonly IAbsoluteDirectoryPath _repoDir = _appDir.CombineDirectory("RepoRoot_PipelineProvider");
    private static readonly IAbsoluteDirectoryPath _sampleDir = _appDir.CombineDirectory("SampleFiles");

    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task BareProcessor_AddedDirectly_AsProvider()
    {
        using var repo = CreateRepo();
        var processor = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg));

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var group = await txn.AddAsync(source, ".jpg", leaveOpen: true, processor, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);

        group.MainFile.Extension.ShouldBe(".jpg");
        group.VariantFiles.Count.ShouldBe(0);
    }

    [TestMethod]
    public async Task Pipeline_WithVariant_ProducesAutoVariant()
    {
        using var repo = CreateRepo();

        var pipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg))
            .ToPipeline()
            .WithVariant("copy", FileProcessingPipeline.Empty);

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var group = await txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);

        group.MainFile.Extension.ShouldBe(".jpg");
        group.VariantFiles.Count.ShouldBe(1);
        group.VariantFiles[0].VariantId.ShouldBe("copy");
        group.VariantFiles[0].Extension.ShouldBe(".jpg");
        File.Exists(group.VariantFiles[0].Path.PathExport).ShouldBeTrue();
    }

    [TestMethod]
    public async Task PipelineGroup_RoutesByExtension()
    {
        using var repo = CreateRepo();

        var jpegPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg));
        var pdfPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Pdf));
        var group = new FileProcessingPipelineSelector(jpegPipeline, pdfPipeline);

        // JPEG path
        await using (var jpegSource = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport))
        await using (var txn = await repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(jpegSource, ".jpg", leaveOpen: true, group, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
            added.MainFile.Extension.ShouldBe(".jpg");
        }

        // PDF path
        await using (var pdfSource = File.OpenRead(_sampleDir.CombineFile("sample.pdf").PathExport))
        await using (var txn = await repo.BeginTransactionAsync())
        {
            var added = await txn.AddAsync(pdfSource, ".pdf", leaveOpen: true, group, TestContext.CancellationToken);
            await txn.CommitAsync(TestContext.CancellationToken);
            added.MainFile.Extension.ShouldBe(".pdf");
        }
    }

    [TestMethod]
    public async Task PipelineGroup_UnmatchedExtension_Throws()
    {
        using var repo = CreateRepo();
        var jpegPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg));
        var group = new FileProcessingPipelineSelector(jpegPipeline);

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.pdf").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        await Should.ThrowAsync<FileProcessingException>(async () =>
            await txn.AddAsync(source, ".pdf", leaveOpen: true, group, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task PipelineGroup_DefaultFallback_HandlesUnmatched()
    {
        using var repo = CreateRepo();

        var jpegPipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg));
        var group = new FileProcessingPipelineSelector(jpegPipeline, FileProcessingPipeline.Empty);

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.pdf").PathExport);
        await using var txn = await repo.BeginTransactionAsync();
        var added = await txn.AddAsync(source, ".pdf", leaveOpen: true, group, TestContext.CancellationToken);
        await txn.CommitAsync(TestContext.CancellationToken);

        added.MainFile.Extension.ShouldBe(".pdf");
    }

    [TestMethod]
    public async Task Progress_SingleProcessor_ReportsZeroAndOne()
    {
        using var repo = CreateRepo();
        var pipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg)).ToPipeline();

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();

        var progress = new List<ProgressValue>();
        await foreach (var pv in txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken).WithCancellation(TestContext.CancellationToken))
            progress.Add(pv);

        await txn.CommitAsync(TestContext.CancellationToken);

        progress.ShouldBe(
        [
            new(null, "FileFormatValidationProcessor", 0.0),
            new(null, "FileFormatValidationProcessor", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_ProcessorMessage_ReportedWithLatestFraction()
    {
        using var repo = CreateRepo();
        var pipeline = new MessageReportingProcessor().ToPipeline();

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();

        var progress = new List<ProgressValue>();
        await foreach (var pv in txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken).WithCancellation(TestContext.CancellationToken))
            progress.Add(pv);

        await txn.CommitAsync(TestContext.CancellationToken);

        progress.ShouldBe(
        [
            new(null, "MessageReportingProcessor", 0.0),
            new(null, "MessageReportingProcessor", 0.0, "Working"),
            new(null, "MessageReportingProcessor", 0.5, "Working"),
            new(null, "MessageReportingProcessor", 1.0, "Working"),
        ]);
    }

    [TestMethod]
    public async Task Progress_MultipleProcessors_ReportZeroAndOneForEach()
    {
        using var repo = CreateRepo();
        var pipeline = new FileProcessingPipeline(
            new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg)),
            new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg)));

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();

        var progress = new List<ProgressValue>();
        await foreach (var pv in txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken).WithCancellation(TestContext.CancellationToken))
            progress.Add(pv);

        await txn.CommitAsync(TestContext.CancellationToken);

        // Two processors with the same display name are disambiguated with a " (2)" suffix on the second.
        progress.ShouldBe(
        [
            new(null, "FileFormatValidationProcessor", 0.0),
            new(null, "FileFormatValidationProcessor", 1.0),
            new(null, "FileFormatValidationProcessor (2)", 0.0),
            new(null, "FileFormatValidationProcessor (2)", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_WithVariant_ReportsForMainAndVariant()
    {
        using var repo = CreateRepo();
        var pipeline = new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg))
            .ToPipeline()
            .WithVariant("copy", new FileFormatValidationProcessor(new FileFormatValidationOptions(FileFormat.Jpeg)));

        await using var source = File.OpenRead(_sampleDir.CombineFile("sample.jpg").PathExport);
        await using var txn = await repo.BeginTransactionAsync();

        var progress = new List<ProgressValue>();
        await foreach (var pv in txn.AddAsync(source, ".jpg", leaveOpen: true, pipeline, TestContext.CancellationToken).WithCancellation(TestContext.CancellationToken))
            progress.Add(pv);

        await txn.CommitAsync(TestContext.CancellationToken);

        // The main pipeline reports under a null variant ID, then the "copy" variant reports under its own variant ID.
        progress.ShouldBe(
        [
            new(null, "FileFormatValidationProcessor", 0.0),
            new(null, "FileFormatValidationProcessor", 1.0),
            new("copy", "FileFormatValidationProcessor", 0.0),
            new("copy", "FileFormatValidationProcessor", 1.0),
        ]);
    }

    private static FileRepo CreateRepo()
    {
        if (_repoDir.Exists)
            _repoDir.Delete(recursive: true);

        var repo = new FileRepo(_repoDir, options => {
            options.DeleteMode = DeleteMode.Immediate;
            options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(60);
        });
        repo.EnsureCreated();
        return repo;
    }
}
