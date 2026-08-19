using System.Runtime.CompilerServices;
using Singulink.IO;

namespace FulcrumFS.Documents;

public static class LibreOfficePathInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        DocumentPdfConversionProcessor.ConfigureWithLibreOffice(DirectoryPath.ParseAbsolute(ProgramDirectoryPath));
    }

    public static string ProgramDirectoryPath
    {
        get
        {
            if (field is not null) return field;

            string value = Environment.GetEnvironmentVariable("LIBREOFFICE_PROGRAM_PATH");

            if (string.IsNullOrWhiteSpace(value))
            {
                var projDir = DirectoryPath.GetAppBase();
                while (projDir?.CombineFile("FulcrumFS.Documents.Tests.csproj").Exists == false)
                {
                    projDir = projDir.ParentDirectory;
                }

                if (projDir is not null)
                {
                    var envFile = projDir.CombineFile("libreoffice_path.txt");
                    if (envFile.Exists)
                    {
                        value = File.ReadAllText(envFile.PathExport).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Must set LIBREOFFICE_PROGRAM_PATH environment variable to run FulcrumFS.Documents.Tests project.");
            }

            field = value;
            return value;
        }
    }
}
