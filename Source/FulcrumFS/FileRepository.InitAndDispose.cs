using System.Runtime.CompilerServices;

namespace FulcrumFS;

/// <content>
/// Contains the implementation of methods that delete files from the repository.
/// </content>
partial class FileRepository
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

    private async ValueTask EnsureInitializedAsync(bool forceLockStreamCheck = false)
    {
        using (await _stateSync.LockAsync())
        {
            await EnsureInitializedAsyncCore(forceLockStreamCheck).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureInitializedAsyncCore(bool forceLockStreamCheck = false)
    {
        if (_lockStream is null || forceLockStreamCheck || _lockStreamCheckWatch.Elapsed.TotalSeconds >= LockStreamCheckIntervalSeconds)
        {
            if (_lockStream is not null)
            {
                try
                {
                    CheckLockStream();
                    return;
                }
                catch (IOException)
                {
                    _lockStream.Dispose();
                    _lockStream = null;
                }
            }

            await Try10Times(InitializeCore).ConfigureAwait(false);
        }

        void CheckLockStream()
        {
            if (_lockStream.Length > 10)
                _lockStream.SetLength(0);
            else
                _lockStream.SetLength(_lockStream.Length + 1);

            _lockStreamCheckWatch.Restart();
        }

        async ValueTask Try10Times(Action action)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    action();
                    break;
                }
                catch (IOException) when (i < 9)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }
    }

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
                void Throw() => throw new ObjectDisposedException(nameof(FileRepository));
                Throw();
            }

            var lockStream = OpenLockStream();

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
            _lockStreamCheckWatch.Restart();
        }
    }
}
