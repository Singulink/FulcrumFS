using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace FulcrumFS.Utilities;

// A class rather than a struct: TryRunAsync is an async method, and an async method of a struct runs on a copy of the instance, so a capture that had no
// exceptions yet lost the one TryRunAsync recorded.
internal sealed class ExceptionListCapture(Func<Exception, bool>? catchExceptionFilter)
{
    private readonly Func<Exception, bool>? _catchExceptionFilter = catchExceptionFilter;
    private List<Exception>? _exceptions;

    [MemberNotNullWhen(true, nameof(ResultException))]
    public bool HasExceptions => _exceptions is not null;

    public Exception? ResultException
    {
        get {
            if (_exceptions is null)
                return null;

            if (_exceptions is [var ex])
                return ex;

            try
            {
                throw new AggregateException(_exceptions);
            }
            catch (AggregateException aex)
            {
                return aex;
            }
        }
    }

    [MemberNotNull(nameof(ResultException))]
    public void Add(Exception exception)
    {
        (_exceptions ??= []).Add(exception);
        Debug.Assert(HasExceptions, "Should have exception");
    }

    [MemberNotNullWhen(false, nameof(ResultException))]
    public bool TryRun(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (_catchExceptionFilter?.Invoke(ex) is not false)
        {
            (_exceptions ??= []).Add(ex);
            Debug.Assert(HasExceptions, "Should have exception");
            return false;
        }
    }

    [MemberNotNullWhen(false, nameof(ResultException))]
    public async Task<bool> TryRunAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (_catchExceptionFilter?.Invoke(ex) is not false)
        {
            (_exceptions ??= []).Add(ex);
            Debug.Assert(HasExceptions, "Should have exception");
            return false;
        }
    }

    [MemberNotNullWhen(false, nameof(ResultException))]
    public async Task<bool> TryRunAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (_catchExceptionFilter?.Invoke(ex) is not false)
        {
            (_exceptions ??= []).Add(ex);
            Debug.Assert(HasExceptions, "Should have exception");
            return false;
        }
    }

    public void ThrowIfHasExceptions()
    {
        if (HasExceptions)
            ExceptionDispatchInfo.Capture(ResultException).Throw();
    }
}
