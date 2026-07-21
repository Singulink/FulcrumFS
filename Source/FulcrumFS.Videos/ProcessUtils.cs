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
    private static readonly SemaphoreSlim _mediumLivedProcessesSemaphore = new(1, 1);
    private static readonly SemaphoreSlim _cheapLongLivedProcessesSemaphore = new(1, 1);
    private static SemaphoreSlim? _processesSemaphore;
    private static SemaphoreSlim ProcessesSemaphore
        => _processesSemaphore ??= new SemaphoreSlim(VideoProcessor.MaxConcurrentProcesses, VideoProcessor.MaxConcurrentProcesses);

    private static async ValueTask<SemaphoreSlim> JoinProcessSemaphoreAsync(
        ProcessLifetime lifetime,
        bool runAsynchronously,
        Func<bool, ValueTask>? queueingCallback,
        CancellationToken cancellationToken)
    {
        // Try to enter the main semaphore without waiting first to avoid unnecessary context switches.
        // Note: short/medium-lived can still use the main semaphore if available, we are just trying to prioritize them running since they're faster to allow
        // tasks to continue asap & not be blocked by a set of long renders for example.
        // Note: we preferentially use more constrained (i.e., ones that are more likely to be wanted to be used, such as the main one; not constrained in terms
        // of what is able to use the semaphore) semaphores always; this ensures that we don't try to get too far into too many tasks at once such that they all
        // slow down / queue repeatedly; it tries to limit slowdown / queueing to happening fewer times overall, even if for a bit longer, and to reduce
        // slowdown of already-running things by minimizing number of extra tasks above normal, while keeping the benefits of getting quicker things done
        // quickly.

        var processesSemaphore = ProcessesSemaphore;
        if (processesSemaphore.Wait(0, cancellationToken: default))
            return processesSemaphore;

        // If the process is not fully long-lived, try to enter the cheap long-lived extra semaphore without waiting.
        if (lifetime is not ProcessLifetime.LongLived && _cheapLongLivedProcessesSemaphore.Wait(0, cancellationToken: default))
            return _cheapLongLivedProcessesSemaphore;

        // If the process is medium-lived or short-lived, try to enter the medium-lived extra semaphore without waiting.
        if (lifetime is ProcessLifetime.MediumLived or ProcessLifetime.ShortLived && _mediumLivedProcessesSemaphore.Wait(0, cancellationToken: default))
            return _mediumLivedProcessesSemaphore;

        // If the process is short-lived, try to enter the short-lived extra semaphore without waiting.
        if (lifetime is ProcessLifetime.ShortLived && _shortLivedProcessesSemaphore.Wait(0, cancellationToken: default))
            return _shortLivedProcessesSemaphore;

        // Notify that we could not get a process slot immediately and have to start queueing.
        if (queueingCallback is not null)
            await queueingCallback(true).ConfigureAwait(false);

        // We always want to report as dequeued when we exit the method, even if throwing.
        try
        {
            // Otherwise, wait asynchronously for availability on the process's own tier semaphore.
            // Note: short/medium-lived processes have their own semaphores to avoid being blocked by long-running ones, but if the process is not actually
            // short/medium-lived, the queue to run such processes quickly may get starved as there's only one slot available per tier for quick running.
            var semaphore = lifetime switch
            {
                ProcessLifetime.ShortLived => _shortLivedProcessesSemaphore,
                ProcessLifetime.MediumLived => _mediumLivedProcessesSemaphore,
                ProcessLifetime.CheapLongLived => _cheapLongLivedProcessesSemaphore,
                _ => processesSemaphore,
            };

            if (runAsynchronously)
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                semaphore.Wait(cancellationToken);

            // If we entered an extra tier semaphore but a slot is available in a longer tier (main first, then cheap long, then medium over short), switch to
            // that one.
            try
            {
                if (semaphore != processesSemaphore && processesSemaphore.Wait(0, cancellationToken: default))
                {
                    semaphore.Release();
                    semaphore = processesSemaphore;
                }
                else if ((semaphore == _mediumLivedProcessesSemaphore || semaphore == _shortLivedProcessesSemaphore) &&
                    _cheapLongLivedProcessesSemaphore.Wait(0, cancellationToken: default))
                {
                    semaphore.Release();
                    semaphore = _cheapLongLivedProcessesSemaphore;
                }
                else if (semaphore == _shortLivedProcessesSemaphore && _mediumLivedProcessesSemaphore.Wait(0, cancellationToken: default))
                {
                    semaphore.Release();
                    semaphore = _mediumLivedProcessesSemaphore;
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
        finally
        {
            // We successfully acquired a semaphore slot, so notify that we are dequeued if applicable.
            if (queueingCallback is not null)
                await queueingCallback(false).ConfigureAwait(false);
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
        ProcessLifetime lifetime,
        bool redirectOutputContinually,
        bool runAsynchronously,
        Func<bool, ValueTask>? queueingCallback,
        CancellationToken cancellationToken)
    {
        List<Task>? redirectTasks = null;
        Process? process = null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var semaphore = await JoinProcessSemaphoreAsync(lifetime, runAsynchronously, queueingCallback, cancellationToken).ConfigureAwait(false);
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

                // Set processor affinity if specified
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    if (VideoProcessor.ProcessorAffinity is { } affinity)
                    {
                        process.ProcessorAffinity = affinity;
                    }
                }

                if (VideoProcessor.ProcessPriorityClass is { } priorityClass)
                {
                    process.PriorityClass = priorityClass;
                }

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
                        using var registration = cancellationToken.Register(() => KillProcessSafely(process));
                        process.WaitForExit();
                        cancellationToken.ThrowIfCancellationRequested(); // In case we exited due to cancellation.
                    }
                }
                catch (OperationCanceledException)
                {
                    KillProcessSafely(process);
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

    // Note about queueingCallback: true means queued, false means dequeued - not called when never queued, and always called with false, even when throwing.
    // - Throwing from queueingCallback results in undefined behavior.
    // - If runAsynchronously is false, the callback should not yield (not a correctness issue though, just means we run on the thread pool instead).

    public static async ValueTask<(string Output, string Error, int ReturnCode)> RunProcessToStringAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        ProcessLifetime lifetime = ProcessLifetime.LongLived,
        Func<bool, ValueTask>? queueingCallback = null,
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
            lifetime,
            redirectOutputContinually: false,
            runAsynchronously,
            queueingCallback,
            cancellationToken).ConfigureAwait(false);

        return (standardOutputWriter.ToString(), standardErrorWriter.ToString(), returnCode);
    }

    public static async ValueTask<string> RunProcessToStringWithErrorHandlingAsync(
        IAbsoluteFilePath fileName,
        IEnumerable<string> arguments,
        ProcessLifetime lifetime = ProcessLifetime.LongLived,
        Func<bool, ValueTask>? queueingCallback = null,
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
            lifetime,
            redirectOutputContinually: false,
            runAsynchronously,
            queueingCallback,
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
        ProcessLifetime lifetime = ProcessLifetime.LongLived,
        bool runAsynchronously = true,
        Func<bool, ValueTask>? queueingCallback = null,
        CancellationToken cancellationToken = default)
    {
        using StringWriter standardErrorWriter = new();

        int returnCode = await RunProcessAsyncImpl(
            fileName,
            arguments,
            standardOutputWriter,
            standardErrorWriter,
            lifetime,
            redirectOutputContinually: true,
            runAsynchronously,
            queueingCallback,
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
