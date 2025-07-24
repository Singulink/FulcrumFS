using Nito.AsyncEx;

namespace FulcrumFS.Utilities;

internal static class AsyncLockExtensions
{
    public static IDisposable Lock(this AsyncLock asyncLock, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return asyncLock.Lock(cancellationToken);

        if (timeout == TimeSpan.Zero)
        {
            try
            {
                return asyncLock.Lock(new CancellationToken(true));
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Failed to acquire lock immediately.");
            }
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return asyncLock.Lock(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("Failed to acquire lock within the specified timeout.");
        }
    }

    public static AwaitableDisposable<IDisposable> LockAsync(this AsyncLock asyncLock, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return new(Impl(asyncLock, timeout, cancellationToken));

        static async Task<IDisposable> Impl(AsyncLock asyncLock, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return await asyncLock.LockAsync(cancellationToken);

            if (timeout == TimeSpan.Zero)
            {
                try
                {
                    return await asyncLock.LockAsync(new CancellationToken(true));
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Failed to acquire lock immediately.");
                }
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                return await asyncLock.LockAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException("Failed to acquire lock within the specified timeout.");
            }
        }
    }
}
