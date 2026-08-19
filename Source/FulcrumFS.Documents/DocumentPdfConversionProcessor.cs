using System.Diagnostics;
using System.Globalization;
using System.Text;
using Singulink.IO;
using Singulink.Threading;

namespace FulcrumFS.Documents;

#pragma warning disable SA1642 // Constructor summary documentation should begin with standard text

/// <summary>
/// Provides functionality to convert documents (word processing documents, spreadsheets and presentations) to PDF using LibreOffice.
/// </summary>
public sealed class DocumentPdfConversionProcessor : FileProcessor
{
    private static InterlockedFlag _libreOfficePathInitialized;

    /// <summary>
    /// <para>
    /// Initializes a new instance of the <see cref="DocumentPdfConversionProcessor"/> class with the specified options.</para>
    /// <para>
    /// Note: you must configure the LibreOffice executable path by calling <see cref="ConfigureWithLibreOffice"/> before creating an instance of this
    /// class.</para>
    /// <para>
    /// Note: if you want to do source document validation, you need to use a <see cref="FileFormatValidationProcessor" /> configured with the appropriate
    /// document formats first and chain this after it, as this class does not perform any validation itself, it just converts the provided document to
    /// PDF.</para>
    /// </summary>
    public DocumentPdfConversionProcessor(DocumentPdfConversionProcessingOptions options)
    {
        Options = options;

        // Check if LibreOffice is configured:
        _ = SofficeExePath;
    }

    /// <summary>
    /// Gets the options used to configure this <see cref="DocumentPdfConversionProcessor" />.
    /// </summary>
    public DocumentPdfConversionProcessingOptions Options { get; }

    internal static IAbsoluteFilePath SofficeExePath
    {
        get => field ?? throw new InvalidOperationException("ConfigureWithLibreOffice must be called before using DocumentPdfConversionProcessor.");
        private set;
    }

    internal static int MaxConcurrentProcesses
    {
        get
        {
            _ = SofficeExePath; // Ensure ConfigureWithLibreOffice has been called.
            return field;
        }
        private set;
    }

    internal static IntPtr? ProcessorAffinity
    {
        get
        {
            _ = SofficeExePath; // Ensure ConfigureWithLibreOffice has been called.
            return field;
        }
        private set;
    }

    internal static ProcessPriorityClass? ProcessPriorityClass
    {
        get
        {
            _ = SofficeExePath; // Ensure ConfigureWithLibreOffice has been called.
            return field;
        }
        private set;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AllowedFileExtensions => field ??= [.. Options.SourceFormats.SelectMany(f => f.Extensions).Distinct()];

    /// <summary>
    /// <para>
    /// Configures the directory containing the LibreOffice program files to use for processing.</para>
    /// <para>
    /// On Windows: the LibreOffice `program` directory (e.g. `C:\Program Files\LibreOffice\program`), which should contain soffice.com or soffice.exe.</para>
    /// <para>
    /// On Linux/macOS: the directory containing the soffice executable (e.g. `/usr/bin` or `/usr/lib/libreoffice/program`) with appropriate execute
    /// permissions.</para>
    /// </summary>
    /// <param name="dirPath">The directory path containing the LibreOffice executables.</param>
    /// <param name="options">Configuration options for the document PDF conversion processor, such as maximum number of processes.</param>
    public static void ConfigureWithLibreOffice(IAbsoluteDirectoryPath dirPath, DocumentPdfConversionConfigurationOptions? options = null)
    {
        IAbsoluteFilePath soffice;

        if (OperatingSystem.IsWindows())
        {
            // Prefer soffice.com, which is the console launcher that blocks until the operation completes and reports the real exit code. Fall back to
            // soffice.exe, which also works for headless conversion.
            soffice = dirPath.CombineFile("soffice.com");

            if (!soffice.Exists)
                soffice = dirPath.CombineFile("soffice.exe");
        }
        else
        {
            soffice = dirPath.CombineFile("soffice");
        }

        if (!soffice.Exists)
            throw new FileNotFoundException("LibreOffice (soffice) executable not found in specified directory.", soffice.ToString());

        int maxConcurrentProcesses = options?.MaxConcurrentProcesses ?? Environment.ProcessorCount;
        IntPtr? processorAffinity = options?.ProcessorAffinity;
        ProcessPriorityClass? processPriorityClass = options?.ProcessPriorityClass;

        if (!_libreOfficePathInitialized.TrySet())
            throw new InvalidOperationException("LibreOffice executable path has already been initialized.");

        // Note: we could see a partially initialized state in a race condition if a user tries to initialize and use simultaneously; we could handle this, but
        // it is unlikely to be worth the complication since there's no good reason to do this anyway, as they should be ensuring that initialization is visible
        // before they try to use it for consistent results anyway.
        MaxConcurrentProcesses = maxConcurrentProcesses;
        ProcessorAffinity = processorAffinity;
        ProcessPriorityClass = processPriorityClass;
        SofficeExePath = soffice;
    }

    /// <inheritdoc/>
    protected override async Task<FileProcessingResult> ProcessAsync(FileProcessingContext context)
    {
        var sourceFile = await context.GetSourceAsFileAsync().ConfigureAwait(false);

        var outputDir = context.GetNewWorkDirectory();
        outputDir.Create();

        // Each conversion gets its own LibreOffice user profile directory so that concurrent conversions do not serialize on (or corrupt) a shared profile.
        var profileDir = context.GetNewWorkDirectory();
        profileDir.Create();

        string[] arguments = [
            "--headless",
            "--norestore",
            "--nolockcheck",
            "--nodefault",
            $"-env:UserInstallation={new Uri(profileDir.PathDisplay).AbsoluteUri}", // PathDisplay, as Uri cannot parse extended-length (\\?\) paths.
            "--convert-to", "pdf",
            "--outdir", outputDir.PathExport,
            sourceFile.PathExport,
        ];

        var (output, error, returnCode) = await ProcessUtils.RunProcessToStringAsync(SofficeExePath, arguments, context.CancellationToken)
            .ConfigureAwait(false);

        var pdfFile = outputDir.CombineFile(sourceFile.NameWithoutExtension + ".pdf");

        // LibreOffice can exit with code 0 without producing an output file for some conversion failures, so check for the output file in addition to the
        // return code.
        if (returnCode != 0 || !pdfFile.Exists)
        {
#if DEBUG
            StringBuilder sb = new();
            sb.AppendLine("Failed to convert the document to PDF.");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process exited with code {returnCode}.");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ExecutablePath: {SofficeExePath.PathExport}");
            sb.AppendLine("Arguments: " + string.Join(" ", arguments));
            sb.AppendLine("StandardError:");
            sb.AppendLine(error);
            sb.AppendLine("StandardOutput:");
            sb.AppendLine(output);
            string msg = sb.ToString();
#else
            string msg = "Failed to convert the document to PDF.";
#endif
            var ex = new FileProcessingException(msg);
            ex.Data["ReturnCode"] = returnCode;
            ex.Data["ExecutablePath"] = SofficeExePath.PathExport;
            ex.Data["Arguments"] = arguments;
            ex.Data["StandardError"] = error;
            ex.Data["StandardOutput"] = output;
            throw ex;
        }

        return FileProcessingResult.File(pdfFile, hasChanges: true);
    }
}
