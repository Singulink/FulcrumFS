using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that delete files from the repository.
/// </content>
partial class FileRepo
{
    /// <summary>
    /// Ensures that the file repository is initialized, creating necessary directories and acquiring locks.
    /// </summary>
    public void EnsureInitialized()
    {
        using (_stateSync.Lock())
        {
            InitializeCore();
        }
    }

    /// <summary>
    /// Disposes of the file repository, releasing any resources and locks held by it.
    /// </summary>
    public void Dispose()
    {
        using (_stateSync.Lock())
        {
            if (_lockStream is not null)
            {
                _lockStream.Dispose();
                _lockStream = null;
            }

            _isDisposed = true;
        }
    }

    private async ValueTask EnsureInitializedAsync(bool forceHealthCheck = false)
    {
        long initStartTimestamp = Stopwatch.GetTimestamp();
        var cts = new CancellationTokenSource(MaxAccessWaitOrRetryTime);

        try
        {
            using (await _stateSync.LockAsync(cts.Token))
            {
                if (_lockStream is null || forceHealthCheck || Stopwatch.GetElapsedTime(_lastSuccessfulHealthCheck) >= HealthCheckInterval)
                {
                    if (_lockStream is not null)
                    {
                        try
                        {
                            CheckHealth();
                            return;
                        }
                        catch (IOException)
                        {
                            _lockStream.Dispose();
                            _lockStream = null;
                        }
                    }

                    await RetryForAccessWaitTime(InitializeCore).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("The operation timed out attempting to get I/O access the repository.");
        }

        void CheckHealth()
        {
            if (_lockStream.Length > 10)
                _lockStream.SetLength(0);
            else
                _lockStream.SetLength(_lockStream.Length + 1);

            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
        }

        async ValueTask RetryForAccessWaitTime(Action action)
        {
            while (true)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (Stopwatch.GetElapsedTime(initStartTimestamp) > MaxAccessWaitOrRetryTime)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// NOTE: MUST BE CALLED WITHIN A STATE SYNC LOCK.
    /// </summary>
    private void InitializeCore()
    {
        if (_lockStream is not null)
            return;

        InitializeCoreSlow();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitializeCoreSlow()
        {
            if (_isDisposed)
            {
                void Throw() => throw new ObjectDisposedException(nameof(FileRepo));
                Throw();
            }

            var lockStream = _lockFilePath.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

            try
            {
                _tempDirectory.Delete(recursive: true);
                _tempDirectory.Create();
                _tempDirectory.Attributes |= FileAttributes.Hidden;
                _cleanupDirectory.Create();
                _cleanupDirectory.Attributes |= FileAttributes.Hidden;
            }
            catch (IOException)
            {
                lockStream.Dispose();
                throw;
            }

            _lockStream = lockStream;
            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
        }
    }
}
