using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace FulcrumFS.Utilities;

[NonCopyable]
internal struct ExceptionListCapture(Func<Exception, bool>? catchExceptionFilter = null)
{
    private readonly Func<Exception, bool>? _catchExceptionFilter = catchExceptionFilter;
    private List<Exception>? _exceptions;

    [MemberNotNullWhen(true, nameof(ResultException))]
    public readonly bool HasExceptions => _exceptions is not null;

    public readonly Exception? ResultException
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
}
