using System.Buffers;
using System.Diagnostics;
using Singulink.IO;

namespace FulcrumFS.Documents;

/// <summary>
/// Provides utility methods for running external processes related to document processing.
/// </summary>
internal static class ProcessUtils
{
    private static SemaphoreSlim? _processesSemaphore;
    private static SemaphoreSlim ProcessesSemaphore
        => _processesSemaphore ??= new SemaphoreSlim(
            DocumentPdfConversionProcessor.MaxConcurrentProcesses,
            DocumentPdfConversionProcessor.MaxConcurrentProcesses);

    public static async ValueTask<(string Output, string Error, int ReturnCode)> RunProcessToStringAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        using StringWriter standardOutputWriter = new();
        using StringWriter standardErrorWriter = new();

        Process? process = null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var semaphore = ProcessesSemaphore;
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName.PathExport,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };

                foreach (string argument in arguments)
                    process.StartInfo.ArgumentList.Add(argument);

                process.Start();

                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    if (DocumentPdfConversionProcessor.ProcessorAffinity is { } affinity)
                    {
                        process.ProcessorAffinity = affinity;
                    }
                }

                if (DocumentPdfConversionProcessor.ProcessPriorityClass is { } priorityClass)
                {
                    process.PriorityClass = priorityClass;
                }

                // Output streams must be redirected continually while the process runs, otherwise the process can block on a full pipe buffer (and on Windows
                // processes will not exit until their output streams are read).
                var redirectTasks = new List<Task> {
                    Task.Run(
                        async () => await RedirectStreamAsync(process.StandardOutput, standardOutputWriter, cancellationToken).ConfigureAwait(false),
                        cancellationToken),
                    Task.Run(
                        async () => await RedirectStreamAsync(process.StandardError, standardErrorWriter, cancellationToken).ConfigureAwait(false),
                        cancellationToken),
                };

                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcessSafely(process);
                    throw;
                }

                await Task.WhenAll(redirectTasks).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (standardOutputWriter.ToString(), standardErrorWriter.ToString(), process.ExitCode);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void KillProcessSafely(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore exceptions from killing the process.
        }
    }

    private static async ValueTask RedirectStreamAsync(StreamReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        // Rent a buffer that we can use for reading/writing:
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);

        // Perform the redirection:
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Return the buffer:
        ArrayPool<char>.Shared.Return(buffer);
    }
}
