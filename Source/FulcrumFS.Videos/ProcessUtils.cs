using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Singulink.IO;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides utility methods for running external processes related to video processing.
/// </summary>
internal static class ProcessUtils
{
    private static readonly SemaphoreSlim _shortLivedProcessesSemaphore = new(1, 1);
    private static SemaphoreSlim? _processesSemaphore;
    private static SemaphoreSlim ProcessesSemaphore
        => _processesSemaphore ??= new SemaphoreSlim(VideoProcessor.MaxConcurrentProcesses, VideoProcessor.MaxConcurrentProcesses);

    private static async ValueTask<SemaphoreSlim> JoinProcessSemaphoreAsync(bool isShortLived, bool runAsynchronously, CancellationToken cancellationToken)
    {
        // Try to enter the main semaphore without waiting first to avoid unnecessary context switches.
        // Note: short-lived can still use the main semaphore if available, we are just trying to prioritize them running since they're fast to allow tasks to
        // continue asap.
        var processesSemaphore = ProcessesSemaphore;
        if (processesSemaphore.Wait(0, cancellationToken: default)) return processesSemaphore;

        // If the process is short-lived, try to enter its extra semaphore without waiting.
        if (isShortLived && _shortLivedProcessesSemaphore.Wait(0, cancellationToken: default)) return _shortLivedProcessesSemaphore;

        // Otherwise, wait asynchronously for availability.
        // Note: short-lived processes have their own semaphore to avoid being blocked by long-running ones, but if the process is not actually short-lived,
        // the queue to run short-lived processes quickly may get starved as there's only one slot available for quick running.
        var semaphore = isShortLived ? _shortLivedProcessesSemaphore : processesSemaphore;
        if (runAsynchronously) await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        else semaphore.Wait(cancellationToken);

        // If we entered the short-lived semaphore but a slot is available in the main semaphore, switch to that one.
        try
        {
            if (semaphore == _shortLivedProcessesSemaphore && processesSemaphore.Wait(0, cancellationToken: default))
            {
                _shortLivedProcessesSemaphore.Release();
                semaphore = processesSemaphore;
            }
        }
        catch
        {
            semaphore.Release();
            throw;
        }

        // Return the semaphore we acquired.
        return semaphore;
    }

    private static async ValueTask RedirectStreamAsync(StreamReader reader, TextWriter writer, bool runAsynchronously, CancellationToken cancellationToken)
    {
        // Rent a buffer that we can use for reading/writing:
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);

        // Perform the redirection:
        int read;
        if (runAsynchronously)
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            while ((read = reader.Read(buffer.AsSpan(0, buffer.Length))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer.AsSpan(0, read));
                writer.Flush();
            }
        }

        // Return the buffer:
        ArrayPool<char>.Shared.Return(buffer);
    }

    private static async ValueTask<int> RunProcessAsyncImpl(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        TextWriter? standardOutputWriter,
        TextWriter? standardErrorWriter,
        bool isShortLived,
        bool redirectOutputContinually,
        bool runAsynchronously,
        CancellationToken cancellationToken)
    {
        List<Task>? redirectTasks = null;
        Process? process = null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var semaphore = await JoinProcessSemaphoreAsync(isShortLived, runAsynchronously, cancellationToken).ConfigureAwait(false);
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

                foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);

                process.Start();

                if (OperatingSystem.IsWindows())
                {
                    // On Windows, processes will not exit until their output streams are read, so we must redirect continually always.
                    redirectOutputContinually = true;
                }

                if (redirectOutputContinually)
                {
                    if (standardOutputWriter != null)
                    {
                        (redirectTasks ??= []).Add(Task.Run(async () => await RedirectStreamAsync(
                            process.StandardOutput,
                            standardOutputWriter,
                            runAsynchronously: true,
                            cancellationToken).ConfigureAwait(false), cancellationToken));
                    }

                    if (standardErrorWriter != null)
                    {
                        (redirectTasks ??= []).Add(Task.Run(async () => await RedirectStreamAsync(
                            process.StandardError,
                            standardErrorWriter,
                            runAsynchronously: true,
                            cancellationToken).ConfigureAwait(false), cancellationToken));
                    }
                }

                try
                {
                    if (runAsynchronously)
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (cancellationToken == CancellationToken.None)
                    {
                        process.WaitForExit();
                    }
                    else
                    {
                        using var registration = cancellationToken.Register(() =>
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill(entireProcessTree: true);
                                }
                            }
                            catch
                            {
                                // Ignore exceptions from killing the process.
                            }
                        });
                        process.WaitForExit();
                        cancellationToken.ThrowIfCancellationRequested(); // In case we exited due to cancellation.
                    }
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Ignore exceptions from killing the process.
                    }

                    throw;
                }
            }
            finally
            {
                semaphore.Release();
            }

            if (!redirectOutputContinually)
            {
                if (standardOutputWriter != null)
                {
                    await RedirectStreamAsync(process.StandardOutput, standardOutputWriter, runAsynchronously, cancellationToken).ConfigureAwait(false);
                }

                if (standardErrorWriter != null)
                {
                    await RedirectStreamAsync(process.StandardError, standardErrorWriter, runAsynchronously, cancellationToken).ConfigureAwait(false);
                }
            }

            if (redirectTasks != null)
            {
                await Task.WhenAll(redirectTasks).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static async ValueTask<(string Output, string Error, int ReturnCode)> RunProcessToStringAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        bool isShortLived = false,
        CancellationToken cancellationToken = default,
        bool runAsynchronously = true)
    {
        using StringWriter standardOutputWriter = new();
        using StringWriter standardErrorWriter = new();

        int returnCode = await RunProcessAsyncImpl(
            fileName,
            arguments,
            standardOutputWriter,
            standardErrorWriter,
            isShortLived,
            redirectOutputContinually: false,
            runAsynchronously,
            cancellationToken).ConfigureAwait(false);

        return (standardOutputWriter.ToString(), standardErrorWriter.ToString(), returnCode);
    }

    public static async ValueTask<string> RunProcessToStringWithErrorHandlingAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        bool isShortLived = false,
        CancellationToken cancellationToken = default,
        bool runAsynchronously = true)
    {
        using StringWriter standardOutputWriter = new();
        using StringWriter standardErrorWriter = new();

        int returnCode = await RunProcessAsyncImpl(
            fileName,
            arguments,
            standardOutputWriter,
            standardErrorWriter,
            isShortLived,
            redirectOutputContinually: false,
            runAsynchronously,
            cancellationToken).ConfigureAwait(false);

        if (returnCode != 0)
        {
#if DEBUG
            StringBuilder sb = new();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process exited with code {returnCode}.");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ExecutablePath: {fileName.PathExport}");
            sb.AppendLine("Arguments: " + string.Join(" ", arguments));
            sb.AppendLine("StandardError:");
            sb.AppendLine(standardErrorWriter.ToString());
            sb.AppendLine("StandardOutput:");
            sb.AppendLine(standardOutputWriter.ToString());
            string msg = sb.ToString();
#else
            string msg = string.Create(CultureInfo.InvariantCulture, $"Process exited with code {returnCode}.");
#endif
            var err = new InvalidOperationException(msg);
            err.Data["ReturnCode"] = returnCode;
            err.Data["ExecutablePath"] = fileName.PathExport;
            err.Data["Arguments"] = arguments;
            err.Data["StandardError"] = standardErrorWriter.ToString();
            err.Data["StandardOutput"] = standardOutputWriter.ToString();
            throw err;
        }

        return standardOutputWriter.ToString();
    }

    public static async ValueTask RunProcessWithErrorHandlingAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        TextWriter? standardOutputWriter,
        bool isShortLived = false,
        bool runAsynchronously = true,
        CancellationToken cancellationToken = default)
    {
        using StringWriter standardErrorWriter = new();

        int returnCode = await RunProcessAsyncImpl(
            fileName,
            arguments,
            standardOutputWriter,
            standardErrorWriter,
            isShortLived,
            redirectOutputContinually: true,
            runAsynchronously,
            cancellationToken).ConfigureAwait(false);

        if (returnCode != 0)
        {
#if DEBUG
            StringBuilder sb = new();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process exited with code {returnCode}.");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ExecutablePath: {fileName.PathExport}");
            sb.AppendLine("Arguments: " + string.Join(" ", arguments));
            sb.AppendLine("StandardError:");
            sb.AppendLine(standardErrorWriter.ToString());
            string msg = sb.ToString();
#else
            string msg = string.Create(CultureInfo.InvariantCulture, $"Process exited with code {returnCode}.");
#endif
            var err = new InvalidOperationException(msg);
            err.Data["ReturnCode"] = returnCode;
            err.Data["ExecutablePath"] = fileName.PathExport;
            err.Data["Arguments"] = arguments;
            err.Data["StandardError"] = standardErrorWriter.ToString();
            throw err;
        }
    }
}
