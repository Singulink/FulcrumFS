using System.Diagnostics;
using System.Runtime.Versioning;
using PrefixClassName.MsTest;
using Shouldly;

namespace FulcrumFS.Videos;

// The tests of the options themselves, which run no processing.

partial class Tests
{
    [TestMethod]
    public void TestSourceValidationOptionsMinAboveMaxListsEveryProblem()
    {
        var ex = Should.Throw<ArgumentException>(() => VideoProcessingOptions.Preserve with {
            VideoSourceValidation = VideoStreamValidationOptions.None with {
                MinWidth = 200,
                MaxWidth = 100,
                MinPixels = 4,
                MaxPixels = 2,
                MinLength = TimeSpan.FromSeconds(2),
                MaxLength = TimeSpan.FromSeconds(1),
                MinStreams = 2,
                MaxStreams = 1,
            },
        });

        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith(
            "VideoStreamValidationOptions.MinWidth (200) is greater than MaxWidth (100). VideoStreamValidationOptions.MinPixels (4) is greater than " +
            "MaxPixels (2). VideoStreamValidationOptions.MinLength (00:00:02) is greater than MaxLength (00:00:01). VideoStreamValidationOptions.MinStreams " +
            "(2) is greater than MaxStreams (1).");

        ex = Should.Throw<ArgumentException>(() => VideoProcessingOptions.Preserve with {
            AudioSourceValidation = AudioStreamValidationOptions.None with {
                MinLength = TimeSpan.FromSeconds(2),
                MaxLength = TimeSpan.FromSeconds(1),
                MinStreams = 2,
                MaxStreams = 1,
            },
        });

        ex.Message.ShouldStartWith(
            "AudioStreamValidationOptions.MinLength (00:00:02) is greater than MaxLength (00:00:01). AudioStreamValidationOptions.MinStreams (2) is " +
            "greater than MaxStreams (1).");

        // Equal bounds, and bounds of which only one is set, are valid.
        _ = VideoProcessingOptions.Preserve with {
            VideoSourceValidation = VideoStreamValidationOptions.None with { MinHeight = 100, MaxHeight = 100, MinWidth = 100 },
            AudioSourceValidation = AudioStreamValidationOptions.None with { MaxStreams = 0 },
        };
    }

    [TestMethod]
    public void TestOptionsOutOfRangeNameTheConstructorParameterAndValueInTheSetters()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new VideoResizeOptions(VideoResizeMode.FitDown, 0, 100)).ParamName.ShouldBe("width");
        Should.Throw<ArgumentOutOfRangeException>(() => new VideoResizeOptions(VideoResizeMode.FitDown, 100, 0)).ParamName.ShouldBe("height");
        Should.Throw<ArgumentOutOfRangeException>(() => new VideoResizeOptions((VideoResizeMode)42, 100, 100)).ParamName.ShouldBe("mode");
        Should.Throw<ArgumentOutOfRangeException>(() => new VideoFpsOptions(VideoFpsMode.LimitByIntegerDivision, 0)).ParamName.ShouldBe("targetFps");
        Should.Throw<ArgumentOutOfRangeException>(() => new VideoFpsOptions((VideoFpsMode)42, 30)).ParamName.ShouldBe("mode");

        var resize = new VideoResizeOptions(VideoResizeMode.FitDown, 640, 480, matchSourceOrientation: true);
        (resize.Mode, resize.Width, resize.Height, resize.MatchSourceOrientation).ShouldBe((VideoResizeMode.FitDown, 640, 480, true));
        Should.Throw<ArgumentOutOfRangeException>(() => resize with { Width = 0 }).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => resize with { Mode = (VideoResizeMode)42 }).ParamName.ShouldBe("value");

        var fps = new VideoFpsOptions(VideoFpsMode.LimitByIntegerDivision, 30);
        Should.Throw<ArgumentOutOfRangeException>(() => fps with { TargetFps = 0 }).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => fps with { Mode = (VideoFpsMode)42 }).ParamName.ShouldBe("value");

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => new VideoProcessorConfigurationOptions { MaxConcurrentProcesses = 0 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is less than 1. (Parameter 'value')");

        ex = Should.Throw<ArgumentOutOfRangeException>(() => new VideoProcessorConfigurationOptions { ThreadLimit = 0 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is less than 1. (Parameter 'value')");

        ex = Should.Throw<ArgumentOutOfRangeException>(() => new VideoProcessorConfigurationOptions { ProcessPriorityClass = (ProcessPriorityClass)42 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is not a defined ProcessPriorityClass value. (Parameter 'value')");

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            ex = Should.Throw<ArgumentOutOfRangeException>(SetProcessorAffinityToZero);
            ex.ParamName.ShouldBe("value");
            ex.Message.ShouldStartWith("The value is zero. (Parameter 'value')");
        }

        ex = Should.Throw<ArgumentOutOfRangeException>(() => new VideoFrameExtractionProcessingOptions { ImageTimestampFraction = 1.5 });
        ex.ParamName.ShouldBe("value");
        ex.Message.ShouldStartWith("The value is less than 0 or greater than 1. (Parameter 'value')");

        Should.Throw<ArgumentOutOfRangeException>(() => VideoStreamValidationOptions.None with { MaxWidth = 0 }).ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => AudioStreamValidationOptions.None with { MinLength = TimeSpan.Zero }).ParamName.ShouldBe("value");
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    private static void SetProcessorAffinityToZero() => _ = new VideoProcessorConfigurationOptions { ProcessorAffinity = 0 };
}
