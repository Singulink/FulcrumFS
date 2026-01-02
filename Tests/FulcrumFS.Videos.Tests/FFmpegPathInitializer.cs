using System.Runtime.CompilerServices;
using Singulink.IO;

namespace FulcrumFS.Videos;

public static class FFmpegPathInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VideoProcessor.ConfigureWithFFmpegExecutables(DirectoryPath.ParseAbsolute(BinariesDirectoryPath));
    }

    public static string BinariesDirectoryPath
    {
        get
        {
            if (field is not null) return field;

            string value = Environment.GetEnvironmentVariable("FFMPEG_BINARIES_PATH");

            if (string.IsNullOrWhiteSpace(value))
            {
                var projDir = DirectoryPath.GetAppBase();
                while (projDir?.CombineFile("FulcrumFS.Videos.Tests.csproj").Exists == false)
                {
                    projDir = projDir.ParentDirectory;
                }

                if (projDir is not null)
                {
                    var envFile = projDir.CombineFile("ffmpeg_path.txt");
                    if (envFile.Exists)
                    {
                        value = File.ReadAllText(envFile.PathExport).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Must set FFMPEG_BINARIES_PATH environment variable to run FulcrumFS.Videos.Tests project.");
            }

            field = value;
            return value;
        }
    }
}
