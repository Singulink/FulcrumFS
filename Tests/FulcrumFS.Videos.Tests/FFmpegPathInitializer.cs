using System.Runtime.CompilerServices;
using Singulink.IO;

namespace FulcrumFS.Videos;

public static class FFmpegPathInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        string env = Environment.GetEnvironmentVariable("FFMPEG_BINARIES_PATH");

        if (string.IsNullOrWhiteSpace(env))
            throw new ArgumentException("Must set FFMPEG_BINARIES_PATH environment variable to run FulcrumFS.Videos.Tests project.");

        VideoProcessor.ConfigureWithFFmpegExecutables(DirectoryPath.ParseAbsolute(env));
    }
}
