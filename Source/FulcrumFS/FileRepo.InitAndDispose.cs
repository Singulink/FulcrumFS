using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <content>
/// Contains the implementations of init and dispose functionality for the file repository.
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

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken) => EnsureInitializedAsync(forceHealthCheck: false, cancellationToken);

    private async ValueTask EnsureInitializedAsync(bool forceHealthCheck, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long initStartTimestamp = Stopwatch.GetTimestamp();

        using (await _stateSync.LockAsync(Options.MaxAccessWaitOrRetryTime, cancellationToken))
        {
            if (_lockStream is null || forceHealthCheck || Stopwatch.GetElapsedTime(_lastSuccessfulHealthCheck) >= Options.HealthCheckInterval)
            {
                var elc = new ExceptionListCapture(ex => ex is IOException);

                if (_lockStream is not null && !elc.TryRun(CheckHealth))
                {
                    _lockStream.Dispose();
                    _lockStream = null;
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (elc.TryRun(InitializeCore))
                        return;

                    if (Stopwatch.GetElapsedTime(initStartTimestamp) > Options.MaxAccessWaitOrRetryTime)
                        throw new TimeoutException("The operation timed out attempting to get I/O access the repository.", elc.ResultException);

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        void CheckHealth()
        {
            if (_lockStream.Length > 10)
                _lockStream.SetLength(0);
            else
                _lockStream.SetLength(_lockStream.Length + 1);

            _lastSuccessfulHealthCheck = Stopwatch.GetTimestamp();
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

            var lockStream = _lockFile.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

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
